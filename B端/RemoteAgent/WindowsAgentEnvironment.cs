using System.IO;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace RemoteAgent;

internal static class WindowsAgentEnvironment
{
  private static readonly Guid DesktopFolderId =
    new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");

  public static string LoadOrCreateMachineDeviceId()
  {
    string machineDirectory = Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
      "AuthorizedDeviceControl");
    string machineFile = Path.Combine(machineDirectory, "device.id");
    try
    {
      Directory.CreateDirectory(machineDirectory);
      if (TryReadId(machineFile, out string? machineId)) return machineId;

      string legacyFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AuthorizedDeviceControl",
        "device.id");
      string id = TryReadId(legacyFile, out string? legacyId)
        ? legacyId
        : Guid.NewGuid().ToString("N");
      WriteIdAtomically(machineFile, id);
      return id;
    }
    catch
    {
      string fallbackDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AuthorizedDeviceControl");
      Directory.CreateDirectory(fallbackDirectory);
      string fallbackFile = Path.Combine(fallbackDirectory, "device.id");
      if (TryReadId(fallbackFile, out string? fallbackId)) return fallbackId;
      string id = Guid.NewGuid().ToString("N");
      WriteIdAtomically(fallbackFile, id);
      return id;
    }
  }

  public static string EnsureCodexWorkspace()
  {
    string desktop = GetDesktopPath();
    string workspace = Path.Combine(desktop, "zzx");
    Directory.CreateDirectory(workspace);
    return workspace;
  }

  public static void DeletePersistentIdentity()
  {
    string[] roots =
    [
      Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
      Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
    ];
    foreach (string root in roots)
    {
      try
      {
        string path = Path.Combine(root, "AuthorizedDeviceControl", "device.id");
        if (File.Exists(path)) File.Delete(path);
      }
      catch { }
    }
    try
    {
      using RegistryKey? profiles = Registry.LocalMachine.OpenSubKey(
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
      if (profiles is null) return;
      foreach (string sid in profiles.GetSubKeyNames())
      {
        try
        {
          using RegistryKey? profile = profiles.OpenSubKey(sid);
          string? rawPath = Convert.ToString(profile?.GetValue("ProfileImagePath"));
          if (string.IsNullOrWhiteSpace(rawPath)) continue;
          string profilePath = Environment.ExpandEnvironmentVariables(rawPath);
          foreach (string appData in new[] { "Roaming", "Local" })
          {
            string path = Path.Combine(
              profilePath,
              "AppData",
              appData,
              "AuthorizedDeviceControl",
              "device.id");
            if (File.Exists(path)) File.Delete(path);
          }
        }
        catch { }
      }
    }
    catch { }
  }

  public static string GetInteractiveUserName()
  {
    int sessionId = NativeSession.FindActiveSessionId();
    if (sessionId < 0) return Environment.UserName;
    IntPtr buffer = IntPtr.Zero;
    try
    {
      if (WTSQuerySessionInformationW(
            IntPtr.Zero,
            sessionId,
            5,
            out buffer,
            out int bytes) &&
          bytes > 2)
      {
        string? value = Marshal.PtrToStringUni(buffer);
        if (!string.IsNullOrWhiteSpace(value)) return value;
      }
    }
    finally
    {
      if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
    }
    return Environment.UserName;
  }

  public static string GetDesktopPath()
  {
    IntPtr path = IntPtr.Zero;
    IntPtr userToken = IntPtr.Zero;
    try
    {
      if (WindowsIdentity.GetCurrent().IsSystem)
      {
        int sessionId = NativeSession.FindActiveSessionId();
        if (sessionId >= 0)
          WTSQueryUserToken((uint)sessionId, out userToken);
      }
      int result = SHGetKnownFolderPath(
        DesktopFolderId,
        KnownFolderFlags.DontVerify,
        userToken,
        out path);
      if (result == 0 && path != IntPtr.Zero)
      {
        string? value = Marshal.PtrToStringUni(path);
        if (!string.IsNullOrWhiteSpace(value)) return value;
      }
    }
    finally
    {
      if (path != IntPtr.Zero) Marshal.FreeCoTaskMem(path);
      if (userToken != IntPtr.Zero) CloseHandle(userToken);
    }
    return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
  }

  private static bool TryReadId(string path, out string id)
  {
    id = string.Empty;
    try
    {
      if (!File.Exists(path)) return false;
      string value = File.ReadAllText(path).Trim();
      if (value.Length is < 16 or > 128) return false;
      id = value;
      return true;
    }
    catch
    {
      return false;
    }
  }

  private static void WriteIdAtomically(string path, string id)
  {
    string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
    File.WriteAllText(temporary, id);
    File.Move(temporary, path, true);
  }

  [Flags]
  private enum KnownFolderFlags : uint
  {
    DontVerify = 0x00004000
  }

  [DllImport("shell32.dll")]
  private static extern int SHGetKnownFolderPath(
    [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
    KnownFolderFlags flags,
    IntPtr token,
    out IntPtr path);

  [DllImport("wtsapi32.dll", SetLastError = true)]
  private static extern bool WTSQueryUserToken(
    uint sessionId,
    out IntPtr token);

  [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern bool WTSQuerySessionInformationW(
    IntPtr server,
    int sessionId,
    int infoClass,
    out IntPtr buffer,
    out int bytesReturned);

  [DllImport("wtsapi32.dll")]
  private static extern void WTSFreeMemory(IntPtr memory);

  [DllImport("kernel32.dll", SetLastError = true)]
  private static extern bool CloseHandle(IntPtr handle);
}
