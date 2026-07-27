using Microsoft.Win32;
using RemoteControl.Shared;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.IO;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace RemoteAgent;

public partial class MainWindow : Window
{
  private readonly string _deviceId = WindowsAgentEnvironment.LoadOrCreateMachineDeviceId();
  private readonly Guid _instanceId;
  private string _controllerHost = NetworkDefaults.DefaultControllerHost;
  private int _controllerPort = NetworkDefaults.Port;
  private long _channelGeneration;
  private bool _everConnected;
  private readonly bool _sessionHelper;
  private readonly bool _statusOnly;
  private readonly bool _inputOnly;
  private readonly bool _excludeInput;
  private readonly bool _startHidden;
  private readonly bool _serviceOwnedStatusUi;
  private DispatcherTimer? _settingsTimer;
  private TcpClient? _client;
  private CancellationTokenSource? _cts;
  private NetworkStream? _stream;
  private readonly SemaphoreSlim _sendLock = new(1, 1);
  private CancellationTokenSource? _videoCts;
  private volatile bool _videoStreaming;
  private long _videoSessionGeneration;
  private readonly object _desktopSessionGate = new();
  private Guid _activeDesktopSession;
  private static readonly object ProcessSampleGate = new();
  private static readonly Dictionary<int, ProcessSample> PreviousProcessSamples = new();
  private static readonly ConcurrentDictionary<string, string> ProcessIconCache = new(StringComparer.OrdinalIgnoreCase);

  [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
  private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
  [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
  [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

  public MainWindow() : this(false, false, false, false, null, false, false) { }

  internal MainWindow(
    bool sessionHelper,
    bool statusOnly,
    bool inputOnly = false,
    bool excludeInput = false,
    Guid? instanceId = null,
    bool startHidden = false,
    bool serviceOwnedStatusUi = false)
  {
    _instanceId = instanceId is { } value && value != Guid.Empty
      ? value
      : Guid.NewGuid();
    _sessionHelper = sessionHelper;
    _statusOnly = statusOnly;
    _inputOnly = inputOnly;
    _excludeInput = excludeInput;
    _startHidden = startHidden;
    _serviceOwnedStatusUi = serviceOwnedStatusUi;
    _inputDispatcher = new WindowsInputDispatcher(_inputInjector);
    InitializeComponent();
    bool integrationTest = string.Equals(
      Environment.GetEnvironmentVariable("ADC_INTEGRATION_TEST"),
      "1",
      StringComparison.Ordinal);
    if (!_sessionHelper && !integrationTest) InitializeTraySupport();
    if (!_statusOnly && !_inputOnly) InitializeSessionAwareness();
    try { SetProcessDPIAware(); } catch { }
    _ = WindowsAgentEnvironment.EnsureCodexWorkspace();
    if (!_statusOnly && !_inputOnly)
      SourceInitialized += (_, _) => InitializeClipboardWatcher();
    if (_statusOnly)
      InitializeAgentStatusListener();
    Closed += (_, _) =>
    {
      if (!_statusOnly && !_inputOnly)
      {
        DisposeClipboardWatcher();
        DisposeSessionAwareness();
      }
      DisposeAgentStatusPipe();
      _settingsTimer?.Stop();
      _settingsTimer = null;
      DisposeTraySupport();
      if (!_statusOnly)
      {
        ForceStopScreenStream();
        Disconnect();
      }
      _inputDispatcher.Dispose();
    };
    Loaded += (_, _) =>
    {
      if (_sessionHelper)
      {
        ShowInTaskbar = false;
        Hide();
      }
      if (_startHidden)
      {
        ShowInTaskbar = false;
        Hide();
      }
      if (_statusOnly)
      {
        _settingsTimer = new DispatcherTimer
        {
          Interval = TimeSpan.FromSeconds(1)
        };
        _settingsTimer.Tick += (_, _) =>
        {
          bool hideTray = AgentSettingsStore.Load().HideTray;
          UpdateTrayVisibility(!hideTray);
          if (!_serviceOwnedStatusUi || !hideTray) return;
          _settingsTimer.Stop();
          CloseServiceOwnedStatusUi();
        };
        _settingsTimer.Start();
      }
      if (_statusOnly) return;
      var args = Environment.GetCommandLineArgs();
      _controllerHost = AgentServiceBootstrap.ResolveControllerHost(args);
      if (string.Equals(
            Environment.GetEnvironmentVariable("ADC_INTEGRATION_TEST"),
            "1",
            StringComparison.Ordinal) &&
          int.TryParse(
            Environment.GetEnvironmentVariable("ADC_TEST_PORT"),
            out int testPort) &&
          testPort is > 0 and <= 65535)
        _controllerPort = testPort;
      if (_cts is null)
      {
        _cts = new CancellationTokenSource();
        if (_inputOnly)
        {
          _ = Task.Run(
            () => UdpInputLoopAsync(
              _controllerHost,
              _controllerPort,
              _cts.Token),
            _cts.Token);
          _ = Task.Run(
            () => TcpInputFallbackConnectLoopAsync(
              _controllerHost,
              _controllerPort,
              _cts.Token),
            _cts.Token);
          _ = Task.Run(
            () => CodexConnectLoopAsync(
              _controllerHost,
              _controllerPort,
              _cts.Token),
            _cts.Token);
        }
        else
          StartConnectLoop(_cts.Token);
      }
    };
  }

  private void CloseServiceOwnedStatusUi()
  {
    if (!_serviceOwnedStatusUi) return;
    AllowTrayExitWithoutStoppingService();
    Close();
  }

  private void ConnectButton_Click(object sender, RoutedEventArgs e)
  {
    if (_cts is not null) return;
    _cts = new CancellationTokenSource();
    StartConnectLoop(_cts.Token);
  }

  private void DisconnectButton_Click(object sender, RoutedEventArgs e) => Disconnect();

  private void StartConnectLoop(CancellationToken token)
  {
    _ = Task.Run(
      () => ConnectLoopAsync(_controllerHost, _controllerPort, token),
      token);
  }

  private async Task ConnectLoopAsync(string host, int port, CancellationToken token)
  {
    while (!token.IsCancellationRequested)
    {
      try
      {
        SetConnectionStatus(_everConnected ? "已断开" : "未链接");
        _client = new TcpClient { NoDelay = true };
        await _client.ConnectAsync(host, port, token);
        _stream = _client.GetStream();
        await WriteLogicalChannelHelloAsync(
          _stream,
          LogicalChannelType.Management,
          token);
        AgentSettingsPayload settings = AgentSettingsStore.Load();
        await SendAsync(MessageType.Hello, new HelloPayload(
          _deviceId,
          Environment.MachineName,
          WindowsAgentEnvironment.GetInteractiveUserName(),
          Environment.MachineName,
          Environment.OSVersion.ToString(),
          "3.3.0-codex-computer-bridge",
          ProtocolVersions.Current,
          DesktopTransportCapabilities.Current,
          settings.StartupEnabled,
          settings.HideTray), token);
        _everConnected = true;
        SetConnectionStatus("已链接");
        _videoCts?.Cancel();
        _videoCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        _ = Task.Run(() => TcpVideoFallbackConnectLoopAsync(host, port, _videoCts.Token), _videoCts.Token);
        _ = Task.Run(() => UdpH264VideoLoopAsync(host, port, _videoCts.Token), _videoCts.Token);
        if (!_excludeInput)
        {
          _ = Task.Run(() => UdpInputLoopAsync(host, port, _videoCts.Token), _videoCts.Token);
          _ = Task.Run(() => TcpInputFallbackConnectLoopAsync(host, port, _videoCts.Token), _videoCts.Token);
        }
        _ = Task.Run(() => ClipboardConnectLoopAsync(host, port, _videoCts.Token), _videoCts.Token);
        _ = Task.Run(() => FileTransferConnectLoopAsync(host, port, _videoCts.Token), _videoCts.Token);
        _ = Task.Run(() => TerminalConnectLoopAsync(host, port, _videoCts.Token), _videoCts.Token);
        _ = Task.Run(() => RegistryConnectLoopAsync(host, port, _videoCts.Token), _videoCts.Token);
        if (!_excludeInput)
          _ = Task.Run(() => CodexConnectLoopAsync(host, port, _videoCts.Token), _videoCts.Token);
        _ = Task.Run(() => HeartbeatLoopAsync(token), token);
        await ReadLoopAsync(token);
      }
      catch (OperationCanceledException) { break; }
      catch (Exception)
      {
        SetConnectionStatus(_everConnected ? "已断开" : "未链接");
        await Task.Delay(TimeSpan.FromSeconds(2), token).ContinueWith(_ => { });
      }
      finally
      {
        ForceStopScreenStream();
        try { _videoCts?.Cancel(); CloseUdpDesktopClients(); CloseTcpDesktopFallbackChannels(); _clipboardStream?.Dispose(); _clipboardClient?.Close(); _fileStream?.Dispose(); _fileClient?.Close(); CloseDedicatedChannels(); CloseCodexChannel(); } catch { }
        _stream?.Dispose(); _client?.Close(); _stream = null; _client = null;
        if (!token.IsCancellationRequested)
          SetConnectionStatus(_everConnected ? "已断开" : "未链接");
      }
    }
  }

  private async Task WriteLogicalChannelHelloAsync(
    NetworkStream stream,
    LogicalChannelType channel,
    CancellationToken token)
  {
    await LogicalChannelProtocol.WriteHelloAsync(
      stream,
      channel,
      _deviceId,
      _instanceId,
      Interlocked.Increment(ref _channelGeneration),
      token);
  }

  private async Task HeartbeatLoopAsync(CancellationToken token)
  {
    while (!token.IsCancellationRequested && _stream is not null)
    {
      try
      {
        await SendAsync(
          MessageType.Heartbeat,
          new { Time = DateTimeOffset.Now.ToString("O") },
          token);
        if (_sessionHelper) QueueAgentStatus("已链接");
      }
      catch { break; }
      await Task.Delay(TimeSpan.FromSeconds(5), token).ContinueWith(_ => { });
    }
  }

  private async Task ReadLoopAsync(CancellationToken token)
  {
    while (!token.IsCancellationRequested && _stream is not null)
    {
      var msg = await FramedJsonTransport.ReadAsync(_stream, token);
      if (msg is null) break;
      // Desktop lifecycle changes must preserve wire order. Dispatching Start
      // and Stop on unrelated worker tasks allowed a late Stop from an old
      // window to win over a newer Start.
      if (msg.Type is MessageType.ScreenStreamStart or
          MessageType.ScreenStreamStop or
          MessageType.AudioStreamStart or
          MessageType.AudioStreamStop or
          MessageType.AgentUninstallRequest)
        await HandleAsync(msg, token);
      else
        _ = Task.Run(() => HandleAsync(msg, token), token);
    }
  }

  private async Task HandleAsync(RemoteMessage msg, CancellationToken token)
  {
    try
    {
      switch (msg.Type)
      {
        case MessageType.SystemInfoRequest:
          await ReplyAsync(msg, MessageType.SystemInfoResponse, GetDetailedSystemInfo(), token); break;
        case MessageType.ScreenStreamStart:
          await ReplyAsync(
            msg,
            MessageType.ScreenStreamStart,
            StartScreenStream(msg.Payload.As<DesktopSessionPayload>()
              ?? throw new InvalidOperationException("远控会话参数无效。")),
            token);
          break;
        case MessageType.ScreenStreamStop:
          await ReplyAsync(
            msg,
            MessageType.ScreenStreamStop,
            StopScreenStream(msg.Payload.As<DesktopSessionPayload>()
              ?? throw new InvalidOperationException("远控会话参数无效。")),
            token);
          break;
        case MessageType.AudioStreamStart:
          await ReplyAsync(
            msg,
            MessageType.AudioStreamStartResponse,
            StartSystemAudio(msg.Payload.As<DesktopSessionPayload>()
              ?? new DesktopSessionPayload(string.Empty)),
            token);
          break;
        case MessageType.AudioStreamStop:
          await ReplyAsync(
            msg,
            MessageType.AudioStreamStopResponse,
            StopSystemAudio(msg.Payload.As<DesktopSessionPayload>()
              ?? new DesktopSessionPayload(string.Empty)),
            token);
          break;
        case MessageType.CommandRequest:
          await ReplyAsync(msg, MessageType.CommandResponse, await RunCommandAsync(msg.Payload.As<CommandRequestPayload>()!), token); break;
        case MessageType.PowerActionRequest:
          await ReplyAsync(
            msg,
            MessageType.PowerActionResponse,
            SystemPowerController.Queue(
              msg.Payload.As<PowerActionPayload>()?.Action
              ?? throw new InvalidOperationException("系统操作参数无效。")),
            token);
          break;
        case MessageType.AgentSettingsUpdateRequest:
          await ReplyAsync(
            msg,
            MessageType.AgentSettingsUpdateResponse,
            AgentSettingsStore.Save(
              msg.Payload.As<AgentSettingsPayload>()
                ?? throw new InvalidOperationException("B端设置参数无效。")),
            token);
          break;
        case MessageType.AgentUninstallRequest:
        {
          OperationResultPayload result = ValidateAgentUninstall(
            msg.Payload.As<AgentUninstallPayload>()
              ?? new AgentUninstallPayload(string.Empty));
          await ReplyAsync(
            msg,
            MessageType.AgentUninstallResponse,
            result,
            token);
          if (result.Success) ScheduleAgentUninstall();
          break;
        }
        case MessageType.DrivesRequest:
          await ReplyAsync(
            msg,
            MessageType.DrivesResponse,
            DriveInfo.GetDrives()
              .Where(d => d.IsReady)
              .Select(d => new DriveInfoPayload(
                d.Name,
                d.DriveType.ToString(),
                d.DriveFormat,
                d.TotalSize,
                d.AvailableFreeSpace,
                d.VolumeLabel))
              .ToList(),
            token);
          break;
        case MessageType.DirectoryRequest:
          await ReplyAsync(msg, MessageType.DirectoryResponse, ListDirectory(msg.Payload.As<PathPayload>()?.Path ?? "C:\\"), token); break;
        case MessageType.DeleteRequest:
          await ReplyAsync(msg, MessageType.DeleteResponse, DeletePath(msg.Payload.As<PathPayload>()?.Path ?? ""), token); break;
        case MessageType.CreateDirectoryRequest:
          await ReplyAsync(msg, MessageType.CreateDirectoryResponse, CreateDirectory(msg.Payload.As<PathPayload>()?.Path ?? ""), token); break;
        case MessageType.RenameRequest:
          await ReplyAsync(msg, MessageType.RenameResponse, RenamePath(msg.Payload.As<RenamePayload>()!), token); break;
        case MessageType.FilePropertiesRequest:
          await ReplyAsync(msg, MessageType.FilePropertiesResponse, GetProperties(msg.Payload.As<PathPayload>()?.Path ?? ""), token); break;
        case MessageType.ThumbnailRequest:
          await ReplyAsync(msg, MessageType.ThumbnailResponse, CreateThumbnail(msg.Payload.As<PathPayload>()?.Path ?? ""), token); break;
        case MessageType.ProcessListRequest:
          await ReplyAsync(msg, MessageType.ProcessListResponse, ListProcesses(), token);
          break;
        case MessageType.ProcessIconsRequest:
          await ReplyAsync(
            msg,
            MessageType.ProcessIconsResponse,
            ListProcessIcons(msg.Payload.As<ProcessIconsRequestPayload>()),
            token);
          break;
        case MessageType.ProcessKillRequest:
          await ReplyAsync(msg, MessageType.ProcessKillResponse, KillProcess(msg.Payload.As<int>()), token); break;
        case MessageType.ServiceListRequest:
          await ReplyAsync(msg, MessageType.ServiceListResponse, ListServices(), token); break;
        case MessageType.ServiceControlRequest:
          await ReplyAsync(
            msg,
            MessageType.ServiceControlResponse,
            await ControlServiceAsync(
              msg.Payload.As<ServiceControlPayload>()
                ?? throw new InvalidOperationException("服务控制参数无效。"),
              token),
            token);
          break;
        case MessageType.ServiceDetailsRequest:
          await ReplyAsync(
            msg,
            MessageType.ServiceDetailsResponse,
            GetServiceDetails(
              msg.Payload.As<string>()
                ?? throw new InvalidOperationException("服务名称无效。")),
            token);
          break;
        case MessageType.RegistryReadRequest:
          await ReplyAsync(msg, MessageType.RegistryReadResponse, ReadRegistry(msg.Payload.As<RegistryReadPayload>()!), token); break;
      }
    }
    catch (Exception ex)
    {
      await ReplyAsync(msg, MessageType.Error, new ErrorPayload(ex.Message), token);
    }
  }

  private DetailedSystemInfoPayload GetDetailedSystemInfo()
  {
    string ips = string.Join(", ", Dns.GetHostAddresses(Dns.GetHostName())
      .Where(x => x.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(x))
      .Select(x => x.ToString()));
    string processor = QueryWmi("Win32_Processor", "Name").FirstOrDefault() ?? $"{Environment.ProcessorCount} 核处理器";
    string clock = QueryWmi("Win32_Processor", "MaxClockSpeed").FirstOrDefault() ?? "";
    if (double.TryParse(clock, out var mhz) && mhz > 0) processor += $" ({mhz / 1000d:F2} GHz)";
    string graphics = string.Join(", ", QueryWmi("Win32_VideoController", "Name").Distinct());
    if (string.IsNullOrWhiteSpace(graphics)) graphics = "未知";
    string installedRam = GetRamText();
    string storage = GetStorageText();
    var cv = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
    string productName = Convert.ToString(cv?.GetValue("ProductName")) ?? Environment.OSVersion.ToString();
    string displayVersion = Convert.ToString(cv?.GetValue("DisplayVersion")) ?? Convert.ToString(cv?.GetValue("ReleaseId")) ?? "";
    string build = Convert.ToString(cv?.GetValue("CurrentBuildNumber")) ?? Environment.OSVersion.Version.Build.ToString();
    string ubr = Convert.ToString(cv?.GetValue("UBR")) ?? "0";
    if (int.TryParse(build, out var buildNumber) && buildNumber >= 22000 && productName.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
      productName = productName.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
    string productId = Convert.ToString(cv?.GetValue("ProductId")) ?? "";
    string installDate = FormatInstallDate(cv?.GetValue("InstallDate"));
    string experience = GetExperiencePack();
    string systemType = Environment.Is64BitOperatingSystem ? "64 位操作系统，基于 x64 的处理器" : "32 位操作系统";
    return new DetailedSystemInfoPayload(
      Environment.MachineName,
      processor.Trim(),
      installedRam,
      graphics,
      storage,
      _deviceId,
      productId,
      systemType,
      "没有可用于此显示器的笔或触控输入",
      productName,
      displayVersion,
      installDate,
      $"{build}.{ubr}",
      experience,
      ips);
  }

  private static IEnumerable<string> QueryWmi(string scope, string property)
  {
    try
    {
      using var searcher = new ManagementObjectSearcher($"SELECT {property} FROM {scope}");
      foreach (var obj in searcher.Get())
      {
        string? value = Convert.ToString(obj[property]);
        if (!string.IsNullOrWhiteSpace(value)) yield return value.Trim();
      }
    }
    finally { }
  }

  private static string GetRamText()
  {
    try
    {
      using var cs = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
      using var pm = new ManagementObjectSearcher("SELECT Capacity FROM Win32_PhysicalMemory");
      using var os = new ManagementObjectSearcher("SELECT FreePhysicalMemory FROM Win32_OperatingSystem");
      ulong installed = pm.Get().Cast<ManagementObject>().Select(x => Convert.ToUInt64(x["Capacity"])).Aggregate(0UL, (a, b) => a + b);
      ulong total = installed > 0 ? installed : cs.Get().Cast<ManagementObject>().Select(x => Convert.ToUInt64(x["TotalPhysicalMemory"])).FirstOrDefault();
      ulong freeKb = os.Get().Cast<ManagementObject>().Select(x => Convert.ToUInt64(x["FreePhysicalMemory"])).FirstOrDefault();
      double totalGb = total / 1024d / 1024 / 1024;
      double freeGb = freeKb / 1024d / 1024;
      return $"{totalGb:F1} GB ({freeGb:F1} GB 可用)";
    }
    catch { return "未知"; }
  }

  private static string GetStorageText()
  {
    try
    {
      long total = 0, free = 0;
      foreach (var d in DriveInfo.GetDrives().Where(x => x.IsReady && x.DriveType == DriveType.Fixed))
      {
        total += d.TotalSize;
        free += d.AvailableFreeSpace;
      }
      return $"已使用 {FormatSize(total - free)}，共 {FormatSize(total)}";
    }
    catch { return "未知"; }
  }

  private static string FormatSize(long bytes) => bytes switch
  {
    > 1024L * 1024 * 1024 * 1024 => (bytes / 1024d / 1024 / 1024 / 1024).ToString("F1") + " TB",
    > 1024L * 1024 * 1024 => (bytes / 1024d / 1024 / 1024).ToString("F0") + " GB",
    > 1024L * 1024 => (bytes / 1024d / 1024).ToString("F0") + " MB",
    _ => bytes + " B"
  };

  private static string FormatInstallDate(object? value)
  {
    try
    {
      if (value is int seconds) return DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime.ToString("yyyy-MM-dd");
      if (int.TryParse(Convert.ToString(value), out var s)) return DateTimeOffset.FromUnixTimeSeconds(s).LocalDateTime.ToString("yyyy-MM-dd");
    }
    catch { }
    return "";
  }

  private static string GetExperiencePack()
  {
    try
    {
      string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemApps");
      var pack = Directory.EnumerateDirectories(folder, "MicrosoftWindows.Client.CBS_*").Select(Path.GetFileName).FirstOrDefault();
      return pack ?? "";
    }
    catch { return ""; }
  }

  private static async Task<CommandResponsePayload> RunCommandAsync(CommandRequestPayload req)
  {
    using var p = new Process();
    p.StartInfo = new ProcessStartInfo
    {
      FileName = string.IsNullOrWhiteSpace(req.FileName) ? "powershell.exe" : req.FileName,
      Arguments = req.Arguments,
      WorkingDirectory = Directory.Exists(req.WorkingDirectory) ? req.WorkingDirectory : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true
    };
    p.Start();
    var outputTask = p.StandardOutput.ReadToEndAsync();
    var errorTask = p.StandardError.ReadToEndAsync();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(req.TimeoutSeconds, 1, 600)));
    try { await p.WaitForExitAsync(cts.Token); }
    catch { try { p.Kill(true); } catch { } return new CommandResponsePayload(-1, await outputTask, "命令超时，已终止。" + await errorTask); }
    return new CommandResponsePayload(p.ExitCode, await outputTask, await errorTask);
  }

  private static DirectoryResponsePayload ListDirectory(string path)
  {
    var items = new List<DirectoryItemPayload>();
    foreach (var d in Directory.EnumerateDirectories(path)) { var di = new DirectoryInfo(d); items.Add(new(di.Name, di.FullName, true, 0, di.LastWriteTime, string.Empty)); }
    foreach (var f in Directory.EnumerateFiles(path)) { var fi = new FileInfo(f); items.Add(new(fi.Name, fi.FullName, false, fi.Length, fi.LastWriteTime, fi.Extension)); }
    return new DirectoryResponsePayload(path, items);
  }

  private static OperationResultPayload DeletePath(string path)
  {
    if (Directory.Exists(path)) Directory.Delete(path, true); else if (File.Exists(path)) File.Delete(path); else return new(false, "路径不存在");
    return new(true, "删除成功");
  }


  private static OperationResultPayload CreateDirectory(string path)
  {
    Directory.CreateDirectory(path);
    return new(true, "文件夹已创建");
  }

  private static OperationResultPayload RenamePath(RenamePayload req)
  {
    if (Directory.Exists(req.OldPath)) Directory.Move(req.OldPath, req.NewPath);
    else if (File.Exists(req.OldPath)) File.Move(req.OldPath, req.NewPath);
    else return new(false, "源路径不存在");
    return new(true, "重命名完成");
  }

  private static FilePropertiesPayload GetProperties(string path)
  {
    if (Directory.Exists(path))
    {
      var d = new DirectoryInfo(path);
      return new(d.Name, d.FullName, true, 0, d.CreationTime, d.LastWriteTime, d.Attributes.ToString(), string.Empty);
    }
    var f = new FileInfo(path);
    if (!f.Exists) throw new FileNotFoundException(path);
    return new(f.Name, f.FullName, false, f.Length, f.CreationTime, f.LastWriteTime, f.Attributes.ToString(), f.Extension);
  }

  private static ThumbnailPayload CreateThumbnail(string path)
  {
    try
    {
      if (!File.Exists(path) || !IsImage(path)) return new(path, string.Empty, false, "不是图片文件");
      using var image = Image.FromFile(path);
      const int max = 192;
      double scale = Math.Min(max / (double)image.Width, max / (double)image.Height);
      if (scale > 1) scale = 1;
      int w = Math.Max(1, (int)(image.Width * scale));
      int h = Math.Max(1, (int)(image.Height * scale));
      using var bmp = new Bitmap(w, h);
      using (var g = Graphics.FromImage(bmp))
      {
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(image, 0, 0, w, h);
      }
      using var ms = new MemoryStream();
      bmp.Save(ms, ImageFormat.Png);
      return new(path, Convert.ToBase64String(ms.ToArray()), true, "OK");
    }
    catch (Exception ex) { return new(path, string.Empty, false, ex.Message); }
  }

  private static bool IsImage(string path)
  {
    string ext = Path.GetExtension(path).ToLowerInvariant();
    return ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" or ".webp";
  }

  private OperationResultPayload StartScreenStream(DesktopSessionPayload request)
  {
    if (!Guid.TryParseExact(request.SessionId, "N", out Guid sessionId))
      throw new InvalidOperationException("远控会话标识无效。");
    lock (_desktopSessionGate)
    {
      _activeDesktopSession = sessionId;
      Interlocked.Increment(ref _videoSessionGeneration);
      _videoQuality.ResetForNewSession();
      _videoStreaming = true;
    }
    return new(true, "屏幕推流已启动");
  }

  private OperationResultPayload StopScreenStream(DesktopSessionPayload request)
  {
    if (!Guid.TryParseExact(request.SessionId, "N", out Guid sessionId))
      throw new InvalidOperationException("远控会话标识无效。");
    lock (_desktopSessionGate)
    {
      if (_activeDesktopSession != sessionId)
      {
        return new(true, "过期会话已忽略");
      }
      _videoStreaming = false;
      _activeDesktopSession = Guid.Empty;
      _inputInjector.ReleaseAll();
    }
    StopSystemAudio(request);
    return new(true, "屏幕推流已停止");
  }

  private void ForceStopScreenStream()
  {
    lock (_desktopSessionGate)
    {
      _videoStreaming = false;
      _activeDesktopSession = Guid.Empty;
      _inputInjector.ReleaseAll();
    }
    ForceStopSystemAudio();
  }

  private static List<ProcessInfoPayload> ListProcesses()
  {
    DateTime now = DateTime.UtcNow;
    var currentSamples = new Dictionary<int, ProcessSample>();
    var result = new List<ProcessInfoPayload>();
    Dictionary<int, ProcessSample> previousSamples;
    lock (ProcessSampleGate) previousSamples = new Dictionary<int, ProcessSample>(PreviousProcessSamples);
    IReadOnlyDictionary<int, string> windowTitles = CaptureMainWindowTitles();

    foreach (Process process in Process.GetProcesses())
    {
      try
      {
        int id = process.Id;
        string name = SafeProcessName(process);
        TimeSpan totalProcessorTime = process.TotalProcessorTime;
        currentSamples[id] = new ProcessSample(totalProcessorTime, now);

        double cpu = 0;
        if (previousSamples.TryGetValue(id, out ProcessSample old))
        {
          double elapsedMs = Math.Max(1, (now - old.TimestampUtc).TotalMilliseconds);
          double usedMs = Math.Max(0, (totalProcessorTime - old.TotalProcessorTime).TotalMilliseconds);
          cpu = Math.Clamp(usedMs / elapsedMs / Math.Max(1, Environment.ProcessorCount) * 100d, 0, 100);
        }

        result.Add(new ProcessInfoPayload(
          id,
          name,
          windowTitles.GetValueOrDefault(id, ""),
          Math.Round(cpu, 1),
          Math.Max(0, process.WorkingSet64 / 1024 / 1024),
          "",
          windowTitles.ContainsKey(id)));
      }
      catch
      {
        try { result.Add(new ProcessInfoPayload(process.Id, SafeProcessName(process), "", 0, 0, "", false)); }
        catch { }
      }
      finally { process.Dispose(); }
    }

    lock (ProcessSampleGate)
    {
      PreviousProcessSamples.Clear();
      foreach ((int id, ProcessSample sample) in currentSamples) PreviousProcessSamples[id] = sample;
    }

    return result.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).Take(2000).ToList();
  }

  private static IReadOnlyDictionary<int, string> CaptureMainWindowTitles()
  {
    var titles = new Dictionary<int, string>();
    EnumWindows((window, _) =>
    {
      if (!IsWindowVisible(window)) return true;
      int length = GetWindowTextLength(window);
      if (length <= 0) return true;
      GetWindowThreadProcessId(window, out uint processId);
      if (processId == 0 || titles.ContainsKey((int)processId)) return true;
      var text = new StringBuilder(length + 1);
      if (GetWindowText(window, text, text.Capacity) > 0)
        titles[(int)processId] = text.ToString();
      return true;
    }, IntPtr.Zero);
    return titles;
  }

  private static List<ProcessIconPayload> ListProcessIcons(ProcessIconsRequestPayload? request)
  {
    var result = new List<ProcessIconPayload>();
    foreach (int processId in (request?.ProcessIds ?? [])
      .Where(id => id > 0)
      .Distinct()
      .Take(8))
    {
      string icon = "";
      try
      {
        using Process process = Process.GetProcessById(processId);
        icon = GetProcessIconPng(process);
      }
      catch { }
      result.Add(new ProcessIconPayload(processId, icon));
    }
    return result;
  }

  private static string SafeProcessName(Process p)
  {
    try { return p.ProcessName; } catch { return "Unknown"; }
  }

  private static string GetProcessIconPng(Process p)
  {
    try
    {
      string? file = p.MainModule?.FileName;
      if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return "";
      return ProcessIconCache.GetOrAdd(file, static path =>
      {
        using System.Drawing.Icon? icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
        if (icon is null) return "";
        using var bitmap = icon.ToBitmap();
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        return Convert.ToBase64String(ms.ToArray());
      });
    }
    catch { return ""; }
  }
  private static OperationResultPayload KillProcess(int pid) { Process.GetProcessById(pid).Kill(true); return new(true, "进程已结束"); }
  private static List<ServiceInfoPayload> ListServices()
  {
    var result = new List<ServiceInfoPayload>();
    foreach (ServiceController service in ServiceController.GetServices())
    {
      try
      {
        service.Refresh();
        result.Add(new ServiceInfoPayload(
          service.ServiceName,
          service.DisplayName,
          service.Status.ToString(),
          ReadServiceStartType(service.ServiceName, service.StartType.ToString()),
          service.CanStop,
          service.CanPauseAndContinue));
      }
      catch
      {
        try { result.Add(new ServiceInfoPayload(service.ServiceName, service.DisplayName, "Unknown", "Unknown")); }
        catch { }
      }
      finally { service.Dispose(); }
    }
    return result.OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase).Take(2000).ToList();
  }

  private static RegistryReadResponsePayload ReadRegistry(RegistryReadPayload req)
  {
    using RegistryKey hive = OpenRegistryHive(req.Hive, req.View);
    RegistryKey? opened = string.IsNullOrWhiteSpace(req.SubKey) ? null : hive.OpenSubKey(req.SubKey);
    RegistryKey key = opened ?? (string.IsNullOrWhiteSpace(req.SubKey) ? hive : throw new InvalidOperationException("注册表键不存在。"));
    try
    {
      var values = new List<RegistryValuePayload>();
      foreach (string name in key.GetValueNames().Take(2000))
      {
        try
        {
          RegistryValueKind kind = key.GetValueKind(name);
          object? raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
          values.Add(new RegistryValuePayload(
            string.IsNullOrEmpty(name) ? "(默认)" : name,
            kind.ToString(),
            FormatRegistryValue(raw),
            raw as string,
            raw is string[] strings ? strings.ToList() : null,
            raw as byte[],
            raw switch
            {
              int number => number,
              long number => number,
              _ => null
            },
            name));
        }
        catch (Exception ex)
        {
          values.Add(new RegistryValuePayload(string.IsNullOrEmpty(name) ? "(默认)" : name, "Unknown", ex.Message));
        }
      }
      return new RegistryReadResponsePayload(
        string.IsNullOrWhiteSpace(req.SubKey) ? req.Hive : req.Hive + "\\" + req.SubKey,
        key.GetSubKeyNames().OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).Take(2000).ToList(),
        values);
    }
    finally { opened?.Dispose(); }
  }

  private static string FormatRegistryValue(object? value) => value switch
  {
    null => "(数值未设置)",
    byte[] bytes => BitConverter.ToString(bytes).Replace("-", " "),
    string[] strings => string.Join("; ", strings),
    _ => Convert.ToString(value) ?? string.Empty
  };

  private static RegistryKey OpenRegistryHive(string hive, RegistryViewMode view)
  {
    RegistryHive registryHive = hive.ToUpperInvariant() switch
    {
      "HKCU" => RegistryHive.CurrentUser,
      "HKLM" => RegistryHive.LocalMachine,
      "HKCR" => RegistryHive.ClassesRoot,
      "HKU" => RegistryHive.Users,
      "HKCC" => RegistryHive.CurrentConfig,
      _ => throw new InvalidOperationException("不支持的注册表根节点。")
    };
    RegistryView registryView = view switch
    {
      RegistryViewMode.Registry32 => RegistryView.Registry32,
      RegistryViewMode.Registry64 => RegistryView.Registry64,
      _ => RegistryView.Default
    };
    return RegistryKey.OpenBaseKey(registryHive, registryView);
  }

  private readonly record struct ProcessSample(TimeSpan TotalProcessorTime, DateTime TimestampUtc);

  private async Task SendAsync<T>(MessageType type, T payload, CancellationToken token)
  {
    if (_stream is null) return;
    await _sendLock.WaitAsync(token);
    try { await FramedJsonTransport.WriteAsync(_stream, new RemoteMessage { Type = type, DeviceId = _deviceId, DeviceName = Environment.MachineName, Payload = MessagePayload.ToElement(payload) }, token); }
    finally { _sendLock.Release(); }
  }
  private async Task ReplyAsync<T>(RemoteMessage request, MessageType type, T payload, CancellationToken token)
  {
    if (_stream is null) return;
    await _sendLock.WaitAsync(token);
    try { await FramedJsonTransport.WriteAsync(_stream, new RemoteMessage { RequestId = request.RequestId, Type = type, DeviceId = _deviceId, DeviceName = Environment.MachineName, Payload = MessagePayload.ToElement(payload) }, token); }
    finally { _sendLock.Release(); }
  }

  private void Disconnect()
  {
    ForceStopScreenStream();
    CloseCodexChannel();
    _cts?.Cancel();
    _cts = null;
    CloseUdpDesktopClients();
    _stream?.Dispose();
    _client?.Close();
    SetConnectionStatus(_everConnected ? "已断开" : "未链接");
  }
  private void SetConnectionStatus(string text)
  {
    if (_sessionHelper) QueueAgentStatus(text);
    void Apply()
    {
      StatusText.Text = text;
      StatusText.Foreground = text switch
      {
        "已链接" => System.Windows.Media.Brushes.LimeGreen,
        "已断开" => System.Windows.Media.Brushes.Red,
        _ => new System.Windows.Media.SolidColorBrush(
          System.Windows.Media.Color.FromRgb(244, 197, 66))
      };
    }
    if (Dispatcher.CheckAccess()) Apply();
    else _ = Dispatcher.BeginInvoke(Apply, DispatcherPriority.Background);
  }
}





