using RemoteControl.Shared;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;

namespace ControlCenter;

internal sealed class RustDeskSessionManager : IDisposable
{
  private readonly ConcurrentDictionary<(string DeviceId, long SessionId), Session> _sessions = new();
  private readonly ConcurrentDictionary<string, Session> _sessionsByDevice =
    new(StringComparer.OrdinalIgnoreCase);
  private long _nextSessionId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
  private bool _disposed;

  public async Task OpenAsync(DeviceView device, bool viewOnly, CancellationToken token = default)
  {
    ObjectDisposedException.ThrowIf(_disposed, this);
    if (_sessionsByDevice.TryGetValue(device.DeviceId, out Session? existing))
    {
      if (existing.ViewOnly == viewOnly && existing.TryActivate())
        return;
      await RemoveSessionAsync(existing);
    }

    long sessionId = Interlocked.Increment(ref _nextSessionId);
    var session = new Session(device, sessionId, viewOnly);
    if (!_sessionsByDevice.TryAdd(device.DeviceId, session))
      return;
    _sessions[(device.DeviceId, sessionId)] = session;

    try
    {
      await session.StartAsync(token);
      _ = session.Completion.ContinueWith(
        _ => RemoveSessionAsync(session),
        CancellationToken.None,
        TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default).Unwrap();
    }
    catch
    {
      await RemoveSessionAsync(session);
      throw;
    }
  }

  public bool AttachAgentTunnel(
    string deviceId,
    long sessionId,
    TcpClient client)
  {
    if (_sessions.TryGetValue((deviceId, sessionId), out Session? session) &&
        session.AttachAgent(client))
      return true;
    return false;
  }

  private async Task RemoveSessionAsync(Session session)
  {
    _sessions.TryRemove((session.DeviceId, session.SessionId), out _);
    if (_sessionsByDevice.TryGetValue(session.DeviceId, out Session? current) &&
        ReferenceEquals(current, session))
      _sessionsByDevice.TryRemove(session.DeviceId, out _);
    await session.StopRemoteAsync();
    await session.DisposeAsync();
  }

  public void Dispose()
  {
    if (_disposed) return;
    _disposed = true;
    Session[] sessions = _sessions.Values.Distinct().ToArray();
    _sessions.Clear();
    _sessionsByDevice.Clear();
    foreach (Session session in sessions)
      session.DisposeAsync().AsTask().GetAwaiter().GetResult();
  }

  private sealed class Session : IAsyncDisposable
  {
    private readonly DeviceView _device;
    private readonly bool _viewOnly;
    private readonly TcpListener _localListener = new(IPAddress.Loopback, 0);
    private readonly TaskCompletionSource<TcpClient> _agentTunnel =
      new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetime = new();
    private TcpClient? _localClient;
    private TcpClient? _agentClient;
    private Process? _launcherProcess;
    private string? _launcherExecutable;
    private Task _completion = Task.CompletedTask;
    private int _disposed;

    public Session(DeviceView device, long sessionId, bool viewOnly)
    {
      _device = device;
      SessionId = sessionId;
      _viewOnly = viewOnly;
    }

    public string DeviceId => _device.DeviceId;
    public long SessionId { get; }
    public bool ViewOnly => _viewOnly;
    public Task Completion => _completion;

    public async Task StartAsync(CancellationToken token)
    {
      _localListener.Start(1);
      int localPort = ((IPEndPoint)_localListener.LocalEndpoint).Port;

      using var startup = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
      startup.CancelAfter(TimeSpan.FromSeconds(25));
      RemoteMessage response = await _device.RequestAsync(
        MessageType.RustDeskSessionStartRequest,
        new RustDeskSessionPayload(SessionId, _viewOnly),
        25,
        startup.Token);
      OperationResultPayload result = response.Payload.As<OperationResultPayload>()
        ?? throw new InvalidOperationException("B端返回了无效的RustDesk会话结果。");
      if (!result.Success) throw new InvalidOperationException(result.Message);

      string executable = RustDeskRuntime.Extract(
        $"XiaoChenRemoteController_{SessionId}.exe");
      _launcherExecutable = executable;
      var startInfo = new ProcessStartInfo(executable)
      {
        UseShellExecute = false,
        WorkingDirectory = Path.GetDirectoryName(executable)!,
        CreateNoWindow = false
      };
      startInfo.ArgumentList.Add("--connect");
      startInfo.ArgumentList.Add($"127.0.0.1:{localPort}");
      startInfo.Environment["XIAOCHEN_EMBEDDED"] = "1";
      startInfo.Environment["XIAOCHEN_PEER_NAME"] = _device.DisplayTitle;
      if (_viewOnly)
      {
        startInfo.ArgumentList.Add("--xiaochen-view-only");
        startInfo.Environment["XIAOCHEN_VIEW_ONLY"] = "1";
        startInfo.Environment["XIAOCHEN_ALLOW_CONTROL_TOGGLE"] = "1";
      }
      _launcherProcess = Process.Start(startInfo)
        ?? throw new InvalidOperationException("无法启动RustDesk控制核心。");

      Task<TcpClient> localAccept = _localListener.AcceptTcpClientAsync(startup.Token).AsTask();
      Task<TcpClient> agentAccept = _agentTunnel.Task.WaitAsync(startup.Token);
      await Task.WhenAll(localAccept, agentAccept);
      _localClient = await localAccept;
      _agentClient = await agentAccept;
      ConfigureTunnelSocket(_localClient);
      ConfigureTunnelSocket(_agentClient);
      _localListener.Stop();
      _completion = BridgeAsync(_localClient, _agentClient, _lifetime.Token);
    }

    public bool AttachAgent(TcpClient client)
    {
      ConfigureTunnelSocket(client);
      if (_agentTunnel.TrySetResult(client)) return true;
      return false;
    }

    private static void ConfigureTunnelSocket(TcpClient client)
    {
      client.NoDelay = true;
      client.Client.SetSocketOption(
        SocketOptionLevel.Socket,
        SocketOptionName.KeepAlive,
        true);
    }

    public bool TryActivate()
    {
      try
      {
        if (_launcherProcess is { HasExited: false, MainWindowHandle: not 0 })
        {
          NativeMethods.ShowWindow(_launcherProcess.MainWindowHandle, 9);
          NativeMethods.SetForegroundWindow(_launcherProcess.MainWindowHandle);
          return true;
        }
      }
      catch { }
      return false;
    }

    private static async Task BridgeAsync(
      TcpClient local,
      TcpClient remote,
      CancellationToken token)
    {
      NetworkStream localStream = local.GetStream();
      NetworkStream remoteStream = remote.GetStream();
      using var bridge = CancellationTokenSource.CreateLinkedTokenSource(token);
      Task upstream = PumpAsync(localStream, remoteStream, remote.Client, bridge.Token);
      Task downstream = PumpAsync(remoteStream, localStream, local.Client, bridge.Token);
      Task first = await Task.WhenAny(upstream, downstream);
      Task remaining = ReferenceEquals(first, upstream) ? downstream : upstream;
      if (first.IsFaulted || first.IsCanceled)
        await bridge.CancelAsync();
      else
      {
        try { await remaining.WaitAsync(TimeSpan.FromSeconds(2), token); }
        catch { await bridge.CancelAsync(); }
      }
      try { await Task.WhenAll(upstream, downstream); } catch { }
    }

    private static async Task PumpAsync(
      Stream source,
      Stream destination,
      Socket destinationSocket,
      CancellationToken token)
    {
      await source.CopyToAsync(destination, token);
      try { destinationSocket.Shutdown(SocketShutdown.Send); } catch { }
    }

    public async Task StopRemoteAsync()
    {
      using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
      try
      {
        await _device.RequestAsync(
          MessageType.RustDeskSessionStopRequest,
          new RustDeskSessionPayload(SessionId, _viewOnly),
          2,
          timeout.Token);
      }
      catch { }
    }

    public async ValueTask DisposeAsync()
    {
      if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
      try { await _lifetime.CancelAsync(); } catch { }
      try { _localListener.Stop(); } catch { }
      try { _localClient?.Close(); } catch { }
      try { _agentClient?.Close(); } catch { }
      try
      {
        if (_launcherProcess is { HasExited: false })
        {
          _launcherProcess.CloseMainWindow();
          if (!_launcherProcess.WaitForExit(1000))
            _launcherProcess.Kill(entireProcessTree: true);
        }
      }
      catch { }
      _launcherProcess?.Dispose();
      RustDeskRuntime.KillProcesses(_launcherExecutable);
      try
      {
        if (!string.IsNullOrWhiteSpace(_launcherExecutable))
          File.Delete(_launcherExecutable);
      }
      catch { }
      _localClient?.Dispose();
      _agentClient?.Dispose();
      _lifetime.Dispose();
    }
  }

  private static class NativeMethods
  {
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr hWnd, int command);
  }
}

internal static class RustDeskRuntime
{
  private static readonly object Gate = new();
  private static readonly Lazy<string> RuntimeRoot = new(CreateRuntimeRoot);

  public static string Extract(string executableName)
  {
    string? developmentDirectory = Environment.GetEnvironmentVariable("XIAOCHEN_RUSTDESK_RUNTIME");
    if (!string.IsNullOrWhiteSpace(developmentDirectory))
    {
      string developmentExe = Path.Combine(developmentDirectory, executableName);
      if (File.Exists(developmentExe)) return developmentExe;
      string originalExe = Path.Combine(developmentDirectory, "rustdesk.exe");
      if (File.Exists(originalExe))
      {
        File.Copy(originalExe, developmentExe, overwrite: true);
        return developmentExe;
      }
    }

    string root = Path.Combine(RuntimeRoot.Value, "Controller");
    string executable = Path.Combine(root, executableName);
    if (File.Exists(executable)) return executable;

    lock (Gate)
    {
      if (File.Exists(executable)) return executable;
      Directory.CreateDirectory(root);
      Assembly assembly = typeof(RustDeskRuntime).Assembly;
      string resourceName = assembly.GetManifestResourceNames().SingleOrDefault(
        name => name.EndsWith("RustDeskRuntime.zip", StringComparison.OrdinalIgnoreCase))
        ?? throw new FileNotFoundException("内置RustDesk运行库缺失。");
      using Stream resource = assembly.GetManifestResourceStream(resourceName)
        ?? throw new FileNotFoundException("无法读取内置RustDesk运行库。");
      using var archive = new ZipArchive(resource, ZipArchiveMode.Read);
      archive.ExtractToDirectory(root, overwriteFiles: true);
      string original = Path.Combine(root, "rustdesk.exe");
      if (!File.Exists(original))
        throw new FileNotFoundException("RustDesk核心可执行文件缺失。", original);
      File.Copy(original, executable, overwrite: true);
      return executable;
    }
  }

  private static string CreateRuntimeRoot()
  {
    Assembly assembly = typeof(RustDeskRuntime).Assembly;
    string resourceName = assembly.GetManifestResourceNames().SingleOrDefault(
      name => name.EndsWith("RustDeskRuntime.zip", StringComparison.OrdinalIgnoreCase))
      ?? throw new FileNotFoundException("内置RustDesk运行库缺失。");
    using Stream resource = assembly.GetManifestResourceStream(resourceName)
      ?? throw new FileNotFoundException("无法读取内置RustDesk运行库。");
    string fingerprint = Convert.ToHexString(SHA256.HashData(resource))[..16];
    return Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
      "XiaoChenRemote",
      $"RustDesk-{fingerprint}");
  }

  public static void KillProcesses(string? executable)
  {
    if (string.IsNullOrWhiteSpace(executable)) return;
    string fullPath = Path.GetFullPath(executable);
    string processName = Path.GetFileNameWithoutExtension(fullPath);
    foreach (Process process in Process.GetProcessesByName(processName))
    {
      using (process)
      {
        try
        {
          if (!string.Equals(
                process.MainModule?.FileName,
                fullPath,
                StringComparison.OrdinalIgnoreCase))
            continue;
          if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch { }
      }
    }
  }
}
