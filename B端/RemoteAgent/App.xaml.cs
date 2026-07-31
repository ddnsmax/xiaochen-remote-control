using System.ServiceProcess;
using System.Threading;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace RemoteAgent;

public partial class App : System.Windows.Application
{
  private const int SwRestore = 9;
  private Mutex? _singleInstanceMutex;

  [DllImport("user32.dll")]
  private static extern bool ShowWindow(IntPtr window, int command);
  [DllImport("user32.dll")]
  private static extern bool SetForegroundWindow(IntPtr window);

  protected override void OnStartup(StartupEventArgs e)
  {
    string[] args = e.Args;
    bool helper = args.Contains("--session-helper", StringComparer.OrdinalIgnoreCase);
    bool statusUi = args.Contains("--status-ui", StringComparer.OrdinalIgnoreCase);
    if (helper)
      WindowsInputDesktop.AttachProcessToInteractiveWindowStation();
    base.OnStartup(e);
    if (args.Contains("--service", StringComparer.OrdinalIgnoreCase))
    {
      ShutdownMode = ShutdownMode.OnExplicitShutdown;
      ServiceBase.Run(new AgentWindowsService(args));
      Shutdown();
      return;
    }
    if (args.Contains("--install-service", StringComparer.OrdinalIgnoreCase))
    {
      ShutdownMode = ShutdownMode.OnExplicitShutdown;
      int exitCode = AgentServiceBootstrap.InstallAndStart(args);
      Environment.ExitCode = exitCode;
      Shutdown(exitCode);
      return;
    }

    bool integrationTest = string.Equals(
      Environment.GetEnvironmentVariable("ADC_INTEGRATION_TEST"),
      "1",
      StringComparison.Ordinal);
    if (!helper &&
        !statusUi &&
        !integrationTest &&
        !AgentServiceBootstrap.IsAdministrator())
    {
      if (!AgentServiceBootstrap.TryRelaunchElevated(args))
        System.Windows.MessageBox.Show(
          "B端需要管理员权限才能提供完整的远程控制。",
          "Agebt B端",
          MessageBoxButton.OK,
          MessageBoxImage.Error);
      Shutdown();
      return;
    }
    if (!helper && !statusUi && !integrationTest)
      AgentServiceBootstrap.TerminateIncompatibleAgentProcesses();
    if (!helper && !integrationTest)
    {
      string executablePath = Path.GetFullPath(
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName
        ?? "RemoteAgent.exe");
      string pathIdentity = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(executablePath.ToUpperInvariant())))[..16];
      _singleInstanceMutex = new Mutex(
        initiallyOwned: true,
        name: $@"Local\AuthorizedDeviceControl.RemoteAgent.Ui.{pathIdentity}",
        createdNew: out bool createdNew);
      if (!createdNew)
      {
        ActivateExistingWindow();
        _singleInstanceMutex.Dispose();
        _singleInstanceMutex = null;
        Shutdown();
        return;
      }
    }
    if (!helper && !integrationTest)
      AgentServiceBootstrap.TerminateStaleUiInstances();
    Guid instanceId = AgentServiceBootstrap.ReadInstanceId(args) ?? Guid.NewGuid();
    bool serviceStarted = !helper &&
                          !statusUi &&
                          !integrationTest &&
                          AgentServiceBootstrap.TryStartForCurrentUi(args, instanceId);
    if (!helper && !statusUi && !integrationTest && !serviceStarted)
    {
      System.Windows.MessageBox.Show(
        "SYSTEM远程控制服务启动失败，B端不会以低权限模式继续运行。",
        "Agebt B端",
        MessageBoxButton.OK,
        MessageBoxImage.Error);
      Shutdown();
      return;
    }
    var window = new MainWindow(
      sessionHelper: helper,
      statusOnly: statusUi || !helper && !integrationTest,
      instanceId: instanceId,
      startHidden: statusUi,
      serviceOwnedStatusUi: statusUi);
    MainWindow = window;
    window.Show();
  }

  private static void ActivateExistingWindow()
  {
    try
    {
      int currentId = Environment.ProcessId;
      foreach (Process process in Process.GetProcessesByName(
                 Process.GetCurrentProcess().ProcessName))
      {
        using (process)
        {
          if (process.Id == currentId || process.MainWindowHandle == IntPtr.Zero)
            continue;
          ShowWindow(process.MainWindowHandle, SwRestore);
          SetForegroundWindow(process.MainWindowHandle);
          return;
        }
      }
    }
    catch { }
  }

  protected override void OnExit(ExitEventArgs e)
  {
    try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
    _singleInstanceMutex?.Dispose();
    _singleInstanceMutex = null;
    base.OnExit(e);
  }
}
