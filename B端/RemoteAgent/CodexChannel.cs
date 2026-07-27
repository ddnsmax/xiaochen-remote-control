using RemoteControl.Shared;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace RemoteAgent;

public partial class MainWindow
{
  private readonly SemaphoreSlim _codexSendLock = new(1, 1);
  private readonly ConcurrentDictionary<string, Process> _codexProcesses =
    new(StringComparer.OrdinalIgnoreCase);
  private TcpClient? _codexClient;

  private async Task CodexConnectLoopAsync(
    string host,
    int port,
    CancellationToken token)
  {
    // The production session helper deliberately has no management stream:
    // the SYSTEM service owns management while the helper owns interactive
    // desktop and Codex operations.  Gating this loop on _stream made the
    // helper's Codex channel exit before its first connection attempt.
    while (!token.IsCancellationRequested && (_stream is not null || _inputOnly))
    {
      TcpClient? client = null;
      try
      {
        client = new TcpClient
        {
          NoDelay = true,
          ReceiveBufferSize = 256 * 1024,
          SendBufferSize = 256 * 1024
        };
        await client.ConnectAsync(host, port, token);
        _codexClient = client;
        NetworkStream stream = client.GetStream();
        await WriteLogicalChannelHelloAsync(stream, LogicalChannelType.Codex, token);
        while (!token.IsCancellationRequested &&
               ReferenceEquals(_codexClient, client))
        {
          CodexPacket? packet = await BinaryCodexProtocol.ReadAsync(stream, token);
          if (packet is null) break;
          if (packet.Type == CodexPacketType.Cancel)
          {
            CancelCodexProcess(packet.RequestId);
            continue;
          }
          if (packet.Type == CodexPacketType.Request)
            _ = Task.Run(() => HandleCodexRequestAsync(stream, packet, token), token);
        }
      }
      catch (OperationCanceledException) { break; }
      catch
      {
        await Task.Delay(TimeSpan.FromSeconds(2), token).ContinueWith(_ => { });
      }
      finally
      {
        if (ReferenceEquals(_codexClient, client)) _codexClient = null;
        try { client?.Close(); } catch { }
      }
    }
  }

  private async Task HandleCodexRequestAsync(
    NetworkStream stream,
    CodexPacket request,
    CancellationToken connectionToken)
  {
    try
    {
      string workspace = WindowsAgentEnvironment.EnsureCodexWorkspace();
      string cwd = ResolveCodexPath(
        string.IsNullOrWhiteSpace(request.WorkingDirectory)
          ? workspace
          : request.WorkingDirectory,
        workspace);
      switch (request.Operation.Trim().ToLowerInvariant())
      {
        case "get_workspace":
          await SendCodexAsync(
            stream,
            Result(request, text: workspace),
            connectionToken);
          break;
        case "list":
          string listPath = ResolveCodexPath(request.Path, cwd);
          var entries = Directory.EnumerateFileSystemEntries(listPath)
            .Select(path =>
            {
              bool directory = Directory.Exists(path);
              var info = directory
                ? (FileSystemInfo)new DirectoryInfo(path)
                : new FileInfo(path);
              return new
              {
                name = info.Name,
                fullPath = info.FullName,
                isDirectory = directory,
                length = directory ? 0 : ((FileInfo)info).Length,
                lastWriteTimeUtc = info.LastWriteTimeUtc
              };
            })
            .ToList();
          await SendCodexAsync(
            stream,
            Result(request, text: JsonSerializer.Serialize(entries)),
            connectionToken);
          break;
        case "read":
          string readPath = ResolveCodexPath(request.Path, cwd);
          byte[] content = await File.ReadAllBytesAsync(readPath, connectionToken);
          await SendCodexAsync(
            stream,
            Result(request, data: content, text: TryDecodeUtf8(content)),
            connectionToken);
          break;
        case "write":
          string writePath = ResolveCodexPath(request.Path, cwd);
          Directory.CreateDirectory(
            Path.GetDirectoryName(writePath)
            ?? throw new IOException("目标路径无效。"));
          byte[] writeData = request.Data.Length > 0
            ? request.Data
            : Encoding.UTF8.GetBytes(request.Text);
          await File.WriteAllBytesAsync(writePath, writeData, connectionToken);
          await SendCodexAsync(
            stream,
            Result(request, text: writePath),
            connectionToken);
          break;
        case "replace":
          await ReplaceCodexTextAsync(stream, request, cwd, connectionToken);
          break;
        case "mkdir":
          string directoryPath = ResolveCodexPath(request.Path, cwd);
          Directory.CreateDirectory(directoryPath);
          await SendCodexAsync(
            stream,
            Result(request, text: directoryPath),
            connectionToken);
          break;
        case "delete":
          string deletePath = ResolveCodexPath(request.Path, cwd);
          if (Directory.Exists(deletePath))
            Directory.Delete(deletePath, true);
          else
            File.Delete(deletePath);
          await SendCodexAsync(stream, Result(request), connectionToken);
          break;
        case "move":
          string source = ResolveCodexPath(request.Path, cwd);
          string destination = ResolveCodexPath(request.DestinationPath, cwd);
          Directory.CreateDirectory(
            Path.GetDirectoryName(destination)
            ?? throw new IOException("目标路径无效。"));
          if (Directory.Exists(source))
            Directory.Move(source, destination);
          else
            File.Move(source, destination, true);
          await SendCodexAsync(
            stream,
            Result(request, text: destination),
            connectionToken);
          break;
        case "shell":
          await RunCodexProcessAsync(stream, request, cwd, connectionToken);
          return;
        default:
          if (!await TryHandleCodexDesktopRequestAsync(
                stream,
                request,
                cwd,
                connectionToken))
            throw new InvalidOperationException(
              $"不支持的 Codex 操作：{request.Operation}");
          break;
      }
      await SendCodexAsync(
        stream,
        Completed(request, 0, true, ""),
        connectionToken);
    }
    catch (OperationCanceledException)
    {
      await TrySendCodexAsync(
        stream,
        Completed(request, -1, false, "任务已停止。"),
        CancellationToken.None);
    }
    catch (Exception ex)
    {
      await TrySendCodexAsync(
        stream,
        request with
        {
          Type = CodexPacketType.Error,
          Success = false,
          Message = ex.Message
        },
        CancellationToken.None);
      await TrySendCodexAsync(
        stream,
        Completed(request, -1, false, ex.Message),
        CancellationToken.None);
    }
  }

  private async Task ReplaceCodexTextAsync(
    NetworkStream stream,
    CodexPacket request,
    string cwd,
    CancellationToken token)
  {
    string path = ResolveCodexPath(request.Path, cwd);
    using JsonDocument json = JsonDocument.Parse(request.Text);
    string oldText = json.RootElement.GetProperty("old").GetString() ?? "";
    string newText = json.RootElement.GetProperty("new").GetString() ?? "";
    string content = await File.ReadAllTextAsync(path, token);
    int first = content.IndexOf(oldText, StringComparison.Ordinal);
    if (first < 0) throw new InvalidOperationException("未找到待替换文本。");
    if (content.IndexOf(oldText, first + oldText.Length, StringComparison.Ordinal) >= 0)
      throw new InvalidOperationException("待替换文本不唯一。");
    content = content[..first] + newText + content[(first + oldText.Length)..];
    await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), token);
    await SendCodexAsync(stream, Result(request, text: path), token);
  }

  private async Task RunCodexProcessAsync(
    NetworkStream stream,
    CodexPacket request,
    string cwd,
    CancellationToken connectionToken)
  {
    string shell = request.Shell.Trim().ToLowerInvariant();
    ProcessStartInfo start = shell is "powershell" or "pwsh"
      ? new ProcessStartInfo(
          "powershell.exe",
          $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command {QuotePowerShell(request.Command)}")
      : new ProcessStartInfo("cmd.exe", $"/d /s /c \"{request.Command}\"");
    start.WorkingDirectory = cwd;
    start.UseShellExecute = false;
    start.CreateNoWindow = true;
    start.RedirectStandardOutput = true;
    start.RedirectStandardError = true;
    start.StandardOutputEncoding = Encoding.UTF8;
    start.StandardErrorEncoding = Encoding.UTF8;

    using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
    if (!process.Start()) throw new InvalidOperationException("无法启动远程进程。");
    _codexProcesses[request.RequestId] = process;
    using var timeout = new CancellationTokenSource(
      TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 1, 86400)));
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(
      connectionToken,
      timeout.Token);
    long sequence = 0;
    try
    {
      Task stdout = PumpCodexOutputAsync(
        stream,
        request,
        process.StandardOutput,
        false,
        () => Interlocked.Increment(ref sequence),
        linked.Token);
      Task stderr = PumpCodexOutputAsync(
        stream,
        request,
        process.StandardError,
        true,
        () => Interlocked.Increment(ref sequence),
        linked.Token);
      await process.WaitForExitAsync(linked.Token);
      await Task.WhenAll(stdout, stderr);
      await SendCodexAsync(
        stream,
        Completed(request, process.ExitCode, process.ExitCode == 0, ""),
        connectionToken);
    }
    catch
    {
      TryKillProcessTree(process);
      throw;
    }
    finally
    {
      _codexProcesses.TryRemove(request.RequestId, out _);
    }
  }

  private async Task PumpCodexOutputAsync(
    NetworkStream stream,
    CodexPacket request,
    StreamReader reader,
    bool isError,
    Func<long> nextSequence,
    CancellationToken token)
  {
    char[] buffer = new char[4096];
    while (!token.IsCancellationRequested)
    {
      int read = await reader.ReadAsync(buffer.AsMemory(), token);
      if (read == 0) break;
      await SendCodexAsync(
        stream,
        request with
        {
          Type = CodexPacketType.Output,
          Sequence = nextSequence(),
          Text = new string(buffer, 0, read),
          Message = isError ? "stderr" : "stdout"
        },
        token);
    }
  }

  private void CancelCodexProcess(string requestId)
  {
    if (_codexProcesses.TryGetValue(requestId, out Process? process))
      TryKillProcessTree(process);
  }

  private void CloseCodexChannel()
  {
    foreach (Process process in _codexProcesses.Values)
      TryKillProcessTree(process);
    _codexProcesses.Clear();
    try { _codexClient?.Close(); } catch { }
    _codexClient = null;
  }

  private async Task SendCodexAsync(
    NetworkStream stream,
    CodexPacket packet,
    CancellationToken token)
  {
    await _codexSendLock.WaitAsync(token);
    try { await BinaryCodexProtocol.WriteAsync(stream, packet, token); }
    finally { _codexSendLock.Release(); }
  }

  private async Task TrySendCodexAsync(
    NetworkStream stream,
    CodexPacket packet,
    CancellationToken token)
  {
    try { await SendCodexAsync(stream, packet, token); } catch { }
  }

  private static CodexPacket Result(
    CodexPacket request,
    string text = "",
    byte[]? data = null) =>
    request with
    {
      Type = CodexPacketType.Result,
      Text = text,
      Data = data ?? [],
      Success = true,
      Message = ""
    };

  private static CodexPacket Completed(
    CodexPacket request,
    int exitCode,
    bool success,
    string message) =>
    request with
    {
      Type = CodexPacketType.Completed,
      ExitCode = exitCode,
      Success = success,
      Message = message
    };

  private static string ResolveCodexPath(string path, string cwd)
  {
    if (string.IsNullOrWhiteSpace(path)) return Path.GetFullPath(cwd);
    return Path.GetFullPath(
      Path.IsPathFullyQualified(path)
        ? path
        : Path.Combine(cwd, path));
  }

  private static string TryDecodeUtf8(byte[] data)
  {
    try { return new UTF8Encoding(false, true).GetString(data); }
    catch { return ""; }
  }

  private static string QuotePowerShell(string command) =>
    "'" + command.Replace("'", "''") + "' | Invoke-Expression";

  private static void TryKillProcessTree(Process process)
  {
    try
    {
      if (!process.HasExited) process.Kill(true);
    }
    catch { }
  }
}
