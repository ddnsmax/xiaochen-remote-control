using RemoteControl.Shared;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Windows.Threading;

namespace ControlCenter;

public partial class MainWindow
{
  private readonly ConcurrentQueue<(DeviceView Device, TerminalPacket Packet)> _terminalUiQueue = new();
  private readonly ConcurrentDictionary<string, TerminalCommandState> _activeTerminalCommands = new();
  private long _terminalCommandOrder;
  private DispatcherTimer? _terminalUiTimer;
  private DispatcherTimer? _registryRefreshDebounce;
  private RegistryChangedPayload? _pendingRegistryChange;

  private void InitializeDedicatedUi()
  {
    _terminalUiTimer = new DispatcherTimer(DispatcherPriority.Render)
    {
      Interval = TimeSpan.FromMilliseconds(15)
    };
    _terminalUiTimer.Tick += (_, _) => DrainTerminalOutput();
    _terminalUiTimer.Start();

    _registryRefreshDebounce = new DispatcherTimer(DispatcherPriority.Background)
    {
      Interval = TimeSpan.FromMilliseconds(250)
    };
    _registryRefreshDebounce.Tick += async (_, _) =>
    {
      _registryRefreshDebounce.Stop();
      RegistryChangedPayload? change = _pendingRegistryChange;
      _pendingRegistryChange = null;
      if (change is null || MainTabs.SelectedIndex != 6) return;
      string path = BuildRegistryPath(change.Hive, change.SubKey);
      if (!string.Equals(
            NormalizeRegistryPath(RegistryPathBox.Text),
            NormalizeRegistryPath(path),
            StringComparison.OrdinalIgnoreCase)) return;
      try { await NavigateRegistryPathAsync(path, subscribe: false); }
      catch (Exception ex) { SetStatus("注册表自动刷新失败：" + ex.Message); }
    };
  }

  private void DisposeDedicatedUi()
  {
    _terminalUiTimer?.Stop();
    _registryRefreshDebounce?.Stop();
    _activeTerminalCommands.Clear();
  }

  private async Task<DeviceView?> WaitForDeviceAsync(
    string? deviceId,
    Guid instanceId,
    CancellationToken token)
  {
    if (string.IsNullOrWhiteSpace(deviceId) || instanceId == Guid.Empty)
      return null;
    for (int index = 0; index < 80 && !token.IsCancellationRequested; index++)
    {
      if (_devicesById.TryGetValue(deviceId, out DeviceView? device) &&
          device.IsOnline &&
          device.AcceptsInstance(instanceId))
        return device;
      await Task.Delay(125, token);
    }
    return null;
  }

  private void SubscribeDedicatedDeviceEvents(DeviceView device)
  {
    device.TerminalPacketReceived += packet =>
      _terminalUiQueue.Enqueue((device, packet));
    device.TerminalGenerationEnded += generation =>
      Dispatcher.BeginInvoke(() => EndTerminalGeneration(device, generation));
    device.RegistryChanged += change =>
      Dispatcher.BeginInvoke(() => OnRegistryChanged(device, change));
  }

  private void RegisterTerminalCommand(string commandId, DeviceView device)
  {
    _activeTerminalCommands[commandId] = new TerminalCommandState(
      device,
      device.TerminalGeneration,
      Interlocked.Increment(ref _terminalCommandOrder));
    UpdateTerminalStopButton();
  }

  private void EndTerminalGeneration(DeviceView device, long generation)
  {
    int removed = 0;
    foreach (var pair in _activeTerminalCommands)
    {
      if (ReferenceEquals(pair.Value.Device, device) &&
          pair.Value.Generation == generation &&
          _activeTerminalCommands.TryRemove(pair.Key, out _))
        removed++;
    }
    if (removed > 0 &&
        DeviceList.SelectedItem is DeviceView selected &&
        ReferenceEquals(selected, device))
      AppendTerminalText($"\r\n[终端通道已断开，{removed} 个运行任务已终止]\r\n");
    UpdateTerminalStopButton();
  }

  private void UpdateTerminalStopButton()
  {
    if (TerminalStopButton is null) return;
    if (DeviceList.SelectedItem is not DeviceView device)
    {
      TerminalStopButton.IsEnabled = false;
      return;
    }
    TerminalStopButton.IsEnabled = _activeTerminalCommands.Values.Any(
      state => ReferenceEquals(state.Device, device));
  }

  private void DrainTerminalOutput()
  {
    if (_terminalUiQueue.IsEmpty) return;
    var builder = new System.Text.StringBuilder();
    int drained = 0;
    while (drained < 512 &&
           _terminalUiQueue.TryDequeue(out var item))
    {
      drained++;
      TerminalPacket packet = item.Packet;
      if (packet.Type is TerminalPacketType.Completed or
          TerminalPacketType.Cancelled or
          TerminalPacketType.Failed)
      {
        _activeTerminalCommands.TryRemove(packet.CommandId, out _);
        UpdateTerminalStopButton();
      }

      if (DeviceList.SelectedItem is not DeviceView selected ||
          !ReferenceEquals(selected, item.Device))
        continue;

      switch (packet.Type)
      {
        case TerminalPacketType.Started:
          break;
        case TerminalPacketType.StandardOutput:
          builder.Append(packet.Text);
          break;
        case TerminalPacketType.StandardError:
          builder.Append(packet.Text);
          break;
        case TerminalPacketType.Completed:
          builder.Append("\r\n[退出代码 ")
            .Append(packet.ExitCode)
            .Append(" · ")
            .Append(packet.CommandId[..Math.Min(8, packet.CommandId.Length)])
            .Append("]\r\n");
          SetStatus("命令执行完成");
          break;
        case TerminalPacketType.Cancelled:
          builder.Append("\r\n[命令已终止 · ")
            .Append(packet.CommandId[..Math.Min(8, packet.CommandId.Length)])
            .Append("]\r\n");
          SetStatus("命令已终止");
          break;
        case TerminalPacketType.Failed:
          builder.Append("\r\n[命令失败] ")
            .Append(packet.Text)
            .Append("\r\n");
          SetStatus("命令执行失败：" + packet.Text);
          break;
      }
    }

    if (builder.Length > 0) AppendTerminalText(builder.ToString());
  }

  private void AppendTerminalText(string text)
  {
    const int maxCharacters = 2_000_000;
    CommandOutput.AppendText(text);
    if (CommandOutput.Text.Length > maxCharacters)
      CommandOutput.Text =
        CommandOutput.Text[^1_500_000..];
    CommandOutput.ScrollToEnd();
  }

  private async void CancelTerminalCommand_Click(
    object sender,
    System.Windows.RoutedEventArgs e)
  {
    try
    {
      DeviceView device = SelectedDevice();
      string? commandId = _activeTerminalCommands
        .Where(pair => ReferenceEquals(pair.Value.Device, device))
        .OrderByDescending(pair => pair.Value.Order)
        .Select(pair => pair.Key)
        .FirstOrDefault();
      if (commandId is null)
      {
        SetStatus("当前设备没有正在运行的命令。");
        return;
      }
      await device.SendTerminalPacketAsync(BinaryTerminalProtocol.Cancel(commandId));
      TerminalStopButton.IsEnabled = false;
      SetStatus($"正在终止命令 · {commandId[..8]}");
    }
    catch (Exception ex) { SetStatus("终止命令失败：" + ex.Message); }
  }

  private void ClearTerminalOutput_Click(
    object sender,
    System.Windows.RoutedEventArgs e) =>
    CommandOutput.Clear();

  private sealed record TerminalCommandState(
    DeviceView Device,
    long Generation,
    long Order);

  private void OnRegistryChanged(DeviceView device, RegistryChangedPayload change)
  {
    if (DeviceList.SelectedItem is not DeviceView selected ||
        !ReferenceEquals(selected, device)) return;
    _pendingRegistryChange = change;
    _registryRefreshDebounce?.Stop();
    _registryRefreshDebounce?.Start();
  }

  private static string BuildRegistryPath(string hive, string subKey) =>
    string.IsNullOrWhiteSpace(subKey) ? hive : hive + "\\" + subKey;

  private static string NormalizeRegistryPath(string path) =>
    path.Trim().TrimEnd('\\')
      .Replace("HKEY_CURRENT_USER", "HKCU", StringComparison.OrdinalIgnoreCase)
      .Replace("HKEY_LOCAL_MACHINE", "HKLM", StringComparison.OrdinalIgnoreCase)
      .Replace("HKEY_CLASSES_ROOT", "HKCR", StringComparison.OrdinalIgnoreCase)
      .Replace("HKEY_USERS", "HKU", StringComparison.OrdinalIgnoreCase)
      .Replace("HKEY_CURRENT_CONFIG", "HKCC", StringComparison.OrdinalIgnoreCase);
}

public sealed partial class DeviceView
{
  private readonly object _terminalGate = new();
  private TcpClient? _terminalClient;
  private NetworkStream? _terminalStream;
  private CancellationTokenSource? _terminalCts;
  private readonly SemaphoreSlim _terminalWriteLock = new(1, 1);
  private long _terminalGeneration;

  private readonly object _registryGate = new();
  private TcpClient? _registryClient;
  private NetworkStream? _registryStream;
  private CancellationTokenSource? _registryCts;
  private readonly SemaphoreSlim _registryWriteLock = new(1, 1);
  private readonly ConcurrentDictionary<
    string,
    TaskCompletionSource<RemoteMessage>> _registryPending = new();
  private long _registryGeneration;

  public event Action<TerminalPacket>? TerminalPacketReceived;
  public event Action<long>? TerminalGenerationEnded;
  public event Action<RegistryChangedPayload>? RegistryChanged;

  public bool IsTerminalConnected
  {
    get { lock (_terminalGate) return _terminalStream is not null; }
  }

  public long TerminalGeneration
  {
    get { lock (_terminalGate) return _terminalGeneration; }
  }

  public bool IsRegistryConnected
  {
    get { lock (_registryGate) return _registryStream is not null; }
  }

  public void AttachTerminalClient(TcpClient client, CancellationToken parentToken)
  {
    TcpClient? previous;
    CancellationTokenSource? previousCts;
    long previousGeneration;
    lock (_terminalGate)
    {
      previous = _terminalClient;
      previousCts = _terminalCts;
      previousGeneration = _terminalGeneration;
      _terminalClient = client;
      _terminalStream = client.GetStream();
      _terminalCts =
        CancellationTokenSource.CreateLinkedTokenSource(parentToken);
      _terminalGeneration++;
      long generation = _terminalGeneration;
      _ = Task.Run(
        () => TerminalReadLoopAsync(client, generation, _terminalCts.Token),
        CancellationToken.None);
    }
    try { previousCts?.Cancel(); previous?.Close(); } catch { }
    if (previous is not null) TerminalGenerationEnded?.Invoke(previousGeneration);
    Changed(nameof(IsTerminalConnected));
  }

  private async Task TerminalReadLoopAsync(
    TcpClient client,
    long generation,
    CancellationToken token)
  {
    try
    {
      NetworkStream stream = client.GetStream();
      while (!token.IsCancellationRequested)
      {
        TerminalPacket? packet =
          await BinaryTerminalProtocol.ReadAsync(stream, token);
        if (packet is null) break;
        TerminalPacketReceived?.Invoke(packet);
      }
    }
    catch (OperationCanceledException) { }
    catch { }
    finally
    {
      try { client.Close(); } catch { }
      lock (_terminalGate)
      {
        if (generation == _terminalGeneration &&
            ReferenceEquals(_terminalClient, client))
        {
          _terminalClient = null;
          _terminalStream = null;
        }
      }
      if (generation == _terminalGeneration)
        TerminalGenerationEnded?.Invoke(generation);
      Changed(nameof(IsTerminalConnected));
    }
  }

  public async Task SendTerminalPacketAsync(
    TerminalPacket packet,
    CancellationToken token = default)
  {
    NetworkStream stream;
    lock (_terminalGate)
      stream = _terminalStream
        ?? throw new InvalidOperationException("流式终端通道尚未连接。");

    await _terminalWriteLock.WaitAsync(token);
    try
    {
      await BinaryTerminalProtocol.WriteAsync(stream, packet, token);
    }
    finally { _terminalWriteLock.Release(); }
  }

  public void AttachRegistryClient(TcpClient client, CancellationToken parentToken)
  {
    TcpClient? previous;
    CancellationTokenSource? previousCts;
    lock (_registryGate)
    {
      previous = _registryClient;
      previousCts = _registryCts;
      _registryClient = client;
      _registryStream = client.GetStream();
      _registryCts =
        CancellationTokenSource.CreateLinkedTokenSource(parentToken);
      _registryGeneration++;
      long generation = _registryGeneration;
      foreach (TaskCompletionSource<RemoteMessage> pending in _registryPending.Values)
        pending.TrySetException(
          new IOException("注册表通道已重新连接，请重试当前操作。"));
      _registryPending.Clear();
      _ = Task.Run(
        () => RegistryReadLoopAsync(client, generation, _registryCts.Token),
        CancellationToken.None);
    }
    try { previousCts?.Cancel(); previous?.Close(); } catch { }
    Changed(nameof(IsRegistryConnected));
  }

  private async Task RegistryReadLoopAsync(
    TcpClient client,
    long generation,
    CancellationToken token)
  {
    try
    {
      NetworkStream stream = client.GetStream();
      while (!token.IsCancellationRequested)
      {
        RemoteMessage? message =
          await FramedJsonTransport.ReadAsync(stream, token);
        if (message is null) break;
        if (message.Type == MessageType.RegistryChanged)
        {
          RegistryChangedPayload? changed =
            message.Payload.As<RegistryChangedPayload>();
          if (changed is not null) RegistryChanged?.Invoke(changed);
        }
        else if (_registryPending.TryRemove(
                   message.RequestId,
                   out TaskCompletionSource<RemoteMessage>? pending))
        {
          pending.TrySetResult(message);
        }
      }
    }
    catch (OperationCanceledException) { }
    catch { }
    finally
    {
      try { client.Close(); } catch { }
      bool current;
      lock (_registryGate)
      {
        current =
          generation == _registryGeneration &&
          ReferenceEquals(_registryClient, client);
        if (current)
        {
          _registryClient = null;
          _registryStream = null;
        }
      }
      if (current)
      {
        foreach (TaskCompletionSource<RemoteMessage> pending in _registryPending.Values)
          pending.TrySetException(new IOException("注册表通道已断开。"));
        _registryPending.Clear();
      }
      Changed(nameof(IsRegistryConnected));
    }
  }

  public async Task<RemoteMessage> RegistryRequestAsync(
    MessageType type,
    object payload,
    int timeoutSeconds = 30,
    CancellationToken cancellationToken = default)
  {
    var message = new RemoteMessage
    {
      RequestId = Guid.NewGuid().ToString("N"),
      Type = type,
      DeviceId = DeviceId,
      DeviceName = DeviceName,
      Payload = MessagePayload.ToElement(payload)
    };
    var completion =
      new TaskCompletionSource<RemoteMessage>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    _registryPending[message.RequestId] = completion;
    try
    {
      NetworkStream stream;
      lock (_registryGate)
        stream = _registryStream
          ?? throw new InvalidOperationException("独立注册表通道尚未连接。");

      await _registryWriteLock.WaitAsync(cancellationToken);
      try
      {
        await FramedJsonTransport.WriteAsync(
          stream,
          message,
          CancellationToken.None);
      }
      finally { _registryWriteLock.Release(); }

      using var timeout =
        new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
      using var linked =
        CancellationTokenSource.CreateLinkedTokenSource(
          timeout.Token,
          cancellationToken);
      await using var registration = linked.Token.Register(
        () => completion.TrySetCanceled(linked.Token));
      RemoteMessage response = await completion.Task;
      if (response.Type == MessageType.Error)
        throw new InvalidOperationException(
          response.Payload.As<ErrorPayload>()?.Message ?? "远端注册表错误。");
      return response;
    }
    finally { _registryPending.TryRemove(message.RequestId, out _); }
  }

  private void CloseDedicatedChannelClients()
  {
    try { _terminalCts?.Cancel(); _terminalClient?.Close(); } catch { }
    try { _registryCts?.Cancel(); _registryClient?.Close(); } catch { }
    _terminalClient = null;
    _terminalStream = null;
    _registryClient = null;
    _registryStream = null;
  }
}
