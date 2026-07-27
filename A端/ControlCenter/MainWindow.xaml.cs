using Microsoft.Win32;
using RemoteControl.Shared;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Diagnostics;
using VirtualDataObject = VirtualFileDataObject.VirtualFileDataObject;

namespace ControlCenter;

public partial class MainWindow : Window
{
  public ObservableCollection<DeviceView> Devices { get; } = new();
  public ObservableCollection<FileItemView> FileItems { get; } = new();
  public ICollectionView FileItemsView { get; }
  public ObservableCollection<TransferTaskView> Transfers { get; } = new();
  public ObservableCollection<ProcessInfoView> Processes { get; } = new();
  public ICollectionView ProcessesView { get; }
  public ObservableCollection<ServiceInfoView> Services { get; } = new();
  public ICollectionView ServicesView { get; }
  public ObservableCollection<RegistryKeyView> RegistryRoots { get; } = new();
  public ObservableCollection<RegistryValuePayload> RegistryValues { get; } = new();

  private TcpListener? _listener;
  private CancellationTokenSource? _serverCts;
  private readonly ConcurrentDictionary<(string DeviceId, LogicalChannelType Channel), ChannelGeneration>
    _channelGenerations = new();
  private readonly ConcurrentDictionary<string, DeviceView> _devicesById = new();
  private readonly ConcurrentDictionary<string, byte> _reportedIncompatibleAgents = new(StringComparer.OrdinalIgnoreCase);
  private readonly ConcurrentDictionary<string, byte> _deletedDeviceIds =
    new(StringComparer.OrdinalIgnoreCase);
  private readonly DeviceMetadataStore _metadataStore = new();
  private readonly List<string> _fileHistory = new();
  private int _fileHistoryIndex = -1;
  private bool _atThisComputer = true;
  private bool _navigatingHistory;
  private Point _fileDragStart;
  private readonly DispatcherTimer _processTimer = new() { Interval = TimeSpan.FromSeconds(1) };
  private readonly DispatcherTimer _serviceTimer = new() { Interval = TimeSpan.FromSeconds(3) };
  private readonly Dictionary<string, DesktopControlWindow> _desktopWindows =
    new(StringComparer.OrdinalIgnoreCase);
  private bool _processRefreshInFlight;
  private bool _processIconRefreshInFlight;
  private CancellationTokenSource? _processIconCts;
  private readonly HashSet<int> _processIconRequested = [];
  private readonly Dictionary<string, bool> _processGroupExpansion =
    new(StringComparer.CurrentCultureIgnoreCase)
    {
      ["应用"] = true,
      ["后台进程"] = true
    };
  private bool _serviceRefreshInFlight;
  private bool _registryRefreshInFlight;
  private CancellationTokenSource? _registryLoadCts;
  private string? _registryLoadedDeviceId;
  private CancellationTokenSource? _systemInfoCts;
  private int _systemInfoGeneration;

  public MainWindow()
  {
    InitializeComponent();
    InitializeTraySupport();
    InitializeDedicatedUi();
    InitializeCodexBridge();
    FileItemsView = CollectionViewSource.GetDefaultView(FileItems);
    FileItemsView.Filter = FilterFileItem;
    ProcessesView = CollectionViewSource.GetDefaultView(Processes);
    ProcessesView.Filter = FilterProcessItem;
    ProcessesView.GroupDescriptions.Add(
      new PropertyGroupDescription(nameof(ProcessInfoView.Category)));
    ProcessesView.SortDescriptions.Add(
      new SortDescription(nameof(ProcessInfoView.CategoryOrder), ListSortDirection.Ascending));
    ProcessesView.SortDescriptions.Add(
      new SortDescription(nameof(ProcessInfoView.Name), ListSortDirection.Ascending));
    ServicesView = CollectionViewSource.GetDefaultView(Services);
    ServicesView.Filter = FilterServiceItem;
    _processTimer.Tick += async (_, _) => await RefreshProcessesAsync();
    _serviceTimer.Tick += async (_, _) => await RefreshServicesAsync();
    DataContext = this;
    RestoreRememberedDevices();
    Loaded += (_, _) =>
    {
      StartServer_Click(this, new RoutedEventArgs());
    };
  }

  private async void StartServer_Click(object sender, RoutedEventArgs e)
  {
    if (_listener is not null) return;
    int port = NetworkDefaults.Port;
    try
    {
      _serverCts = new CancellationTokenSource();
      _listener = new TcpListener(IPAddress.Any, port);
      _listener.Start();
      StartUdpDesktopServer(port, _serverCts.Token);
      StatusItem.Content = $"正在监听 0.0.0.0:{port}（TCP业务 + UDP桌面）";
      _ = AcceptLoopAsync(_serverCts.Token);
    }
    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
    {
      try { _listener?.Stop(); } catch { }
      _listener = null;
      StopUdpDesktopServer();
      _serverCts?.Dispose();
      _serverCts = null;
      StatusItem.Content = $"端口 {port} 已由另一个A端实例占用。";
      MessageBox.Show(
        "检测到另一个A端实例已经在运行，请检查现有窗口或右下角托盘。",
        "小陈远控QQ;3890053645",
        MessageBoxButton.OK,
        MessageBoxImage.Information);
      Close();
    }
    await Task.CompletedTask;
  }

  private void StopServer_Click(object sender, RoutedEventArgs e)
  {
    _serverCts?.Cancel(); _serverCts = null;
    _listener?.Stop(); _listener = null;
    StopUdpDesktopServer();
    _channelGenerations.Clear();
    _devicesById.Clear();
    foreach (var d in Devices) d.Close();
    Devices.Clear();
    StatusItem.Content = "已停止监听";
  }

  private async Task AcceptLoopAsync(CancellationToken token)
  {
    while (!token.IsCancellationRequested && _listener is not null)
    {
      try
      {
        var client = await _listener.AcceptTcpClientAsync(token);
        client.NoDelay = true;
        _ = Task.Run(() => RouteAcceptedClientAsync(client, token), CancellationToken.None);
      }
      catch (OperationCanceledException) { break; }
      catch (Exception ex) { await Dispatcher.InvokeAsync(() => StatusItem.Content = "监听错误: " + ex.Message); }
    }
  }

  private async Task RouteAcceptedClientAsync(TcpClient client, CancellationToken token)
  {
    try
    {
      using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
      using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
      NetworkStream stream = client.GetStream();
      LogicalChannelHello? channel =
        await LogicalChannelProtocol.ReadHelloAsync(stream, linked.Token);
      if (channel is null)
      {
        client.Close();
        return;
      }
      if (channel.ProtocolVersion != ProtocolVersions.Current)
      {
        string endpoint =
          (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ??
          "未知地址";
        if (channel.Channel == LogicalChannelType.Management &&
            _reportedIncompatibleAgents.TryAdd(endpoint, 0))
        {
          await Dispatcher.InvokeAsync(() =>
            StatusItem.Content =
              $"已拒绝旧版B端 {endpoint}（协议 {channel.ProtocolVersion}），请替换为本交付中的B端.exe。");
        }
        client.Close();
        return;
      }
      if (!AcceptChannelGeneration(channel))
      {
        client.Close();
        return;
      }

      if (channel.Channel == LogicalChannelType.Management)
      {
        await AttachManagementClientAsync(client, channel, token, linked.Token);
        return;
      }

      DeviceView? device = await WaitForDeviceAsync(
        channel.DeviceId,
        channel.InstanceId,
        token);
      if (device is null)
      {
        client.Close();
        return;
      }

      switch (channel.Channel)
      {
        case LogicalChannelType.Video:
          if (!await VerifyHelloAsync(
                () => BinaryVideoProtocol.ReadHelloAsync(stream, linked.Token),
                channel.DeviceId))
            break;
          device.AttachTcpVideoClient(client, token);
          return;
        case LogicalChannelType.Input:
          if (!await VerifyHelloAsync(
                () => BinaryControlProtocol.ReadHelloAsync(stream, linked.Token),
                channel.DeviceId))
            break;
          device.AttachTcpInputClient(client, token);
          return;
        case LogicalChannelType.Clipboard:
          if (!await VerifyHelloAsync(
                () => BinaryClipboardProtocol.ReadHelloAsync(stream, linked.Token),
                channel.DeviceId))
            break;
          device.AttachClipboardClient(client, token);
          return;
        case LogicalChannelType.File:
          if (!await VerifyHelloAsync(
                () => BinaryFileTransferProtocol.ReadHelloAsync(stream, linked.Token),
                channel.DeviceId))
            break;
          device.AttachFileClient(client, token);
          return;
        case LogicalChannelType.Terminal:
          if (!await VerifyHelloAsync(
                () => BinaryTerminalProtocol.ReadHelloAsync(stream, linked.Token),
                channel.DeviceId))
            break;
          device.AttachTerminalClient(client, token);
          return;
        case LogicalChannelType.Registry:
          RemoteMessage? registryHello =
            await FramedJsonTransport.ReadAsync(stream, linked.Token);
          HelloPayload? registryPayload = registryHello?.Payload.As<HelloPayload>();
          if (registryHello?.Type != MessageType.Hello ||
              !string.Equals(
                registryPayload?.DeviceId,
                channel.DeviceId,
                StringComparison.OrdinalIgnoreCase))
            break;
          device.AttachRegistryClient(client, token);
          return;
        case LogicalChannelType.Codex:
          device.AttachCodexClient(client, token);
          return;
      }
    }
    catch (OperationCanceledException) { }
    catch { }
    try { client.Close(); } catch { }
  }

  private async Task AttachManagementClientAsync(
    TcpClient client,
    LogicalChannelHello channel,
    CancellationToken serverToken,
    CancellationToken handshakeToken)
  {
    var helloMessage = await FramedJsonTransport.ReadAsync(client.GetStream(), handshakeToken);
    HelloPayload? hello = helloMessage?.Payload.As<HelloPayload>();
    if (helloMessage?.Type != MessageType.Hello ||
        hello is null ||
        !string.Equals(hello.DeviceId, channel.DeviceId, StringComparison.OrdinalIgnoreCase))
    {
      client.Close();
      return;
    }
    if (_deletedDeviceIds.ContainsKey(hello.DeviceId))
    {
      client.Close();
      return;
    }
    if (hello.ProtocolVersion != ProtocolVersions.Current)
    {
      string endpoint =
        (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "未知地址";
      if (_reportedIncompatibleAgents.TryAdd(endpoint, 0))
      {
        await Dispatcher.InvokeAsync(() =>
          StatusItem.Content =
            $"已拒绝旧版B端 {endpoint}（协议 {hello.ProtocolVersion}），请替换为同一交付中的B端.exe。");
      }
      client.Close();
      return;
    }

    DeviceView device;
    long generation;
    if (_devicesById.TryGetValue(hello.DeviceId, out DeviceView? existing))
    {
      device = existing;
      generation =
        await Dispatcher.InvokeAsync(() => device.RebindManagementClient(
          client,
          hello,
          channel.InstanceId));
    }
    else
    {
      device = new DeviceView(client, hello, channel.InstanceId);
      device.MetadataResolver = id => _metadataStore.Get(id);
      SubscribeDedicatedDeviceEvents(device);
      SubscribeCodexDeviceEvents(device);
      generation = device.CurrentGeneration;
      _devicesById[hello.DeviceId] = device;
      await Dispatcher.InvokeAsync(() =>
      {
        Devices.Add(device);
        if (DeviceList.SelectedItem is null)
          DeviceList.SelectedItem = device;
      });
    }
    device.ConfigureUdpTransport(SendUdpDesktopAsync, serverToken);
    await Dispatcher.InvokeAsync(() =>
    {
      device.ApplyResolvedMetadata();
      _metadataStore.SaveDevice(device);
      if (DeviceList.SelectedItem is null)
        DeviceList.SelectedItem = device;
      UpdateSelectedDeviceHeader();
    });
    _ = device.ReadLoopAsync(
      () => Dispatcher.Invoke(() => OnDeviceConnectionChanged(device)),
      () => Dispatcher.Invoke(() => OnDeviceConnectionChanged(device)),
      serverToken,
      generation);
    if (device.ScreenStreamRequested) _ = device.ResumeScreenStreamAsync();
  }

  private bool AcceptChannelGeneration(LogicalChannelHello hello)
  {
    var key = (hello.DeviceId, hello.Channel);
    while (true)
    {
      if (!_channelGenerations.TryGetValue(key, out ChannelGeneration current))
        return _channelGenerations.TryAdd(
          key,
          new ChannelGeneration(hello.InstanceId, hello.Generation));
      if (current.InstanceId == hello.InstanceId &&
          hello.Generation <= current.Generation)
        return false;
      if (_channelGenerations.TryUpdate(
            key,
            new ChannelGeneration(hello.InstanceId, hello.Generation),
            current))
        return true;
    }
  }

  private static async Task<bool> VerifyHelloAsync(
    Func<Task<string?>> reader,
    string expectedDeviceId) =>
    string.Equals(
      await reader(),
      expectedDeviceId,
      StringComparison.OrdinalIgnoreCase);

  private readonly record struct ChannelGeneration(Guid InstanceId, long Generation);

  private DeviceView SelectedDevice()
  {
    if (DeviceList.SelectedItem is DeviceView d && d.IsOnline) return d;
    DeviceView? fallback = Devices.FirstOrDefault(device =>
      device.IsOnline && !device.IsHidden);
    if (fallback is not null)
    {
      DeviceList.SelectedItem = fallback;
      DeviceList.ScrollIntoView(fallback);
      return fallback;
    }
    throw new InvalidOperationException("请先选择一台在线设备。");
  }

  private async Task<RemoteMessage> RequestAsync(
    MessageType type,
    object payload,
    int timeoutSeconds = 60,
    CancellationToken cancellationToken = default) =>
    await SelectedDevice().RequestAsync(type, payload, timeoutSeconds, cancellationToken);
  private static T ReadPayload<T>(RemoteMessage response, MessageType expectedType)
  {
    if (response.Type != expectedType)
      throw new InvalidOperationException($"A/B协议响应不匹配：期望 {expectedType}，实际 {response.Type}。请确保两端来自同一交付版本。");
    try
    {
      return response.Payload.As<T>()
        ?? throw new InvalidOperationException($"远端返回的 {expectedType} 内容为空。");
    }
    catch (JsonException ex)
    {
      throw new InvalidOperationException($"A/B数据结构不兼容（{expectedType}）。请替换为同一交付中的A端和B端程序。", ex);
    }
  }
  private void SetStatus(string text) => StatusItem.Content = text;

  private async void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
  {
    UpdateTerminalStopButton();
    DeviceView? selected = DeviceList.SelectedItem as DeviceView;
    Task systemInfoTask = Task.CompletedTask;
    if (selected is null || !selected.IsOnline)
    {
      CancelSystemInfoLoad();
      ClearSystemInfo();
    }
    else
      systemInfoTask = LoadSystemInfoAsync(selected, navigateHome: false);

    if (IsLoaded && MainTabs.SelectedIndex == 4)
    {
      Processes.Clear();
      _processIconRequested.Clear();
      await RefreshProcessesAsync();
    }
    if (IsLoaded && MainTabs.SelectedIndex == 6)
      await LoadRegistryRootsFromSelectedDeviceAsync(force: true);
    await systemInfoTask;
  }
  private void RefreshDeviceView_Click(object sender, RoutedEventArgs e) => DeviceList.Items.Refresh();
  private void UpdateSelectedDeviceHeader() { }

  private void OnDeviceConnectionChanged(DeviceView device)
  {
    _metadataStore.SaveDevice(device);
    UpdateSelectedDeviceHeader();
    if (ReferenceEquals(DeviceList.SelectedItem, device) && device.IsOnline)
      _ = LoadSystemInfoAsync(device, navigateHome: false);
    if (MainTabs.SelectedIndex != 6 || !ReferenceEquals(DeviceList.SelectedItem, device)) return;
    if (!device.IsOnline)
      ClearRegistryUi("所选设备已离线，注册表内容已清除。");
    else
      _ = LoadRegistryRootsFromSelectedDeviceAsync(force: true);
  }

  private void OpenDesktopTab_Click(object sender, RoutedEventArgs e)
  {
    try
    {
      var d = SelectedDevice();
      bool allowControl = (sender as FrameworkElement)?.Tag?.ToString() != "View";
      if (_desktopWindows.TryGetValue(d.DeviceId, out DesktopControlWindow? existing))
      {
        existing.SetControlMode(allowControl);
        if (!existing.IsVisible) existing.Show();
        if (existing.WindowState == WindowState.Minimized)
          existing.WindowState = WindowState.Normal;
        existing.Activate();
        existing.Focus();
        SetStatus($"已激活桌面{(allowControl ? "控制" : "观看")}窗口：{d.DisplayTitle}");
        return;
      }
      var win = new DesktopControlWindow(d, allowControl) { Owner = this };
      _desktopWindows[d.DeviceId] = win;
      win.Closed += (_, _) =>
      {
        if (_desktopWindows.TryGetValue(d.DeviceId, out DesktopControlWindow? current) &&
            ReferenceEquals(current, win))
          _desktopWindows.Remove(d.DeviceId);
      };
      win.Show();
      SetStatus($"已打开桌面{(allowControl ? "控制" : "观看")}窗口：{d.DisplayTitle}");
    }
    catch (Exception ex) { SetStatus($"打开实时远程桌面失败：{ex.GetType().Name}: {ex.Message}"); }
  }
  private async void OpenFilesTab_Click(object sender, RoutedEventArgs e) { MainTabs.SelectedIndex = 2; await LoadDrivesAsync(addHistory: true); }
  private async void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
  {
    if (e.Source != MainTabs || !IsLoaded) return;

    _processTimer.Stop();
    _serviceTimer.Stop();
    CancelSystemInfoLoad();
    _processIconCts?.Cancel();
    _processIconCts?.Dispose();
    _processIconCts = null;
    try
    {
      switch (MainTabs.SelectedIndex)
      {
        case 2 when FileItems.Count == 0:
          await LoadDrivesAsync(addHistory: true);
          break;
        case 4:
          _processIconCts = new CancellationTokenSource();
          await RefreshProcessesAsync();
          _processTimer.Start();
          break;
        case 5:
          await RefreshServicesAsync();
          _serviceTimer.Start();
          break;
        case 6 when !_registryRefreshInFlight:
          _registryRefreshInFlight = true;
          try { await LoadRegistryRootsFromSelectedDeviceAsync(force: false); }
          finally { _registryRefreshInFlight = false; }
          break;
      }
    }
    catch (Exception ex) { SetStatus(ex.Message); }
  }
  private void OpenTerminalTab_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 3;
  private void OpenProcessTab_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 4;
  private void OpenServiceTab_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 5;
  private void OpenRegistryTab_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 6;

  private void GenerateAgent_Click(object sender, RoutedEventArgs e)
  {
    var window = new AgentGeneratorWindow { Owner = this };
    window.ShowDialog();
  }

  private async void SystemInfo_Click(object sender, RoutedEventArgs e)
  {
    DeviceView? device = DeviceList.SelectedItem as DeviceView;
    if (device is null || !device.IsOnline)
    {
      SetStatus("请先选择一台在线设备。");
      return;
    }
    await LoadSystemInfoAsync(device, navigateHome: true);
  }

  private async Task LoadSystemInfoAsync(DeviceView device, bool navigateHome)
  {
    if (!device.IsOnline) return;
    if (navigateHome) MainTabs.SelectedIndex = 0;

    CancelSystemInfoLoad();
    var cts = new CancellationTokenSource();
    _systemInfoCts = cts;
    int generation = ++_systemInfoGeneration;
    ShowSystemInfoLoading();
    try
    {
      RemoteMessage response = await device.RequestAsync(
        MessageType.SystemInfoRequest,
        new { },
        20,
        cts.Token);
      DetailedSystemInfoPayload info = ReadPayload<DetailedSystemInfoPayload>(
        response,
        MessageType.SystemInfoResponse);
      if (cts.IsCancellationRequested ||
          generation != _systemInfoGeneration ||
          !ReferenceEquals(DeviceList.SelectedItem, device))
        return;
      ApplySystemInfo(info);
      SetStatus($"{device.DisplayTitle} · 系统信息已刷新");
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
      if (generation == _systemInfoGeneration &&
          ReferenceEquals(DeviceList.SelectedItem, device))
      {
        ClearSystemInfo();
        SetStatus(ex.Message);
      }
    }
    finally
    {
      if (ReferenceEquals(_systemInfoCts, cts))
      {
        _systemInfoCts = null;
        cts.Dispose();
      }
    }
  }

  private void ApplySystemInfo(DetailedSystemInfoPayload info)
  {
    InfoMachine.Text = info.DeviceName;
    InfoProcessor.Text = info.Processor;
    InfoRam.Text = info.InstalledRam;
    InfoGpu.Text = info.Graphics;
    InfoStorage.Text = info.Storage;
    InfoDeviceId.Text = info.DeviceId;
    InfoProductId.Text = info.ProductId;
    InfoSystemType.Text = info.SystemType;
    InfoPenTouch.Text = info.PenAndTouch;
    InfoIp.Text = info.LocalIpAddresses;
    InfoOs.Text = info.WindowsEdition;
    InfoDisplayVersion.Text = info.DisplayVersion;
    InfoInstallDate.Text = info.InstallDate;
    InfoOsBuild.Text = info.OsBuild;
    InfoExperience.Text = info.ExperiencePack;
  }

  private void ShowSystemInfoLoading()
  {
    ClearSystemInfo("正在获取…");
  }

  private void ClearSystemInfo(string value = "-")
  {
    foreach (TextBlock field in new[]
    {
      InfoMachine, InfoProcessor, InfoRam, InfoGpu, InfoStorage,
      InfoDeviceId, InfoProductId, InfoSystemType, InfoPenTouch, InfoIp,
      InfoOs, InfoDisplayVersion, InfoInstallDate, InfoOsBuild, InfoExperience
    })
      field.Text = value;
  }

  private void CancelSystemInfoLoad()
  {
    _systemInfoGeneration++;
    CancellationTokenSource? current = _systemInfoCts;
    _systemInfoCts = null;
    try { current?.Cancel(); } catch { }
    current?.Dispose();
  }

  private void RestoreRememberedDevices()
  {
    foreach ((string deviceId, DeviceMetadata metadata) in _metadataStore.GetAll())
    {
      DeviceView device = DeviceView.CreateOffline(deviceId, metadata);
      device.MetadataResolver = id => _metadataStore.Get(id);
      SubscribeDedicatedDeviceEvents(device);
      SubscribeCodexDeviceEvents(device);
      _devicesById[deviceId] = device;
      Devices.Add(device);
    }
  }

  private DeviceView? DeviceFromMenu(object sender)
  {
    if (sender is FrameworkElement fe && fe.DataContext is DeviceView d) return d;
    return DeviceList.SelectedItem as DeviceView;
  }

  private async void RestartDevice_Click(object sender, RoutedEventArgs e)
  {
    var device = DeviceFromMenu(sender);
    if (device is null) return;
    if (!ConfirmationWindow.Show(
          this,
          "重启设备",
          $"确认立即重启 {device.DisplayTitle}？")) return;
    await ExecutePowerActionAsync(
      device,
      PowerAction.Restart,
      "重启命令已由SYSTEM服务接受");
  }

  private async void ShutdownDevice_Click(object sender, RoutedEventArgs e)
  {
    var device = DeviceFromMenu(sender);
    if (device is null) return;
    if (!ConfirmationWindow.Show(
          this,
          "关闭设备",
          $"确认立即关闭 {device.DisplayTitle}？")) return;
    await ExecutePowerActionAsync(
      device,
      PowerAction.Shutdown,
      "关机命令已由SYSTEM服务接受");
  }

  private async void LockDevice_Click(object sender, RoutedEventArgs e)
  {
    var device = DeviceFromMenu(sender);
    if (device is null) return;
    await ExecutePowerActionAsync(device, PowerAction.Lock, "锁屏命令已执行");
  }

  private async void ToggleStartup_Click(object sender, RoutedEventArgs e)
  {
    DeviceView? device = DeviceFromMenu(sender);
    if (device is null) return;
    await UpdateAgentSettingsAsync(
      device,
      new AgentSettingsPayload(!device.StartupEnabled, device.HideTray));
  }

  private async void ToggleHideTray_Click(object sender, RoutedEventArgs e)
  {
    DeviceView? device = DeviceFromMenu(sender);
    if (device is null) return;
    await UpdateAgentSettingsAsync(
      device,
      new AgentSettingsPayload(device.StartupEnabled, !device.HideTray));
  }

  private async Task UpdateAgentSettingsAsync(
    DeviceView device,
    AgentSettingsPayload requested)
  {
    if (!device.IsOnline) return;
    try
    {
      RemoteMessage response = await device.RequestAsync(
        MessageType.AgentSettingsUpdateRequest,
        requested,
        15);
      AgentSettingsPayload actual = ReadPayload<AgentSettingsPayload>(
        response,
        MessageType.AgentSettingsUpdateResponse);
      device.ApplyAgentSettings(actual);
      SetStatus(
        $"{device.DisplayTitle} · 开机启动{(actual.StartupEnabled ? "已开启" : "已关闭")}，" +
        $"隐藏托盘{(actual.HideTray ? "已开启" : "已关闭")}");
    }
    catch (Exception ex)
    {
      SetStatus($"{device.DisplayTitle} · 设置失败：{ex.Message}");
    }
  }

  private async Task ExecutePowerActionAsync(
    DeviceView device,
    PowerAction action,
    string successMessage)
  {
    if (!device.IsOnline)
    {
      SetStatus("该设备当前离线，无法执行操作。");
      return;
    }

    try
    {
      RemoteMessage response = await device.RequestAsync(
        MessageType.PowerActionRequest,
        new PowerActionPayload(action),
        15);
      OperationResultPayload result = ReadPayload<OperationResultPayload>(
        response,
        MessageType.PowerActionResponse);
      if (!result.Success) throw new InvalidOperationException(result.Message);
      SetStatus($"{device.DisplayTitle} · {successMessage}");
    }
    catch (Exception ex)
    {
      SetStatus($"{device.DisplayTitle} · 操作失败：{ex.Message}");
    }
  }

  private void RemarkDevice_Click(object sender, RoutedEventArgs e)
  {
    var d = DeviceFromMenu(sender); if (d is null) return;
    string? remark = PromptWindow.ShowDialog(this, "修改备注", "备注会保存在A端，用来记住这台设备。", d.Remark);
    if (remark is null) return;
    var meta = _metadataStore.Get(d.DeviceId);
    meta.Remark = remark.Trim();
    d.ApplyMetadata(meta);
    _metadataStore.SaveDevice(d);
    UpdateSelectedDeviceHeader();
  }

  private async void DeleteDevice_Click(object sender, RoutedEventArgs e)
  {
    var d = DeviceFromMenu(sender); if (d is null) return;
    if (!d.IsOnline)
    {
      MessageBox.Show(
        this,
        "设备当前离线，无法确认并删除B端软件。请在设备上线后重试。",
        "删除设备",
        MessageBoxButton.OK,
        MessageBoxImage.Information);
      return;
    }
    if (!ConfirmationWindow.Show(
          this,
          "删除设备",
          $"确认删除 {d.DisplayTitle}？这会停止并删除该电脑上的B端软件。"))
      return;
    try
    {
      RemoteMessage response = await d.RequestAsync(
        MessageType.AgentUninstallRequest,
        new AgentUninstallPayload(d.DeviceId),
        10);
      OperationResultPayload result = ReadPayload<OperationResultPayload>(
        response,
        MessageType.AgentUninstallResponse);
      if (!result.Success)
        throw new InvalidOperationException(result.Message);
      _deletedDeviceIds[d.DeviceId] = 0;
      _metadataStore.Remove(d.DeviceId);
      _devicesById.TryRemove(d.DeviceId, out _);
      d.Close();
      Devices.Remove(d);
      UpdateSelectedDeviceHeader();
      SetStatus($"{d.DisplayTitle} 的B端删除任务已启动。");
    }
    catch (Exception ex)
    {
      _deletedDeviceIds.TryRemove(d.DeviceId, out _);
      MessageBox.Show(
        this,
        "B端未确认删除，设备记录已保留。\n\n" + ex.Message,
        "删除失败",
        MessageBoxButton.OK,
        MessageBoxImage.Error);
    }
  }

  private async void RunCommand_Click(object sender, RoutedEventArgs e)
  {
    string command = CmdArgs.Text.Trim();
    if (command.Length == 0) return;
    string? commandId = null;
    try
    {
      DeviceView device = SelectedDevice();
      string shell = ((ComboBoxItem)ShellBox.SelectedItem).Content.ToString() == "CMD"
        ? "CMD"
        : "PowerShell";
      commandId = Guid.NewGuid().ToString("N");
      RegisterTerminalCommand(commandId, device);
      AppendTerminalText(
        $"\r\n[{shell} · {DateTime.Now:HH:mm:ss} · {commandId[..8]}]\r\n{shell}> {command}\r\n\r\n");
      await device.SendTerminalPacketAsync(
        BinaryTerminalProtocol.Start(commandId, shell, command, string.Empty));
      SetStatus($"命令已发送 · {commandId[..8]}");
    }
    catch (Exception ex)
    {
      if (commandId is not null) _activeTerminalCommands.TryRemove(commandId, out _);
      UpdateTerminalStopButton();
      AppendTerminalText("\r\n[发送失败] " + ex.Message + "\r\n");
      SetStatus("命令发送失败：" + ex.Message);
    }
  }

  private void CmdArgs_KeyDown(object sender, KeyEventArgs e)
  {
    if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
    e.Handled = true;
    RunCommand_Click(sender, new RoutedEventArgs());
  }

  private async void Drives_Click(object sender, RoutedEventArgs e)
  {
    await LoadDrivesAsync(addHistory: true);
  }

  private async Task LoadDrivesAsync(bool addHistory)
  {
    try
    {
      var r = await RequestAsync(MessageType.DrivesRequest, new { });
      _atThisComputer = true;
      PathBox.Text = "此电脑";
      FileItems.Clear();
      foreach (var d in ReadPayload<List<DriveInfoPayload>>(r, MessageType.DrivesResponse))
        FileItems.Add(FileItemView.FromDrive(d));
      FileItemsView.Refresh();
      FileStatus.Text = $"此电脑 · {FileItems.Count} 个磁盘";
      if (addHistory) AddFileHistory("此电脑");
    }
    catch (Exception ex) { FileStatus.Text = ex.Message; }
  }

  private async void ListDirectory_Click(object sender, RoutedEventArgs e)
  {
    if (_atThisComputer || string.Equals(PathBox.Text, "此电脑", StringComparison.OrdinalIgnoreCase))
      await LoadDrivesAsync(addHistory: false);
    else
      await RefreshFilesAsync(addHistory: false);
  }

  private async Task RefreshFilesAsync(bool addHistory = true)
  {
    try
    {
      var r = await RequestAsync(MessageType.DirectoryRequest, new PathPayload(PathBox.Text));
      var payload = ReadPayload<DirectoryResponsePayload>(r, MessageType.DirectoryResponse);
      _atThisComputer = false;
      PathBox.Text = payload.Path;
      FileItems.Clear();
      foreach (var item in payload.Items.OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name)) FileItems.Add(FileItemView.FromDirectory(item));
      FileItemsView.Refresh();
      FileStatus.Text = $"{FileItems.Count} 项";
      if (addHistory) AddFileHistory(payload.Path);
    }
    catch (Exception ex) { FileStatus.Text = ex.Message; }
  }

  private async void ParentDir_Click(object sender, RoutedEventArgs e)
  {
    try
    {
      if (_atThisComputer) return;
      string current = PathBox.Text.TrimEnd('\\');
      var parent = Directory.GetParent(current);
      if (parent is null)
      {
        await LoadDrivesAsync(addHistory: true);
        return;
      }
      PathBox.Text = parent.FullName;
      await RefreshFilesAsync();
    }
    catch (Exception ex) { FileStatus.Text = ex.Message; }
  }

  private async void FileGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
  {
    if (FileGrid.SelectedItem is not FileItemView f) return;
    if (f.IsDirectory)
    {
      PathBox.Text = f.FullPath;
      await RefreshFilesAsync();
    }
    else await OpenRemoteFileLocallyAsync(f);
  }

  private async void OpenRemoteFile_Click(object sender, RoutedEventArgs e)
  {
    if (FileGrid.SelectedItem is not FileItemView f || f.IsDirectory) return;
    await OpenRemoteFileLocallyAsync(f);
  }

  private async Task OpenRemoteFileLocallyAsync(FileItemView file)
  {
    try
    {
      string local = await DownloadToCacheAsync(file);
      Process.Start(new ProcessStartInfo(local) { UseShellExecute = true });
      FileStatus.Text = "已使用A端本地软件打开：" + file.Name;
    }
    catch (Exception ex) { FileStatus.Text = ex.Message; }
  }

  private async Task<string> DownloadToCacheAsync(FileItemView f)
  {
    string safeDevice = (SelectedDevice().DeviceId.Length > 0 ? SelectedDevice().DeviceId : SelectedDevice().DeviceName).Replace(':', '_');
    string pathKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(f.FullPath))).Substring(0, 20);
    string dir = System.IO.Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
      "AuthorizedDeviceControl",
      "OpenCache",
      safeDevice,
      pathKey);
    Directory.CreateDirectory(dir);
    string local = System.IO.Path.Combine(dir, SanitizeFileName(f.Name));
    if (File.Exists(local))
    {
      var cached = new FileInfo(local);
      if (cached.Length == f.Length &&
          (f.LastWriteTime == DateTime.MinValue || Math.Abs((cached.LastWriteTime - f.LastWriteTime).TotalSeconds) < 3))
        return local;
    }
    var progress = new Progress<FileTransferProgress>(p =>
      FileStatus.Text = $"正在读取 {f.Name} · {FormatTransferProgress(p)}");
    await SelectedDevice().DownloadFileAsync(f.FullPath, local, f.Length, progress);
    if (f.LastWriteTime != DateTime.MinValue) File.SetLastWriteTime(local, f.LastWriteTime);
    return local;
  }

  private static string SanitizeFileName(string name)
  {
    foreach (var ch in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
    return name;
  }

  private async void CreateFolder_Click(object sender, RoutedEventArgs e)
  {
    try
    {
      if (_atThisComputer) { FileStatus.Text = "请先进入一个磁盘。"; return; }
      string name = Microsoft.VisualBasic.Interaction.InputBox("新文件夹名称", "新建文件夹", "新建文件夹");
      if (string.IsNullOrWhiteSpace(name)) return;
      string target = System.IO.Path.Combine(PathBox.Text, name);
      await RequestAsync(MessageType.CreateDirectoryRequest, new PathPayload(target));
      await RefreshFilesAsync();
    }
    catch (Exception ex) { FileStatus.Text = ex.Message; }
  }

  private async void RenamePath_Click(object sender, RoutedEventArgs e)
  {
    try
    {
      if (FileGrid.SelectedItem is not FileItemView f) return;
      if (f.IsDrive) { FileStatus.Text = "磁盘不能重命名。"; return; }
      string name = Microsoft.VisualBasic.Interaction.InputBox("新名称", "重命名", f.Name);
      if (string.IsNullOrWhiteSpace(name) || name == f.Name) return;
      string newPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(f.FullPath) ?? PathBox.Text, name);
      await RequestAsync(MessageType.RenameRequest, new RenamePayload(f.FullPath, newPath));
      await RefreshFilesAsync();
    }
    catch (Exception ex) { FileStatus.Text = ex.Message; }
  }

  private async void FileProperties_Click(object sender, RoutedEventArgs e)
  {
    try
    {
      if (FileGrid.SelectedItem is not FileItemView f) return;
      var r = await RequestAsync(MessageType.FilePropertiesRequest, new PathPayload(f.FullPath));
      var prop = r.Payload.As<FilePropertiesPayload>()!;
      MessageBox.Show($"名称: {prop.Name}\n路径: {prop.FullPath}\n类型: {(prop.IsDirectory ? "文件夹" : prop.Extension)}\n大小: {FileItemView.FormatSize(prop.Length)}\n创建: {prop.CreationTime}\n修改: {prop.LastWriteTime}\n属性: {prop.Attributes}", "属性");
    }
    catch (Exception ex) { FileStatus.Text = ex.Message; }
  }

  private async void DownloadFile_Click(object sender, RoutedEventArgs e)
  {
    try
    {
      var selected = FileGrid.SelectedItems.Cast<FileItemView>().ToList();
      if (selected.Count == 0) return;
      if (selected.Count == 1 && !selected[0].IsDirectory)
      {
        var file = selected[0];
        var dlg = new SaveFileDialog { FileName = file.Name };
        if (dlg.ShowDialog() != true) return;
        await StartTransferAsync("下载", file.Name, async (task, token) =>
          await SelectedDevice().DownloadFileAsync(file.FullPath, dlg.FileName, file.Length, task.Progress, token));
      }
      else
      {
        var folder = new OpenFolderDialog { Title = "选择本机保存位置" };
        if (folder.ShowDialog() != true) return;
        foreach (FileItemView item in selected)
        {
          string local = Path.Combine(folder.FolderName, item.Name);
          await StartTransferAsync("下载", item.Name, async (task, token) =>
          {
            if (item.IsDirectory) await DownloadRemoteDirectoryAsync(item.FullPath, local, task, token);
            else await SelectedDevice().DownloadFileAsync(item.FullPath, local, item.Length, task.Progress, token);
          });
        }
      }
    }
    catch (Exception ex) { FileStatus.Text = ex.Message; }
  }

  private async void UploadFile_Click(object sender, RoutedEventArgs e)
  {
    try
    {
      if (_atThisComputer) { FileStatus.Text = "请先进入一个磁盘。"; return; }
      var dlg = new OpenFileDialog { Multiselect = true };
      if (dlg.ShowDialog() != true) return;
      await UploadLocalPathsAsync(
        dlg.FileNames,
        ResolveUploadTargetDirectory(),
        confirmOverwrite: true);
      await RefreshFilesAsync();
    }
    catch (Exception ex) { FileStatus.Text = ex.Message; }
  }

  private async void DeletePath_Click(object sender, RoutedEventArgs e)
  {
    try
    {
      if (FileGrid.SelectedItem is not FileItemView selected || selected.IsDrive) return;
      string path = selected.FullPath;
      if (MessageBox.Show("确认删除远程路径？\n" + path, "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
      var r = await RequestAsync(MessageType.DeleteRequest, new PathPayload(path));
      FileStatus.Text = r.Payload.As<OperationResultPayload>()?.Message ?? "删除完成";
      await RefreshFilesAsync();
    }
    catch (Exception ex) { FileStatus.Text = ex.Message; }
  }
  private async void PathBox_KeyDown(object sender, KeyEventArgs e)
  {
    if (e.Key != Key.Enter) return;
    e.Handled = true;
    if (string.Equals(PathBox.Text.Trim(), "此电脑", StringComparison.OrdinalIgnoreCase))
      await LoadDrivesAsync(addHistory: true);
    else
      await RefreshFilesAsync();
  }

  private static bool FilterFileItem(object item) => item is FileItemView;

  private void AddFileHistory(string path)
  {
    if (_navigatingHistory) return;
    if (_fileHistoryIndex >= 0 && _fileHistoryIndex < _fileHistory.Count &&
        string.Equals(_fileHistory[_fileHistoryIndex], path, StringComparison.OrdinalIgnoreCase)) return;
    if (_fileHistoryIndex + 1 < _fileHistory.Count)
      _fileHistory.RemoveRange(_fileHistoryIndex + 1, _fileHistory.Count - _fileHistoryIndex - 1);
    _fileHistory.Add(path);
    _fileHistoryIndex = _fileHistory.Count - 1;
  }

  private async void FileBack_Click(object sender, RoutedEventArgs e) => await NavigateHistoryAsync(-1);
  private async void FileForward_Click(object sender, RoutedEventArgs e) => await NavigateHistoryAsync(1);

  private async Task NavigateHistoryAsync(int delta)
  {
    int target = _fileHistoryIndex + delta;
    if (target < 0 || target >= _fileHistory.Count) return;
    _fileHistoryIndex = target;
    _navigatingHistory = true;
    try
    {
      string path = _fileHistory[target];
      if (path == "此电脑") await LoadDrivesAsync(addHistory: false);
      else { PathBox.Text = path; await RefreshFilesAsync(addHistory: false); }
    }
    finally { _navigatingHistory = false; }
  }

  private void FileContextMenu_Opened(object sender, RoutedEventArgs e)
  {
    bool selected = FileGrid.SelectedItem is FileItemView;
    bool selectedDrive = FileGrid.SelectedItem is FileItemView { IsDrive: true };
    FileOpenMenu.Visibility = selected && !selectedDrive ? Visibility.Visible : Visibility.Collapsed;
    FileDownloadMenu.Visibility = selected && !selectedDrive ? Visibility.Visible : Visibility.Collapsed;
    FileUploadMenu.Visibility = !_atThisComputer ? Visibility.Visible : Visibility.Collapsed;
    FileRenameMenu.Visibility = selected && !selectedDrive ? Visibility.Visible : Visibility.Collapsed;
    FileDeleteMenu.Visibility = selected && !selectedDrive ? Visibility.Visible : Visibility.Collapsed;
    FilePropertiesMenu.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
    FileNewFolderMenu.Visibility = !_atThisComputer ? Visibility.Visible : Visibility.Collapsed;
  }

  private void FileGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
  {
    DependencyObject? source = e.OriginalSource as DependencyObject;
    ListBoxItem? item = FindVisualParent<ListBoxItem>(source);
    if (item is null) FileGrid.SelectedItems.Clear();
    else if (!item.IsSelected)
    {
      FileGrid.SelectedItems.Clear();
      item.IsSelected = true;
    }
  }

  private void FileGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
    _fileDragStart = e.GetPosition(FileGrid);

  private void FileGrid_PreviewMouseMove(object sender, MouseEventArgs e)
  {
    if (e.LeftButton != MouseButtonState.Pressed) return;
    Point current = e.GetPosition(FileGrid);
    if (Math.Abs(current.X - _fileDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
        Math.Abs(current.Y - _fileDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
    var selected = FileGrid.SelectedItems.Cast<FileItemView>().Where(x => !x.IsDirectory).ToList();
    if (selected.Count == 0) return;
    try
    {
      DeviceView device = SelectedDevice();
      var data = new VirtualDataObject();
      data.PreferredDropEffect = DragDropEffects.Copy;
      data.SetData(selected.Select(file => new VirtualDataObject.FileDescriptor
      {
        Name = file.Name,
        Length = file.Length,
        ChangeTimeUtc = file.LastWriteTime.ToUniversalTime(),
        StreamContents = destination =>
        {
          var progress = new Progress<FileTransferProgress>(p =>
            Dispatcher.BeginInvoke(() => FileStatus.Text = $"正在拖出 {file.Name} · {FormatTransferProgress(p)}"));
          device.CopyRemoteFileToStreamAsync(file.FullPath, destination, file.Length, progress)
            .GetAwaiter().GetResult();
        }
      }));
      VirtualDataObject.DoDragDrop(FileGrid, data, DragDropEffects.Copy);
    }
    catch (Exception ex) { FileStatus.Text = "拖出下载失败：" + ex.Message; }
  }

  private void FileGrid_DragEnter(object sender, DragEventArgs e)
  {
    e.Effects = !_atThisComputer &&
                DeviceList.SelectedItem is DeviceView { IsOnline: true } &&
                e.Data.GetDataPresent(DataFormats.FileDrop, autoConvert: true)
      ? DragDropEffects.Copy : DragDropEffects.None;
    e.Handled = true;
  }

  private async void FileGrid_Drop(object sender, DragEventArgs e)
  {
    e.Handled = true;
    if (_atThisComputer ||
        !e.Data.GetDataPresent(DataFormats.FileDrop, autoConvert: true))
      return;
    if (e.Data.GetData(DataFormats.FileDrop, autoConvert: true) is not string[] paths ||
        paths.Length == 0)
      return;
    try
    {
      string target = ResolveCurrentRemoteDirectory();
      await UploadLocalPathsAsync(paths, target, confirmOverwrite: true);
      await RefreshFilesAsync(addHistory: false);
    }
    catch (Exception ex) { FileStatus.Text = "拖入上传失败：" + ex.Message; }
  }

  private string ResolveUploadTargetDirectory() =>
    FileGrid.SelectedItem is FileItemView { IsDirectory: true } folder
      ? folder.FullPath : ResolveCurrentRemoteDirectory();

  private string ResolveCurrentRemoteDirectory()
  {
    string path = PathBox.Text.Trim();
    if (path.Length == 0 || string.Equals(path, "此电脑", StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException("请先进入B端的一个磁盘目录。");
    string? root = Path.GetPathRoot(path);
    return string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
      ? path
      : path.TrimEnd('\\');
  }

  private async Task UploadLocalPathsAsync(
    IEnumerable<string> paths,
    string remoteDirectory,
    bool confirmOverwrite = false)
  {
    foreach (string path in paths)
    {
      string name = Path.GetFileName(path.TrimEnd(
        Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar));
      if (name.Length == 0) continue;
      if (confirmOverwrite &&
          IsCurrentDirectory(remoteDirectory) &&
          FileItems.Any(item =>
            string.Equals(item.Name, name, StringComparison.CurrentCultureIgnoreCase)) &&
          !ConfirmationWindow.Show(
            this,
            "同名项目",
            $"当前目录已存在“{name}”，是否继续上传并覆盖同名文件？"))
        continue;

      if (File.Exists(path))
      {
        string target = Path.Combine(remoteDirectory, name);
        await StartTransferAsync("上传", name, async (task, token) =>
          await SelectedDevice().UploadFileAsync(path, target, task.Progress, token));
      }
      else if (Directory.Exists(path))
      {
        string target = Path.Combine(remoteDirectory, name);
        await UploadLocalDirectoryAsync(path, target, CancellationToken.None);
      }
    }
  }

  private bool IsCurrentDirectory(string remoteDirectory) =>
    string.Equals(
      remoteDirectory.TrimEnd('\\'),
      ResolveCurrentRemoteDirectory().TrimEnd('\\'),
      StringComparison.OrdinalIgnoreCase);

  private async Task UploadLocalDirectoryAsync(string localDirectory, string remoteDirectory, CancellationToken token)
  {
    await RequestAsync(MessageType.CreateDirectoryRequest, new PathPayload(remoteDirectory));
    foreach (string directory in Directory.EnumerateDirectories(localDirectory))
      await UploadLocalDirectoryAsync(directory, Path.Combine(remoteDirectory, Path.GetFileName(directory)), token);
    foreach (string file in Directory.EnumerateFiles(localDirectory))
    {
      string target = Path.Combine(remoteDirectory, Path.GetFileName(file));
      await StartTransferAsync("上传", Path.GetFileName(file), async (task, ct) =>
        await SelectedDevice().UploadFileAsync(file, target, task.Progress, ct));
    }
  }

  private async Task DownloadRemoteDirectoryAsync(
    string remoteDirectory, string localDirectory, TransferTaskView task, CancellationToken token)
  {
    Directory.CreateDirectory(localDirectory);
    var response = await RequestAsync(MessageType.DirectoryRequest, new PathPayload(remoteDirectory));
    var payload = ReadPayload<DirectoryResponsePayload>(response, MessageType.DirectoryResponse);
    foreach (DirectoryItemPayload item in payload.Items)
    {
      token.ThrowIfCancellationRequested();
      string local = Path.Combine(localDirectory, item.Name);
      if (item.IsDirectory) await DownloadRemoteDirectoryAsync(item.FullPath, local, task, token);
      else await SelectedDevice().DownloadFileAsync(item.FullPath, local, item.Length, task.Progress, token);
    }
  }

  private async Task StartTransferAsync(
    string direction, string displayName, Func<TransferTaskView, CancellationToken, Task> action)
  {
    var task = new TransferTaskView(direction, displayName);
    Transfers.Add(task);
    try
    {
      await action(task, task.Token);
      task.MarkCompleted();
      FileStatus.Text = $"{direction}完成：{displayName}";
    }
    catch (OperationCanceledException)
    {
      task.MarkCanceled();
      FileStatus.Text = $"{direction}已取消：{displayName}";
    }
    catch (Exception ex)
    {
      task.MarkFailed(ex.Message);
      FileStatus.Text = $"{direction}失败：{ex.Message}";
    }
  }

  private static string FormatTransferProgress(FileTransferProgress progress)
  {
    double percent = progress.Total <= 0 ? 100 : progress.Transferred * 100d / progress.Total;
    return $"{percent:F0}% · {FileItemView.FormatSize((long)progress.BytesPerSecond)}/s";
  }

  private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
  {
    while (child is not null)
    {
      if (child is T target) return target;
      child = VisualTreeHelper.GetParent(child);
    }
    return null;
  }

  private void StartView_Click(object sender, RoutedEventArgs e) => OpenDesktopTab_Click(sender, e);
  private async void ProcessList_Click(object sender, RoutedEventArgs e) => await RefreshProcessesAsync();
  private async Task RefreshProcessesAsync()
  {
    if (_processRefreshInFlight || MainTabs.SelectedIndex != 4) return;
    if (DeviceList.SelectedItem is not DeviceView device || !device.IsOnline)
    {
      Processes.Clear();
      SetStatus("请先选择一台在线设备。");
      return;
    }
    _processRefreshInFlight = true;
    try
    {
      var response = await device.RequestAsync(
        MessageType.ProcessListRequest,
        new ProcessListRequestPayload([]),
        30);
      var latest = ReadPayload<List<ProcessInfoPayload>>(response, MessageType.ProcessListResponse);
      var incoming = latest.ToDictionary(x => x.Id);
      var existing = Processes.ToDictionary(x => x.Id);

      foreach (ProcessInfoPayload payload in latest)
      {
        if (existing.TryGetValue(payload.Id, out ProcessInfoView? item)) item.Update(payload);
        else Processes.Add(ProcessInfoView.FromPayload(payload));
      }
      for (int index = Processes.Count - 1; index >= 0; index--)
        if (!incoming.ContainsKey(Processes[index].Id)) Processes.RemoveAt(index);
      _processIconRequested.RemoveWhere(id => !incoming.ContainsKey(id));

      ProcessesView.Refresh();
      SetStatus($"进程实时刷新 · {Processes.Count} 个 · {DateTime.Now:HH:mm:ss}");
      if (_processIconCts is { IsCancellationRequested: false } iconCts)
        _ = RefreshProcessIconsAsync(iconCts.Token);
    }
    catch (Exception ex) { SetStatus(ex.Message); }
    finally { _processRefreshInFlight = false; }
  }

  private async Task RefreshProcessIconsAsync(CancellationToken token)
  {
    if (_processIconRefreshInFlight || MainTabs.SelectedIndex != 4 || token.IsCancellationRequested) return;

    List<int> processIds = Processes
      .Where(process => !process.HasRemoteIcon && !_processIconRequested.Contains(process.Id))
      .Select(process => process.Id)
      .Take(8)
      .ToList();
    if (processIds.Count == 0) return;

    _processIconRefreshInFlight = true;
    foreach (int processId in processIds) _processIconRequested.Add(processId);
    try
    {
      RemoteMessage response = await RequestAsync(
        MessageType.ProcessIconsRequest,
        new ProcessIconsRequestPayload(processIds),
        10,
        token);
      token.ThrowIfCancellationRequested();
      List<ProcessIconPayload> icons = ReadPayload<List<ProcessIconPayload>>(
        response,
        MessageType.ProcessIconsResponse);
      var processById = Processes.ToDictionary(process => process.Id);
      foreach (ProcessIconPayload icon in icons)
        if (processById.TryGetValue(icon.Id, out ProcessInfoView? process))
          process.ApplyIcon(icon.IconBase64Png);
    }
    catch (OperationCanceledException)
    {
      foreach (int processId in processIds) _processIconRequested.Remove(processId);
    }
    catch
    {
      foreach (int processId in processIds) _processIconRequested.Remove(processId);
    }
    finally
    {
      _processIconRefreshInFlight = false;
    }
  }

  private bool FilterProcessItem(object item)
  {
    if (item is not ProcessInfoView process) return false;
    string query = ProcessSearch?.Text?.Trim() ?? string.Empty;
    return query.Length == 0 ||
      process.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
      process.Id.ToString().Contains(query, StringComparison.Ordinal) ||
      process.MainWindowTitle.Contains(query, StringComparison.CurrentCultureIgnoreCase);
  }

  private void ProcessSearch_TextChanged(object sender, TextChangedEventArgs e) => ProcessesView.Refresh();

  private void ProcessGroup_Loaded(object sender, RoutedEventArgs e)
  {
    if (sender is not Expander expander ||
        expander.DataContext is not CollectionViewGroup group)
      return;
    string name = Convert.ToString(group.Name) ?? string.Empty;
    expander.IsExpanded = !_processGroupExpansion.TryGetValue(name, out bool expanded)
      || expanded;
  }

  private void ProcessGroup_Expanded(object sender, RoutedEventArgs e)
  {
    if (e.OriginalSource != sender ||
        sender is not Expander expander ||
        expander.DataContext is not CollectionViewGroup group)
      return;
    _processGroupExpansion[Convert.ToString(group.Name) ?? string.Empty] = true;
  }

  private void ProcessGroup_Collapsed(object sender, RoutedEventArgs e)
  {
    if (e.OriginalSource != sender ||
        sender is not Expander expander ||
        expander.DataContext is not CollectionViewGroup group)
      return;
    _processGroupExpansion[Convert.ToString(group.Name) ?? string.Empty] = false;
  }

  private async void ProcessKill_Click(object sender, RoutedEventArgs e)
  {
    try { if (ProcessGrid.SelectedItem is not ProcessInfoView p) return; if (MessageBox.Show($"结束进程 {p.Name} ({p.Id})？", "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return; await RequestAsync(MessageType.ProcessKillRequest, p.Id); await RefreshProcessesAsync(); } catch (Exception ex) { SetStatus(ex.Message); }
  }
  private void CopyPid_Click(object sender, RoutedEventArgs e) { if (ProcessGrid.SelectedItem is ProcessInfoView p) Clipboard.SetText(p.Id.ToString()); }

  private async void ServiceList_Click(object sender, RoutedEventArgs e) => await RefreshServicesAsync();
  private async void ServiceControl_Click(object sender, RoutedEventArgs e)
  {
    if (ServiceGrid.SelectedItem is not ServiceInfoView service ||
        sender is not MenuItem menu ||
        !Enum.TryParse(menu.Tag?.ToString(), out ServiceControlAction action))
      return;
    if (action is ServiceControlAction.Stop or ServiceControlAction.Restart)
    {
      string verb = action == ServiceControlAction.Stop ? "停止" : "重启";
      if (MessageBox.Show(
            $"{verb}服务“{service.DisplayName}”？",
            "确认服务操作",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) != MessageBoxResult.Yes)
        return;
    }
    await ExecuteServiceControlAsync(
      new ServiceControlPayload(service.ServiceName, action));
  }

  private async void ServiceStartType_Click(object sender, RoutedEventArgs e)
  {
    if (ServiceGrid.SelectedItem is not ServiceInfoView service ||
        sender is not MenuItem menu ||
        string.IsNullOrWhiteSpace(menu.Tag?.ToString()))
      return;
    await ExecuteServiceControlAsync(
      new ServiceControlPayload(
        service.ServiceName,
        ServiceControlAction.SetStartType,
        menu.Tag!.ToString()!));
  }

  private async Task ExecuteServiceControlAsync(ServiceControlPayload request)
  {
    try
    {
      SetStatus($"正在操作服务 {request.ServiceName}…");
      RemoteMessage response = await RequestAsync(
        MessageType.ServiceControlRequest,
        request,
        45);
      OperationResultPayload result = ReadPayload<OperationResultPayload>(
        response,
        MessageType.ServiceControlResponse);
      SetStatus(result.Message);
      await RefreshServicesAsync();
    }
    catch (Exception ex)
    {
      SetStatus("服务操作失败：" + ex.Message);
      MessageBox.Show(
        "服务操作失败：\n" + ex.Message +
        "\n\n修改Windows服务通常需要在B端使用管理员权限运行程序。",
        "服务管理",
        MessageBoxButton.OK,
        MessageBoxImage.Error);
    }
  }

  private async void ServiceProperties_Click(object sender, RoutedEventArgs e)
  {
    if (ServiceGrid.SelectedItem is not ServiceInfoView service) return;
    try
    {
      RemoteMessage response = await RequestAsync(
        MessageType.ServiceDetailsRequest,
        service.ServiceName,
        30);
      ServiceDetailsPayload details = ReadPayload<ServiceDetailsPayload>(
        response,
        MessageType.ServiceDetailsResponse);
      new ServiceDetailsWindow(details) { Owner = this }.ShowDialog();
    }
    catch (Exception ex) { SetStatus("读取服务属性失败：" + ex.Message); }
  }

  private void ServiceGrid_PreviewMouseRightButtonDown(
    object sender,
    MouseButtonEventArgs e)
  {
    DependencyObject? current = e.OriginalSource as DependencyObject;
    while (current is not null && current is not DataGridRow)
      current = VisualTreeHelper.GetParent(current);
    if (current is DataGridRow row)
    {
      row.IsSelected = true;
      row.Focus();
    }
  }

  private async Task RefreshServicesAsync()
  {
    if (_serviceRefreshInFlight || MainTabs.SelectedIndex != 5) return;
    _serviceRefreshInFlight = true;
    try
    {
      var response = await RequestAsync(MessageType.ServiceListRequest, new { }, 30);
      var latest = ReadPayload<List<ServiceInfoPayload>>(response, MessageType.ServiceListResponse);
      var incoming = latest.ToDictionary(x => x.ServiceName, StringComparer.OrdinalIgnoreCase);
      var existing = Services.ToDictionary(x => x.ServiceName, StringComparer.OrdinalIgnoreCase);

      foreach (ServiceInfoPayload payload in latest)
      {
        if (existing.TryGetValue(payload.ServiceName, out ServiceInfoView? item)) item.Update(payload);
        else Services.Add(ServiceInfoView.FromPayload(payload));
      }
      for (int index = Services.Count - 1; index >= 0; index--)
        if (!incoming.ContainsKey(Services[index].ServiceName)) Services.RemoveAt(index);

      ServicesView.Refresh();
      SetStatus($"服务实时刷新 · {Services.Count} 个 · {DateTime.Now:HH:mm:ss}");
    }
    catch (Exception ex) { SetStatus(ex.Message); }
    finally { _serviceRefreshInFlight = false; }
  }

  private bool FilterServiceItem(object item)
  {
    if (item is not ServiceInfoView service) return false;
    string query = ServiceSearch?.Text?.Trim() ?? string.Empty;
    return query.Length == 0 ||
      service.ServiceName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
      service.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase);
  }

  private void ServiceSearch_TextChanged(object sender, TextChangedEventArgs e) => ServicesView.Refresh();

  private DeviceView RequireSelectedRegistryDevice()
  {
    if (DeviceList.SelectedItem is DeviceView { IsOnline: true } device)
      return device;
    throw new InvalidOperationException("请先选择一台在线设备。");
  }

  private void ClearRegistryUi(string? status = null)
  {
    _registryLoadCts?.Cancel();
    _registryLoadCts?.Dispose();
    _registryLoadCts = null;
    _registryLoadedDeviceId = null;
    RegistryRoots.Clear();
    RegistryValues.Clear();
    RegistryPathBox.Text = string.Empty;
    if (!string.IsNullOrWhiteSpace(status)) SetStatus(status);
  }

  private async Task LoadRegistryRootsFromSelectedDeviceAsync(bool force)
  {
    if (DeviceList.SelectedItem is not DeviceView { IsOnline: true } device)
    {
      ClearRegistryUi("请选择一台在线设备。");
      return;
    }
    if (!force && RegistryRoots.Count > 0 &&
        string.Equals(_registryLoadedDeviceId, device.DeviceId, StringComparison.OrdinalIgnoreCase))
      return;

    _registryLoadCts?.Cancel();
    _registryLoadCts?.Dispose();
    _registryLoadCts = new CancellationTokenSource();
    CancellationToken token = _registryLoadCts.Token;
    string deviceId = device.DeviceId;
    string previousPath = RegistryPathBox.Text.Trim();
    var roots = new List<RegistryKeyView>();
    var rootSpecs = new (string Hive, string DisplayName)[]
    {
      ("HKCU", "HKEY_CURRENT_USER"),
      ("HKLM", "HKEY_LOCAL_MACHINE"),
      ("HKCR", "HKEY_CLASSES_ROOT"),
      ("HKU", "HKEY_USERS"),
      ("HKCC", "HKEY_CURRENT_CONFIG")
    };

    try
    {
      for (int attempt = 0; attempt < 40 && !device.IsRegistryConnected; attempt++)
        await Task.Delay(50, token);
      if (!device.IsRegistryConnected)
        throw new InvalidOperationException("B端注册表通道尚未就绪，请点击刷新重试。");

      foreach ((string hive, string displayName) in rootSpecs)
      {
        RemoteMessage response = await device.RegistryRequestAsync(
          MessageType.RegistryReadRequest,
          new RegistryReadPayload(hive, string.Empty, SelectedRegistryView),
          15,
          token);
        RegistryReadResponsePayload payload = ReadPayload<RegistryReadResponsePayload>(
          response,
          MessageType.RegistryReadResponse);
        var root = RegistryKeyView.CreateRoot(hive, displayName);
        root.Children.Clear();
        foreach (string child in payload.SubKeys)
          root.Children.Add(RegistryKeyView.CreateChild(root, child));
        root.IsLoaded = true;
        roots.Add(root);
      }

      token.ThrowIfCancellationRequested();
      if (DeviceList.SelectedItem is not DeviceView selected ||
          !selected.IsOnline ||
          !string.Equals(selected.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
        return;

      RegistryRoots.Clear();
      foreach (RegistryKeyView root in roots) RegistryRoots.Add(root);
      RegistryValues.Clear();
      RegistryPathBox.Text = string.Empty;
      _registryLoadedDeviceId = deviceId;
      SetStatus($"已从 {device.DisplayTitle} 实时读取注册表根项。");

      if (!string.IsNullOrWhiteSpace(previousPath))
        await NavigateRegistryPathAsync(previousPath);
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
      if (!token.IsCancellationRequested)
      {
        ClearRegistryUi();
        SetStatus("注册表读取失败：" + ex.Message);
      }
    }
  }

  private async void RegistryTreeItem_Expanded(object sender, RoutedEventArgs e)
  {
    if (e.OriginalSource is TreeViewItem { DataContext: RegistryKeyView node } && !node.IsPlaceholder)
      await LoadRegistryNodeAsync(node, updateValues: false);
  }

  private async void RegistryTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
  {
    if (e.NewValue is RegistryKeyView { IsPlaceholder: false } node)
      await LoadRegistryNodeAsync(node, updateValues: true);
  }

  private async Task LoadRegistryNodeAsync(RegistryKeyView node, bool updateValues)
  {
    if (node.IsLoading) return;
    node.IsLoading = true;
    try
    {
      DeviceView device = RequireSelectedRegistryDevice();
      var response = await device.RegistryRequestAsync(
        MessageType.RegistryReadRequest,
        new RegistryReadPayload(node.Hive, node.SubKey, SelectedRegistryView),
        30);
      RegistryReadResponsePayload payload = ReadPayload<RegistryReadResponsePayload>(
        response,
        MessageType.RegistryReadResponse);

      if (!node.IsLoaded)
      {
        node.Children.Clear();
        foreach (string child in payload.SubKeys)
          node.Children.Add(RegistryKeyView.CreateChild(node, child));
        node.IsLoaded = true;
      }

      if (updateValues)
      {
        RegistryPathBox.Text = payload.KeyPath;
        RegistryValues.Clear();
        foreach (RegistryValuePayload value in payload.Values) RegistryValues.Add(value);
        SetStatus($"注册表 · {payload.KeyPath} · {payload.SubKeys.Count} 个子项 · {payload.Values.Count} 个值");
        _ = device.RegistryRequestAsync(
          MessageType.RegistryWatchRequest,
          new RegistryWatchPayload(node.Hive, node.SubKey, SelectedRegistryView),
          10);
      }
    }
    catch (Exception ex) { SetStatus("注册表读取失败：" + ex.Message); }
    finally { node.IsLoading = false; }
  }

  private async Task NavigateRegistryPathAsync(string path, bool subscribe = true)
  {
    string normalized = path.Trim().Trim('\\');
    if (normalized.Length == 0) normalized = "HKCU";
    string[] parts = normalized.Split('\\', 2);
    string hive = parts[0].ToUpperInvariant() switch
    {
      "HKEY_CURRENT_USER" => "HKCU",
      "HKEY_LOCAL_MACHINE" => "HKLM",
      "HKEY_CLASSES_ROOT" => "HKCR",
      "HKEY_USERS" => "HKU",
      "HKEY_CURRENT_CONFIG" => "HKCC",
      "HKCU" or "HKLM" or "HKCR" or "HKU" or "HKCC" => parts[0].ToUpperInvariant(),
      _ => throw new InvalidOperationException("注册表路径必须以 HKCU、HKLM、HKCR、HKU 或 HKCC 开头。")
    };
    string subKey = parts.Length > 1 ? parts[1] : string.Empty;
    DeviceView device = RequireSelectedRegistryDevice();
    var response = await device.RegistryRequestAsync(
      MessageType.RegistryReadRequest,
      new RegistryReadPayload(hive, subKey, SelectedRegistryView),
      30);
    RegistryReadResponsePayload payload = ReadPayload<RegistryReadResponsePayload>(response, MessageType.RegistryReadResponse);
    RegistryPathBox.Text = payload.KeyPath;
    RegistryValues.Clear();
    foreach (RegistryValuePayload value in payload.Values) RegistryValues.Add(value);
    SetStatus($"注册表 · {payload.KeyPath} · {payload.SubKeys.Count} 个子项 · {payload.Values.Count} 个值");
    if (subscribe)
    {
      _ = device.RegistryRequestAsync(
        MessageType.RegistryWatchRequest,
        new RegistryWatchPayload(hive, subKey, SelectedRegistryView),
        10);
    }
  }

  private async void RegistryGo_Click(object sender, RoutedEventArgs e)
  {
    try { await NavigateRegistryPathAsync(RegistryPathBox.Text); }
    catch (Exception ex) { SetStatus("注册表读取失败：" + ex.Message); }
  }

  private async void RegistryParent_Click(object sender, RoutedEventArgs e)
  {
    string path = RegistryPathBox.Text.Trim().TrimEnd('\\');
    int separator = path.LastIndexOf('\\');
    if (separator < 0) return;
    try { await NavigateRegistryPathAsync(path[..separator]); }
    catch (Exception ex) { SetStatus("注册表读取失败：" + ex.Message); }
  }

  private void RegistryPathBox_KeyDown(object sender, KeyEventArgs e)
  {
    if (e.Key != Key.Enter) return;
    e.Handled = true;
    RegistryGo_Click(sender, new RoutedEventArgs());
  }

  private RegistryViewMode SelectedRegistryView =>
    RegistryViewBox?.SelectedIndex == 1
      ? RegistryViewMode.Registry32
      : RegistryViewMode.Registry64;

  private async void RegistryViewBox_SelectionChanged(
    object sender,
    SelectionChangedEventArgs e)
  {
    if (!IsLoaded || MainTabs.SelectedIndex != 6) return;
    try
    {
      await LoadRegistryRootsFromSelectedDeviceAsync(force: true);
    }
    catch (Exception ex) { SetStatus("切换注册表视图失败：" + ex.Message); }
  }

  private RegistryKeyView? SelectedRegistryNode =>
    RegistryTree.SelectedItem as RegistryKeyView;

  private async Task<OperationResultPayload> MutateRegistryAsync(
    RegistryMutationPayload mutation)
  {
    RemoteMessage response = await SelectedDevice().RegistryRequestAsync(
      MessageType.RegistryMutationRequest,
      mutation,
      30);
    return ReadPayload<OperationResultPayload>(
      response,
      MessageType.RegistryMutationResponse);
  }

  private async Task ReloadRegistryNodeAsync(
    RegistryKeyView node,
    bool updateValues)
  {
    node.IsLoaded = false;
    node.Children.Clear();
    node.Children.Add(RegistryKeyView.CreatePlaceholderNode());
    await LoadRegistryNodeAsync(node, updateValues);
  }

  private async void RegistryCreateKey_Click(object sender, RoutedEventArgs e)
  {
    RegistryKeyView? node = SelectedRegistryNode;
    if (node is null || node.IsPlaceholder) return;
    string? name = PromptWindow.ShowDialog(
      this,
      "新建注册表项",
      $"在 {BuildRegistryPath(node.Hive, node.SubKey)} 下创建新项：",
      "新项");
    if (name is null) return;
    try
    {
      OperationResultPayload result = await MutateRegistryAsync(new RegistryMutationPayload(
        RegistryMutationKind.CreateKey,
        node.Hive,
        node.SubKey,
        name.Trim(),
        View: SelectedRegistryView));
      if (!result.Success) throw new InvalidOperationException(result.Message);
      await ReloadRegistryNodeAsync(node, updateValues: true);
      SetStatus(result.Message);
    }
    catch (Exception ex) { SetStatus("创建注册表项失败：" + ex.Message); }
  }

  private async void RegistryRenameKey_Click(object sender, RoutedEventArgs e)
  {
    RegistryKeyView? node = SelectedRegistryNode;
    if (node is null || node.IsPlaceholder || node.Parent is null)
    {
      SetStatus("注册表根节点不能重命名。");
      return;
    }
    string? name = PromptWindow.ShowDialog(
      this,
      "重命名注册表项",
      "请输入新的项名称：",
      node.DisplayName);
    if (name is null) return;
    try
    {
      OperationResultPayload result = await MutateRegistryAsync(new RegistryMutationPayload(
        RegistryMutationKind.RenameKey,
        node.Hive,
        node.Parent.SubKey,
        node.DisplayName,
        name.Trim(),
        View: SelectedRegistryView));
      if (!result.Success) throw new InvalidOperationException(result.Message);
      await ReloadRegistryNodeAsync(node.Parent, updateValues: true);
      await NavigateRegistryPathAsync(
        BuildRegistryPath(
          node.Hive,
          string.IsNullOrWhiteSpace(node.Parent.SubKey)
            ? name.Trim()
            : node.Parent.SubKey + "\\" + name.Trim()));
      SetStatus(result.Message);
    }
    catch (Exception ex) { SetStatus("重命名注册表项失败：" + ex.Message); }
  }

  private async void RegistryDeleteKey_Click(object sender, RoutedEventArgs e)
  {
    RegistryKeyView? node = SelectedRegistryNode;
    if (node is null || node.IsPlaceholder || node.Parent is null)
    {
      SetStatus("注册表根节点不能删除。");
      return;
    }
    if (MessageBox.Show(
          $"确认删除注册表项及其全部子项？\n{BuildRegistryPath(node.Hive, node.SubKey)}",
          "删除注册表项",
          MessageBoxButton.YesNo,
          MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
    try
    {
      RegistryKeyView parent = node.Parent;
      OperationResultPayload result = await MutateRegistryAsync(new RegistryMutationPayload(
        RegistryMutationKind.DeleteKey,
        node.Hive,
        parent.SubKey,
        node.DisplayName,
        View: SelectedRegistryView));
      if (!result.Success) throw new InvalidOperationException(result.Message);
      await ReloadRegistryNodeAsync(parent, updateValues: true);
      await NavigateRegistryPathAsync(BuildRegistryPath(parent.Hive, parent.SubKey));
      SetStatus(result.Message);
    }
    catch (Exception ex) { SetStatus("删除注册表项失败：" + ex.Message); }
  }

  private async void RegistryCreateValue_Click(object sender, RoutedEventArgs e)
  {
    try
    {
      string kind = Convert.ToString((sender as FrameworkElement)?.Tag) ?? "String";
      var editor = new RegistryValueEditorWindow(kind) { Owner = this };
      if (editor.ShowDialog() != true || editor.Result is null) return;
      (string hive, string subKey) = ParseCurrentRegistryPath();
      RegistryValueEditResult edit = editor.Result;
      OperationResultPayload result = await MutateRegistryAsync(
        CreateRegistryValueMutation(
          RegistryMutationKind.CreateValue,
          hive,
          subKey,
          edit));
      if (!result.Success) throw new InvalidOperationException(result.Message);
      await NavigateRegistryPathAsync(BuildRegistryPath(hive, subKey));
      SetStatus(result.Message);
    }
    catch (Exception ex) { SetStatus("创建注册表值失败：" + ex.Message); }
  }

  private async void RegistryModifyValue_Click(object sender, RoutedEventArgs e)
  {
    if (RegistryValueGrid.SelectedItem is not RegistryValuePayload value) return;
    try
    {
      var editor = new RegistryValueEditorWindow(value.Type, value)
      {
        Owner = this
      };
      if (editor.ShowDialog() != true || editor.Result is null) return;
      (string hive, string subKey) = ParseCurrentRegistryPath();
      RegistryValueEditResult edit = editor.Result with
      {
        Name = value.RawName ?? value.Name
      };
      OperationResultPayload result = await MutateRegistryAsync(
        CreateRegistryValueMutation(
          RegistryMutationKind.SetValue,
          hive,
          subKey,
          edit));
      if (!result.Success) throw new InvalidOperationException(result.Message);
      await NavigateRegistryPathAsync(BuildRegistryPath(hive, subKey));
      SetStatus(result.Message);
    }
    catch (Exception ex) { SetStatus("修改注册表值失败：" + ex.Message); }
  }

  private async void RegistryRenameValue_Click(object sender, RoutedEventArgs e)
  {
    if (RegistryValueGrid.SelectedItem is not RegistryValuePayload value) return;
    if (string.Equals(value.Name, "(默认)", StringComparison.Ordinal))
    {
      SetStatus("默认值不能重命名。");
      return;
    }
    string? name = PromptWindow.ShowDialog(
      this,
      "重命名注册表值",
      "请输入新的数值名称：",
      value.Name);
    if (name is null) return;
    try
    {
      (string hive, string subKey) = ParseCurrentRegistryPath();
      OperationResultPayload result = await MutateRegistryAsync(new RegistryMutationPayload(
        RegistryMutationKind.RenameValue,
        hive,
        subKey,
        value.RawName ?? value.Name,
        name.Trim(),
        View: SelectedRegistryView));
      if (!result.Success) throw new InvalidOperationException(result.Message);
      await NavigateRegistryPathAsync(BuildRegistryPath(hive, subKey));
      SetStatus(result.Message);
    }
    catch (Exception ex) { SetStatus("重命名注册表值失败：" + ex.Message); }
  }

  private async void RegistryDeleteValue_Click(object sender, RoutedEventArgs e)
  {
    if (RegistryValueGrid.SelectedItem is not RegistryValuePayload value) return;
    if (MessageBox.Show(
          $"确认删除注册表值“{value.Name}”？",
          "删除注册表值",
          MessageBoxButton.YesNo,
          MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
    try
    {
      (string hive, string subKey) = ParseCurrentRegistryPath();
      OperationResultPayload result = await MutateRegistryAsync(new RegistryMutationPayload(
        RegistryMutationKind.DeleteValue,
        hive,
        subKey,
        value.RawName ?? value.Name,
        View: SelectedRegistryView));
      if (!result.Success) throw new InvalidOperationException(result.Message);
      await NavigateRegistryPathAsync(BuildRegistryPath(hive, subKey));
      SetStatus(result.Message);
    }
    catch (Exception ex) { SetStatus("删除注册表值失败：" + ex.Message); }
  }

  private async void RegistryRefresh_Click(object sender, RoutedEventArgs e)
  {
    try
    {
      await LoadRegistryRootsFromSelectedDeviceAsync(force: true);
    }
    catch (Exception ex) { SetStatus("注册表刷新失败：" + ex.Message); }
  }

  private void RegistryValueGrid_MouseDoubleClick(
    object sender,
    MouseButtonEventArgs e)
  {
    if (RegistryValueGrid.SelectedItem is RegistryValuePayload)
      RegistryModifyValue_Click(sender, new RoutedEventArgs());
  }

  private void RegistryTree_PreviewMouseRightButtonDown(
    object sender,
    MouseButtonEventArgs e)
  {
    TreeViewItem? item = FindVisualParent<TreeViewItem>(
      e.OriginalSource as DependencyObject);
    if (item is not null) item.IsSelected = true;
  }

  private void RegistryValueGrid_PreviewMouseRightButtonDown(
    object sender,
    MouseButtonEventArgs e)
  {
    DataGridRow? row = FindVisualParent<DataGridRow>(
      e.OriginalSource as DependencyObject);
    if (row is not null) row.IsSelected = true;
    else RegistryValueGrid.UnselectAll();
  }

  private (string Hive, string SubKey) ParseCurrentRegistryPath()
  {
    string normalized = NormalizeRegistryPath(RegistryPathBox.Text);
    string[] parts = normalized.Split('\\', 2);
    return (
      parts[0].ToUpperInvariant(),
      parts.Length > 1 ? parts[1] : string.Empty);
  }

  private RegistryMutationPayload CreateRegistryValueMutation(
    RegistryMutationKind kind,
    string hive,
    string subKey,
    RegistryValueEditResult edit) =>
    new(
      kind,
      hive,
      subKey,
      edit.Name,
      ValueKind: edit.Kind,
      StringValue: edit.StringValue,
      MultiStringValue: edit.MultiStringValue,
      BinaryValue: edit.BinaryValue,
      IntegerValue: edit.IntegerValue,
      View: SelectedRegistryView);

  protected override void OnClosed(EventArgs e)
  {
    foreach (DesktopControlWindow window in _desktopWindows.Values.ToArray())
      try { window.Close(); } catch { }
    _desktopWindows.Clear();
    DisposeTraySupport();
    _processTimer.Stop();
    _serviceTimer.Stop();
    _processIconCts?.Cancel();
    _processIconCts?.Dispose();
    DisposeDedicatedUi();
    DisposeCodexBridge();
    StopServer_Click(this, new RoutedEventArgs());
    base.OnClosed(e);
  }
}

public sealed class TransferTaskView : INotifyPropertyChanged
{
  private readonly CancellationTokenSource _cts = new();
  private double _percent;
  private string _statusText;

  public string Direction { get; }
  public string DisplayName { get; }
  public double Percent { get => _percent; private set { _percent = value; Changed(nameof(Percent)); } }
  public string StatusText { get => _statusText; private set { _statusText = value; Changed(nameof(StatusText)); } }
  public CancellationToken Token => _cts.Token;
  public IProgress<FileTransferProgress> Progress { get; }
  public ICommand CancelCommand { get; }
  public event PropertyChangedEventHandler? PropertyChanged;

  public TransferTaskView(string direction, string displayName)
  {
    Direction = direction;
    DisplayName = displayName;
    _statusText = direction + "准备中";
    Progress = new Progress<FileTransferProgress>(p =>
    {
      Percent = p.Total <= 0 ? 100 : Math.Clamp(p.Transferred * 100d / p.Total, 0, 100);
      StatusText = $"{Percent:F0}% · {FileItemView.FormatSize((long)p.BytesPerSecond)}/s";
    });
    CancelCommand = new RelayCommand(() => _cts.Cancel());
  }

  public void MarkCompleted() { Percent = 100; StatusText = "已完成"; }
  public void MarkCanceled() => StatusText = "已取消";
  public void MarkFailed(string message) => StatusText = "失败：" + message;
  private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Action execute) : ICommand
{
  public event EventHandler? CanExecuteChanged { add { } remove { } }
  public bool CanExecute(object? parameter) => true;
  public void Execute(object? parameter) => execute();
}

public enum FileKind { Folder, Image, Video, Audio, Text, Other }

public sealed class ProcessInfoView : INotifyPropertyChanged
{
  private string _name = "";
  private string _mainWindowTitle = "";
  private double _cpuPercent;
  private long _workingSetMb;
  private ImageSource _icon = CreateFallbackIcon();
  private bool _isApplication;

  public int Id { get; init; }
  public string Name { get => _name; private set { _name = value; Changed(nameof(Name)); } }
  public string MainWindowTitle { get => _mainWindowTitle; private set { _mainWindowTitle = value; Changed(nameof(MainWindowTitle)); } }
  public double CpuPercent { get => _cpuPercent; private set { _cpuPercent = value; Changed(nameof(CpuPercent)); Changed(nameof(CpuText)); } }
  public long WorkingSetMb { get => _workingSetMb; private set { _workingSetMb = value; Changed(nameof(WorkingSetMb)); Changed(nameof(MemoryText)); } }
  public ImageSource Icon { get => _icon; private set { _icon = value; Changed(nameof(Icon)); } }
  public bool HasRemoteIcon { get; private set; }
  public bool IsApplication
  {
    get => _isApplication;
    private set
    {
      if (_isApplication == value) return;
      _isApplication = value;
      Changed(nameof(IsApplication));
      Changed(nameof(Category));
      Changed(nameof(CategoryOrder));
    }
  }
  public string Category => IsApplication ? "应用" : "后台进程";
  public int CategoryOrder => IsApplication ? 0 : 1;
  public string CpuText => CpuPercent.ToString("F1") + "%";
  public string MemoryText => WorkingSetMb + " MB";
  public event PropertyChangedEventHandler? PropertyChanged;

  public static ProcessInfoView FromPayload(ProcessInfoPayload payload)
  {
    var item = new ProcessInfoView { Id = payload.Id };
    item.Update(payload);
    return item;
  }

  public void Update(ProcessInfoPayload payload)
  {
    Name = payload.Name;
    MainWindowTitle = payload.MainWindowTitle;
    CpuPercent = payload.CpuPercent;
    WorkingSetMb = payload.WorkingSetMb;
    IsApplication = payload.IsApplication;
    if (!string.IsNullOrWhiteSpace(payload.IconBase64Png))
    {
      Icon = DecodeIcon(payload.IconBase64Png);
      HasRemoteIcon = true;
    }
  }

  public void ApplyIcon(string base64Png)
  {
    if (string.IsNullOrWhiteSpace(base64Png)) return;
    Icon = DecodeIcon(base64Png);
    HasRemoteIcon = true;
  }

  private static ImageSource DecodeIcon(string base64)
  {
    try
    {
      if (string.IsNullOrWhiteSpace(base64)) return CreateFallbackIcon();
      byte[] bytes = Convert.FromBase64String(base64);
      using var ms = new MemoryStream(bytes);
      var img = new BitmapImage();
      img.BeginInit(); img.CacheOption = BitmapCacheOption.OnLoad; img.StreamSource = ms; img.EndInit(); img.Freeze();
      return img;
    }
    catch { return CreateFallbackIcon(); }
  }

  private static ImageSource CreateFallbackIcon()
  {
    double dpi = Application.Current?.MainWindow is null ? 1.0 : VisualTreeHelper.GetDpi(Application.Current.MainWindow).PixelsPerDip;
    var drawing = new FormattedText("▣", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Symbol"), 18, Brushes.SlateGray, dpi);
    var group = new DrawingGroup();
    using (var ctx = group.Open()) ctx.DrawText(drawing, new Point(0, 0));
    return new DrawingImage(group);
  }

  private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class ServiceInfoView : INotifyPropertyChanged
{
  private string _displayName = "";
  private string _status = "";
  private string _startType = "";
  private bool _canStop;
  private bool _canPauseAndContinue;

  public string ServiceName { get; init; } = "";
  public string DisplayName { get => _displayName; private set { _displayName = value; Changed(nameof(DisplayName)); } }
  public string Status { get => _status; private set { _status = value; Changed(nameof(Status)); Changed(nameof(StatusText)); Changed(nameof(StatusBrush)); } }
  public string StartType { get => _startType; private set { _startType = value; Changed(nameof(StartType)); Changed(nameof(StartTypeText)); } }
  public bool CanStop { get => _canStop; private set { _canStop = value; Changed(nameof(CanStop)); } }
  public bool CanPauseAndContinue { get => _canPauseAndContinue; private set { _canPauseAndContinue = value; Changed(nameof(CanPauseAndContinue)); } }
  public string StatusText => Status switch
  {
    "Running" => "运行中",
    "Stopped" => "已停止",
    "StartPending" => "正在启动",
    "StopPending" => "正在停止",
    "Paused" => "已暂停",
    _ => Status
  };
  public string StartTypeText => StartType switch
  {
    "Automatic" => "自动",
    "AutomaticDelayed" => "自动（延迟启动）",
    "Manual" => "手动",
    "Disabled" => "禁用",
    "Boot" => "引导",
    "System" => "系统",
    _ => StartType
  };
  public Brush StatusBrush => Status switch
  {
    "Running" => new SolidColorBrush(Color.FromRgb(34, 197, 94)),
    "StartPending" or "StopPending" => new SolidColorBrush(Color.FromRgb(59, 130, 246)),
    _ => new SolidColorBrush(Color.FromRgb(148, 163, 184))
  };
  public event PropertyChangedEventHandler? PropertyChanged;

  public static ServiceInfoView FromPayload(ServiceInfoPayload payload)
  {
    var item = new ServiceInfoView { ServiceName = payload.ServiceName };
    item.Update(payload);
    return item;
  }

  public void Update(ServiceInfoPayload payload)
  {
    DisplayName = payload.DisplayName;
    Status = payload.Status;
    StartType = payload.StartType;
    CanStop = payload.CanStop;
    CanPauseAndContinue = payload.CanPauseAndContinue;
  }

  private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RegistryKeyView : INotifyPropertyChanged
{
  private bool _isExpanded;

  public string Hive { get; init; } = "";
  public string SubKey { get; init; } = "";
  public string DisplayName { get; init; } = "";
  public RegistryKeyView? Parent { get; init; }
  public bool IsPlaceholder { get; init; }
  public bool IsLoaded { get; set; }
  public bool IsLoading { get; set; }
  public ObservableCollection<RegistryKeyView> Children { get; } = new();
  public bool IsExpanded
  {
    get => _isExpanded;
    set
    {
      if (_isExpanded == value) return;
      _isExpanded = value;
      PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
    }
  }
  public event PropertyChangedEventHandler? PropertyChanged;

  public static RegistryKeyView CreateRoot(string hive, string displayName)
  {
    var root = new RegistryKeyView { Hive = hive, DisplayName = displayName };
    root.Children.Add(CreatePlaceholder());
    return root;
  }

  public static RegistryKeyView CreateChild(RegistryKeyView parent, string name)
  {
    var child = new RegistryKeyView
    {
      Hive = parent.Hive,
      SubKey = string.IsNullOrWhiteSpace(parent.SubKey) ? name : parent.SubKey + "\\" + name,
      DisplayName = name,
      Parent = parent
    };
    child.Children.Add(CreatePlaceholder());
    return child;
  }

  public static RegistryKeyView CreatePlaceholderNode() =>
    new() { DisplayName = "正在加载…", IsPlaceholder = true, IsLoaded = true };

  private static RegistryKeyView CreatePlaceholder() => CreatePlaceholderNode();
}

public sealed class FileItemView
{
  public string Name { get; init; } = "";
  public string FullPath { get; init; } = "";
  public bool IsDirectory { get; init; }
  public bool IsDrive { get; init; }
  public long Length { get; init; }
  public long TotalSize { get; init; }
  public long AvailableFreeSpace { get; init; }
  public string Extension { get; init; } = "";
  public FileKind Kind { get; init; }
  public ImageSource Icon => ShellIconProvider.GetIcon(IsDrive, IsDirectory, Extension);
  public string TypeText => IsDrive ? "磁盘" : Kind switch { FileKind.Folder => "文件夹", FileKind.Image => "图片", FileKind.Video => "视频", FileKind.Audio => "音频", FileKind.Text => "文本", _ => string.IsNullOrWhiteSpace(Extension) ? "文件" : Extension.TrimStart('.').ToUpperInvariant() };
  public string SizeText { get; init; } = "";
  public DateTime LastWriteTime { get; init; }
  public string LastWriteTimeText => LastWriteTime == DateTime.MinValue ? "" : LastWriteTime.ToString("yyyy-MM-dd HH:mm");
  public string SecondaryText => IsDrive
    ? $"{FormatSize(AvailableFreeSpace)} 可用，共 {FormatSize(TotalSize)}"
    : IsDirectory ? "文件夹" : string.IsNullOrWhiteSpace(Extension) ? "文件" : Extension.TrimStart('.').ToUpperInvariant();
  public double UsedPercent => !IsDrive || TotalSize <= 0
    ? 0
    : Math.Clamp((TotalSize - AvailableFreeSpace) * 100d / TotalSize, 0, 100);
  public bool IsLowSpace => IsDrive &&
    TotalSize > 0 &&
    AvailableFreeSpace < Math.Min(TotalSize * 0.1, 20L * 1024 * 1024 * 1024);

  public static FileItemView FromDirectory(DirectoryItemPayload i) => new() { Name = i.Name, FullPath = i.FullPath, IsDirectory = i.IsDirectory, Length = i.Length, Extension = i.Extension, Kind = i.IsDirectory ? FileKind.Folder : DetectKind(i.Extension), SizeText = i.IsDirectory ? "" : FormatSize(i.Length), LastWriteTime = i.LastWriteTime };
  public static FileItemView FromDrive(DriveInfoPayload d) => new()
  {
    Name = $"{(string.IsNullOrWhiteSpace(d.VolumeLabel) ? DriveTypeText(d.DriveType) : d.VolumeLabel)} ({d.Name.TrimEnd('\\')})",
    FullPath = d.Name,
    IsDirectory = true,
    IsDrive = true,
    Kind = FileKind.Folder,
    TotalSize = d.TotalSize,
    AvailableFreeSpace = d.AvailableFreeSpace,
    SizeText = FormatSize(d.AvailableFreeSpace) + " 可用",
    LastWriteTime = DateTime.MinValue
  };
  public static string FormatSize(long bytes) => bytes switch { > 1024L * 1024 * 1024 => (bytes / 1024d / 1024 / 1024).ToString("F1") + " GB", > 1024L * 1024 => (bytes / 1024d / 1024).ToString("F1") + " MB", > 1024 => (bytes / 1024d).ToString("F1") + " KB", _ => bytes + " B" };
  private static FileKind DetectKind(string ext)
  {
    ext = ext.ToLowerInvariant();
    if (ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" or ".webp") return FileKind.Image;
    if (ext is ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv") return FileKind.Video;
    if (ext is ".mp3" or ".wav" or ".flac" or ".aac" or ".m4a" or ".wma") return FileKind.Audio;
    if (ext is ".txt" or ".log" or ".json" or ".xml" or ".cs" or ".py" or ".js" or ".html" or ".css" or ".md" or ".ini" or ".bat" or ".ps1") return FileKind.Text;
    return FileKind.Other;
  }
  private static string DriveTypeText(string type) => type switch
  {
    "Fixed" => "本地磁盘",
    "Removable" => "可移动磁盘",
    "Network" => "网络磁盘",
    "CDRom" => "光驱",
    _ => "磁盘"
  };
}

internal static class ShellIconProvider
{
  private const uint ShgfiIcon = 0x000000100;
  private const uint ShgfiLargeIcon = 0x000000000;
  private const uint ShgfiUseFileAttributes = 0x000000010;
  private const uint FileAttributeDirectory = 0x00000010;
  private const uint FileAttributeNormal = 0x00000080;
  private static readonly ConcurrentDictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);

  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  private struct ShFileInfo
  {
    public IntPtr Icon;
    public int IconIndex;
    public uint Attributes;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
  }

  [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
  private static extern IntPtr SHGetFileInfo(
    string path,
    uint fileAttributes,
    out ShFileInfo fileInfo,
    uint fileInfoSize,
    uint flags);

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool DestroyIcon(IntPtr icon);

  public static ImageSource GetIcon(bool isDrive, bool isDirectory, string extension)
  {
    string key = isDrive ? "drive" : isDirectory ? "folder" : "file:" + extension.ToLowerInvariant();
    return Cache.GetOrAdd(key, _ => LoadIcon(isDrive, isDirectory, extension));
  }

  private static ImageSource LoadIcon(bool isDrive, bool isDirectory, string extension)
  {
    string path = isDrive ? "C:\\" : isDirectory ? "folder" : "file" + (string.IsNullOrWhiteSpace(extension) ? ".bin" : extension);
    uint attributes = isDirectory || isDrive ? FileAttributeDirectory : FileAttributeNormal;
    uint flags = ShgfiIcon | ShgfiLargeIcon | (isDrive ? 0 : ShgfiUseFileAttributes);
    SHGetFileInfo(path, attributes, out ShFileInfo info, (uint)Marshal.SizeOf<ShFileInfo>(), flags);
    if (info.Icon == IntPtr.Zero) return CreateFallback(isDirectory);
    try
    {
      BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(
        info.Icon,
        Int32Rect.Empty,
        BitmapSizeOptions.FromWidthAndHeight(64, 64));
      source.Freeze();
      return source;
    }
    finally { DestroyIcon(info.Icon); }
  }

  private static ImageSource CreateFallback(bool folder)
  {
    var drawing = new FormattedText(
      folder ? "📁" : "📄",
      System.Globalization.CultureInfo.CurrentUICulture,
      FlowDirection.LeftToRight,
      new Typeface("Segoe UI Emoji"),
      40,
      Brushes.Black,
      1);
    var group = new DrawingGroup();
    using (DrawingContext context = group.Open()) context.DrawText(drawing, new Point(0, 0));
    group.Freeze();
    return new DrawingImage(group);
  }
}
public sealed partial class DeviceView : INotifyPropertyChanged
{
  private readonly object _managementGate = new();
  private TcpClient? _client;
  private NetworkStream? _stream;
  private long _managementGeneration = 1;
  private string _screenSessionId = string.Empty;
  private Guid _instanceId;
  private readonly ConcurrentDictionary<string, TaskCompletionSource<RemoteMessage>> _pending = new();
  private readonly SemaphoreSlim _writeLock = new(1, 1);
  private string _status = "连接中";
  public event PropertyChangedEventHandler? PropertyChanged;
  public event Action<RemoteVideoFrame>? VideoFrameReceived;
  public event Action<RemoteAudioFrame>? AudioFrameReceived;
  public event Action<string>? VideoStatusReceived;
  public event Action<string>? ClipboardTextReceived;
  public Func<string, DeviceMetadata>? MetadataResolver { get; set; }
  public string DeviceId { get; private set; } = string.Empty;
  public string DeviceName { get; private set; } = "未知设备";
  public string UserName { get; private set; } = "";
  public string OperatingSystem { get; private set; } = "";
  public string RemoteEndPoint { get; private set; }
  public string LastHeartbeat { get; private set; } = "";
  public string Remark { get; private set; } = "";
  public bool IsHidden { get; private set; }
  public bool StartupEnabled { get; private set; }
  public bool HideTray { get; private set; }
  public string StartupMenuHeader =>
    $"开机启动({(StartupEnabled ? "开" : "关")})";
  public string HideTrayMenuHeader =>
    $"隐藏托盘({(HideTray ? "开" : "关")})";
  public Guid InstanceId
  {
    get { lock (_managementGate) return _instanceId; }
  }
  public DesktopTransportCapabilities DesktopCapabilities { get; private set; }
  public string DisplayTitle => DeviceName;
  public string RemarkLine => string.IsNullOrWhiteSpace(Remark) ? UserName : Remark;
  public bool IsOnline => string.Equals(_status, "在线", StringComparison.Ordinal);
  public bool ScreenStreamRequested
  {
    get { lock (_managementGate) return _screenSessionId.Length > 0; }
  }
  public Brush StatusForeground => IsOnline ? new SolidColorBrush(Color.FromRgb(124, 242, 163)) : new SolidColorBrush(Color.FromRgb(255, 112, 112));
  public Brush StatusBackground => IsOnline ? new SolidColorBrush(Color.FromRgb(18, 61, 42)) : new SolidColorBrush(Color.FromRgb(73, 28, 34));
  public string Status { get => _status; set { _status = value; Changed(nameof(Status)); Changed(nameof(IsOnline)); Changed(nameof(StatusForeground)); Changed(nameof(StatusBackground)); } }
  public long CurrentGeneration { get { lock (_managementGate) return _managementGeneration; } }

  public DeviceView(TcpClient client, HelloPayload hello, Guid instanceId)
  {
    if (instanceId == Guid.Empty)
      throw new InvalidOperationException("B端实例标识无效。");
    _client = client;
    _client.NoDelay = true;
    _stream = client.GetStream();
    _instanceId = instanceId;
    RemoteEndPoint = client.Client.RemoteEndPoint?.ToString() ?? "";
    ApplyHello(hello);
  }

  private DeviceView(string deviceId, DeviceMetadata metadata)
  {
    DeviceId = deviceId;
    DeviceName = string.IsNullOrWhiteSpace(metadata.DeviceName)
      ? "未知设备"
      : metadata.DeviceName;
    UserName = metadata.UserName ?? "";
    RemoteEndPoint = metadata.RemoteEndPoint ?? "";
    Status = "离线";
    ApplyMetadata(metadata);
  }

  public static DeviceView CreateOffline(string deviceId, DeviceMetadata metadata)
  {
    if (string.IsNullOrWhiteSpace(deviceId))
      throw new ArgumentException("设备ID不能为空。", nameof(deviceId));
    return new DeviceView(deviceId, metadata);
  }

  public long RebindManagementClient(
    TcpClient client,
    HelloPayload hello,
    Guid instanceId)
  {
    if (instanceId == Guid.Empty)
      throw new InvalidOperationException("B端实例标识无效。");
    ResetTransientChannelsForReconnect();
    TcpClient? previousClient;
    lock (_managementGate)
    {
      previousClient = _client;
      _client = client;
      _client.NoDelay = true;
      _stream = client.GetStream();
      _instanceId = instanceId;
      RemoteEndPoint = client.Client.RemoteEndPoint?.ToString() ?? "";
      _managementGeneration++;
      ApplyHello(hello);
      foreach (var pending in _pending.Values) pending.TrySetException(new IOException("设备已重新连接，请重试当前操作。"));
      _pending.Clear();
    }
    try { previousClient?.Close(); } catch { }
    ChangedAll();
    return CurrentGeneration;
  }

  public bool AcceptsInstance(Guid instanceId) =>
    instanceId != Guid.Empty && InstanceId == instanceId;

  public void ApplyResolvedMetadata()
  {
    if (MetadataResolver is not null) ApplyMetadata(MetadataResolver(DeviceId));
  }

  private void ApplyHello(HelloPayload hello)
  {
    DeviceId = hello.DeviceId;
    DeviceName = hello.DeviceName;
    UserName = hello.UserName;
    OperatingSystem = hello.OperatingSystem;
    DesktopCapabilities = hello.DesktopCapabilities;
    StartupEnabled = hello.StartupEnabled;
    HideTray = hello.HideTray;
    Status = "在线";
    Changed(nameof(StartupEnabled));
    Changed(nameof(HideTray));
    Changed(nameof(StartupMenuHeader));
    Changed(nameof(HideTrayMenuHeader));
    Touch();
  }

  public void ApplyAgentSettings(AgentSettingsPayload settings)
  {
    StartupEnabled = settings.StartupEnabled;
    HideTray = settings.HideTray;
    Changed(nameof(StartupEnabled));
    Changed(nameof(HideTray));
    Changed(nameof(StartupMenuHeader));
    Changed(nameof(HideTrayMenuHeader));
  }

  public async Task ReadLoopAsync(Action onChanged, Action onDisconnected, CancellationToken token, long generation)
  {
    NetworkStream stream;
    TcpClient client;
    lock (_managementGate)
    {
      if (generation != _managementGeneration) return;
      stream = _stream ?? throw new IOException("设备管理通道尚未连接。");
      client = _client ?? throw new IOException("设备管理通道尚未连接。");
    }
    try
    {
      while (!token.IsCancellationRequested)
      {
        var msg = await FramedJsonTransport.ReadAsync(stream, token); if (msg is null) break;
        if (msg.Type == MessageType.Hello)
        {
          var h = msg.Payload.As<HelloPayload>()!;
          ApplyHello(h);
          ApplyResolvedMetadata();
          Touch(); ChangedAll(); onChanged();
        }
        else if (msg.Type == MessageType.Heartbeat) Touch();
        else if (_pending.TryRemove(msg.RequestId, out var tcs)) tcs.TrySetResult(msg);
      }
    }
    catch { }
    finally
    {
      bool current;
      lock (_managementGate) current = generation == _managementGeneration;
      try { stream.Dispose(); client.Close(); } catch { }
      if (current)
      {
        Status = "离线";
        foreach (var pending in _pending.Values) pending.TrySetException(new IOException("设备已离线。"));
        _pending.Clear();
        onDisconnected();
      }
    }
  }
  public async Task<RemoteMessage> RequestAsync(
    MessageType type,
    object payload,
    int timeoutSeconds,
    CancellationToken cancellationToken = default)
  {
    UpdateScreenStreamIntent(type, payload);
    var msg = new RemoteMessage { RequestId = Guid.NewGuid().ToString("N"), Type = type, Payload = MessagePayload.ToElement(payload) };
    var tcs = new TaskCompletionSource<RemoteMessage>(TaskCreationOptions.RunContinuationsAsynchronously); _pending[msg.RequestId] = tcs;
    try
    {
      await _writeLock.WaitAsync(cancellationToken);
      try
      {
        NetworkStream stream;
        lock (_managementGate)
          stream = _stream ?? throw new IOException("设备管理通道尚未连接。");
        // A framed message must be written atomically with respect to cancellation.
        // Canceling between the length prefix and JSON body would desynchronize the
        // long-lived management channel for every later request.
        await FramedJsonTransport.WriteAsync(stream, msg, CancellationToken.None);
      }
      finally { _writeLock.Release(); }
      using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
      using var cts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
      await using var _ = cts.Token.Register(() => tcs.TrySetCanceled(cts.Token));
      var result = await tcs.Task;
      if (result.Type == MessageType.Error) throw new InvalidOperationException(result.Payload.As<ErrorPayload>()?.Message ?? "远端错误");
      return result;
    }
    finally
    {
      _pending.TryRemove(msg.RequestId, out _);
    }
  }
  public async Task SendAsync(MessageType type, object payload)
  {
    UpdateScreenStreamIntent(type, payload);
    var msg = new RemoteMessage { RequestId = Guid.NewGuid().ToString("N"), Type = type, Payload = MessagePayload.ToElement(payload) };
    await _writeLock.WaitAsync();
    try
    {
      NetworkStream stream;
      lock (_managementGate)
        stream = _stream ?? throw new IOException("设备管理通道尚未连接。");
      await FramedJsonTransport.WriteAsync(stream, msg, CancellationToken.None);
    }
    finally { _writeLock.Release(); }
  }

  public async Task ResumeScreenStreamAsync()
  {
    string sessionId;
    lock (_managementGate) sessionId = _screenSessionId;
    if (sessionId.Length == 0 || !IsOnline) return;
    try
    {
      await RequestAsync(
        MessageType.ScreenStreamStart,
        new DesktopSessionPayload(sessionId),
        20);
    }
    catch { }
  }

  public void ForgetScreenStreamSession(string sessionId)
  {
    if (string.IsNullOrWhiteSpace(sessionId)) return;
    lock (_managementGate)
    {
      if (string.Equals(
            _screenSessionId,
            sessionId,
            StringComparison.OrdinalIgnoreCase))
        _screenSessionId = string.Empty;
    }
  }

  private void UpdateScreenStreamIntent(MessageType type, object payload)
  {
    if (payload is not DesktopSessionPayload session ||
        string.IsNullOrWhiteSpace(session.SessionId))
      return;
    lock (_managementGate)
    {
      if (type == MessageType.ScreenStreamStart)
        _screenSessionId = session.SessionId;
      else if (type == MessageType.ScreenStreamStop &&
               string.Equals(
                 _screenSessionId,
                 session.SessionId,
                 StringComparison.OrdinalIgnoreCase))
        _screenSessionId = string.Empty;
    }
  }
  private TcpClient? _clipboardClient;
  private NetworkStream? _clipboardStream;
  private CancellationTokenSource? _clipboardCts;
  private readonly SemaphoreSlim _clipboardWriteLock = new(1, 1);
  private ClipboardFileReceiver? _clipboardReceiver;
  public event Action<IReadOnlyList<string>>? ClipboardFilesReceived;

  public bool IsVideoConnected =>
    IsUdpPeerFresh(UdpDesktopPeerRole.VideoProducer) || IsTcpVideoConnected;

  public bool IsInputConnected =>
    IsUdpPeerFresh(UdpDesktopPeerRole.InputExecutor) || IsTcpInputConnected;

  public async Task SendControlAsync(ControlPacket packet, CancellationToken token = default)
  {
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
    timeout.CancelAfter(TimeSpan.FromSeconds(1));
    bool videoFeedback = packet.Type == ControlPacketType.VideoFeedback;
    UdpDesktopPeerRole role = videoFeedback
      ? UdpDesktopPeerRole.VideoProducer
      : UdpDesktopPeerRole.InputExecutor;
    if (IsUdpPeerFresh(role))
    {
      try
      {
        await SendUdpControlAsync(packet, timeout.Token);
        return;
      }
      catch when (!timeout.IsCancellationRequested)
      {
        // A public-network UDP mapping can disappear between the freshness
        // check and the send. Continue on the already-authenticated TCP path.
      }
    }
    if (videoFeedback)
      await SendTcpVideoControlAsync(packet, timeout.Token);
    else
      await SendTcpInputControlAsync(packet, timeout.Token);
  }

  public InputResultPacket? LastInputResult { get; private set; }
  public DateTime LastInputResultAt { get; private set; }
  public event Action<InputResultPacket>? InputResultReceived;

  public void AttachClipboardClient(TcpClient client, CancellationToken parentToken)
  {
    try { _clipboardCts?.Cancel(); _clipboardClient?.Close(); _clipboardReceiver?.Dispose(); } catch { }
    _clipboardClient = client;
    _clipboardStream = client.GetStream();
    _clipboardCts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
    string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AuthorizedDeviceControl", "Clipboard", DeviceId);
    _clipboardReceiver = new ClipboardFileReceiver(cache);
    _ = Task.Run(() => ClipboardReadLoopAsync(client, _clipboardCts.Token), _clipboardCts.Token);
  }

  private async Task ClipboardReadLoopAsync(TcpClient client, CancellationToken token)
  {
    try
    {
      NetworkStream stream = client.GetStream();
      while (!token.IsCancellationRequested)
      {
        ClipboardPacket? packet = await BinaryClipboardProtocol.ReadAsync(stream, token);
        if (packet is null) break;
        if (packet.Type == ClipboardPacketType.Text)
        {
          ClipboardTextReceived?.Invoke(BinaryClipboardProtocol.ReadText(packet));
          continue;
        }
        var paths = await (_clipboardReceiver?.ProcessAsync(packet, token) ?? Task.FromResult<IReadOnlyList<string>?>(null));
        if (paths is { Count: > 0 }) ClipboardFilesReceived?.Invoke(paths);
      }
    }
    catch (OperationCanceledException) { }
    catch { }
    finally { try { client.Close(); } catch { } }
  }

  public async Task SendClipboardTextAsync(string text, CancellationToken token = default)
  {
    NetworkStream? stream = _clipboardStream;
    if (stream is null) throw new InvalidOperationException("剪贴板通道尚未连接。");
    await _clipboardWriteLock.WaitAsync(token);
    try
    {
      if (ReferenceEquals(_clipboardStream, stream)) await BinaryClipboardProtocol.WriteAsync(stream, BinaryClipboardProtocol.Text(text), token);
    }
    finally { _clipboardWriteLock.Release(); }
  }

  public async Task SendClipboardFilesAsync(IEnumerable<string> paths, CancellationToken token = default, Action<long, long>? progress = null)
  {
    NetworkStream? stream = _clipboardStream;
    if (stream is null) throw new InvalidOperationException("剪贴板通道尚未连接。");
    await BinaryClipboardProtocol.SendFilesAsync(stream, _clipboardWriteLock, paths, token, progress);
  }

  private void ResetTransientChannelsForReconnect()
  {
    try { CloseUdpDesktop(); } catch { }
    try { CloseTcpDesktopFallback(); } catch { }
    try
    {
      _clipboardCts?.Cancel();
      _clipboardStream?.Dispose();
      _clipboardClient?.Close();
      _clipboardReceiver?.Dispose();
      _clipboardStream = null;
      _clipboardClient = null;
    }
    catch { }
    try { CloseFileChannel(); } catch { }
    try { CloseDedicatedChannelClients(); } catch { }
    try { CloseCodexClient(); } catch { }
  }

  public void Close()
  {
    try
    {
      ResetTransientChannelsForReconnect();
      _stream?.Dispose();
      _client?.Close();
    }
    catch { }
  }
  public void ApplyMetadata(DeviceMetadata meta)
  {
    Remark = meta.Remark ?? "";
    IsHidden = meta.Hidden;
    Changed(nameof(Remark)); Changed(nameof(IsHidden)); Changed(nameof(DisplayTitle)); Changed(nameof(RemarkLine));
  }
  private void Touch() { LastHeartbeat = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"); Changed(nameof(LastHeartbeat)); }
  private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
  private void ChangedAll() { foreach (var n in new[] { nameof(DeviceId), nameof(DeviceName), nameof(UserName), nameof(OperatingSystem), nameof(RemoteEndPoint), nameof(Status), nameof(LastHeartbeat), nameof(DisplayTitle), nameof(RemarkLine), nameof(InstanceId), nameof(DesktopCapabilities) }) Changed(n); }
}

public sealed class DeviceMetadata
{
  public string Remark { get; set; } = "";
  public bool Hidden { get; set; }
  public string DeviceName { get; set; } = "";
  public string UserName { get; set; } = "";
  public string RemoteEndPoint { get; set; } = "";
}

public sealed class DeviceMetadataStore
{
  private readonly object _gate = new();
  private readonly string _file;
  private readonly Dictionary<string, DeviceMetadata> _items;
  private readonly Dictionary<string, DeviceMetadata> _legacyItems;
  private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

  public DeviceMetadataStore()
  {
    string executableDirectory =
      System.IO.Path.GetDirectoryName(Environment.ProcessPath ?? "") ??
      AppContext.BaseDirectory;
    _file = System.IO.Path.Combine(executableDirectory, "devices.json");
    try
    {
      _items = File.Exists(_file)
        ? JsonSerializer.Deserialize<Dictionary<string, DeviceMetadata>>(File.ReadAllText(_file), Options) ?? new()
        : new();
    }
    catch { _items = new(); }
    string legacyFile = System.IO.Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
      "AuthorizedDeviceControl",
      "devices.json");
    try
    {
      _legacyItems = File.Exists(legacyFile)
        ? JsonSerializer.Deserialize<Dictionary<string, DeviceMetadata>>(
            File.ReadAllText(legacyFile),
            Options) ?? new()
        : new();
    }
    catch { _legacyItems = new(); }
  }

  public DeviceMetadata Get(string deviceId)
  {
    if (string.IsNullOrWhiteSpace(deviceId)) return new DeviceMetadata();
    lock (_gate)
    {
      if (_items.TryGetValue(deviceId, out DeviceMetadata? metadata))
        return metadata;
      return _legacyItems.TryGetValue(deviceId, out metadata)
        ? metadata
        : new DeviceMetadata();
    }
  }

  public void Save(string deviceId, DeviceMetadata meta)
  {
    if (string.IsNullOrWhiteSpace(deviceId)) return;
    lock (_gate)
    {
      _items[deviceId] = meta;
      Persist();
    }
  }

  public void SaveDevice(DeviceView device)
  {
    if (string.IsNullOrWhiteSpace(device.DeviceId)) return;
    DeviceMetadata current = Get(device.DeviceId);
    Save(device.DeviceId, new DeviceMetadata
    {
      Remark = device.Remark,
      Hidden = current.Hidden,
      DeviceName = device.DeviceName,
      UserName = device.UserName,
      RemoteEndPoint = device.RemoteEndPoint
    });
  }

  public IReadOnlyList<KeyValuePair<string, DeviceMetadata>> GetAll()
  {
    lock (_gate)
      return _items
        .Where(item => !string.IsNullOrWhiteSpace(item.Key))
        .Select(item => new KeyValuePair<string, DeviceMetadata>(item.Key, item.Value))
        .ToList();
  }

  public void Remove(string deviceId)
  {
    if (string.IsNullOrWhiteSpace(deviceId)) return;
    lock (_gate)
      if (_items.Remove(deviceId)) Persist();
  }

  private void Persist()
  {
    string temporary = _file + ".tmp";
    File.WriteAllText(temporary, JsonSerializer.Serialize(_items, Options), new UTF8Encoding(false));
    File.Move(temporary, _file, true);
  }
}
