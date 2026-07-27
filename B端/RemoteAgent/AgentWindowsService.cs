using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using RemoteControl.Shared;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using Microsoft.Win32;

namespace RemoteAgent;

internal static class AgentServiceBootstrap
{
  public const string ServiceName = "AuthorizedDeviceControlAgent";

  public static bool TryRelaunchElevated(string[] args)
  {
    try
    {
      string executable = Environment.ProcessPath
        ?? throw new InvalidOperationException("无法确定B端程序路径。");
      var start = new ProcessStartInfo
      {
        FileName = executable,
        UseShellExecute = true,
        Verb = "runas",
        WorkingDirectory = Path.GetDirectoryName(executable)
          ?? Environment.CurrentDirectory
      };
      foreach (string argument in args) start.ArgumentList.Add(argument);
      return Process.Start(start) is not null;
    }
    catch
    {
      return false;
    }
  }

  public static bool TryStartForCurrentUi(string[] args, Guid instanceId)
  {
    try
    {
      string host = ResolveControllerHost(args);
      string executable = Path.GetFullPath(
        Environment.ProcessPath
        ?? throw new InvalidOperationException("无法确定B端程序路径。"));
      if (TryStartExistingService(executable))
        return true;
      if (IsAdministrator())
      {
        return InstallAndStart(
          [
            "--connect", host,
            "--instance-id", instanceId.ToString("N")
          ]) == 0;
      }

      var start = new ProcessStartInfo
      {
        FileName = executable,
        UseShellExecute = true,
        Verb = "runas",
        WindowStyle = ProcessWindowStyle.Hidden,
        Arguments =
          $"--install-service --connect \"{host}\" " +
          $"--instance-id {instanceId:N}"
      };
      using Process? installer = Process.Start(start);
      if (installer is null) return false;
      if (!installer.WaitForExit(30000))
      {
        try { installer.Kill(true); } catch { }
        return false;
      }
      return installer.ExitCode == 0;
    }
    catch
    {
      return false;
    }
  }

  public static void TerminateStaleUiInstances()
  {
    int currentPid = Environment.ProcessId;
    string processName = Process.GetCurrentProcess().ProcessName;
    foreach (Process process in Process.GetProcessesByName(processName))
    {
      using (process)
      {
        try
        {
          if (process.Id == currentPid || process.MainWindowHandle == IntPtr.Zero)
            continue;
          process.Kill();
          process.WaitForExit(5000);
        }
        catch { }
      }
    }
  }

  public static void TerminateIncompatibleAgentProcesses()
  {
    int currentPid = Environment.ProcessId;
    string currentExecutable = Environment.ProcessPath ?? string.Empty;
    string currentVersion = FileVersionInfo.GetVersionInfo(currentExecutable).FileVersion
      ?? string.Empty;
    foreach (Process process in Process.GetProcesses())
    {
      using (process)
      {
        try
        {
          if (process.Id == currentPid) continue;
          FileVersionInfo? info = process.MainModule?.FileVersionInfo;
          if (info is null) continue;
          bool sameProduct = string.Equals(
            info.ProductName,
            "RemoteAgent",
            StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
              info.OriginalFilename,
              "RemoteAgent.dll",
              StringComparison.OrdinalIgnoreCase);
          if (!sameProduct || string.Equals(
                info.FileVersion,
                currentVersion,
                StringComparison.OrdinalIgnoreCase))
            continue;
          process.Kill(entireProcessTree: true);
          process.WaitForExit(5000);
        }
        catch { }
      }
    }
  }

  public static int InstallAndStart(string[] args)
  {
    try
    {
      string host = ResolveControllerHost(args);
      Guid instanceId = ReadInstanceId(args)
        ?? throw new InvalidOperationException("Missing remote session instance id.");
      string executable = Environment.ProcessPath
        ?? throw new InvalidOperationException("无法确定B端程序路径。");
      string imagePath =
        $"\"{executable}\" --service --connect \"{host}\" " +
        $"--instance-id {instanceId:N}";
      AgentSettingsPayload settings = AgentSettingsStore.Load();
      string startupType = settings.StartupEnabled ? "auto" : "demand";

      RunSc($"stop {ServiceName}", 5000, acceptFailure: true);
      RunSc($"delete {ServiceName}", 5000, acceptFailure: true);
      WaitForServiceDeletion();
      RunSc(
        $"create {ServiceName} binPath= \"{EscapeScValue(imagePath)}\" " +
        $"start= {startupType} obj= LocalSystem DisplayName= \"Agebt B端会话服务\"",
        15000,
        acceptFailure: false);
      RunSc(
        $"description {ServiceName} \"为Agebt B端提供锁屏和RDP会话内的采集与输入。\"",
        5000,
        acceptFailure: true);
      RunSc(
        $"failure {ServiceName} reset= 60 actions= restart/1000/restart/3000/restart/10000",
        5000,
        acceptFailure: true);
      RunSc($"start {ServiceName}", 15000, acceptFailure: false);
      return 0;
    }
    catch
    {
      return 1;
    }
  }

  public static void StopAndRemove()
  {
    RunSc($"stop {ServiceName}", 10000, acceptFailure: true);
    RunSc($"delete {ServiceName}", 10000, acceptFailure: true);
  }

  public static void StopOnly()
  {
    RunSc($"stop {ServiceName}", 10000, acceptFailure: true);
  }

  private static bool TryStartExistingService(string executable)
  {
    try
    {
      using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
        $@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
      string imagePath = Convert.ToString(key?.GetValue("ImagePath")) ?? string.Empty;
      if (imagePath.Length == 0 ||
          !imagePath.Contains(executable, StringComparison.OrdinalIgnoreCase))
        return false;

      using var service = new ServiceController(ServiceName);
      service.Refresh();
      if (service.Status == ServiceControllerStatus.StopPending)
      {
        service.WaitForStatus(
          ServiceControllerStatus.Stopped,
          TimeSpan.FromSeconds(15));
        service.Refresh();
      }
      if (service.Status == ServiceControllerStatus.Stopped)
      {
        service.Start();
        service.WaitForStatus(
          ServiceControllerStatus.Running,
          TimeSpan.FromSeconds(15));
        service.Refresh();
      }
      return service.Status is ServiceControllerStatus.Running
        or ServiceControllerStatus.StartPending;
    }
    catch
    {
      return false;
    }
  }

  public static void ConfigureStartup(bool enabled)
  {
    RunSc(
      $"config {ServiceName} start= {(enabled ? "auto" : "demand")}",
      10000,
      acceptFailure: false);
  }

  public static string? ReadArgument(string[] args, string name)
  {
    int index = Array.FindIndex(
      args,
      value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length
      ? args[index + 1]
      : null;
  }

  public static string ResolveControllerHost(string[] args)
  {
    string? explicitHost = ReadArgument(args, "--connect");
    if (!string.IsNullOrWhiteSpace(explicitHost))
      return explicitHost.Trim();
    string executable = Environment.ProcessPath ?? string.Empty;
    if (executable.Length > 0 &&
        RemoteControl.Shared.GeneratedAgentConfiguration.TryReadFromExecutable(
          executable,
          out string generatedHost))
      return generatedHost;
    return RemoteControl.Shared.NetworkDefaults.DefaultControllerHost;
  }

  public static Guid? ReadInstanceId(string[] args)
  {
    string? value = ReadArgument(args, "--instance-id");
    return Guid.TryParse(value, out Guid parsed) && parsed != Guid.Empty
      ? parsed
      : null;
  }

  internal static bool IsAdministrator()
  {
    using WindowsIdentity identity = WindowsIdentity.GetCurrent();
    return new WindowsPrincipal(identity).IsInRole(
      WindowsBuiltInRole.Administrator);
  }

  internal static int RunSc(
    string arguments,
    int timeoutMilliseconds,
    bool acceptFailure)
  {
    using var process = Process.Start(new ProcessStartInfo
    {
      FileName = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "sc.exe"),
      Arguments = arguments,
      UseShellExecute = false,
      CreateNoWindow = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true
    }) ?? throw new InvalidOperationException("无法启动服务控制器。");
    if (!process.WaitForExit(timeoutMilliseconds))
    {
      try { process.Kill(true); } catch { }
      throw new System.TimeoutException("Windows服务操作超时。");
    }
    if (!acceptFailure && process.ExitCode != 0)
      throw new Win32Exception(
        process.ExitCode,
        process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd());
    return process.ExitCode;
  }

  private static string EscapeScValue(string value) =>
    value.Replace("\"", "\\\"");

  private static void WaitForServiceDeletion()
  {
    for (int attempt = 0; attempt < 40; attempt++)
    {
      if (RunSc(
            $"query {ServiceName}",
            3000,
            acceptFailure: true) != 0)
        return;
      Thread.Sleep(250);
    }
    throw new System.TimeoutException("旧的SYSTEM远程控制服务未能及时退出。");
  }
}

internal sealed class AgentWindowsService : ServiceBase
{
  private readonly string _host;
  private readonly string _executable;
  private readonly Guid _instanceId;
  private readonly object _gate = new();
  private System.Threading.Timer? _watchdog;
  private Process? _helper;
  private int _helperSession = -1;
  private Process? _statusUi;
  private int _statusUiSession = -1;
  private long _lastStatusUiCheck;
  private int _stopping;

  public AgentWindowsService(string[] args)
  {
    ServiceName = AgentServiceBootstrap.ServiceName;
    CanStop = true;
    CanShutdown = true;
    CanHandleSessionChangeEvent = true;
    AutoLog = false;
    _host = AgentServiceBootstrap.ResolveControllerHost(args);
    _instanceId = AgentServiceBootstrap.ReadInstanceId(args)
      ?? throw new InvalidOperationException("Missing remote session instance id.");
    _executable = Environment.ProcessPath
      ?? throw new InvalidOperationException("无法确定B端程序路径。");
  }

  protected override void OnStart(string[] args)
  {
    RestartHelper();
    _watchdog = new System.Threading.Timer(
      _ => WatchdogTick(),
      null,
      TimeSpan.FromMilliseconds(100),
      TimeSpan.FromMilliseconds(200));
  }

  protected override void OnStop()
  {
    Interlocked.Exchange(ref _stopping, 1);
    _watchdog?.Dispose();
    _watchdog = null;
    StopHelper();
    StopStatusUi();
    SendStatus("已断开");
  }

  protected override void OnShutdown() => OnStop();

  protected override void OnSessionChange(
    SessionChangeDescription changeDescription)
  {
    base.OnSessionChange(changeDescription);
    ThreadPool.QueueUserWorkItem(_ =>
    {
      Thread.Sleep(250);
      if (Volatile.Read(ref _stopping) != 0) return;
      int activeSession = NativeSession.FindActiveSessionId();
      lock (_gate)
      {
        if (activeSession >= 0 &&
            (_helper is null ||
             _helper.HasExited ||
             _helperSession != activeSession))
          RestartHelperCore(activeSession);
      }
    });
  }

  private void WatchdogTick()
  {
    if (Volatile.Read(ref _stopping) != 0) return;
    int activeSession = NativeSession.FindActiveSessionId();
    lock (_gate)
    {
      if (activeSession >= 0 &&
          (_helper is null || _helper.HasExited || _helperSession != activeSession))
        RestartHelperCore(activeSession);
    }
    if (Environment.TickCount64 - Interlocked.Read(ref _lastStatusUiCheck) >= 1000)
    {
      Interlocked.Exchange(ref _lastStatusUiCheck, Environment.TickCount64);
      ReconcileStatusUi(activeSession);
    }
  }

  private void RestartHelper()
  {
    if (Volatile.Read(ref _stopping) != 0) return;
    int sessionId = NativeSession.FindActiveSessionId();
    if (sessionId < 0)
    {
      SendStatus("未链接");
      return;
    }
    lock (_gate) RestartHelperCore(sessionId);
  }

  private void RestartHelperCore(int sessionId)
  {
    StopHelperCore();
    SendStatus("已断开");
    try
    {
      int pid = NativeSession.CreateSystemProcessInSession(
        sessionId,
        _executable,
        $"\"{_executable}\" --session-helper --connect \"{_host}\" " +
        $"--instance-id {_instanceId:N}");
      _helper = Process.GetProcessById(pid);
      _helperSession = sessionId;
    }
    catch
    {
      _helper = null;
      _helperSession = -1;
      SendStatus("已断开");
    }
  }

  private void StopHelper()
  {
    lock (_gate) StopHelperCore();
  }

  private void ReconcileStatusUi(int sessionId)
  {
    lock (_gate)
    {
      AgentSettingsPayload settings = AgentSettingsStore.Load();
      if (settings.HideTray || sessionId < 0)
      {
        StopStatusUiCore();
        return;
      }
      try
      {
        if (_statusUi is not null &&
            !_statusUi.HasExited &&
            _statusUiSession == sessionId)
          return;
      }
      catch { }
      StopStatusUiCore();
      Process? existing = FindExistingStatusUi(sessionId);
      if (existing is not null)
      {
        _statusUi = existing;
        _statusUiSession = sessionId;
        return;
      }
      try
      {
        int pid = NativeSession.CreateSystemProcessInSession(
          sessionId,
          _executable,
          $"--status-ui --connect \"{_host}\" --instance-id {_instanceId:N}");
        _statusUi = Process.GetProcessById(pid);
        _statusUiSession = sessionId;
      }
      catch
      {
        _statusUi = null;
        _statusUiSession = -1;
      }
    }
  }

  private Process? FindExistingStatusUi(int sessionId)
  {
    string processName = Path.GetFileNameWithoutExtension(_executable);
    foreach (Process process in Process.GetProcessesByName(processName))
    {
      try
      {
        if (process.Id != _helper?.Id &&
            process.SessionId == sessionId &&
            string.Equals(
              process.MainModule?.FileName,
              _executable,
              StringComparison.OrdinalIgnoreCase))
          return process;
      }
      catch { }
      process.Dispose();
    }
    return null;
  }

  private void StopStatusUi()
  {
    lock (_gate) StopStatusUiCore();
  }

  private void StopStatusUiCore()
  {
    try
    {
      if (_statusUi is not null && !_statusUi.HasExited)
        _statusUi.Kill(true);
    }
    catch { }
    _statusUi?.Dispose();
    _statusUi = null;
    _statusUiSession = -1;
  }

  private void StopHelperCore()
  {
    try
    {
      if (_helper is not null && !_helper.HasExited)
        _helper.Kill(true);
    }
    catch { }
    _helper?.Dispose();
    _helper = null;
    _helperSession = -1;
  }

  private static void SendStatus(string status)
  {
    try
    {
      using var pipe = new NamedPipeClientStream(
        ".",
        "AuthorizedDeviceControl.AgentStatus",
        PipeDirection.Out);
      pipe.Connect(500);
      using var writer = new StreamWriter(
        pipe,
        new UTF8Encoding(false),
        1024,
        true)
      {
        AutoFlush = true
      };
      writer.WriteLine(status);
    }
    catch { }
  }
}

internal static class NativeSession
{
  private const int WtsCurrentServerHandle = 0;
  private const int WtsActive = 0;
  private const uint TokenAssignPrimary = 0x0001;
  private const uint TokenDuplicate = 0x0002;
  private const uint TokenQuery = 0x0008;
  private const uint TokenAdjustSessionId = 0x0100;
  private const uint MaximumAllowed = 0x02000000;
  private const int SecurityImpersonation = 2;
  private const int TokenPrimary = 1;
  private const int TokenUiAccess = 26;
  private const uint CreateUnicodeEnvironment = 0x00000400;
  private const uint CreateNoWindow = 0x08000000;

  public static int FindActiveSessionId()
  {
    IntPtr sessions = IntPtr.Zero;
    try
    {
      if (WTSEnumerateSessionsW(
            IntPtr.Zero,
            0,
            1,
            out sessions,
            out int count))
      {
        int size = Marshal.SizeOf<WtsSessionInfo>();
        for (int index = 0; index < count; index++)
        {
          WtsSessionInfo info = Marshal.PtrToStructure<WtsSessionInfo>(
            IntPtr.Add(sessions, index * size));
          if (info.State == WtsActive) return info.SessionId;
        }
      }
    }
    finally
    {
      if (sessions != IntPtr.Zero) WTSFreeMemory(sessions);
    }
    uint console = WTSGetActiveConsoleSessionId();
    return console == uint.MaxValue ? -1 : unchecked((int)console);
  }

  public static int CreateSystemProcessInSession(
    int sessionId,
    string application,
    string commandLine)
  {
    using Process sessionSystemProcess = Process
      .GetProcessesByName("winlogon")
      .FirstOrDefault(process => process.SessionId == sessionId)
      ?? throw new InvalidOperationException(
        $"找不到会话 {sessionId} 的SYSTEM交互进程。");
    if (!OpenProcessToken(
          sessionSystemProcess.Handle,
          TokenAssignPrimary |
          TokenDuplicate |
          TokenQuery |
          TokenAdjustSessionId,
          out IntPtr processToken))
      throw new Win32Exception(Marshal.GetLastWin32Error());
    IntPtr primaryToken = IntPtr.Zero;
    IntPtr environment = IntPtr.Zero;
    try
    {
      if (!DuplicateTokenEx(
            processToken,
            MaximumAllowed,
            IntPtr.Zero,
            SecurityImpersonation,
            TokenPrimary,
            out primaryToken))
        throw new Win32Exception(Marshal.GetLastWin32Error());
      int uiAccess = 1;
      if (!SetTokenInformation(
            primaryToken,
            TokenUiAccess,
            ref uiAccess,
            sizeof(int)))
        throw new Win32Exception(Marshal.GetLastWin32Error());
      if (!CreateEnvironmentBlock(
            out environment,
            primaryToken,
            false))
        environment = IntPtr.Zero;

      var startup = new StartupInfo
      {
        cb = Marshal.SizeOf<StartupInfo>(),
        lpDesktop = @"winsta0\default"
      };
      var command = new StringBuilder(commandLine);
      if (!CreateProcessAsUserW(
            primaryToken,
            application,
            command,
            IntPtr.Zero,
            IntPtr.Zero,
            false,
            CreateUnicodeEnvironment | CreateNoWindow,
            environment,
            Path.GetDirectoryName(application),
            ref startup,
            out ProcessInformation process))
        throw new Win32Exception(Marshal.GetLastWin32Error());
      try { return unchecked((int)process.dwProcessId); }
      finally
      {
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
      }
    }
    finally
    {
      if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
      if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
      CloseHandle(processToken);
    }
  }

  public static int CreateUserShellProcessInSession(
    int sessionId,
    string target,
    string arguments,
    string workingDirectory)
  {
    using Process userShell = Process
      .GetProcessesByName("explorer")
      .FirstOrDefault(process => process.SessionId == sessionId)
      ?? throw new InvalidOperationException(
        $"找不到会话 {sessionId} 的用户桌面进程。");
    if (!OpenProcessToken(
          userShell.Handle,
          TokenAssignPrimary | TokenDuplicate | TokenQuery,
          out IntPtr processToken))
      throw new Win32Exception(Marshal.GetLastWin32Error());
    IntPtr primaryToken = IntPtr.Zero;
    IntPtr environment = IntPtr.Zero;
    try
    {
      if (!DuplicateTokenEx(
            processToken,
            MaximumAllowed,
            IntPtr.Zero,
            SecurityImpersonation,
            TokenPrimary,
            out primaryToken))
        throw new Win32Exception(Marshal.GetLastWin32Error());
      if (!CreateEnvironmentBlock(out environment, primaryToken, false))
        environment = IntPtr.Zero;

      string commandProcessor = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "cmd.exe");
      string escapedTarget = target.Replace("\"", "\"\"");
      string commandLine =
        $"\"{commandProcessor}\" /d /s /c start \"\" \"{escapedTarget}\" {arguments}";
      var startup = new StartupInfo
      {
        cb = Marshal.SizeOf<StartupInfo>(),
        lpDesktop = @"winsta0\default"
      };
      var command = new StringBuilder(commandLine);
      if (!CreateProcessAsUserW(
            primaryToken,
            commandProcessor,
            command,
            IntPtr.Zero,
            IntPtr.Zero,
            false,
            CreateUnicodeEnvironment | CreateNoWindow,
            environment,
            Directory.Exists(workingDirectory)
              ? workingDirectory
              : Path.GetDirectoryName(target),
            ref startup,
            out ProcessInformation process))
        throw new Win32Exception(Marshal.GetLastWin32Error());
      try { return unchecked((int)process.dwProcessId); }
      finally
      {
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
      }
    }
    finally
    {
      if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
      if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
      CloseHandle(processToken);
    }
  }

  public static int CreateUserProcessInSession(
    int sessionId,
    string application,
    string arguments)
  {
    using Process userShell = Process
      .GetProcessesByName("explorer")
      .FirstOrDefault(process => process.SessionId == sessionId)
      ?? throw new InvalidOperationException(
        $"找不到会话 {sessionId} 的用户桌面进程。");
    if (!OpenProcessToken(
          userShell.Handle,
          TokenAssignPrimary | TokenDuplicate | TokenQuery,
          out IntPtr processToken))
      throw new Win32Exception(Marshal.GetLastWin32Error());
    IntPtr primaryToken = IntPtr.Zero;
    IntPtr environment = IntPtr.Zero;
    try
    {
      if (!DuplicateTokenEx(
            processToken,
            MaximumAllowed,
            IntPtr.Zero,
            SecurityImpersonation,
            TokenPrimary,
            out primaryToken))
        throw new Win32Exception(Marshal.GetLastWin32Error());
      if (!CreateEnvironmentBlock(out environment, primaryToken, false))
        environment = IntPtr.Zero;
      var startup = new StartupInfo
      {
        cb = Marshal.SizeOf<StartupInfo>(),
        lpDesktop = @"winsta0\default"
      };
      var command = new StringBuilder($"\"{application}\" {arguments}");
      if (!CreateProcessAsUserW(
            primaryToken,
            application,
            command,
            IntPtr.Zero,
            IntPtr.Zero,
            false,
            CreateUnicodeEnvironment,
            environment,
            Path.GetDirectoryName(application),
            ref startup,
            out ProcessInformation process))
        throw new Win32Exception(Marshal.GetLastWin32Error());
      try { return unchecked((int)process.dwProcessId); }
      finally
      {
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
      }
    }
    finally
    {
      if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
      if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
      CloseHandle(processToken);
    }
  }

  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  private struct WtsSessionInfo
  {
    public int SessionId;
    public IntPtr WinStationName;
    public int State;
  }

  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  private struct StartupInfo
  {
    public int cb;
    public string? lpReserved;
    public string? lpDesktop;
    public string? lpTitle;
    public int dwX;
    public int dwY;
    public int dwXSize;
    public int dwYSize;
    public int dwXCountChars;
    public int dwYCountChars;
    public int dwFillAttribute;
    public int dwFlags;
    public short wShowWindow;
    public short cbReserved2;
    public IntPtr lpReserved2;
    public IntPtr hStdInput;
    public IntPtr hStdOutput;
    public IntPtr hStdError;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct ProcessInformation
  {
    public IntPtr hProcess;
    public IntPtr hThread;
    public uint dwProcessId;
    public uint dwThreadId;
  }

  [DllImport("wtsapi32.dll", SetLastError = true)]
  private static extern bool WTSEnumerateSessionsW(
    IntPtr server,
    int reserved,
    int version,
    out IntPtr sessions,
    out int count);

  [DllImport("wtsapi32.dll")]
  private static extern void WTSFreeMemory(IntPtr memory);


  [DllImport("kernel32.dll")]
  private static extern uint WTSGetActiveConsoleSessionId();

  [DllImport("advapi32.dll", SetLastError = true)]
  private static extern bool OpenProcessToken(
    IntPtr process,
    uint desiredAccess,
    out IntPtr token);

  [DllImport("advapi32.dll", SetLastError = true)]
  private static extern bool DuplicateTokenEx(
    IntPtr existingToken,
    uint desiredAccess,
    IntPtr tokenAttributes,
    int impersonationLevel,
    int tokenType,
    out IntPtr newToken);

  [DllImport("advapi32.dll", SetLastError = true)]
  private static extern bool SetTokenInformation(
    IntPtr token,
    int tokenInformationClass,
    ref int tokenInformation,
    int tokenInformationLength);


  [DllImport("userenv.dll", SetLastError = true)]
  private static extern bool CreateEnvironmentBlock(
    out IntPtr environment,
    IntPtr token,
    bool inherit);

  [DllImport("userenv.dll")]
  private static extern bool DestroyEnvironmentBlock(IntPtr environment);

  [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern bool CreateProcessAsUserW(
    IntPtr token,
    string applicationName,
    StringBuilder commandLine,
    IntPtr processAttributes,
    IntPtr threadAttributes,
    bool inheritHandles,
    uint creationFlags,
    IntPtr environment,
    string? currentDirectory,
    ref StartupInfo startupInfo,
    out ProcessInformation processInformation);

  [DllImport("kernel32.dll", SetLastError = true)]
  private static extern bool CloseHandle(IntPtr handle);
}
