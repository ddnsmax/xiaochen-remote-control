using System.Runtime.InteropServices;

namespace RemoteControl.Shared;

public sealed class MultimediaThreadScope : IDisposable
{
  private readonly IntPtr _handle;

  private MultimediaThreadScope(IntPtr handle) => _handle = handle;

  public static MultimediaThreadScope Enter(string taskName)
  {
    if (!OperatingSystem.IsWindows()) return new(IntPtr.Zero);
    try
    {
      IntPtr handle = AvSetMmThreadCharacteristics(taskName, out _);
      return new(handle);
    }
    catch (DllNotFoundException) { return new(IntPtr.Zero); }
    catch (EntryPointNotFoundException) { return new(IntPtr.Zero); }
  }

  public void Dispose()
  {
    if (_handle == IntPtr.Zero) return;
    try { AvRevertMmThreadCharacteristics(_handle); }
    catch { }
  }

  [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
  private static extern IntPtr AvSetMmThreadCharacteristics(
    string taskName,
    out uint taskIndex);

  [DllImport("avrt.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
}
