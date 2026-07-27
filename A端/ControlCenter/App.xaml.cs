using System.Threading;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace ControlCenter;

public partial class App : Application
{
  private const int SwRestore = 9;
  private Mutex? _singleInstanceMutex;

  [DllImport("user32.dll")]
  private static extern bool ShowWindow(IntPtr window, int command);
  [DllImport("user32.dll")]
  private static extern bool SetForegroundWindow(IntPtr window);

  protected override void OnStartup(StartupEventArgs e)
  {
    int generatorIndex = Array.FindIndex(
      e.Args,
      value => value.Equals(
        "--generate-agent",
        StringComparison.OrdinalIgnoreCase));
    if (generatorIndex >= 0)
    {
      ShutdownMode = ShutdownMode.OnExplicitShutdown;
      base.OnStartup(e);
      try
      {
        if (generatorIndex + 2 >= e.Args.Length)
          throw new InvalidOperationException(
            "--generate-agent requires a host and output path.");
        AgentPackageGenerator.Generate(
          e.Args[generatorIndex + 1],
          e.Args[generatorIndex + 2]);
        Environment.ExitCode = 0;
      }
      catch
      {
        Environment.ExitCode = 1;
      }
      Shutdown(Environment.ExitCode);
      return;
    }

    _singleInstanceMutex = new Mutex(
      initiallyOwned: true,
      name: @"Local\AuthorizedDeviceControl.ControlCenter",
      createdNew: out bool createdNew);
    if (!createdNew)
    {
      ActivateExistingWindow();
      _singleInstanceMutex.Dispose();
      _singleInstanceMutex = null;
      Environment.Exit(0);
      return;
    }
    base.OnStartup(e);
    var window = new MainWindow();
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
