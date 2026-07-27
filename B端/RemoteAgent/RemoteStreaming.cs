using H264Sharp;
using RemoteControl.Shared;
using SharpGen.Runtime;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Collections.Specialized;
using System.Collections.Concurrent;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Threading;
using Clipboard = System.Windows.Clipboard;
using Lennox.LibYuvSharp;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

namespace RemoteAgent;

public partial class MainWindow
{
  private readonly AdaptiveVideoController _videoQuality = new();
  private readonly WindowsInputInjector _inputInjector = new();
  private readonly WindowsInputDispatcher _inputDispatcher;
  private string _lastClipboardText = string.Empty;
  private volatile bool _settingRemoteClipboard;
  private TcpClient? _clipboardClient;
  private NetworkStream? _clipboardStream;
  private readonly SemaphoreSlim _clipboardWriteLock = new(1, 1);
  private ClipboardFileReceiver? _clipboardReceiver;
  private HwndSource? _clipboardWindowSource;
  private IntPtr _clipboardWindowHandle;
  private uint _remoteClipboardSequence;
  private uint _lastSentClipboardSequence;
  private int _clipboardSyncing;
  private DispatcherTimer? _clipboardTimer;

  private const int WmClipboardUpdate = 0x031D;
  [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
  [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
  [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();

  private static int InitializeLowLatencyEncoder(H264Encoder encoder, int width, int height, int bitrate, int fps)
  {
    var param = encoder.GetDefaultParameters();
    param.iUsageType = EUsageType.SCREEN_CONTENT_REAL_TIME;
    param.iPicWidth = width;
    param.iPicHeight = height;
    param.iTargetBitrate = bitrate;
    param.iRCMode = RC_MODES.RC_BITRATE_MODE;
    param.fMaxFrameRate = fps;
    param.iTemporalLayerNum = 1;
    param.iSpatialLayerNum = 1;
    param.iComplexityMode = ECOMPLEXITY_MODE.LOW_COMPLEXITY;
    // Loss feedback and scene-change detection request an IDR immediately.
    // This one-second period is the final safety net for a damaged reference
    // chain instead of the previous visible two-to-three second recovery.
    param.uiIntraPeriod = (uint)Math.Max(fps, 30);
    param.iNumRefFrame = 1;
    param.eSpsPpsIdStrategy = EParameterSetStrategy.SPS_LISTING_AND_PPS_INCREASING;
    param.bPrefixNalAddingCtrl = false;
    param.bEnableSSEI = false;
    param.bSimulcastAVC = false;
    param.iEntropyCodingModeFlag = 0;
    // Preserve predictive-frame continuity. The outer QoS controller lowers
    // FPS before the encoder is allowed to sacrifice desktop frames.
    param.bEnableFrameSkip = false;
    param.iMaxBitrate = bitrate;
    param.iMinQp = 0;
    param.iMaxQp = 34;
    param.uiMaxNalSize = 0;
    param.bEnableLongTermReference = false;
    param.iMultipleThreadIdc = (ushort)Math.Clamp(Environment.ProcessorCount, 1, 16);
    param.bUseLoadBalancing = true;
    param.bEnableDenoise = false;
    param.bEnableBackgroundDetection = true;
    param.bEnableAdaptiveQuant = true;
    param.bEnableSceneChangeDetect = true;
    param.bIsLosslessLink = false;
    param.bFixRCOverShoot = true;
    param.iIdrBitrateRatio = 120;

    var layer = param.sSpatialLayers[0];
    layer.iVideoWidth = width;
    layer.iVideoHeight = height;
    layer.fFrameRate = fps;
    layer.iSpatialBitrate = bitrate;
    layer.iMaxSpatialBitrate = bitrate;
    layer.uiProfileIdc = EProfileIdc.PRO_BASELINE;
    layer.uiLevelIdc = ELevelIdc.LEVEL_UNKNOWN;
    layer.iDLayerQp = 0;
    layer.bVideoSignalTypePresent = true;
    layer.uiVideoFormat = 5;
    layer.bFullRange = true;
    layer.bColorDescriptionPresent = true;
    layer.uiColorPrimaries = 1;
    layer.uiTransferCharacteristics = 13;
    layer.uiColorMatrix = 6;
    param.sSpatialLayers[0] = layer;
    return encoder.Initialize(param);
  }

  private void InitializeClipboardWatcher()
  {
    _clipboardWindowSource = PresentationSource.FromVisual(this) as HwndSource;
    if (_clipboardWindowSource is null) return;
    _clipboardWindowHandle = _clipboardWindowSource.Handle;
    _clipboardWindowSource.AddHook(ClipboardWindowProc);
    AddClipboardFormatListener(_clipboardWindowHandle);
    _clipboardTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
    _clipboardTimer.Tick += ClipboardTimer_Tick;
    _clipboardTimer.Start();
  }

  private void ClipboardTimer_Tick(object? sender, EventArgs e)
  {
    uint sequence = GetClipboardSequenceNumber();
    if (sequence != 0 && sequence != _remoteClipboardSequence && sequence != _lastSentClipboardSequence)
      QueueClipboardSync();
  }

  private void DisposeClipboardWatcher()
  {
    if (_clipboardTimer is not null)
    {
      _clipboardTimer.Stop();
      _clipboardTimer.Tick -= ClipboardTimer_Tick;
      _clipboardTimer = null;
    }
    try
    {
      if (_clipboardWindowHandle != IntPtr.Zero) RemoveClipboardFormatListener(_clipboardWindowHandle);
      _clipboardWindowSource?.RemoveHook(ClipboardWindowProc);
    }
    catch { }
    _clipboardWindowSource = null;
    _clipboardWindowHandle = IntPtr.Zero;
    try { _clipboardReceiver?.Dispose(); } catch { }
  }

  private IntPtr ClipboardWindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
  {
    if (message == WmClipboardUpdate) QueueClipboardSync();
    return IntPtr.Zero;
  }

  private void QueueClipboardSync()
  {
    if (_clipboardStream is null) return;
    Dispatcher.BeginInvoke(async () => await SyncLocalClipboardAsync());
  }

  private async Task SyncLocalClipboardAsync()
  {
    if (_settingRemoteClipboard || Interlocked.Exchange(ref _clipboardSyncing, 1) != 0) return;
    try
    {
      ClipboardSnapshot? snapshot = await ReadClipboardWithRetryAsync();
      if (snapshot is null) return;
      uint sequence = snapshot.Sequence;
      if (sequence == 0 || sequence == _remoteClipboardSequence || sequence == _lastSentClipboardSequence) return;

      if (snapshot.Paths is { Length: > 0 })
      {
        string[] paths = snapshot.Paths.Where(x => !IsClipboardCachePath(x)).ToArray();
        if (paths.Length == 0) return;
        _lastSentClipboardSequence = sequence;
        await SendClipboardFilesAsync(paths, CancellationToken.None);
      }
      else if (snapshot.Text is not null)
      {
        string text = snapshot.Text;
        if (System.Text.Encoding.UTF8.GetByteCount(text) > 8 * 1024 * 1024) return;
        _lastClipboardText = text;
        _lastSentClipboardSequence = sequence;
        await SendClipboardTextAsync(text, CancellationToken.None);
      }
      else
      {
        _lastSentClipboardSequence = sequence;
      }
    }
    catch (Exception) { }
    finally
    {
      Interlocked.Exchange(ref _clipboardSyncing, 0);
    }
  }

  private async Task ClipboardConnectLoopAsync(string host, int clipboardPort, CancellationToken token)
  {
    while (!token.IsCancellationRequested && _stream is not null)
    {
      try
      {
        _clipboardClient = new TcpClient { NoDelay = true, SendBufferSize = 128 * 1024, ReceiveBufferSize = 128 * 1024 };
        await _clipboardClient.ConnectAsync(host, clipboardPort, token);
        _clipboardStream = _clipboardClient.GetStream();
        await WriteLogicalChannelHelloAsync(_clipboardStream, LogicalChannelType.Clipboard, token);
        await BinaryClipboardProtocol.WriteHelloAsync(_clipboardStream, _deviceId, token);
        string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AuthorizedDeviceControl", "Clipboard", "Controller");
        _clipboardReceiver?.Dispose();
        _clipboardReceiver = new ClipboardFileReceiver(cache);
        QueueClipboardSync();

        NetworkStream stream = _clipboardStream;
        while (!token.IsCancellationRequested && ReferenceEquals(_clipboardStream, stream))
        {
          ClipboardPacket? packet = await BinaryClipboardProtocol.ReadAsync(stream, token);
          if (packet is null) break;
          if (packet.Type == ClipboardPacketType.Text)
          {
            await ApplyRemoteClipboardTextAsync(BinaryClipboardProtocol.ReadText(packet));
            continue;
          }
          IReadOnlyList<string>? paths = await _clipboardReceiver.ProcessAsync(packet, token);
          if (paths is { Count: > 0 }) await ApplyRemoteClipboardFilesAsync(paths);
        }
      }
      catch (OperationCanceledException) { break; }
      catch (Exception)
      {
        await Task.Delay(750, token).ContinueWith(_ => { });
      }
      finally
      {
        try { _clipboardStream?.Dispose(); _clipboardClient?.Close(); } catch { }
        _clipboardStream = null;
        _clipboardClient = null;
      }
    }
  }

  private async Task ApplyRemoteClipboardTextAsync(string text)
  {
    await Dispatcher.InvokeAsync(async () =>
    {
      _settingRemoteClipboard = true;
      try
      {
        if (await SetClipboardWithRetryAsync(() => Clipboard.SetText(text)))
        {
          _lastClipboardText = text;
          _remoteClipboardSequence = GetClipboardSequenceNumber();
        }
      }
      finally { _settingRemoteClipboard = false; }
    }).Task.Unwrap();
  }

  private async Task ApplyRemoteClipboardFilesAsync(IReadOnlyList<string> paths)
  {
    await Dispatcher.InvokeAsync(async () =>
    {
      _settingRemoteClipboard = true;
      try
      {
        var files = new StringCollection();
        files.AddRange(paths.ToArray());
        if (await SetClipboardWithRetryAsync(() => Clipboard.SetFileDropList(files)))
          _remoteClipboardSequence = GetClipboardSequenceNumber();
      }
      finally { _settingRemoteClipboard = false; }
    }).Task.Unwrap();
  }

  private static async Task<ClipboardSnapshot?> ReadClipboardWithRetryAsync()
  {
    for (int attempt = 0; attempt < 8; attempt++)
    {
      try
      {
        uint sequence = GetClipboardSequenceNumber();
        if (Clipboard.ContainsFileDropList())
        {
          string[] paths = Clipboard.GetFileDropList().Cast<string>()
            .Where(x => File.Exists(x) || Directory.Exists(x)).ToArray();
          return new ClipboardSnapshot(sequence, paths, null);
        }
        if (Clipboard.ContainsText()) return new ClipboardSnapshot(sequence, null, Clipboard.GetText());
        return new ClipboardSnapshot(sequence, null, null);
      }
      catch (COMException) when (attempt < 7)
      {
        await Task.Delay(20 * (attempt + 1));
      }
    }
    return null;
  }

  private static async Task<bool> SetClipboardWithRetryAsync(Action setter)
  {
    for (int attempt = 0; attempt < 8; attempt++)
    {
      try { setter(); return true; }
      catch (COMException) when (attempt < 7) { await Task.Delay(20 * (attempt + 1)); }
    }
    return false;
  }

  private sealed record ClipboardSnapshot(uint Sequence, string[]? Paths, string? Text);

  private async Task SendClipboardTextAsync(string text, CancellationToken token)
  {
    NetworkStream? stream = _clipboardStream;
    if (stream is null) throw new IOException("剪贴板通道尚未连接。");
    await _clipboardWriteLock.WaitAsync(token);
    try
    {
      if (ReferenceEquals(_clipboardStream, stream)) await BinaryClipboardProtocol.WriteAsync(stream, BinaryClipboardProtocol.Text(text), token);
    }
    finally { _clipboardWriteLock.Release(); }
  }

  private async Task SendClipboardFilesAsync(IEnumerable<string> paths, CancellationToken token)
  {
    NetworkStream? stream = _clipboardStream;
    if (stream is null) throw new IOException("剪贴板通道尚未连接。");
    await BinaryClipboardProtocol.SendFilesAsync(stream, _clipboardWriteLock, paths, token);
  }

  private static bool IsClipboardCachePath(string path)
  {
    string root = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AuthorizedDeviceControl", "Clipboard")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    try { return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase); }
    catch { return false; }
  }


}

internal sealed record VideoProfile(
  string Name,
  int MaximumWidth,
  int MaximumHeight,
  int Fps,
  int Bitrate);


internal sealed class DxgiDesktopCapture : IDisposable
{
  // A helper launched from the LocalSystem service runs in the interactive
  // session but owns a SYSTEM token. On affected Windows 11 builds,
  // IDXGIOutputDuplication.AcquireNextFrame can block indefinitely in that
  // configuration even when a finite timeout is supplied. GDI capture remains
  // available on winsta0 and, combined with H.264, keeps the video pipeline
  // real-time while preserving SYSTEM-level input on secure desktops.
  private readonly bool _forceGdi = WindowsIdentity.GetCurrent().IsSystem;
  private IDXGIFactory1? _factory;
  private IDXGIAdapter1? _adapter;
  private IDXGIOutput? _output;
  private IDXGIOutput1? _output1;
  private IDXGIOutputDuplication? _duplication;
  private ID3D11Device? _device;
  private ID3D11DeviceContext? _context;
  private ID3D11Texture2D? _staging;
  private RgbImage? _scaledFrame;
  private Bitmap? _gdiBitmap;
  private Graphics? _gdiGraphics;
  private Rectangle _bounds;
  private DateTime _nextDxgiRetryUtc;
  private bool _hasDeliveredFrame;
  public string Mode { get; private set; } = "DXGI Desktop Duplication";

  public DxgiDesktopCapture()
  {
    if (_forceGdi)
    {
      Mode = "GDI (SYSTEM session) + H.264";
      return;
    }
    try { Initialize(); }
    catch { DisposeDxgi(); _nextDxgiRetryUtc = DateTime.UtcNow.AddSeconds(2); Mode = "GDI fallback"; }
  }

  private void Initialize()
  {
    _factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<IDXGIFactory1>();
    _factory.EnumAdapters1(0, out _adapter).CheckError();
    _adapter.EnumOutputs(0, out _output).CheckError();
    _output1 = _output.QueryInterface<IDXGIOutput1>();
    var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
    D3D11CreateDevice(_adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, levels, out _device, out _context).CheckError();
    _duplication = _output1.DuplicateOutput(_device);
    var r = _output.Description.DesktopCoordinates;
    _bounds = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
  }

  public bool TryCapture(
    int maximumWidth,
    int maximumHeight,
    Action<IntPtr, int, int, int, Rectangle> consume)
  {
    if (_forceGdi)
      return TryCaptureGdi(maximumWidth, maximumHeight, consume);

    if (_duplication is null || _device is null || _context is null)
    {
      if (DateTime.UtcNow >= _nextDxgiRetryUtc)
      {
        try { Initialize(); Mode = "DXGI Desktop Duplication"; }
        catch { DisposeDxgi(); _nextDxgiRetryUtc = DateTime.UtcNow.AddSeconds(2); }
      }
      if (_duplication is null || _device is null || _context is null)
        return TryCaptureGdi(maximumWidth, maximumHeight, consume);
    }
    IDXGIResource? resource = null;
    bool acquired = false;
    bool invokingConsumer = false;
    try
    {
      var result = _duplication.AcquireNextFrame(0, out _, out resource);
      if (result.Failure || resource is null)
      {
        // Desktop Duplication is change-driven and may time out forever on a
        // completely static desktop immediately after a viewer connects. Seed
        // the stream once with GDI, then return to DXGI change frames.
        if (!_hasDeliveredFrame && TryCaptureGdi(maximumWidth, maximumHeight, consume))
        {
          _hasDeliveredFrame = true;
          return true;
        }
        return false;
      }
      acquired = true;
      using var desktop = resource.QueryInterface<ID3D11Texture2D>();
      var desc = desktop.Description;
      if (_staging is null || _staging.Description.Width != desc.Width || _staging.Description.Height != desc.Height)
      {
        _staging?.Dispose();
        var stagingDesc = desc;
        stagingDesc.BindFlags = BindFlags.None;
        stagingDesc.CPUAccessFlags = CpuAccessFlags.Read;
        stagingDesc.Usage = ResourceUsage.Staging;
        stagingDesc.MiscFlags = ResourceOptionFlags.None;
        _staging = _device.CreateTexture2D(stagingDesc);
      }
      _context.CopyResource(_staging, desktop);
      var mapped = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
      try { invokingConsumer = true; ConsumePossiblyScaled(mapped.DataPointer, (int)mapped.RowPitch, (int)desc.Width, (int)desc.Height, _bounds, maximumWidth, maximumHeight, consume); invokingConsumer = false; }
      finally { _context.Unmap(_staging, 0); }
      _hasDeliveredFrame = true;
      return true;
    }
    catch
    {
      if (invokingConsumer) throw;
      DisposeDxgi(); _nextDxgiRetryUtc = DateTime.UtcNow.AddSeconds(1); Mode = "GDI fallback";
      return TryCaptureGdi(maximumWidth, maximumHeight, consume);
    }
    finally
    {
      resource?.Dispose();
      if (acquired) try { _duplication?.ReleaseFrame(); } catch { }
    }
  }

  public bool TryCaptureForced(
    int maximumWidth,
    int maximumHeight,
    Action<IntPtr, int, int, int, Rectangle> consume) =>
    TryCapture(maximumWidth, maximumHeight, consume) ||
    TryCaptureGdi(maximumWidth, maximumHeight, consume);

  private void ConsumePossiblyScaled(
    IntPtr pointer,
    int stride,
    int width,
    int height,
    Rectangle bounds,
    int maximumWidth,
    int maximumHeight,
    Action<IntPtr, int, int, int, Rectangle> consume)
  {
    double scale = Math.Min(
      1.0,
      Math.Min(
        maximumWidth / (double)Math.Max(1, width),
        maximumHeight / (double)Math.Max(1, height)));
    int targetWidth = Math.Max(2, ((int)Math.Round(width * scale)) & ~1);
    int targetHeight = Math.Max(2, ((int)Math.Round(height * scale)) & ~1);
    if (targetWidth == width && targetHeight == height) { consume(pointer, stride, width, height, bounds); return; }
    if (_scaledFrame is null || _scaledFrame.Width != targetWidth || _scaledFrame.Height != targetHeight)
    {
      _scaledFrame?.Dispose();
      _scaledFrame = new RgbImage(H264Sharp.ImageFormat.Bgra, targetWidth, targetHeight);
    }
    unsafe
    {
      int result = LibYuv.ARGBScale((byte*)pointer, stride, width, height, (byte*)_scaledFrame.NativeBytes, _scaledFrame.Stride, targetWidth, targetHeight, FilterMode.Bilinear);
      if (result != 0) throw new InvalidOperationException($"libyuv 缩放失败：{result}");
    }
    consume(_scaledFrame.NativeBytes, _scaledFrame.Stride, targetWidth, targetHeight, bounds);
  }

  private bool TryCaptureGdi(
    int maximumWidth,
    int maximumHeight,
    Action<IntPtr, int, int, int, Rectangle> consume)
  {
    bool invokingConsumer = false;
    try
    {
      var bounds = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
      if (_gdiBitmap is null || _gdiBitmap.Width != bounds.Width || _gdiBitmap.Height != bounds.Height)
      {
        _gdiGraphics?.Dispose();
        _gdiBitmap?.Dispose();
        _gdiBitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        _gdiGraphics = Graphics.FromImage(_gdiBitmap);
      }
      _gdiGraphics!.CopyFromScreen(
        bounds.Left,
        bounds.Top,
        0,
        0,
        bounds.Size,
        CopyPixelOperation.SourceCopy);
      var bits = _gdiBitmap.LockBits(
        new Rectangle(0, 0, _gdiBitmap.Width, _gdiBitmap.Height),
        ImageLockMode.ReadOnly,
        PixelFormat.Format32bppArgb);
      try { invokingConsumer = true; ConsumePossiblyScaled(bits.Scan0, bits.Stride, _gdiBitmap.Width, _gdiBitmap.Height, bounds, maximumWidth, maximumHeight, consume); invokingConsumer = false; }
      finally { _gdiBitmap.UnlockBits(bits); }
      return true;
    }
    catch { if (invokingConsumer) throw; return false; }
  }

  private void DisposeDxgi()
  {
    _staging?.Dispose(); _duplication?.Dispose(); _output1?.Dispose(); _output?.Dispose(); _context?.Dispose(); _device?.Dispose(); _adapter?.Dispose(); _factory?.Dispose();
    _staging = null; _duplication = null; _output1 = null; _output = null; _context = null; _device = null; _adapter = null; _factory = null;
    _scaledFrame?.Dispose(); _scaledFrame = null;
    _hasDeliveredFrame = false;
  }
  public void Dispose()
  {
    DisposeDxgi();
    _gdiGraphics?.Dispose();
    _gdiBitmap?.Dispose();
    _gdiGraphics = null;
    _gdiBitmap = null;
  }
}

internal sealed class WindowsInputInjector
{
  private readonly Dictionary<ushort, KeyState> _keys = new();
  private readonly HashSet<byte> _buttons = new();
  private readonly object _gate = new();
  private int _lastX, _lastY;

  private const uint InputMouse = 0, InputKeyboard = 1;
  private const uint MouseMove = 0x0001, LeftDown = 0x0002, LeftUp = 0x0004, RightDown = 0x0008, RightUp = 0x0010, MiddleDown = 0x0020, MiddleUp = 0x0040, WheelFlag = 0x0800, Absolute = 0x8000, VirtualDesk = 0x4000;
  private const uint KeyUp = 0x0002, UnicodeKey = 0x0004, ScanCode = 0x0008, ExtendedKey = 0x0001;

  [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint nInputs, INPUT[] inputs, int cbSize);
  [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

  public void Move(int x, int y) { lock (_gate) { _lastX = x; _lastY = y; SendMousePosition(x, y); } }
  public void Button(int x, int y, byte button, bool down)
  {
    lock (_gate)
    {
      _lastX = x; _lastY = y; SendMousePosition(x, y);
      uint flag = button switch { 1 when down => LeftDown, 1 => LeftUp, 2 when down => RightDown, 2 => RightUp, 3 when down => MiddleDown, _ => MiddleUp };
      SendMouse(0, 0, 0, flag);
      if (down) _buttons.Add(button); else _buttons.Remove(button);
    }
  }
  public void Wheel(int x, int y, int delta) { lock (_gate) { _lastX = x; _lastY = y; SendMousePosition(x, y); SendMouse(0, 0, unchecked((uint)delta), WheelFlag); } }
  public void Key(ushort virtualKey, ushort scanCode, bool down, bool extended)
  {
    lock (_gate)
    {
      uint flags = scanCode != 0 ? ScanCode : 0;
      if (!down) flags |= KeyUp;
      if (extended) flags |= ExtendedKey;
      var input = new INPUT { type = InputKeyboard, U = new InputUnion { ki = new KEYBDINPUT { wVk = scanCode == 0 ? virtualKey : (ushort)0, wScan = scanCode, dwFlags = flags } } };
      SendChecked(input);
      if (down) _keys[virtualKey] = new(scanCode, extended); else _keys.Remove(virtualKey);
    }
  }
  public void Text(string text)
  {
    lock (_gate)
    {
      foreach (char value in text)
      {
        var down = new INPUT
        {
          type = InputKeyboard,
          U = new InputUnion
          {
            ki = new KEYBDINPUT
            {
              wVk = 0,
              wScan = value,
              dwFlags = UnicodeKey
            }
          }
        };
        var up = down;
        up.U.ki.dwFlags = UnicodeKey | KeyUp;
        SendChecked(down);
        SendChecked(up);
      }
    }
  }
  public void ReleaseAll()
  {
    lock (_gate)
    {
      foreach (byte b in _buttons.ToArray()) Button(_lastX, _lastY, b, false);
      foreach (var pair in _keys.ToArray()) Key(pair.Key, pair.Value.ScanCode, false, pair.Value.Extended);
      _buttons.Clear(); _keys.Clear();
    }
  }
  private static void SendMousePosition(int x, int y)
  {
    int left = GetSystemMetrics(76), top = GetSystemMetrics(77), width = Math.Max(2, GetSystemMetrics(78)), height = Math.Max(2, GetSystemMetrics(79));
    int nx = (int)Math.Clamp(Math.Round((x - left) * 65535d / (width - 1)), 0, 65535);
    int ny = (int)Math.Clamp(Math.Round((y - top) * 65535d / (height - 1)), 0, 65535);
    SendMouse(nx, ny, 0, MouseMove | Absolute | VirtualDesk);
  }
  private static void SendMouse(int x, int y, uint data, uint flags)
  {
    var input = new INPUT { type = InputMouse, U = new InputUnion { mi = new MOUSEINPUT { dx = x, dy = y, mouseData = data, dwFlags = flags } } };
    SendChecked(input);
  }
  private static void SendChecked(INPUT input)
  {
    Marshal.SetLastPInvokeError(0);
    if (SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>()) == 1) return;
    int error = Marshal.GetLastPInvokeError();
    throw new WindowsInputDispatcher.InputInjectionException(
      WindowsInputDispatcher.InputInjectionStage.SendInput,
      error,
      "Windows拒绝了远程输入注入。");
  }

  [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion U; }
  [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
  [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public UIntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public UIntPtr dwExtraInfo; }
  private readonly record struct KeyState(ushort ScanCode, bool Extended);
}

internal sealed class WindowsInputDispatcher : IDisposable
{
  private readonly WindowsInputInjector _injector;
  private readonly BlockingCollection<InputWorkItem> _queue = new(1024);
  private readonly CancellationTokenSource _shutdown = new();
  private readonly Thread _thread;
  private int _disposed;

  public WindowsInputDispatcher(WindowsInputInjector injector)
  {
    _injector = injector;
    _thread = new Thread(ThreadMain)
    {
      IsBackground = true,
      Name = "Agebt B端输入注入线程"
    };
    _thread.Start();
  }

  public Task ExecuteAsync(
    ControlPacket packet,
    long desktopGeneration,
    CancellationToken token) =>
    EnqueueAsync(packet, desktopGeneration, token);

  public Task ReleaseAllAsync(
    long desktopGeneration,
    CancellationToken token) =>
    EnqueueAsync(
      BinaryControlProtocol.ReleaseAll(),
      desktopGeneration,
      token);

  public Task TypeTextAsync(
    string text,
    long desktopGeneration,
    CancellationToken token) =>
    EnqueueAsync(text, desktopGeneration, token);

  public void QueueReleaseAll(long desktopGeneration)
  {
    if (Volatile.Read(ref _disposed) != 0) return;
    var item = new InputWorkItem(
      BinaryControlProtocol.ReleaseAll(),
      desktopGeneration,
      CancellationToken.None);
    if (!_queue.TryAdd(item)) item.Completion.TrySetCanceled();
  }

  private Task EnqueueAsync(
    ControlPacket packet,
    long desktopGeneration,
    CancellationToken token)
  {
    ObjectDisposedException.ThrowIf(
      Volatile.Read(ref _disposed) != 0,
      this);
    var item = new InputWorkItem(packet, desktopGeneration, token);
    if (!_queue.TryAdd(item, 100, token))
      throw new IOException("远程输入队列已满。");
    return item.Completion.Task;
  }

  private Task EnqueueAsync(
    string text,
    long desktopGeneration,
    CancellationToken token)
  {
    ObjectDisposedException.ThrowIf(
      Volatile.Read(ref _disposed) != 0,
      this);
    var item = new InputWorkItem(text, desktopGeneration, token);
    if (!_queue.TryAdd(item, 100, token))
      throw new IOException("远程输入队列已满。");
    return item.Completion.Task;
  }

  private void ThreadMain()
  {
    try
    {
      while (!_shutdown.IsCancellationRequested)
      {
        try
        {
          if (!_queue.TryTake(out InputWorkItem? item, 50, _shutdown.Token) ||
              item is null)
            continue;
          if (item.Token.IsCancellationRequested)
          {
            item.Completion.TrySetCanceled(item.Token);
            continue;
          }
          try
          {
            try
            {
              WindowsInputDesktop.AttachCurrentThread(item.DesktopGeneration);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
              throw new InputInjectionException(
                InputInjectionStage.AttachDesktop,
                ex.NativeErrorCode,
                ex.Message);
            }
            if (item.Text is not null)
              _injector.Text(item.Text);
            else
              Dispatch(item.Packet!);
            item.Completion.TrySetResult();
          }
          catch (Exception ex)
          {
            item.Completion.TrySetException(ex);
          }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
          break;
        }
      }
    }
    finally
    {
      while (_queue.TryTake(out InputWorkItem? pending))
        pending.Completion.TrySetCanceled();
    }
  }

  internal sealed class InputInjectionException(
    InputInjectionStage stage,
    int nativeErrorCode,
    string message) :
    System.ComponentModel.Win32Exception(
      EncodeInputError(stage, nativeErrorCode),
      message)
  {
    private static int EncodeInputError(
      InputInjectionStage stage,
      int nativeErrorCode) =>
      ((int)stage * 100000) + Math.Abs(nativeErrorCode);
  }

  internal enum InputInjectionStage
  {
    AttachDesktop = 1,
    SendInput = 2
  }

  private void Dispatch(ControlPacket packet)
  {
    switch (packet.Type)
    {
      case ControlPacketType.MouseMove:
        MouseMovePacket move = BinaryControlProtocol.ReadMouseMove(packet);
        _injector.Move(move.X, move.Y);
        break;
      case ControlPacketType.MouseButton:
        MouseButtonPacket button = BinaryControlProtocol.ReadMouseButton(packet);
        _injector.Button(
          button.X,
          button.Y,
          button.Button,
          button.Down);
        break;
      case ControlPacketType.MouseWheel:
        MouseWheelPacket wheel = BinaryControlProtocol.ReadMouseWheel(packet);
        _injector.Wheel(wheel.X, wheel.Y, wheel.Delta);
        break;
      case ControlPacketType.Key:
        KeyPacket key = BinaryControlProtocol.ReadKey(packet);
        _injector.Key(
          key.VirtualKey,
          key.ScanCode,
          key.Down,
          key.Extended);
        break;
      case ControlPacketType.ReleaseAll:
        _injector.ReleaseAll();
        break;
      default:
        throw new InvalidOperationException(
          $"不是输入注入数据包：{packet.Type}");
    }
  }

  public void Dispose()
  {
    if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
    _shutdown.Cancel();
    _queue.CompleteAdding();
    if (_thread.Join(TimeSpan.FromMilliseconds(100)))
    {
      _queue.Dispose();
      _shutdown.Dispose();
    }
  }

  private sealed class InputWorkItem(
    ControlPacket? packet,
    long desktopGeneration,
    CancellationToken token)
  {
    public ControlPacket? Packet { get; } = packet;
    public string? Text { get; }
    public long DesktopGeneration { get; } = desktopGeneration;
    public CancellationToken Token { get; } = token;
    public TaskCompletionSource Completion { get; } =
      new(TaskCreationOptions.RunContinuationsAsynchronously);

    public InputWorkItem(
      string text,
      long desktopGeneration,
      CancellationToken token) :
      this((ControlPacket?)null, desktopGeneration, token)
    {
      Text = text;
    }
  }
}
