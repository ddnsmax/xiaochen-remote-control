using Microsoft.Win32;
using RemoteControl.Shared;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace RemoteAgent;

public partial class MainWindow
{
  private const int SwRestore = 9;
  private const uint WmClose = 0x0010;

  [DllImport("user32.dll")]
  private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
  [DllImport("user32.dll")]
  private static extern bool ShowWindowAsync(IntPtr window, int command);
  [DllImport("user32.dll")]
  private static extern bool SetForegroundWindow(IntPtr window);
  [DllImport("user32.dll")]
  private static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")]
  private static extern bool BringWindowToTop(IntPtr window);
  [DllImport("user32.dll")]
  private static extern IntPtr SetFocus(IntPtr window);
  [DllImport("user32.dll")]
  private static extern bool AttachThreadInput(uint attach, uint attachTo, bool value);
  [DllImport("kernel32.dll")]
  private static extern uint GetCurrentThreadId();
  [DllImport("user32.dll")]
  private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
  [DllImport("user32.dll")]
  private static extern bool GetCursorPos(out NativePoint point);

  private async Task<bool> TryHandleCodexDesktopRequestAsync(
    NetworkStream stream,
    CodexPacket request,
    string cwd,
    CancellationToken token)
  {
    string operation = request.Operation.Trim().ToLowerInvariant();
    switch (operation)
    {
      case "capabilities":
        await SendCodexAsync(
          stream,
          Result(request, text: JsonSerializer.Serialize(new
          {
            protocol = 2,
            desktop = new[]
            {
              "capture_screen", "screen_info", "mouse_move", "click",
              "double_click", "scroll", "keypress", "type_text",
              "list_windows", "activate_window", "close_window",
              "launch_app", "get_clipboard", "set_clipboard"
            },
            system = new[]
            {
              "shell", "list", "read", "write", "replace", "mkdir",
              "move", "delete", "get_workspace", "cancel"
            },
            interactiveSession = Process.GetCurrentProcess().SessionId,
            identity = WindowsIdentity.GetCurrent().Name,
            isSystem = WindowsIdentity.GetCurrent().IsSystem
          })),
          token);
        return true;

      case "capture_screen":
      case "screenshot":
      {
        CodexScreenCapture capture = CaptureCodexScreen();
        await SendCodexAsync(
          stream,
          Result(
            request,
            text: JsonSerializer.Serialize(new
            {
              capture.Left,
              capture.Top,
              capture.Width,
              capture.Height,
              format = "image/png",
              capturedAtUtc = DateTimeOffset.UtcNow
            }),
            data: capture.Png),
          token);
        return true;
      }

      case "screen_info":
      {
        Rectangle bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        GetCursorPos(out NativePoint cursor);
        await SendCodexAsync(
          stream,
          Result(request, text: JsonSerializer.Serialize(new
          {
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            cursorX = cursor.X,
            cursorY = cursor.Y,
            sessionId = Process.GetCurrentProcess().SessionId
          })),
          token);
        return true;
      }

      case "mouse_move":
      {
        CodexDesktopAction action = ReadDesktopAction(request.Text);
        await SendInputAsync(BinaryControlProtocol.MouseMove(action.X, action.Y), token);
        await SendCodexAsync(stream, Result(request), token);
        return true;
      }

      case "click":
      case "double_click":
      {
        CodexDesktopAction action = ReadDesktopAction(request.Text);
        int clicks = operation == "double_click"
          ? 2
          : Math.Clamp(action.Clicks, 1, 2);
        byte button = ParseMouseButton(action.Button);
        for (int index = 0; index < clicks; index++)
        {
          await SendInputAsync(
            BinaryControlProtocol.MouseButton(action.X, action.Y, button, true, (byte)clicks),
            token);
          await SendInputAsync(
            BinaryControlProtocol.MouseButton(action.X, action.Y, button, false, (byte)clicks),
            token);
          if (index + 1 < clicks) await Task.Delay(45, token);
        }
        await SendCodexAsync(stream, Result(request), token);
        return true;
      }

      case "scroll":
      {
        CodexDesktopAction action = ReadDesktopAction(request.Text);
        await SendInputAsync(
          BinaryControlProtocol.MouseWheel(action.X, action.Y, action.Delta),
          token);
        await SendCodexAsync(stream, Result(request), token);
        return true;
      }

      case "keypress":
      {
        CodexDesktopAction action = ReadDesktopAction(request.Text);
        string keys = string.IsNullOrWhiteSpace(action.Keys)
          ? request.Text
          : action.Keys;
        await SendKeypressAsync(keys, token);
        await SendCodexAsync(stream, Result(request), token);
        return true;
      }

      case "type_text":
      {
        string text = TryReadActionText(request.Text) ?? request.Text;
        await _inputDispatcher.TypeTextAsync(
          text,
          Interlocked.Read(ref _desktopEnvironmentGeneration),
          token);
        await SendCodexAsync(stream, Result(request), token);
        return true;
      }

      case "list_windows":
        await SendCodexAsync(
          stream,
          Result(request, text: JsonSerializer.Serialize(ListCodexWindows())),
          token);
        return true;

      case "activate_window":
      {
        CodexDesktopAction action = ReadDesktopAction(request.Text);
        IntPtr handle = ResolveWindow(action);
        ActivateCodexWindow(handle);
        await SendCodexAsync(stream, Result(request, text: handle.ToInt64().ToString()), token);
        return true;
      }

      case "close_window":
      {
        CodexDesktopAction action = ReadDesktopAction(request.Text);
        IntPtr handle = ResolveWindow(action);
        if (!PostMessage(handle, WmClose, IntPtr.Zero, IntPtr.Zero))
          throw new InvalidOperationException("无法关闭目标窗口。");
        await SendCodexAsync(stream, Result(request, text: handle.ToInt64().ToString()), token);
        return true;
      }

      case "launch_app":
      {
        CodexLaunchResult launch = LaunchCodexApplication(
          string.IsNullOrWhiteSpace(request.Path) ? request.Text : request.Path,
          request.Command,
          cwd);
        await SendCodexAsync(
          stream,
          Result(request, text: JsonSerializer.Serialize(launch)),
          token);
        return true;
      }

      case "get_clipboard":
      {
        string text = await Dispatcher.InvokeAsync(() =>
          System.Windows.Clipboard.ContainsText()
            ? System.Windows.Clipboard.GetText()
            : string.Empty);
        await SendCodexAsync(stream, Result(request, text: text), token);
        return true;
      }

      case "set_clipboard":
        await Dispatcher.InvokeAsync(() =>
          System.Windows.Clipboard.SetText(request.Text ?? string.Empty));
        await SendCodexAsync(stream, Result(request), token);
        return true;

      default:
        return false;
    }
  }

  private Task SendInputAsync(ControlPacket packet, CancellationToken token) =>
    _inputDispatcher.ExecuteAsync(
      packet,
      Interlocked.Read(ref _desktopEnvironmentGeneration),
      token);

  private static CodexScreenCapture CaptureCodexScreen()
  {
    WindowsInputDesktop.AttachCurrentThread(0);
    Rectangle bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
    if (bounds.Width <= 0 || bounds.Height <= 0)
      throw new InvalidOperationException("当前交互桌面没有可用显示器。");
    using var bitmap = new Bitmap(
      bounds.Width,
      bounds.Height,
      PixelFormat.Format32bppArgb);
    using (Graphics graphics = Graphics.FromImage(bitmap))
      graphics.CopyFromScreen(
        bounds.Left,
        bounds.Top,
        0,
        0,
        bounds.Size,
        CopyPixelOperation.SourceCopy);
    using var memory = new MemoryStream();
    bitmap.Save(memory, ImageFormat.Png);
    return new CodexScreenCapture(
      bounds.Left,
      bounds.Top,
      bounds.Width,
      bounds.Height,
      memory.ToArray());
  }

  private async Task SendKeypressAsync(string expression, CancellationToken token)
  {
    string[] keys = expression
      .Trim()
      .Trim('[', ']', '"')
      .Replace("\"", string.Empty)
      .Split(['+', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (keys.Length == 0) throw new InvalidOperationException("快捷键不能为空。");
    var modifiers = new List<ushort>();
    ushort primary = 0;
    foreach (string key in keys)
    {
      ushort virtualKey = ParseVirtualKey(key);
      if (virtualKey is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C)
        modifiers.Add(virtualKey);
      else
        primary = virtualKey;
    }
    foreach (ushort modifier in modifiers)
      await SendInputAsync(BinaryControlProtocol.Key(modifier, 0, true, IsExtendedKey(modifier)), token);
    if (primary != 0)
    {
      await SendInputAsync(BinaryControlProtocol.Key(primary, 0, true, IsExtendedKey(primary)), token);
      await SendInputAsync(BinaryControlProtocol.Key(primary, 0, false, IsExtendedKey(primary)), token);
    }
    for (int index = modifiers.Count - 1; index >= 0; index--)
    {
      ushort modifier = modifiers[index];
      await SendInputAsync(BinaryControlProtocol.Key(modifier, 0, false, IsExtendedKey(modifier)), token);
    }
  }

  private static ushort ParseVirtualKey(string value)
  {
    string key = value.Trim().ToUpperInvariant();
    if (key.Length == 1)
    {
      char character = key[0];
      if (character is >= 'A' and <= 'Z' or >= '0' and <= '9')
        return character;
    }
    return key switch
    {
      "CTRL" or "CONTROL" => 0x11,
      "SHIFT" => 0x10,
      "ALT" => 0x12,
      "WIN" or "WINDOWS" or "META" => 0x5B,
      "ENTER" or "RETURN" => 0x0D,
      "TAB" => 0x09,
      "ESC" or "ESCAPE" => 0x1B,
      "SPACE" => 0x20,
      "BACKSPACE" => 0x08,
      "DELETE" or "DEL" => 0x2E,
      "INSERT" => 0x2D,
      "HOME" => 0x24,
      "END" => 0x23,
      "PAGEUP" or "PGUP" => 0x21,
      "PAGEDOWN" or "PGDN" => 0x22,
      "LEFT" => 0x25,
      "UP" => 0x26,
      "RIGHT" => 0x27,
      "DOWN" => 0x28,
      "F1" => 0x70, "F2" => 0x71, "F3" => 0x72, "F4" => 0x73,
      "F5" => 0x74, "F6" => 0x75, "F7" => 0x76, "F8" => 0x77,
      "F9" => 0x78, "F10" => 0x79, "F11" => 0x7A, "F12" => 0x7B,
      _ => throw new InvalidOperationException($"不支持的按键：{value}")
    };
  }

  private static bool IsExtendedKey(ushort key) =>
    key is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or
      0x2D or 0x2E or 0x5B or 0x5C;

  private static byte ParseMouseButton(string value) =>
    value.Trim().ToLowerInvariant() switch
    {
      "" or "left" => 1,
      "right" => 2,
      "middle" => 3,
      _ => throw new InvalidOperationException($"不支持的鼠标按钮：{value}")
    };

  private static CodexDesktopAction ReadDesktopAction(string text)
  {
    if (string.IsNullOrWhiteSpace(text)) return new CodexDesktopAction();
    try
    {
      return JsonSerializer.Deserialize<CodexDesktopAction>(
        text,
        new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
          PropertyNameCaseInsensitive = true
        }) ?? new CodexDesktopAction();
    }
    catch (JsonException)
    {
      return new CodexDesktopAction(Keys: text, Text: text);
    }
  }

  private static string? TryReadActionText(string text)
  {
    if (string.IsNullOrWhiteSpace(text) || !text.TrimStart().StartsWith('{'))
      return null;
    try { return ReadDesktopAction(text).Text; }
    catch { return null; }
  }

  private static List<CodexWindowInfo> ListCodexWindows()
  {
    var windows = new List<CodexWindowInfo>();
    EnumWindows((window, _) =>
    {
      if (!IsWindowVisible(window)) return true;
      int length = GetWindowTextLength(window);
      if (length <= 0) return true;
      var title = new StringBuilder(length + 1);
      if (GetWindowText(window, title, title.Capacity) <= 0) return true;
      GetWindowThreadProcessId(window, out uint processId);
      GetWindowRect(window, out NativeRect rectangle);
      string processName = string.Empty;
      try
      {
        using Process process = Process.GetProcessById(unchecked((int)processId));
        processName = process.ProcessName;
      }
      catch { }
      windows.Add(new CodexWindowInfo(
        window.ToInt64(),
        title.ToString(),
        unchecked((int)processId),
        processName,
        rectangle.Left,
        rectangle.Top,
        Math.Max(0, rectangle.Right - rectangle.Left),
        Math.Max(0, rectangle.Bottom - rectangle.Top)));
      return true;
    }, IntPtr.Zero);
    return windows;
  }

  private static IntPtr ResolveWindow(CodexDesktopAction action)
  {
    if (action.WindowHandle != 0) return new IntPtr(action.WindowHandle);
    CodexWindowInfo? window = ListCodexWindows().FirstOrDefault(item =>
      (!string.IsNullOrWhiteSpace(action.Title) &&
       item.Title.Contains(action.Title, StringComparison.OrdinalIgnoreCase)) ||
      (!string.IsNullOrWhiteSpace(action.Process) &&
       item.Process.Equals(action.Process, StringComparison.OrdinalIgnoreCase)));
    return window is null
      ? throw new InvalidOperationException("没有找到目标窗口。")
      : new IntPtr(window.WindowHandle);
  }

  private static void ActivateCodexWindow(IntPtr handle)
  {
    IntPtr foreground = GetForegroundWindow();
    uint foregroundThread = foreground == IntPtr.Zero
      ? 0
      : GetWindowThreadProcessId(foreground, out _);
    uint targetThread = GetWindowThreadProcessId(handle, out _);
    uint currentThread = GetCurrentThreadId();
    bool attachedForeground = foregroundThread != 0 &&
      foregroundThread != currentThread &&
      AttachThreadInput(currentThread, foregroundThread, true);
    bool attachedTarget = targetThread != 0 &&
      targetThread != currentThread &&
      AttachThreadInput(currentThread, targetThread, true);
    try
    {
      ShowWindowAsync(handle, SwRestore);
      BringWindowToTop(handle);
      SetForegroundWindow(handle);
      SetFocus(handle);
    }
    finally
    {
      if (attachedTarget) AttachThreadInput(currentThread, targetThread, false);
      if (attachedForeground)
        AttachThreadInput(currentThread, foregroundThread, false);
    }
    if (GetForegroundWindow() != handle)
      throw new InvalidOperationException("目标窗口未取得前台输入焦点。");
  }

  private static CodexLaunchResult LaunchCodexApplication(
    string application,
    string arguments,
    string workingDirectory)
  {
    if (string.IsNullOrWhiteSpace(application))
      throw new InvalidOperationException("应用名称或路径不能为空。");
    string target = ResolveApplicationTarget(application.Trim());
    int sessionId = Process.GetCurrentProcess().SessionId;
    int processId;
    if (WindowsIdentity.GetCurrent().IsSystem)
    {
      processId = NativeSession.CreateUserShellProcessInSession(
        sessionId,
        target,
        arguments,
        workingDirectory);
    }
    else
    {
      using Process process = Process.Start(new ProcessStartInfo
      {
        FileName = target,
        Arguments = arguments ?? string.Empty,
        WorkingDirectory = Directory.Exists(workingDirectory)
          ? workingDirectory
          : Environment.CurrentDirectory,
        UseShellExecute = true
      }) ?? throw new InvalidOperationException("无法启动目标应用。");
      processId = process.Id;
    }
    return new CodexLaunchResult(
      true,
      target,
      processId,
      sessionId,
      DateTimeOffset.UtcNow);
  }

  private static string ResolveApplicationTarget(string application)
  {
    if (File.Exists(application) || Directory.Exists(application) ||
        Uri.TryCreate(application, UriKind.Absolute, out _))
      return application;

    string executableName = Path.HasExtension(application)
      ? application
      : application + ".exe";
    foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
               .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
    {
      string candidate = Path.Combine(directory.Trim(), executableName);
      if (File.Exists(candidate)) return candidate;
    }

    string appPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + executableName;
    foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
    {
      using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
      using RegistryKey? key = baseKey.OpenSubKey(appPath);
      if (key?.GetValue(null) is string registered && File.Exists(registered))
        return registered;
    }

    string normalized = Path.GetFileNameWithoutExtension(application);
    foreach (string root in EnumerateApplicationShortcutRoots())
    {
      if (!Directory.Exists(root)) continue;
      try
      {
        string? exact = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories)
          .FirstOrDefault(path => Path.GetFileNameWithoutExtension(path)
            .Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
      }
      catch { }
    }
    return application;
  }

  private static IEnumerable<string> EnumerateApplicationShortcutRoots()
  {
    yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
    yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
    string usersRoot = Path.Combine(
      Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\",
      "Users");
    if (!Directory.Exists(usersRoot)) yield break;
    foreach (string profile in Directory.EnumerateDirectories(usersRoot))
    {
      yield return Path.Combine(profile, "Desktop");
      yield return Path.Combine(
        profile,
        "AppData",
        "Roaming",
        "Microsoft",
        "Windows",
        "Start Menu",
        "Programs");
    }
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct NativePoint
  {
    public int X;
    public int Y;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct NativeRect
  {
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
  }
}

internal sealed record CodexDesktopAction(
  int X = 0,
  int Y = 0,
  string Button = "left",
  int Clicks = 1,
  int Delta = 0,
  string Keys = "",
  string Text = "",
  long WindowHandle = 0,
  string Title = "",
  string Process = "");

internal sealed record CodexScreenCapture(
  int Left,
  int Top,
  int Width,
  int Height,
  byte[] Png);

internal sealed record CodexWindowInfo(
  long WindowHandle,
  string Title,
  int ProcessId,
  string Process,
  int Left,
  int Top,
  int Width,
  int Height);

internal sealed record CodexLaunchResult(
  bool Success,
  string Target,
  int ProcessId,
  int SessionId,
  DateTimeOffset StartedAtUtc);
