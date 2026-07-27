using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace RemoteControl.Shared;

public static class H264NativeResolver
{
  private const string WrapperName = "H264SharpNative-win64.dll";
  private const string CiscoName = "openh264-2.4.1-win64.dll";
  private static readonly object Gate = new();
  private static IntPtr _wrapperHandle;
  private static string? _resolvedPath;

  public static string Resolve(Assembly h264SharpAssembly)
  {
    lock (Gate)
    {
      if (_resolvedPath is not null) return _resolvedPath;

      if (_wrapperHandle == IntPtr.Zero)
      {
        NativeLibrary.TryLoad(
          WrapperName,
          h264SharpAssembly,
          DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories,
          out _wrapperHandle);
      }

      if (_wrapperHandle != IntPtr.Zero)
      {
        var modulePath = new StringBuilder(32768);
        if (GetModuleFileName(_wrapperHandle, modulePath, modulePath.Capacity) > 0)
        {
          string? directory = Path.GetDirectoryName(modulePath.ToString());
          if (directory is not null)
          {
            string candidate = Path.Combine(directory, CiscoName);
            if (File.Exists(candidate)) return _resolvedPath = StageCiscoLibrary(candidate);
          }
        }
      }

      foreach (string directory in CandidateDirectories())
      {
        string candidate = Path.Combine(directory, CiscoName);
        if (File.Exists(candidate)) return _resolvedPath = StageCiscoLibrary(candidate);
      }

      return _resolvedPath = CiscoName;
    }
  }

  private static IEnumerable<string> CandidateDirectories()
  {
    yield return AppContext.BaseDirectory;
    string? nativeSearch = AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string;
    if (!string.IsNullOrWhiteSpace(nativeSearch))
      foreach (string item in nativeSearch.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        yield return item;
  }

  private static string StageCiscoLibrary(string sourcePath)
  {
    // The native wrapper receives this path as a narrow string. A single-file
    // app is extracted below a directory named after the exe, so a Chinese exe
    // name makes the wrapper fail even when the DLL exists. Stage it under an
    // ASCII-only child directory before passing the absolute path.
    string root = Path.Combine(Path.GetTempPath(), "ADCNative", "h264sharp-1.8.0-win64");
    Directory.CreateDirectory(root);
    string targetPath = Path.Combine(root, CiscoName);
    var source = new FileInfo(sourcePath);
    var target = new FileInfo(targetPath);
    if (target.Exists && target.Length == source.Length) return targetPath;

    string temporaryPath = targetPath + "." + Environment.ProcessId + ".tmp";
    try
    {
      File.Copy(sourcePath, temporaryPath, true);
      File.Move(temporaryPath, targetPath, true);
    }
    catch
    {
      try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
      target.Refresh();
      if (!target.Exists || target.Length != source.Length) throw;
    }
    return targetPath;
  }

  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern int GetModuleFileName(IntPtr module, StringBuilder fileName, int size);
}
