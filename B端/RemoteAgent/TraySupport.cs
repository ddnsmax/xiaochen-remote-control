using System.ComponentModel;
using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace RemoteAgent;

public partial class MainWindow
{
  private Forms.NotifyIcon? _trayIcon;
  private Icon? _applicationTrayIcon;
  private bool _allowApplicationExit;
  private bool _trayDisposed;
  private int _exitWatchdogStarted;

  private void InitializeTraySupport()
  {
    try
    {
      string? executable = Environment.ProcessPath;
      if (!string.IsNullOrWhiteSpace(executable))
        _applicationTrayIcon = System.Drawing.Icon.ExtractAssociatedIcon(executable);
    }
    catch { }
    _trayIcon = new Forms.NotifyIcon
    {
      Icon = _applicationTrayIcon ?? SystemIcons.Application,
      Text = "Agebt B端",
      Visible = !AgentSettingsStore.Load().HideTray
    };
    var menu = new Forms.ContextMenuStrip();
    menu.Items.Add("打开窗口", null, (_, _) => Dispatcher.BeginInvoke(RestoreFromTray));
    menu.Items.Add(new Forms.ToolStripSeparator());
    menu.Items.Add("退出", null, (_, _) => Dispatcher.BeginInvoke(ExitFromTray));
    _trayIcon.ContextMenuStrip = menu;
    _trayIcon.MouseDoubleClick += (_, e) =>
    {
      if (e.Button == Forms.MouseButtons.Left)
        Dispatcher.BeginInvoke(RestoreFromTray);
    };
    Closing += MainWindow_ClosingForTray;
    StateChanged += MainWindow_StateChangedForTray;
  }

  private void MainWindow_ClosingForTray(object? sender, CancelEventArgs e)
  {
    if (_allowApplicationExit) return;
    MessageBoxResult result = System.Windows.MessageBox.Show(
      this,
      "是否退出B端？退出后将断开与A端的连接。",
      "确认退出",
      MessageBoxButton.YesNo,
      MessageBoxImage.Question,
      MessageBoxResult.No);
    if (result == MessageBoxResult.Yes)
    {
      e.Cancel = true;
      BeginExitWatchdog();
    }
    else
      e.Cancel = true;
  }

  private void MainWindow_StateChangedForTray(object? sender, EventArgs e)
  {
    if (WindowState != WindowState.Minimized) return;
    Dispatcher.BeginInvoke(() =>
    {
      ShowInTaskbar = false;
      Hide();
    });
  }

  private void RestoreFromTray()
  {
    if (_trayDisposed) return;
    Show();
    ShowInTaskbar = true;
    WindowState = WindowState.Normal;
    Activate();
    Focus();
  }

  private void ExitFromTray()
  {
    BeginExitWatchdog();
  }

  private void AllowTrayExitWithoutStoppingService()
  {
    _allowApplicationExit = true;
  }

  private void UpdateTrayVisibility(bool visible)
  {
    if (_trayDisposed || _trayIcon is null) return;
    _trayIcon.Visible = visible;
  }

  private void BeginExitWatchdog()
  {
    if (Interlocked.Exchange(ref _exitWatchdogStarted, 1) != 0) return;
    Hide();
    ShowInTaskbar = false;
    ThreadPool.QueueUserWorkItem(_ =>
    {
      try { AgentServiceBootstrap.StopOnly(); }
      catch { }
      Dispatcher.BeginInvoke(() =>
      {
        _allowApplicationExit = true;
        Close();
        System.Windows.Application.Current.Shutdown();
      });
    });
    ThreadPool.QueueUserWorkItem(_ =>
    {
      Thread.Sleep(12000);
      Environment.Exit(0);
    });
  }

  private void DisposeTraySupport()
  {
    if (_trayDisposed) return;
    _trayDisposed = true;
    if (_trayIcon is not null)
    {
      _trayIcon.Visible = false;
      _trayIcon.ContextMenuStrip?.Dispose();
      _trayIcon.Dispose();
      _trayIcon = null;
    }
    _applicationTrayIcon?.Dispose();
    _applicationTrayIcon = null;
  }
}
