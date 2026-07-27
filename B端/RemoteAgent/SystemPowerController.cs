using Microsoft.Win32;
using RemoteControl.Shared;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace RemoteAgent;

internal static class SystemPowerController
{
  private const uint TokenAdjustPrivileges = 0x0020;
  private const uint TokenQuery = 0x0008;
  private const uint SePrivilegeEnabled = 0x00000002;
  private const uint ShutdownReasonMajorApplication = 0x00040000;
  private const uint ShutdownReasonMinorMaintenance = 0x00000001;
  private const uint ShutdownReasonFlagPlanned = 0x80000000;

  public static OperationResultPayload Queue(PowerAction action)
  {
    try
    {
      _ = action switch
      {
        PowerAction.Lock => "锁屏",
        PowerAction.Restart => "重启",
        PowerAction.Shutdown => "关机",
        PowerAction.SecureAttention => "安全注意序列",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
      };
      using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
      {
        if (!identity.IsSystem)
          throw new InvalidOperationException(
            "系统操作必须由B端LocalSystem会话助手执行。");
      }
      if (action is PowerAction.Restart or PowerAction.Shutdown)
        EnablePrivilege("SeShutdownPrivilege");

      _ = Task.Run(async () =>
      {
        await Task.Delay(500).ConfigureAwait(false);
        try { Execute(action); }
        catch { }
      });
      return new OperationResultPayload(true, "SYSTEM服务已接受系统操作。");
    }
    catch (Exception ex)
    {
      return new OperationResultPayload(false, ex.Message);
    }
  }

  private static void Execute(PowerAction action)
  {
    switch (action)
    {
      case PowerAction.Lock:
        if (!LockWorkStation())
          throw new Win32Exception(Marshal.GetLastWin32Error());
        break;
      case PowerAction.Restart:
        InitiateShutdown(reboot: true);
        break;
      case PowerAction.Shutdown:
        InitiateShutdown(reboot: false);
        break;
      case PowerAction.SecureAttention:
        SendSecureAttention();
        break;
      default:
        throw new ArgumentOutOfRangeException(nameof(action));
    }
  }

  private static void InitiateShutdown(bool reboot)
  {
    EnablePrivilege("SeShutdownPrivilege");
    uint reason = ShutdownReasonMajorApplication |
                  ShutdownReasonMinorMaintenance |
                  ShutdownReasonFlagPlanned;
    if (!InitiateSystemShutdownExW(
          null,
          reboot ? "远程请求正在重启此计算机。" : "远程请求正在关闭此计算机。",
          0,
          true,
          reboot,
          reason))
      throw new Win32Exception(Marshal.GetLastWin32Error());
  }

  private static void EnablePrivilege(string privilege)
  {
    if (!OpenProcessToken(
          GetCurrentProcess(),
          TokenAdjustPrivileges | TokenQuery,
          out IntPtr token))
      throw new Win32Exception(Marshal.GetLastWin32Error());
    try
    {
      if (!LookupPrivilegeValueW(null, privilege, out Luid luid))
        throw new Win32Exception(Marshal.GetLastWin32Error());
      var privileges = new TokenPrivileges
      {
        PrivilegeCount = 1,
        Privileges = new LuidAndAttributes
        {
          Luid = luid,
          Attributes = SePrivilegeEnabled
        }
      };
      Marshal.SetLastPInvokeError(0);
      if (!AdjustTokenPrivileges(
            token,
            false,
            ref privileges,
            0,
            IntPtr.Zero,
            IntPtr.Zero))
        throw new Win32Exception(Marshal.GetLastWin32Error());
      int error = Marshal.GetLastWin32Error();
      if (error != 0) throw new Win32Exception(error);
    }
    finally
    {
      CloseHandle(token);
    }
  }

  private static void SendSecureAttention()
  {
    const string path =
      @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    using RegistryKey? key = Registry.LocalMachine.CreateSubKey(
      path,
      writable: true);
    object? previous = key?.GetValue("SoftwareSASGeneration");
    try
    {
      key?.SetValue("SoftwareSASGeneration", 1, RegistryValueKind.DWord);
      SendSAS(false);
    }
    finally
    {
      if (key is not null)
      {
        if (previous is null)
          key.DeleteValue("SoftwareSASGeneration", throwOnMissingValue: false);
        else
          key.SetValue("SoftwareSASGeneration", previous, RegistryValueKind.DWord);
      }
    }
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct Luid
  {
    public uint LowPart;
    public int HighPart;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct LuidAndAttributes
  {
    public Luid Luid;
    public uint Attributes;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct TokenPrivileges
  {
    public uint PrivilegeCount;
    public LuidAndAttributes Privileges;
  }

  [DllImport("kernel32.dll")]
  private static extern IntPtr GetCurrentProcess();

  [DllImport("advapi32.dll", SetLastError = true)]
  private static extern bool OpenProcessToken(
    IntPtr process,
    uint desiredAccess,
    out IntPtr token);

  [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern bool LookupPrivilegeValueW(
    string? systemName,
    string name,
    out Luid luid);

  [DllImport("advapi32.dll", SetLastError = true)]
  private static extern bool AdjustTokenPrivileges(
    IntPtr token,
    bool disableAllPrivileges,
    ref TokenPrivileges newState,
    uint bufferLength,
    IntPtr previousState,
    IntPtr returnLength);

  [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern bool InitiateSystemShutdownExW(
    string? machineName,
    string? message,
    uint timeout,
    bool forceAppsClosed,
    bool rebootAfterShutdown,
    uint reason);

  [DllImport("user32.dll", SetLastError = true)]
  private static extern bool LockWorkStation();

  [DllImport("sas.dll", CallingConvention = CallingConvention.StdCall)]
  private static extern void SendSAS([MarshalAs(UnmanagedType.Bool)] bool asUser);

  [DllImport("kernel32.dll")]
  private static extern bool CloseHandle(IntPtr handle);
}
