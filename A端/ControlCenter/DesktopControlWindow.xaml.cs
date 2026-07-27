using H264Sharp;
using RemoteControl.Shared;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.IO;
using System.Windows.Interop;
using System.ComponentModel;
using System.Text;
using Lennox.LibYuvSharp;
using System.Buffers;
using Concentus;
using NAudio.Wave;
using System.Threading.Channels;

namespace ControlCenter;

public partial class DesktopControlWindow : Window
{
  private readonly DeviceView _device;
  private readonly Guid _sessionId = Guid.NewGuid();
  private readonly DispatcherTimer _watchdogTimer;
  private readonly DispatcherTimer _mouseTimer;
  private readonly object _decoderGate = new();
  private H264Decoder? _decoder;
  private long _nextDecoderRetryTicks;
  private string _lastVideoError = string.Empty;
  private readonly CancellationTokenSource _decodeCts = new();
  private readonly CancellationTokenSource _lifetimeCts = new();
  private RgbImage? _decodedImage;
  private readonly OrderedVideoFrameBuffer _videoFrames = new(
    capacity: 45,
    reorderGraceMilliseconds: 12);
  private readonly SemaphoreSlim _decodeSignal = new(0, 1);
  private readonly DispatcherTimer _renderTimer;
  private readonly object _renderGate = new();
  private PendingRenderedFrame? _pendingRenderedFrame;
  private Task? _decodeTask;
  private WriteableBitmap? _videoBitmap;
  private bool _fit = true;
  private bool _fullscreen;
  private bool _closing;
  private bool _moveSending;
  private bool _mouseDirty;
  private bool _leftDown;
  private bool _rightDown;
  private bool _layoutTransition;
  private bool _suppressNextEscapeKeyUp;
  private bool _settingRemoteClipboard;
  private HwndSource? _clipboardWindowSource;
  private IntPtr _clipboardWindowHandle;
  private uint _remoteClipboardSequence;
  private uint _lastSentClipboardSequence;
  private int _clipboardSyncing;
  private int _framesReceived;
  private int _decodeErrors;
  private int _decodeMilliseconds;
  private int _renderMilliseconds;
  private int _displayWidth;
  private int _displayHeight;
  private int _remoteWidth;
  private int _remoteHeight;
  private int _remoteX;
  private int _remoteY;
  private int _lastMouseX;
  private int _lastMouseY;
  private long _lastFrameId;
  private long _lastReceivedFrameId;
  private long _lastFeedbackFrameId;
  private long _lastBytes;
  private bool _requestKeyFrame;
  private volatile bool _controlEnabled = true;
  private string _lastClipboardText = string.Empty;
  private DateTime _lastFrameAt = DateTime.MinValue;
  private WindowState _previousState;
  private WindowStyle _previousStyle;
  private ResizeMode _previousResizeMode;
  private Rect _previousBounds;
  private long _lastInputPingTicks;
  private long _lastClipboardPollTicks;
  private long _lastKeyFrameRequestTicks;
  private int _consecutiveInputFailures;
  private int _feedbackSending;
  private int _closeStarted;
  private readonly object _audioGate = new();
  private readonly Channel<RemoteAudioFrame> _audioFrames =
    Channel.CreateBounded<RemoteAudioFrame>(
      new BoundedChannelOptions(8)
      {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest
      });
  private Task? _audioDecodeTask;
  private IOpusDecoder? _audioDecoder;
  private BufferedWaveProvider? _audioBuffer;
  private WaveOutEvent? _audioOutput;
  private bool _audioEnabled;
  private long _lastAudioSequence;
  private long _lastRenderedVideoTimestampTicks;

  private sealed record PendingRenderedFrame(
    RemoteVideoFrame Frame,
    byte[] Pixels,
    int BufferLength,
    int Stride);

  [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
  [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
  [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
  [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
  [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
  [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
  [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
  [DllImport("user32.dll", SetLastError = true)] private static extern bool IsClipboardFormatAvailable(uint format);
  [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint format);
  [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
  [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);
  [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr memory);
  [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr memory);
  [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr memory);
  [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder? path, uint pathLength);
  private const int WmClipboardUpdate = 0x031D;
  private const uint CfUnicodeText = 13;
  private const uint CfHDrop = 15;
  private const uint GmemMoveableZeroInit = 0x0042;

  public DesktopControlWindow(DeviceView device, bool allowControl)
  {
    InitializeComponent();
    _device = device;
    TitleText.Text = $"{device.DisplayTitle} · {device.RemoteEndPoint}";
    SetControlMode(allowControl);
    Converter.SetOption(ConverterOption.NumThreads, Math.Clamp(Environment.ProcessorCount / 2, 2, 8));
    _watchdogTimer = new DispatcherTimer(
      TimeSpan.FromMilliseconds(50),
      DispatcherPriority.Background,
      WatchdogTimer_Tick,
      Dispatcher);
    _renderTimer = new DispatcherTimer(
      TimeSpan.FromMilliseconds(16),
      DispatcherPriority.Background,
      RenderTimer_Tick,
      Dispatcher);
    _mouseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(8) };
    _mouseTimer.Tick += MouseTimer_Tick;
    SourceInitialized += (_, _) => InitializeClipboardWatcher();
  }

  public void SetControlMode(bool allowControl)
  {
    _controlEnabled = allowControl;
    ControlBox.IsChecked = allowControl;
    Title = $"桌面{(allowControl ? "控制" : "观看")} - {_device.DisplayTitle}";
    if (allowControl && IsLoaded) QueueClipboardSync();
  }

  private async void ControlBox_Changed(object sender, RoutedEventArgs e)
  {
    _controlEnabled = ControlBox.IsChecked == true;
    if (!IsLoaded) return;
    try
    {
      if (_controlEnabled) QueueClipboardSync();
      else await ReleaseAllInputAsync();
      await _device.StartUdpDesktopSessionAsync(
        _sessionId,
        _controlEnabled,
        _lifetimeCts.Token);
    }
    catch (OperationCanceledException) when (_closing) { }
    catch (Exception ex) { SetStatus("更新控制模式失败：" + ex.Message); }
  }

  private async void Window_Loaded(object sender, RoutedEventArgs e)
  {
    if (_closing) return;
    _decodeTask ??= Task.Run(DecodeLatestFramesAsync);
    _audioDecodeTask ??= Task.Run(
      () => ProcessAudioFramesAsync(_decodeCts.Token));
    _device.VideoFrameReceived += OnVideoFrameReceived;
    _device.AudioFrameReceived += OnAudioFrameReceived;
    _device.VideoStatusReceived += OnVideoStatusReceived;
    _device.ClipboardTextReceived += OnRemoteClipboardText;
    _device.ClipboardFilesReceived += OnRemoteClipboardFiles;
    _device.InputResultReceived += OnInputResultReceived;
    _device.PropertyChanged += OnDevicePropertyChanged;
    try
    {
      SetStatus("正在启动DXGI + H.264自适应视频流...");
      await _device.StartUdpDesktopSessionAsync(
        _sessionId,
        _controlEnabled,
        _lifetimeCts.Token);
      await _device.RequestAsync(
        MessageType.ScreenStreamStart,
        new DesktopSessionPayload(_sessionId.ToString("N")),
        8,
        _lifetimeCts.Token);
      if (_closing) return;
      _watchdogTimer.Start();
      _renderTimer.Start();
      _mouseTimer.Start();
      SetStatus("H.264实时视频流已启动；UDP优先，公网受限时自动切换TCP通道");
      UpdateImageLayout();
      Activate(); Surface.Focus(); Keyboard.Focus(Surface);
      if (_controlEnabled) QueueClipboardSync();
    }
    catch (OperationCanceledException) when (_closing) { }
    catch (Exception ex)
    {
      try
      {
        await _device.StopUdpDesktopSessionAsync(
          _sessionId,
          CancellationToken.None);
      }
      catch { }
      _device.ForgetScreenStreamSession(_sessionId.ToString("N"));
      SetStatus("启动远程桌面失败：" + ex.Message);
      DiagnosticText.Text = ex.Message;
    }
  }

  private void Window_Closing(object? sender, CancelEventArgs e) => BeginClose();

  private void Window_Closed(object? sender, EventArgs e)
  {
    BeginClose();
    _ = Task.Run(async () =>
    {
      using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
      try { await _device.SendControlAsync(BinaryControlProtocol.ReleaseAll(), cleanupTimeout.Token); } catch { }
      try
      {
        await _device.RequestAsync(
          MessageType.AudioStreamStop,
          new DesktopSessionPayload(_sessionId.ToString("N")),
          1,
          cleanupTimeout.Token);
      }
      catch { }
      try { await _device.StopUdpDesktopSessionAsync(_sessionId, cleanupTimeout.Token); } catch { }
      try
      {
        await _device.RequestAsync(
          MessageType.ScreenStreamStop,
          new DesktopSessionPayload(_sessionId.ToString("N")),
          1,
          cleanupTimeout.Token);
      }
      catch { }
      Task[] workers = new[] { _decodeTask, _audioDecodeTask }
        .Where(task => task is not null)
        .Cast<Task>()
        .ToArray();
      if (workers.Length > 0)
      {
        try
        {
          await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch { }
      }
      Task[] unfinished = workers.Where(task => !task.IsCompleted).ToArray();
      if (unfinished.Length > 0)
      {
        _ = Task.WhenAll(unfinished).ContinueWith(
          _ => DisposeDecoderResources(),
          CancellationToken.None,
          TaskContinuationOptions.ExecuteSynchronously,
          TaskScheduler.Default);
        return;
      }
      DisposeDecoderResources();
    });
  }

  private void DisposeDecoderResources()
  {
      PendingRenderedFrame? pending;
      lock (_renderGate)
      {
        pending = _pendingRenderedFrame;
        _pendingRenderedFrame = null;
      }
      if (pending is not null)
        ArrayPool<byte>.Shared.Return(pending.Pixels);
      try { _decodedImage?.Dispose(); } catch { }
      lock (_decoderGate)
      {
        try { _decoder?.Dispose(); } catch { }
        _decoder = null;
      }
      _decodeCts.Dispose();
      _lifetimeCts.Dispose();
      _decodeSignal.Dispose();
  }

  private void BeginClose()
  {
    if (Interlocked.Exchange(ref _closeStarted, 1) != 0) return;
    _closing = true;
    _lifetimeCts.Cancel();
    _device.ForgetScreenStreamSession(_sessionId.ToString("N"));
    _decodeCts.Cancel();
    _videoFrames.Reset();
    _watchdogTimer.Stop();
    _renderTimer.Stop();
    _mouseTimer.Stop();
    ClearPendingRenderedFrame();
    Mouse.Capture(null);
    _device.VideoFrameReceived -= OnVideoFrameReceived;
    _device.AudioFrameReceived -= OnAudioFrameReceived;
    _device.VideoStatusReceived -= OnVideoStatusReceived;
    _device.ClipboardTextReceived -= OnRemoteClipboardText;
    _device.ClipboardFilesReceived -= OnRemoteClipboardFiles;
    _device.InputResultReceived -= OnInputResultReceived;
    _device.PropertyChanged -= OnDevicePropertyChanged;
    StopLocalAudio();
    DisposeClipboardWatcher();
  }

  private async void Audio_Click(object sender, RoutedEventArgs e)
  {
    if (_closing) return;
    AudioButton.IsEnabled = false;
    try
    {
      if (_audioEnabled)
      {
        await _device.RequestAsync(
          MessageType.AudioStreamStop,
          new DesktopSessionPayload(_sessionId.ToString("N")),
          5,
          _lifetimeCts.Token);
        StopLocalAudio();
        AudioButton.Content = "获取音频";
      }
      else
      {
        await _device.RequestAsync(
          MessageType.AudioStreamStart,
          new DesktopSessionPayload(_sessionId.ToString("N")),
          5,
          _lifetimeCts.Token);
        _audioEnabled = true;
        AudioButton.Content = "停止音频";
      }
    }
    catch (OperationCanceledException) when (_closing) { }
    catch (Exception ex)
    {
      StopLocalAudio();
      AudioButton.Content = "获取音频";
      SetStatus("系统音频操作失败：" + ex.Message);
    }
    finally
    {
      if (!_closing) AudioButton.IsEnabled = true;
    }
  }

  private void OnAudioFrameReceived(RemoteAudioFrame frame)
  {
    if (_closing || !_audioEnabled || frame.SessionId != _sessionId)
      return;
    _audioFrames.Writer.TryWrite(frame);
  }

  private async Task ProcessAudioFramesAsync(CancellationToken token)
  {
    try
    {
      await foreach (RemoteAudioFrame frame in
        _audioFrames.Reader.ReadAllAsync(token))
      {
        if (_closing || !_audioEnabled || frame.SessionId != _sessionId)
          continue;
        DecodeAndPlayAudioFrame(frame);
      }
    }
    catch (OperationCanceledException) { }
  }

  private void DecodeAndPlayAudioFrame(RemoteAudioFrame frame)
  {
    lock (_audioGate)
    {
      if (_closing || !_audioEnabled) return;
      if (frame.Sequence <= _lastAudioSequence) return;
      _lastAudioSequence = frame.Sequence;
      long videoTimestamp = Interlocked.Read(ref _lastRenderedVideoTimestampTicks);
      if (videoTimestamp > 0 &&
          frame.TimestampTicks <
          videoTimestamp - TimeSpan.FromMilliseconds(250).Ticks)
        return;
      if (_audioDecoder is null)
      {
        OpusCodecFactory.AttemptToUseNativeLibrary = false;
        _audioDecoder = OpusCodecFactory.CreateDecoder(
          frame.SampleRate,
          frame.Channels);
        _audioBuffer = new BufferedWaveProvider(
          new WaveFormat(frame.SampleRate, 16, frame.Channels))
        {
          BufferDuration = TimeSpan.FromMilliseconds(250),
          DiscardOnBufferOverflow = true,
          ReadFully = true
        };
        _audioOutput = new WaveOutEvent
        {
          DesiredLatency = 60,
          NumberOfBuffers = 3
        };
        _audioOutput.Init(_audioBuffer);
        _audioOutput.Play();
      }
      if (_audioBuffer is null || _audioDecoder is null) return;
      if (_audioBuffer.BufferedDuration > TimeSpan.FromMilliseconds(180))
        _audioBuffer.ClearBuffer();
      short[] pcm = new short[frame.FrameSamples * frame.Channels];
      int samples = _audioDecoder.Decode(
        frame.OpusData.AsSpan(),
        pcm.AsSpan(),
        frame.FrameSamples,
        false);
      if (samples <= 0) return;
      int byteCount = samples * frame.Channels * sizeof(short);
      byte[] bytes = new byte[byteCount];
      Buffer.BlockCopy(pcm, 0, bytes, 0, byteCount);
      _audioBuffer.AddSamples(bytes, 0, byteCount);
    }
  }

  private void StopLocalAudio()
  {
    lock (_audioGate)
    {
      _audioEnabled = false;
      _lastAudioSequence = 0;
      while (_audioFrames.Reader.TryRead(out _)) { }
      try { _audioOutput?.Stop(); } catch { }
      _audioOutput?.Dispose();
      _audioOutput = null;
      _audioBuffer = null;
      _audioDecoder = null;
    }
  }

  private void OnDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
  {
    if (_closing || e.PropertyName != nameof(DeviceView.IsOnline) || _device.IsOnline)
      return;
    Dispatcher.BeginInvoke(() =>
    {
      if (!_closing) Close();
    }, DispatcherPriority.Send);
  }

  private async void WatchdogTimer_Tick(object? sender, EventArgs e)
  {
    if (_closing) return;
    long now = Environment.TickCount64;
    if (now - _lastClipboardPollTicks >= 250)
    {
      _lastClipboardPollTicks = now;
      uint clipboardSequence = GetClipboardSequenceNumber();
      if (clipboardSequence != 0 && clipboardSequence != _remoteClipboardSequence && clipboardSequence != _lastSentClipboardSequence)
        QueueClipboardSync();
      UpdateDiagnostics();
    }
    if (_videoFrames.AwaitingKeyFrame && now - _lastKeyFrameRequestTicks >= 150)
    {
      _lastKeyFrameRequestTicks = now;
      _requestKeyFrame = true;
    }
    if (Interlocked.Exchange(ref _feedbackSending, 1) != 0) return;
    try
    {
      bool hasFreshFrameMetrics = _lastFrameId != _lastFeedbackFrameId;
      var feedback = new VideoFeedbackPacket(
        _sessionId,
        Interlocked.Read(ref _lastReceivedFrameId),
        _lastFrameId,
        hasFreshFrameMetrics ? _decodeMilliseconds : 0,
        hasFreshFrameMetrics ? _renderMilliseconds : 0,
        _framesReceived,
        _decodeErrors,
        _requestKeyFrame);
      _lastFeedbackFrameId = _lastFrameId;
      _requestKeyFrame = false;
      await _device.SendControlAsync(BinaryControlProtocol.VideoFeedback(feedback));
      if (now - _lastInputPingTicks >= 1000)
      {
        _lastInputPingTicks = now;
        await _device.SendControlAsync(BinaryControlProtocol.Ping(DateTime.UtcNow.Ticks));
      }
    }
    catch (Exception ex)
    {
      SetStatus("桌面通道正在自动恢复：" + ex.Message);
    }
    finally { Interlocked.Exchange(ref _feedbackSending, 0); }
    if (!_device.IsVideoConnected)
    {
      _requestKeyFrame = true;
      SetStatus("H.264视频通道正在自动恢复…");
    }
  }

  private async void MouseTimer_Tick(object? sender, EventArgs e)
  {
    if (_closing || !_mouseDirty || _moveSending) return;
    _mouseDirty = false; _moveSending = true;
    try { await _device.SendControlAsync(BinaryControlProtocol.MouseMove(_lastMouseX, _lastMouseY)); }
    catch (Exception ex) { SetStatus("鼠标输入发送失败，等待自动重连：" + ex.Message); }
    finally { _moveSending = false; }
  }

  private void OnVideoFrameReceived(RemoteVideoFrame frame)
  {
    if (_closing ||
        frame.SessionId != Guid.Empty && frame.SessionId != _sessionId)
      return;
    long observed;
    do
    {
      observed = Interlocked.Read(ref _lastReceivedFrameId);
      if (frame.FrameId <= observed) break;
    }
    while (Interlocked.CompareExchange(
             ref _lastReceivedFrameId,
             frame.FrameId,
             observed) != observed);
    if (!_videoFrames.Enqueue(frame)) return;
    if (_decodeSignal.CurrentCount == 0)
    {
      try { _decodeSignal.Release(); }
      catch (SemaphoreFullException) { }
    }
  }

  private void OnInputResultReceived(InputResultPacket result)
  {
    if (_closing) return;
    if (result.Success)
    {
      Interlocked.Exchange(ref _consecutiveInputFailures, 0);
      return;
    }
    int failures = Interlocked.Increment(ref _consecutiveInputFailures);
    Dispatcher.BeginInvoke(() =>
    {
      if (result.Win32Error == 10060)
      {
        SetStatus("远程输入确认暂时超时，正在自动恢复，不会关闭控制。");
        return;
      }
      if (failures >= 3) ControlBox.IsChecked = false;
      SetStatus($"远程输入执行失败（{result.SourceType}，错误 {result.Win32Error}，连续 {failures} 次）。");
    });
  }

  private void OnVideoStatusReceived(string message)
  {
    if (_closing) return;
    _lastVideoError = message;
    Dispatcher.BeginInvoke(() =>
    {
      DiagnosticText.Text = message;
      SetStatus(message);
    });
  }

  private async Task DecodeLatestFramesAsync()
  {
    using MultimediaThreadScope multimediaPriority = MultimediaThreadScope.Enter("Playback");
    try
    {
      while (!_decodeCts.IsCancellationRequested && !_closing)
      {
        await _decodeSignal.WaitAsync(_decodeCts.Token);
        while (!_decodeCts.IsCancellationRequested && !_closing)
        {
          if (!_videoFrames.TryTake(
                Environment.TickCount64,
                out RemoteVideoFrame? frame,
                out bool resetDecoder,
                out bool recoveryRequested))
          {
            if (recoveryRequested)
              RegisterContinuityRecovery();
            if (_videoFrames.Count == 0 || _videoFrames.AwaitingKeyFrame) break;
            await Task.Delay(5, _decodeCts.Token);
            continue;
          }
          if (recoveryRequested)
          {
            _decodeErrors++;
            // If TryTake already returned a replacement IDR, that frame is
            // the requested recovery point.  Reset the decoder but do not
            // request another large IDR immediately after it.
            if (!resetDecoder) _requestKeyFrame = true;
            ResetDecoder();
          }
          else if (resetDecoder) ResetDecoder();
          if (frame is null) continue;
          await DecodeAndRenderFrameAsync(frame, _decodeCts.Token);
        }
      }
    }
    catch (OperationCanceledException) { }
  }

  private async Task DecodeAndRenderFrameAsync(
    RemoteVideoFrame frame,
    CancellationToken token)
  {
    if (frame.Codec != VideoCodec.H264)
    {
      EnterVideoRecovery(clearQueuedFrames: true, countError: true);
      return;
    }
    try
    {
      H264Decoder decoder;
      lock (_decoderGate)
      {
        if (_decoder is null)
        {
          long now = Environment.TickCount64;
          if (now < _nextDecoderRetryTicks) return;
          _nextDecoderRetryTicks = now + 2000;
          var candidate = new H264Decoder(
            H264NativeResolver.Resolve(typeof(H264Decoder).Assembly));
          int initializeResult = candidate.Initialize();
          if (initializeResult != 0)
          {
            candidate.Dispose();
            throw new InvalidOperationException(
              $"A端H.264解码器初始化失败：{initializeResult}");
          }
          _decoder = candidate;
        }
        decoder = _decoder;
      }
      if (_decodedImage is null || _decodedImage.Width != frame.Width || _decodedImage.Height != frame.Height)
      {
        _decodedImage?.Dispose();
        _decodedImage = new RgbImage(H264Sharp.ImageFormat.Bgra, frame.Width, frame.Height);
        _videoBitmap = null;
      }

      var decodeWatch = Stopwatch.StartNew();
      bool decoded = decoder.Decode(frame.Data, 0, frame.Data.Length, true, out var state, out YUVImagePointer yuv);
      if (!decoded)
      {
        decodeWatch.Stop();
        _decodeMilliseconds = (int)decodeWatch.ElapsedMilliseconds;
        if (state != DecodingState.dsErrorFree && state != DecodingState.dsFramePending)
          EnterVideoRecovery(clearQueuedFrames: true, countError: true);
        return;
      }

      // H.264 reference frames must still be decoded in order, but obsolete
      // frames do not need an expensive colorspace conversion or UI upload.
      if (_videoFrames.Count > 0)
      {
        decodeWatch.Stop();
        _decodeMilliseconds = (int)decodeWatch.ElapsedMilliseconds;
        return;
      }

      unsafe
      {
        int converted = LibYuv.J420ToARGB((byte*)yuv.Y, yuv.StrideY, (byte*)yuv.U, yuv.StrideUV, (byte*)yuv.V, yuv.StrideUV,
          (byte*)_decodedImage.NativeBytes, _decodedImage.Stride, frame.Width, frame.Height);
        if (converted != 0) throw new InvalidOperationException($"libyuv 全范围颜色还原失败：{converted}");
      }
      decodeWatch.Stop();
      _decodeMilliseconds = (int)decodeWatch.ElapsedMilliseconds;
      if (_videoFrames.Count > 0) return;
      QueueDecodedFrame(frame, _decodedImage);
    }
    catch (Exception ex)
    {
      if (_closing || token.IsCancellationRequested) return;
      EnterVideoRecovery(clearQueuedFrames: true, countError: true);
      _lastVideoError = ex.Message;
      await Dispatcher.InvokeAsync(() =>
      {
        DiagnosticText.Text = ex.Message;
        SetStatus(ex.Message);
      });
    }
  }

  private void RegisterContinuityRecovery()
  {
    _decodeErrors++;
    _requestKeyFrame = true;
    ResetDecoder();
  }

  private void EnterVideoRecovery(bool clearQueuedFrames, bool countError)
  {
    if (countError) _decodeErrors++;
    _requestKeyFrame = true;
    _videoFrames.EnterRecovery(keepNewestKeyFrame: !clearQueuedFrames);
    ResetDecoder();
    if (_decodeSignal.CurrentCount == 0)
    {
      try { _decodeSignal.Release(); }
      catch (SemaphoreFullException) { }
    }
  }

  private void ResetDecoder()
  {
    lock (_decoderGate)
    {
      try { _decoder?.Dispose(); } catch { }
      _decoder = null;
      _nextDecoderRetryTicks = 0;
    }
  }

  private void QueueDecodedFrame(RemoteVideoFrame frame, RgbImage decoded)
  {
    if (_closing) return;
    int stride = Math.Abs(decoded.Stride);
    int length = checked(stride * frame.Height);
    byte[] pixels = ArrayPool<byte>.Shared.Rent(length);
    Marshal.Copy(decoded.NativeBytes, pixels, 0, length);
    var next = new PendingRenderedFrame(frame, pixels, length, stride);
    PendingRenderedFrame? replaced;
    lock (_renderGate)
    {
      replaced = _pendingRenderedFrame;
      _pendingRenderedFrame = next;
    }
    if (replaced is not null)
      ArrayPool<byte>.Shared.Return(replaced.Pixels);
  }

  private void RenderTimer_Tick(object? sender, EventArgs e)
  {
    if (_closing || _layoutTransition) return;
    RenderLatestDecodedFrame();
  }

  private void RenderLatestDecodedFrame()
  {
    PendingRenderedFrame? pending;
    lock (_renderGate)
    {
      pending = _pendingRenderedFrame;
      _pendingRenderedFrame = null;
    }
    try
    {
      if (!_closing && pending is not null)
      {
        var watch = Stopwatch.StartNew();
        RenderDecodedFrame(
          pending.Frame,
          pending.Pixels,
          pending.BufferLength,
          pending.Stride);
        watch.Stop();
        _renderMilliseconds = (int)watch.ElapsedMilliseconds;
        _lastFrameId = pending.Frame.FrameId;
        Interlocked.Exchange(
          ref _lastRenderedVideoTimestampTicks,
          pending.Frame.TimestampTicks);
        _lastBytes = pending.Frame.Data.Length;
        _framesReceived++;
        _lastFrameAt = DateTime.Now;
      }
    }
    finally
    {
      if (pending is not null)
        ArrayPool<byte>.Shared.Return(pending.Pixels);
    }
  }

  private void ClearPendingRenderedFrame()
  {
    PendingRenderedFrame? pending;
    lock (_renderGate)
    {
      pending = _pendingRenderedFrame;
      _pendingRenderedFrame = null;
    }
    if (pending is not null)
      ArrayPool<byte>.Shared.Return(pending.Pixels);
  }

  private void RenderDecodedFrame(
    RemoteVideoFrame frame,
    byte[] pixels,
    int bufferLength,
    int stride)
  {
    bool bitmapCreated = false;
    if (_videoBitmap is null || _videoBitmap.PixelWidth != frame.Width || _videoBitmap.PixelHeight != frame.Height)
    {
      _videoBitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
      bitmapCreated = true;
    }
    _videoBitmap.WritePixels(
      new Int32Rect(0, 0, frame.Width, frame.Height),
      pixels,
      stride,
      0);
    if (bitmapCreated)
    {
      RemoteImage.Source = _videoBitmap;
      NoFrameOverlay.Visibility = Visibility.Collapsed;
    }
    if (UpdateFrameGeometry(frame.Width, frame.Height, frame.SourceWidth, frame.SourceHeight, frame.SourceX, frame.SourceY))
      UpdateImageLayout();
  }

  private bool UpdateFrameGeometry(int displayWidth, int displayHeight, int sourceWidth, int sourceHeight, int sourceX, int sourceY)
  {
    int normalizedSourceWidth = sourceWidth > 0 ? sourceWidth : displayWidth;
    int normalizedSourceHeight = sourceHeight > 0 ? sourceHeight : displayHeight;
    // Encoded dimensions are allowed to change as QoS adapts.  They must not
    // be treated as a desktop layout change or Original/Fit will visibly jump
    // every time the encoder changes profile.
    bool layoutChanged =
      _remoteWidth != normalizedSourceWidth ||
      _remoteHeight != normalizedSourceHeight ||
      _remoteX != sourceX ||
      _remoteY != sourceY;
    _displayWidth = displayWidth; _displayHeight = displayHeight;
    _remoteWidth = normalizedSourceWidth;
    _remoteHeight = normalizedSourceHeight;
    _remoteX = sourceX; _remoteY = sourceY;
    return layoutChanged;
  }

  private async void RefreshFrame_Click(object sender, RoutedEventArgs e)
  {
    _requestKeyFrame = true;
    try
    {
      await _device.SendControlAsync(BinaryControlProtocol.VideoFeedback(
        new(_sessionId, Interlocked.Read(ref _lastReceivedFrameId), _lastFrameId, _decodeMilliseconds, _renderMilliseconds, _framesReceived, _decodeErrors, true)));
      SetStatus("已请求 H.264 关键帧；视频通道异常时会自动恢复。");
    }
    catch (Exception ex) { SetStatus("请求关键帧失败：" + ex.Message); }
  }

  private void UpdateDiagnostics()
  {
    double age = _lastFrameAt == DateTime.MinValue ? double.PositiveInfinity : (DateTime.Now - _lastFrameAt).TotalMilliseconds;
    string last = _lastFrameAt == DateTime.MinValue
      ? "等待首帧"
      : age > 2000 && _device.IsVideoConnected
        ? "桌面静止"
        : $"{age:F0}ms前";
    string text = $"H.264 · 最后帧:{last} · 视频:{_displayWidth}x{_displayHeight} · 桌面:{_remoteWidth}x{_remoteHeight} · 帧:{_framesReceived} · {_lastBytes / 1024}KB · 解码:{_decodeMilliseconds}ms · 渲染:{_renderMilliseconds}ms · 错误:{_decodeErrors}";
    if (_framesReceived == 0 && _lastVideoError.Length > 0)
      text += " · " + _lastVideoError;
    DiagnosticText.Text = text; SetStatus(text);
  }

  private void SetStatus(string text)
  {
    if (!Dispatcher.CheckAccess())
    {
      Dispatcher.BeginInvoke(() => StatusText.Content = text);
      return;
    }
    StatusText.Content = text;
  }
  private void Fit_Click(object sender, RoutedEventArgs e) => ChangeDisplayMode(true);
  private void Original_Click(object sender, RoutedEventArgs e) => ChangeDisplayMode(false);
  private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();
  private void Close_Click(object sender, RoutedEventArgs e) => Close();

  private void ChangeDisplayMode(bool fit)
  {
    if (_layoutTransition) return;
    _layoutTransition = true;
    try
    {
      Mouse.Capture(null);
      _ = ReleaseAllInputAsync();
      _fit = fit;
      UpdateImageLayout();
      Surface.Focus();
      Keyboard.Focus(Surface);
    }
    finally { _layoutTransition = false; }
  }

  private void ToggleFullscreen()
  {
    if (_layoutTransition) return;
    _layoutTransition = true;
    try
    {
      Mouse.Capture(null);
      _ = ReleaseAllInputAsync();
      if (!_fullscreen)
      {
        _previousState = WindowState;
        _previousStyle = WindowStyle;
        _previousResizeMode = ResizeMode;
        _previousBounds = WindowState == WindowState.Normal
          ? new Rect(Left, Top, ActualWidth, ActualHeight)
          : RestoreBounds;
        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;
        FullButton.Content = "退出全屏";
        _fullscreen = true;
      }
      else
      {
        WindowState = WindowState.Normal;
        WindowStyle = _previousStyle;
        ResizeMode = _previousResizeMode;
        if (_previousBounds.Width > 0 && _previousBounds.Height > 0)
        {
          Left = _previousBounds.Left;
          Top = _previousBounds.Top;
          Width = _previousBounds.Width;
          Height = _previousBounds.Height;
        }
        WindowState = _previousState == WindowState.Minimized ? WindowState.Normal : _previousState;
        FullButton.Content = "全屏";
        _fullscreen = false;
      }
      UpdateImageLayout();
      Surface.Focus();
      Keyboard.Focus(Surface);
    }
    finally { _layoutTransition = false; }
  }

  private void Surface_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateImageLayout();

  private void UpdateImageLayout()
  {
    if (Surface.ActualWidth <= 1 || Surface.ActualHeight <= 1) return;
    if (_fit)
    {
      DesktopScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
      DesktopScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
      ImageHost.Width = Math.Max(1, Surface.ActualWidth);
      ImageHost.Height = Math.Max(1, Surface.ActualHeight);
      RemoteImage.Stretch = Stretch.Uniform;
    }
    else
    {
      // "Original" means the stable remote desktop geometry, not the current
      // adaptive H.264 frame size.  This keeps the viewport fixed while video
      // quality changes in the background.
      int width = _remoteWidth > 0 ? _remoteWidth : (RemoteImage.Source as BitmapSource)?.PixelWidth ?? 1;
      int height = _remoteHeight > 0 ? _remoteHeight : (RemoteImage.Source as BitmapSource)?.PixelHeight ?? 1;
      DpiScale dpi = VisualTreeHelper.GetDpi(Surface);
      ImageHost.Width = Math.Max(1, width / Math.Max(0.01, dpi.DpiScaleX));
      ImageHost.Height = Math.Max(1, height / Math.Max(0.01, dpi.DpiScaleY));
      DesktopScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
      DesktopScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
      RemoteImage.Stretch = Stretch.Fill;
    }
  }

  private bool TryGetRenderedImageRect(out Rect rect)
  {
    rect = Rect.Empty;
    if (RemoteImage.Source is not BitmapSource bmp || RemoteImage.ActualWidth <= 0 || RemoteImage.ActualHeight <= 0)
      return false;
    int displayWidth = _remoteWidth > 0 ? _remoteWidth : bmp.PixelWidth;
    int displayHeight = _remoteHeight > 0 ? _remoteHeight : bmp.PixelHeight;
    Point origin = RemoteImage.TranslatePoint(new Point(0, 0), Surface);
    double elementWidth = RemoteImage.ActualWidth;
    double elementHeight = RemoteImage.ActualHeight;
    if (_fit)
    {
      double scale = Math.Min(elementWidth / displayWidth, elementHeight / displayHeight);
      double width = displayWidth * scale;
      double height = displayHeight * scale;
      rect = new Rect(
        origin.X + (elementWidth - width) / 2,
        origin.Y + (elementHeight - height) / 2,
        width, height);
    }
    else rect = new Rect(origin.X, origin.Y, elementWidth, elementHeight);
    return rect.Width > 0 && rect.Height > 0;
  }

  private bool TryMapPoint(MouseEventArgs e, out int x, out int y)
  {
    x = y = 0;
    if (_layoutTransition || RemoteImage.Source is not BitmapSource bmp || !TryGetRenderedImageRect(out Rect rendered))
      return false;
    int displayWidth = _remoteWidth > 0 ? _remoteWidth : bmp.PixelWidth;
    int displayHeight = _remoteHeight > 0 ? _remoteHeight : bmp.PixelHeight;
    int remoteWidth = _remoteWidth > 0 ? _remoteWidth : displayWidth;
    int remoteHeight = _remoteHeight > 0 ? _remoteHeight : displayHeight;
    Point point = e.GetPosition(Surface);
    if (!rendered.Contains(point)) return false;
    double rx = (point.X - rendered.X) / rendered.Width;
    double ry = (point.Y - rendered.Y) / rendered.Height;
    x = _remoteX + (int)Math.Clamp(Math.Floor(rx * remoteWidth), 0, remoteWidth - 1);
    y = _remoteY + (int)Math.Clamp(Math.Floor(ry * remoteHeight), 0, remoteHeight - 1);
    return true;
  }

  private bool CanControl(MouseEventArgs e, out int x, out int y)
  {
    x = y = 0;
    return !_layoutTransition && ControlBox.IsChecked == true && _device.IsInputConnected && TryMapPoint(e, out x, out y);
  }
  private void RemoteSurface_MouseMove(object sender, MouseEventArgs e)
  {
    if (!CanControl(e, out int x, out int y)) return;
    _lastMouseX = x; _lastMouseY = y; _mouseDirty = true; e.Handled = true;
  }
  private async void RemoteSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
  {
    if (!CanControl(e, out int x, out int y)) return;
    _lastMouseX = x; _lastMouseY = y; _leftDown = true; Surface.Focus(); Mouse.Capture(Surface);
    try { await _device.SendControlAsync(BinaryControlProtocol.MouseButton(x, y, 1, true, (byte)Math.Clamp(e.ClickCount, 1, 2))); }
    catch (Exception ex) { SetStatus("鼠标输入失败：" + ex.Message); }
    e.Handled = true;
  }
  private async void RemoteSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
  {
    if (!_leftDown) return;
    if (TryMapPoint(e, out int x, out int y)) { _lastMouseX = x; _lastMouseY = y; }
    _leftDown = false; if (!_rightDown) Mouse.Capture(null);
    try { await _device.SendControlAsync(BinaryControlProtocol.MouseButton(_lastMouseX, _lastMouseY, 1, false, 1)); }
    catch (Exception ex) { SetStatus("鼠标输入失败：" + ex.Message); }
    e.Handled = true;
  }
  private async void RemoteSurface_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
  {
    if (!CanControl(e, out int x, out int y)) return;
    _lastMouseX = x; _lastMouseY = y; _rightDown = true; Surface.Focus(); Mouse.Capture(Surface);
    try { await _device.SendControlAsync(BinaryControlProtocol.MouseButton(x, y, 2, true, 1)); }
    catch (Exception ex) { SetStatus("鼠标输入失败：" + ex.Message); }
    e.Handled = true;
  }
  private async void RemoteSurface_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
  {
    if (!_rightDown) return;
    if (TryMapPoint(e, out int x, out int y)) { _lastMouseX = x; _lastMouseY = y; }
    _rightDown = false; if (!_leftDown) Mouse.Capture(null);
    try { await _device.SendControlAsync(BinaryControlProtocol.MouseButton(_lastMouseX, _lastMouseY, 2, false, 1)); }
    catch (Exception ex) { SetStatus("鼠标输入失败：" + ex.Message); }
    e.Handled = true;
  }
  private async void RemoteSurface_MouseWheel(object sender, MouseWheelEventArgs e)
  {
    if (!CanControl(e, out int x, out int y)) return;
    _lastMouseX = x; _lastMouseY = y;
    try { await _device.SendControlAsync(BinaryControlProtocol.MouseWheel(x, y, e.Delta)); }
    catch (Exception ex) { SetStatus("鼠标输入失败：" + ex.Message); }
    e.Handled = true;
  }
  private async void Surface_LostMouseCapture(object sender, MouseEventArgs e) => await ReleaseMouseButtonsAsync();
  private async void Window_Deactivated(object? sender, EventArgs e) => await ReleaseAllInputAsync();
  private async Task ReleaseMouseButtonsAsync(CancellationToken token = default)
  {
    try
    {
      if (_leftDown) await _device.SendControlAsync(BinaryControlProtocol.MouseButton(_lastMouseX, _lastMouseY, 1, false, 1), token);
      if (_rightDown) await _device.SendControlAsync(BinaryControlProtocol.MouseButton(_lastMouseX, _lastMouseY, 2, false, 1), token);
    }
    catch { }
    _leftDown = _rightDown = false;
  }
  private async Task ReleaseAllInputAsync()
  {
    using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
    await ReleaseMouseButtonsAsync(timeout.Token);
    try { await _device.SendControlAsync(BinaryControlProtocol.ReleaseAll(), timeout.Token); } catch { }
  }

  private async void Window_KeyDown(object sender, KeyEventArgs e)
  {
    Key key = e.Key == Key.System ? e.SystemKey : e.Key;
    if (key == Key.F4 && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
    {
      e.Handled = true;
      Close();
      return;
    }
    if (key == Key.Escape && _fullscreen)
    {
      e.Handled = true;
      _suppressNextEscapeKeyUp = true;
      ToggleFullscreen();
      return;
    }
    if (_layoutTransition || ControlBox.IsChecked != true || !_device.IsInputConnected || e.IsRepeat) return;
    ushort vk = (ushort)KeyInterop.VirtualKeyFromKey(key), scan = (ushort)MapVirtualKey(vk, 0);
    try { await _device.SendControlAsync(BinaryControlProtocol.Key(vk, scan, true, IsExtendedKey(key))); }
    catch (Exception ex) { SetStatus("键盘输入失败：" + ex.Message); }
    e.Handled = true;
  }
  private async void Window_KeyUp(object sender, KeyEventArgs e)
  {
    Key key = e.Key == Key.System ? e.SystemKey : e.Key;
    if (key == Key.Escape && _suppressNextEscapeKeyUp)
    {
      _suppressNextEscapeKeyUp = false;
      e.Handled = true;
      return;
    }
    if (_layoutTransition || ControlBox.IsChecked != true || !_device.IsInputConnected) return;
    ushort vk = (ushort)KeyInterop.VirtualKeyFromKey(key), scan = (ushort)MapVirtualKey(vk, 0);
    try { await _device.SendControlAsync(BinaryControlProtocol.Key(vk, scan, false, IsExtendedKey(key))); }
    catch (Exception ex) { SetStatus("键盘输入失败：" + ex.Message); }
    e.Handled = true;
  }
  private static bool IsExtendedKey(Key key) => key is Key.RightAlt or Key.RightCtrl or Key.Insert or Key.Delete or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Left or Key.Right or Key.Up or Key.Down or Key.NumLock or Key.PrintScreen or Key.Divide;

  private void InitializeClipboardWatcher()
  {
    _clipboardWindowSource = PresentationSource.FromVisual(this) as HwndSource;
    if (_clipboardWindowSource is null) return;
    _clipboardWindowHandle = _clipboardWindowSource.Handle;
    _clipboardWindowSource.AddHook(ClipboardWindowProc);
    AddClipboardFormatListener(_clipboardWindowHandle);
  }

  private void DisposeClipboardWatcher()
  {
    try
    {
      if (_clipboardWindowHandle != IntPtr.Zero) RemoveClipboardFormatListener(_clipboardWindowHandle);
      _clipboardWindowSource?.RemoveHook(ClipboardWindowProc);
    }
    catch { }
    _clipboardWindowSource = null;
    _clipboardWindowHandle = IntPtr.Zero;
  }

  private IntPtr ClipboardWindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
  {
    if (message == WmClipboardUpdate) QueueClipboardSync();
    return IntPtr.Zero;
  }

  private void QueueClipboardSync()
  {
    _ = Task.Run(SyncLocalClipboardAsync);
  }

  private async Task SyncLocalClipboardAsync()
  {
    if (_closing || !_controlEnabled || _settingRemoteClipboard || Interlocked.Exchange(ref _clipboardSyncing, 1) != 0) return;
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
        SetStatus("正在同步文件剪贴板…");
        await _device.SendClipboardFilesAsync(paths, CancellationToken.None, (sent, total) =>
        {
          if (total <= 0) return;
          Dispatcher.BeginInvoke(() => SetStatus($"正在同步文件剪贴板 {sent * 100d / total:F0}%"));
        });
        SetStatus("文件剪贴板已同步到被控端，可直接粘贴。");
      }
      else if (snapshot.Text is not null)
      {
        string text = snapshot.Text;
        if (System.Text.Encoding.UTF8.GetByteCount(text) > 8 * 1024 * 1024) return;
        _lastClipboardText = text;
        _lastSentClipboardSequence = sequence;
        await _device.SendClipboardTextAsync(text);
      }
      else
      {
        _lastSentClipboardSequence = sequence;
      }
    }
    catch (Exception ex) { SetStatus("剪贴板同步失败：" + ex.Message); }
    finally
    {
      Interlocked.Exchange(ref _clipboardSyncing, 0);
    }
  }

  private void OnRemoteClipboardText(string text)
  {
    if (!_controlEnabled) return;
    _ = Task.Run(async () =>
    {
      _settingRemoteClipboard = true;
      try
      {
        if (await SetClipboardTextWithRetryAsync(text))
        {
          _lastClipboardText = text;
          _remoteClipboardSequence = GetClipboardSequenceNumber();
        }
      }
      finally { _settingRemoteClipboard = false; }
    });
  }

  private void OnRemoteClipboardFiles(IReadOnlyList<string> paths)
  {
    if (!_controlEnabled) return;
    _ = Task.Run(async () =>
    {
      _settingRemoteClipboard = true;
      try
      {
        if (await SetClipboardFilesWithRetryAsync(paths))
        {
          _remoteClipboardSequence = GetClipboardSequenceNumber();
          SetStatus("已接收被控端文件剪贴板，可在主控端直接粘贴。");
        }
        else SetStatus("应用远程文件剪贴板失败：系统剪贴板正被其他程序占用。");
      }
      catch (Exception ex) { SetStatus("应用远程文件剪贴板失败：" + ex.Message); }
      finally { _settingRemoteClipboard = false; }
    });
  }

  private Task<ClipboardSnapshot?> ReadClipboardWithRetryAsync()
  {
    return Task.Run<ClipboardSnapshot?>(() =>
    {
      for (int attempt = 0; attempt < 8; attempt++)
      {
        if (!OpenClipboard(IntPtr.Zero))
        {
          Thread.Sleep(20 * (attempt + 1));
          continue;
        }
        try
        {
          uint sequence = GetClipboardSequenceNumber();
          if (IsClipboardFormatAvailable(CfHDrop))
            return new ClipboardSnapshot(sequence, ReadFileDropList(), null);
          if (IsClipboardFormatAvailable(CfUnicodeText))
            return new ClipboardSnapshot(sequence, null, ReadUnicodeText());
          return new ClipboardSnapshot(sequence, null, null);
        }
        finally { CloseClipboard(); }
      }
      return null;
    });
  }

  private static Task<bool> SetClipboardTextWithRetryAsync(string text) =>
    Task.Run(() => SetClipboardWithRetry(CfUnicodeText, BuildUnicodeText(text)));

  private static Task<bool> SetClipboardFilesWithRetryAsync(IReadOnlyList<string> paths) =>
    Task.Run(() => SetClipboardWithRetry(CfHDrop, BuildFileDropList(paths)));

  private static bool SetClipboardWithRetry(uint format, byte[] payload)
  {
    for (int attempt = 0; attempt < 8; attempt++)
    {
      if (!OpenClipboard(IntPtr.Zero))
      {
        Thread.Sleep(20 * (attempt + 1));
        continue;
      }
      IntPtr memory = IntPtr.Zero;
      try
      {
        if (!EmptyClipboard()) continue;
        memory = GlobalAlloc(GmemMoveableZeroInit, (nuint)payload.Length);
        if (memory == IntPtr.Zero) continue;
        IntPtr pointer = GlobalLock(memory);
        if (pointer == IntPtr.Zero) continue;
        try { Marshal.Copy(payload, 0, pointer, payload.Length); }
        finally { GlobalUnlock(memory); }
        if (SetClipboardData(format, memory) != IntPtr.Zero)
        {
          memory = IntPtr.Zero;
          return true;
        }
      }
      finally
      {
        if (memory != IntPtr.Zero) GlobalFree(memory);
        CloseClipboard();
      }
      Thread.Sleep(20 * (attempt + 1));
    }
    return false;
  }

  private static string[] ReadFileDropList()
  {
    IntPtr drop = GetClipboardData(CfHDrop);
    if (drop == IntPtr.Zero) return [];
    uint count = DragQueryFile(drop, uint.MaxValue, null, 0);
    var paths = new List<string>((int)Math.Min(count, int.MaxValue));
    for (uint index = 0; index < count; index++)
    {
      uint length = DragQueryFile(drop, index, null, 0);
      var path = new StringBuilder((int)length + 1);
      if (DragQueryFile(drop, index, path, (uint)path.Capacity) > 0)
        paths.Add(path.ToString());
    }
    return paths.Where(x => File.Exists(x) || Directory.Exists(x)).ToArray();
  }

  private static string? ReadUnicodeText()
  {
    IntPtr handle = GetClipboardData(CfUnicodeText);
    if (handle == IntPtr.Zero) return null;
    IntPtr pointer = GlobalLock(handle);
    if (pointer == IntPtr.Zero) return null;
    try { return Marshal.PtrToStringUni(pointer); }
    finally { GlobalUnlock(handle); }
  }

  private static byte[] BuildUnicodeText(string text) =>
    Encoding.Unicode.GetBytes(text + '\0');

  private static byte[] BuildFileDropList(IReadOnlyList<string> paths)
  {
    string joined = string.Join('\0', paths) + "\0\0";
    byte[] names = Encoding.Unicode.GetBytes(joined);
    byte[] payload = new byte[20 + names.Length];
    BitConverter.GetBytes(20).CopyTo(payload, 0);
    BitConverter.GetBytes(1).CopyTo(payload, 16);
    names.CopyTo(payload, 20);
    return payload;
  }

  private sealed record ClipboardSnapshot(uint Sequence, string[]? Paths, string? Text);

  private static bool IsClipboardCachePath(string path)
  {
    string root = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AuthorizedDeviceControl", "Clipboard")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    try { return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase); }
    catch { return false; }
  }
}
