using RemoteControl.Shared;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ControlCenter;

public partial class MainWindow
{
  public const string CodexPipeName = "AuthorizedDeviceControl.Codex";
  private readonly CancellationTokenSource _codexBridgeCts = new();

  private void InitializeCodexBridge()
  {
    _ = Task.Run(() => CodexPipeAcceptLoopAsync(_codexBridgeCts.Token));
  }

  private void DisposeCodexBridge()
  {
    try { _codexBridgeCts.Cancel(); } catch { }
  }

  private void SubscribeCodexDeviceEvents(DeviceView device) { }

  private async Task CodexPipeAcceptLoopAsync(CancellationToken token)
  {
    while (!token.IsCancellationRequested)
    {
      NamedPipeServerStream? pipe = null;
      try
      {
        pipe = CreateCodexPipeServer();
        await pipe.WaitForConnectionAsync(token);
        NamedPipeServerStream accepted = pipe;
        pipe = null;
        _ = Task.Run(() => HandleCodexPipeAsync(accepted, token), CancellationToken.None);
      }
      catch (OperationCanceledException) { break; }
      catch
      {
        pipe?.Dispose();
        await Task.Delay(500, token).ContinueWith(_ => { });
      }
    }
  }

  private static NamedPipeServerStream CreateCodexPipeServer()
  {
    var security = new PipeSecurity();
    var authenticatedUsers = new SecurityIdentifier(
      WellKnownSidType.AuthenticatedUserSid,
      null);
    security.AddAccessRule(new PipeAccessRule(
      authenticatedUsers,
      PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
      AccessControlType.Allow));
    return NamedPipeServerStreamAcl.Create(
      CodexPipeName,
      PipeDirection.InOut,
      8,
      PipeTransmissionMode.Byte,
      PipeOptions.Asynchronous | PipeOptions.WriteThrough,
      64 * 1024,
      64 * 1024,
      security);
  }

  private async Task HandleCodexPipeAsync(
    NamedPipeServerStream pipe,
    CancellationToken serverToken)
  {
    using (pipe)
    using (var reader = new StreamReader(
      pipe,
      new UTF8Encoding(false),
      false,
      64 * 1024,
      true))
    using (var writer = new StreamWriter(
      pipe,
      new UTF8Encoding(false),
      64 * 1024,
      true)
    {
      AutoFlush = true,
      NewLine = "\n"
    })
    {
      try
      {
        string? line = await reader.ReadLineAsync(serverToken);
        if (string.IsNullOrWhiteSpace(line)) return;
        CodexBridgeRequest request =
          JsonSerializer.Deserialize<CodexBridgeRequest>(
            line,
            CodexBridgeJson.Options)
          ?? throw new InvalidOperationException("Codex 请求为空。");
        request = request with
        {
          RequestId = string.IsNullOrWhiteSpace(request.RequestId)
            ? Guid.NewGuid().ToString("N")
            : request.RequestId
        };

        if (request.Operation.Equals("list_devices", StringComparison.OrdinalIgnoreCase))
        {
          object devices = await Dispatcher.InvokeAsync(() =>
            Devices.Select(device => new
            {
              deviceId = device.DeviceId,
              name = device.DisplayTitle,
              remark = device.Remark,
              user = device.UserName,
              online = device.IsOnline,
              codexConnected = device.IsCodexConnected,
              endpoint = device.RemoteEndPoint
            }).ToArray());
          await WriteBridgeLineAsync(
            writer,
            new
            {
              type = "result",
              requestId = request.RequestId,
              success = true,
              devices
            });
          return;
        }

        DeviceView device = await ResolveCodexDeviceAsync(request.DeviceId);
        if (request.Operation.Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
          await device.CancelCodexRequestAsync(request.RequestId, serverToken);
          await WriteBridgeLineAsync(
            writer,
            new
            {
              type = "completed",
              requestId = request.RequestId,
              success = true,
              message = "已发送停止请求。"
            });
          return;
        }

        var packet = BinaryCodexProtocol.Request(
          request.Operation,
          request.RequestId,
          request.WorkingDirectory,
          request.Path,
          request.DestinationPath,
          request.Shell,
          request.Command,
          request.Text,
          DecodeBridgeData(request.DataBase64),
          request.TimeoutSeconds);
        await device.RunCodexRequestAsync(
          packet,
          async response =>
          {
            await WriteBridgeLineAsync(
              writer,
              new
              {
                type = response.Type.ToString().ToLowerInvariant(),
                response.RequestId,
                deviceId = device.DeviceId,
                response.Operation,
                response.Sequence,
                response.Text,
                dataBase64 = response.Data.Length == 0
                  ? ""
                  : Convert.ToBase64String(response.Data),
                response.ExitCode,
                response.Success,
                response.Message
              });
          },
          serverToken);
      }
      catch (Exception ex)
      {
        try
        {
          await WriteBridgeLineAsync(
            writer,
            new { type = "error", success = false, message = ex.Message });
        }
        catch { }
      }
    }
  }

  private async Task<DeviceView> ResolveCodexDeviceAsync(string requestedId)
  {
    return await Dispatcher.InvokeAsync(() =>
    {
      if (!string.IsNullOrWhiteSpace(requestedId) &&
          _devicesById.TryGetValue(requestedId, out DeviceView? requested) &&
          requested.IsOnline)
        return requested;
      DeviceView? selected = DeviceList.SelectedItem as DeviceView;
      if (selected?.IsOnline == true) return selected;
      return Devices.FirstOrDefault(device => device.IsOnline)
        ?? throw new InvalidOperationException("当前没有在线B端。");
    });
  }

  private static byte[] DecodeBridgeData(string value)
  {
    if (string.IsNullOrWhiteSpace(value)) return [];
    return Convert.FromBase64String(value);
  }

  private static async Task WriteBridgeLineAsync(
    StreamWriter writer,
    object value)
  {
    await writer.WriteLineAsync(
      JsonSerializer.Serialize(value, CodexBridgeJson.Options));
  }
}

public sealed partial class DeviceView
{
  private readonly object _codexGate = new();
  private readonly SemaphoreSlim _codexWriteLock = new(1, 1);
  private readonly ConcurrentDictionary<string, PendingCodexRequest> _codexRequests =
    new(StringComparer.OrdinalIgnoreCase);
  private TcpClient? _codexClient;
  private NetworkStream? _codexStream;
  private CancellationTokenSource? _codexCts;
  private long _codexGeneration;

  public bool IsCodexConnected
  {
    get { lock (_codexGate) return _codexStream is not null; }
  }

  public void AttachCodexClient(TcpClient client, CancellationToken parentToken)
  {
    TcpClient? previous;
    NetworkStream stream = client.GetStream();
    CancellationTokenSource cts;
    long generation;
    lock (_codexGate)
    {
      previous = _codexClient;
      try { _codexCts?.Cancel(); } catch { }
      _codexClient = client;
      _codexStream = stream;
      cts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
      _codexCts = cts;
      generation = ++_codexGeneration;
    }
    try { previous?.Close(); } catch { }
    _ = Task.Run(
      () => CodexReadLoopAsync(client, stream, generation, cts.Token),
      CancellationToken.None);
    Changed(nameof(IsCodexConnected));
  }

  public async Task RunCodexRequestAsync(
    CodexPacket request,
    Func<CodexPacket, Task> onPacket,
    CancellationToken token)
  {
    var pending = new PendingCodexRequest(onPacket);
    if (!_codexRequests.TryAdd(request.RequestId, pending))
      throw new InvalidOperationException("Codex 请求ID重复。");
    try
    {
      await SendCodexPacketAsync(request, token);
      using CancellationTokenRegistration registration = token.Register(() =>
        pending.Completion.TrySetCanceled(token));
      await pending.Completion.Task;
    }
    finally
    {
      _codexRequests.TryRemove(request.RequestId, out _);
    }
  }

  public async Task CancelCodexRequestAsync(
    string requestId,
    CancellationToken token)
  {
    await SendCodexPacketAsync(
      BinaryCodexProtocol.Request("cancel", requestId) with
      {
        Type = CodexPacketType.Cancel
      },
      token);
  }

  private async Task CodexReadLoopAsync(
    TcpClient client,
    NetworkStream stream,
    long generation,
    CancellationToken token)
  {
    try
    {
      while (!token.IsCancellationRequested)
      {
        CodexPacket? packet = await BinaryCodexProtocol.ReadAsync(stream, token);
        if (packet is null) break;
        if (_codexRequests.TryGetValue(
              packet.RequestId,
              out PendingCodexRequest? pending))
        {
          await pending.OnPacket(packet);
          if (packet.Type is CodexPacketType.Completed or CodexPacketType.Error)
          {
            if (packet.Type == CodexPacketType.Error)
              pending.LastError = packet.Message;
            if (packet.Type == CodexPacketType.Completed)
            {
              if (packet.Success)
                pending.Completion.TrySetResult();
              else
                pending.Completion.TrySetException(
                  new InvalidOperationException(
                    packet.Message.Length > 0
                      ? packet.Message
                      : pending.LastError));
            }
          }
        }
      }
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
      foreach (PendingCodexRequest pending in _codexRequests.Values)
        pending.Completion.TrySetException(ex);
    }
    finally
    {
      lock (_codexGate)
      {
        if (_codexGeneration == generation &&
            ReferenceEquals(_codexClient, client))
        {
          _codexClient = null;
          _codexStream = null;
        }
      }
      Changed(nameof(IsCodexConnected));
    }
  }

  private async Task SendCodexPacketAsync(
    CodexPacket packet,
    CancellationToken token)
  {
    NetworkStream stream;
    lock (_codexGate)
      stream = _codexStream
        ?? throw new IOException("Codex远程通道尚未连接。");
    await _codexWriteLock.WaitAsync(token);
    try { await BinaryCodexProtocol.WriteAsync(stream, packet, token); }
    finally { _codexWriteLock.Release(); }
  }

  private void CloseCodexClient()
  {
    lock (_codexGate)
    {
      try { _codexCts?.Cancel(); } catch { }
      try { _codexClient?.Close(); } catch { }
      _codexClient = null;
      _codexStream = null;
      _codexGeneration++;
    }
    foreach (PendingCodexRequest pending in _codexRequests.Values)
      pending.Completion.TrySetException(new IOException("Codex远程通道已断开。"));
    _codexRequests.Clear();
  }

  private sealed class PendingCodexRequest(Func<CodexPacket, Task> onPacket)
  {
    public Func<CodexPacket, Task> OnPacket { get; } = onPacket;
    public TaskCompletionSource Completion { get; } =
      new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string LastError { get; set; } = "Codex远程任务失败。";
  }
}

internal sealed record CodexBridgeRequest(
  string Operation = "",
  string RequestId = "",
  string DeviceId = "",
  string WorkingDirectory = "",
  string Path = "",
  string DestinationPath = "",
  string Shell = "",
  string Command = "",
  string Text = "",
  string DataBase64 = "",
  int TimeoutSeconds = 300);

internal static class CodexBridgeJson
{
  public static readonly JsonSerializerOptions Options =
    new(JsonSerializerDefaults.Web)
    {
      PropertyNameCaseInsensitive = true
    };
}
