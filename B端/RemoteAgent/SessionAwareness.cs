using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;

namespace RemoteAgent;

public partial class MainWindow
{
  private long _desktopEnvironmentGeneration;
  private bool _sessionEventsSubscribed;

  private void InitializeSessionAwareness()
  {
    if (_sessionEventsSubscribed) return;
    SystemEvents.SessionSwitch += OnWindowsSessionSwitch;
    SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    _sessionEventsSubscribed = true;
  }

  private void DisposeSessionAwareness()
  {
    if (!_sessionEventsSubscribed) return;
    SystemEvents.SessionSwitch -= OnWindowsSessionSwitch;
    SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
    _sessionEventsSubscribed = false;
  }

  private void OnWindowsSessionSwitch(
    object sender,
    SessionSwitchEventArgs args) =>
    ResetDesktopEnvironment();

  private void OnDisplaySettingsChanged(object? sender, EventArgs args) =>
    ResetDesktopEnvironment();

  private void ResetDesktopEnvironment()
  {
    Interlocked.Increment(ref _desktopEnvironmentGeneration);
    _inputDispatcher.QueueReleaseAll(
      Interlocked.Read(ref _desktopEnvironmentGeneration));
    try { _ = WindowsAgentEnvironment.EnsureCodexWorkspace(); } catch { }
  }
}

internal static class WindowsInputDesktop
{
  private static IntPtr _interactiveWindowStation;
  [ThreadStatic] private static IntPtr _attachedDesktop;
  [ThreadStatic] private static long _attachedGeneration;
  private const uint DesktopReadObjects = 0x0001;
  private const uint DesktopCreateWindow = 0x0002;
  private const uint DesktopCreateMenu = 0x0004;
  private const uint DesktopHookControl = 0x0008;
  private const uint DesktopEnumerate = 0x0040;
  private const uint DesktopWriteObjects = 0x0080;
  private const uint DesktopSwitchDesktop = 0x0100;
  private const uint GenericWrite = 0x40000000;
  private const uint WindowStationAllAccess = 0x037F;
  private const int UoiName = 2;

  public static void AttachProcessToInteractiveWindowStation()
  {
    if (_interactiveWindowStation != IntPtr.Zero) return;
    IntPtr station = OpenWindowStation(
      "WinSta0",
      false,
      WindowStationAllAccess);
    if (station == IntPtr.Zero)
      throw new System.ComponentModel.Win32Exception(
        Marshal.GetLastWin32Error(),
        "无法打开Windows交互窗口站。");
    if (!SetProcessWindowStation(station))
    {
      int error = Marshal.GetLastWin32Error();
      CloseWindowStation(station);
      throw new System.ComponentModel.Win32Exception(
        error,
        "无法绑定Windows交互窗口站。");
    }
    _interactiveWindowStation = station;
  }

  public static bool TryAttachCurrentThread(long generation)
  {
    try
    {
      AttachCurrentThread(generation);
      return true;
    }
    catch
    {
      return false;
    }
  }

  public static void AttachCurrentThread(long generation)
  {
    if (_attachedDesktop != IntPtr.Zero &&
        _attachedGeneration == generation &&
        IsCurrentThreadOnInputDesktop())
      return;
    IntPtr desktop = OpenInputDesktop(
      0,
      false,
      DesktopReadObjects |
      DesktopCreateWindow |
      DesktopCreateMenu |
      DesktopHookControl |
      DesktopEnumerate |
      DesktopWriteObjects |
      DesktopSwitchDesktop |
      GenericWrite);
    if (desktop == IntPtr.Zero)
      throw new System.ComponentModel.Win32Exception(
        Marshal.GetLastWin32Error(),
        "无法打开当前Windows输入桌面。");
    IntPtr current = GetThreadDesktop(GetCurrentThreadId());
    if (DesktopNamesEqual(current, desktop))
    {
      _attachedGeneration = generation;
      CloseDesktop(desktop);
      return;
    }
    if (!SetThreadDesktop(desktop))
    {
      CloseDesktop(desktop);
      throw new System.ComponentModel.Win32Exception(
        Marshal.GetLastWin32Error(),
        "无法附着当前Windows输入桌面。");
    }
    // A desktop handle assigned to the current thread cannot be closed until
    // that thread exits. Keep one handle per worker thread and replace only
    // when Windows exposes a new input desktop.
    IntPtr previous = _attachedDesktop;
    _attachedDesktop = desktop;
    _attachedGeneration = generation;
    if (previous != IntPtr.Zero) CloseDesktop(previous);
  }

  public static bool IsCurrentThreadOnInputDesktop()
  {
    IntPtr input = OpenInputDesktop(
      0,
      false,
      DesktopReadObjects |
      DesktopEnumerate |
      DesktopSwitchDesktop |
      GenericWrite);
    if (input == IntPtr.Zero) return false;
    try
    {
      return DesktopNamesEqual(
        GetThreadDesktop(GetCurrentThreadId()),
        input);
    }
    finally
    {
      CloseDesktop(input);
    }
  }

  private static bool DesktopNamesEqual(IntPtr left, IntPtr right)
  {
    string? leftName = ReadDesktopName(left);
    string? rightName = ReadDesktopName(right);
    return leftName is not null &&
           rightName is not null &&
           string.Equals(leftName, rightName, StringComparison.OrdinalIgnoreCase);
  }

  private static string? ReadDesktopName(IntPtr desktop)
  {
    if (desktop == IntPtr.Zero) return null;
    var name = new StringBuilder(256);
    return GetUserObjectInformationW(
      desktop,
      UoiName,
      name,
      name.Capacity * sizeof(char),
      out _)
      ? name.ToString()
      : null;
  }

  [DllImport("user32.dll", SetLastError = true)]
  private static extern IntPtr OpenInputDesktop(
    uint flags,
    bool inherit,
    uint desiredAccess);

  [DllImport("user32.dll", SetLastError = true)]
  private static extern bool SetThreadDesktop(IntPtr desktop);

  [DllImport("user32.dll", SetLastError = true)]
  private static extern bool CloseDesktop(IntPtr desktop);

  [DllImport("user32.dll")]
  private static extern IntPtr GetThreadDesktop(uint threadId);

  [DllImport("kernel32.dll")]
  private static extern uint GetCurrentThreadId();

  [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern bool GetUserObjectInformationW(
    IntPtr handle,
    int index,
    StringBuilder information,
    int length,
    out int needed);

  [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern IntPtr OpenWindowStation(
    string windowStation,
    bool inherit,
    uint desiredAccess);

  [DllImport("user32.dll", SetLastError = true)]
  private static extern bool SetProcessWindowStation(IntPtr windowStation);

  [DllImport("user32.dll", SetLastError = true)]
  private static extern bool CloseWindowStation(IntPtr windowStation);
}
