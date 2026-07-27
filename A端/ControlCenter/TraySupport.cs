using System.ComponentModel;
using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace ControlCenter;

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
      Text = "小陈远控QQ;3890053645",
      Visible = true
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
    bool confirmed = ConfirmationWindow.Show(
      this,
      "确认退出",
      "是否退出A端？退出后将停止监听并断开当前设备连接。");
    if (confirmed)
    {
      _allowApplicationExit = true;
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
    _allowApplicationExit = true;
    BeginExitWatchdog();
    Close();
  }

  private void BeginExitWatchdog()
  {
    if (Interlocked.Exchange(ref _exitWatchdogStarted, 1) != 0) return;
    ThreadPool.QueueUserWorkItem(_ =>
    {
      Thread.Sleep(750);
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
