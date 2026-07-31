using RemoteControl.Shared;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;

namespace RemoteAgent;

internal sealed class RustDeskHost : IDisposable
{
  private const int DirectPort = 27184;
  private readonly string _deviceId;
  private readonly Guid _instanceId;
  private readonly ConcurrentDictionary<long, CancellationTokenSource> _sessions = new();
  private readonly SemaphoreSlim _hostGate = new(1, 1);
  private Process? _hostProcess;
  private bool _disposed;

  public RustDeskHost(string deviceId, Guid instanceId)
  {
    _deviceId = deviceId;
    _instanceId = instanceId;
  }

  public async Task<OperationResultPayload> StartSessionAsync(
    RustDeskSessionPayload session,
    string controllerHost,
    int controllerPort,
    CancellationToken lifetime)
  {
    ObjectDisposedException.ThrowIf(_disposed, this);
    await EnsureHostAsync(lifetime);
    await StopSessionAsync(session.SessionId);

    var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    if (!_sessions.TryAdd(session.SessionId, sessionCts))
    {
      sessionCts.Dispose();
      return new(false, "远控会话已存在。");
    }

    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    _ = Task.Run(
      () => RunTunnelAsync(
        session.SessionId,
        controllerHost,
        controllerPort,
        ready,
        sessionCts.Token),
      CancellationToken.None);

    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    timeout.CancelAfter(TimeSpan.FromSeconds(20));
    try
    {
      await ready.Task.WaitAsync(timeout.Token);
      return new(true, "RustDesk远控通道已就绪。");
    }
    catch
    {
      await StopSessionAsync(session.SessionId);
      throw;
    }
  }

  public async Task<OperationResultPayload> StopSessionAsync(long sessionId)
  {
    if (_sessions.TryRemove(sessionId, out CancellationTokenSource? cts))
    {
      try { await cts.CancelAsync(); } catch { }
      cts.Dispose();
    }
    return new(true, "RustDesk远控会话已停止。");
  }

  private async Task RunTunnelAsync(
    long sessionId,
    string controllerHost,
    int controllerPort,
    TaskCompletionSource ready,
    CancellationToken token)
  {
    using var local = new TcpClient { NoDelay = true };
    using var remote = new TcpClient { NoDelay = true };
    try
    {
      await ConnectLocalHostAsync(local, token);
      await remote.ConnectAsync(controllerHost, controllerPort, token);
      ConfigureTunnelSocket(local);
      ConfigureTunnelSocket(remote);
      NetworkStream remoteStream = remote.GetStream();
      await LogicalChannelProtocol.WriteHelloAsync(
        remoteStream,
        LogicalChannelType.RustDesk,
        _deviceId,
        _instanceId,
        sessionId,
        token);
      ready.TrySetResult();

      NetworkStream localStream = local.GetStream();
      using var bridgeCts = CancellationTokenSource.CreateLinkedTokenSource(token);
      Task upstream = PumpAsync(localStream, remoteStream, remote.Client, bridgeCts.Token);
      Task downstream = PumpAsync(remoteStream, localStream, local.Client, bridgeCts.Token);
      Task first = await Task.WhenAny(upstream, downstream);
      Task remaining = ReferenceEquals(first, upstream) ? downstream : upstream;
      if (first.IsFaulted || first.IsCanceled)
        await bridgeCts.CancelAsync();
      else
      {
        try { await remaining.WaitAsync(TimeSpan.FromSeconds(2), token); }
        catch { await bridgeCts.CancelAsync(); }
      }
      try { await Task.WhenAll(upstream, downstream); } catch { }
    }
    catch (Exception ex)
    {
      ready.TrySetException(ex);
    }
    finally
    {
      if (_sessions.TryRemove(sessionId, out CancellationTokenSource? cts))
        cts.Dispose();
    }
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

  private static void ConfigureTunnelSocket(TcpClient client)
  {
    client.NoDelay = true;
    client.Client.SetSocketOption(
      SocketOptionLevel.Socket,
      SocketOptionName.KeepAlive,
      true);
  }

  private static async Task ConnectLocalHostAsync(TcpClient client, CancellationToken token)
  {
    Exception? lastError = null;
    for (int attempt = 0; attempt < 100; attempt++)
    {
      try
      {
        await client.ConnectAsync(IPAddress.Loopback, DirectPort, token);
        return;
      }
      catch (Exception ex) when (ex is SocketException or IOException)
      {
        lastError = ex;
        await Task.Delay(100, token);
      }
    }
    throw new IOException("RustDesk被控服务未能启动。", lastError);
  }

  private async Task EnsureHostAsync(CancellationToken token)
  {
    await _hostGate.WaitAsync(token);
    try
    {
      if (_hostProcess is { HasExited: false }) return;
      string executable = RustDeskRuntime.Extract("XiaoChenRemoteHost.exe");
      var startInfo = new ProcessStartInfo(executable)
      {
        UseShellExecute = false,
        WorkingDirectory = Path.GetDirectoryName(executable)!,
        CreateNoWindow = false
      };
      startInfo.ArgumentList.Add("--server");
      startInfo.Environment["XIAOCHEN_EMBEDDED"] = "1";
      startInfo.Environment["XIAOCHEN_EMBEDDED_HOST"] = "1";
      _hostProcess = Process.Start(startInfo)
        ?? throw new InvalidOperationException("无法启动RustDesk被控核心。");
    }
    finally
    {
      _hostGate.Release();
    }
  }

  public void Dispose()
  {
    if (_disposed) return;
    _disposed = true;
    foreach (CancellationTokenSource cts in _sessions.Values)
    {
      try { cts.Cancel(); } catch { }
      cts.Dispose();
    }
    _sessions.Clear();
    try
    {
      if (_hostProcess is { HasExited: false })
      {
        _hostProcess.CloseMainWindow();
        if (!_hostProcess.WaitForExit(1500))
          _hostProcess.Kill(entireProcessTree: true);
      }
    }
    catch { }
    RustDeskRuntime.KillProcesses(_hostProcess?.StartInfo.FileName);
    _hostProcess?.Dispose();
    _hostGate.Dispose();
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

    string root = Path.Combine(RuntimeRoot.Value, "Host");
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
      Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
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
