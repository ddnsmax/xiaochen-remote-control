using RemoteControl.Shared;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;

namespace RemoteAgent;
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
