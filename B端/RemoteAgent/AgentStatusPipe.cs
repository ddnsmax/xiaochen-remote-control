using System.IO;
using System.IO.Pipes;
using System.Text;

namespace RemoteAgent;

public partial class MainWindow
{
  private const string AgentStatusPipeName =
    "AuthorizedDeviceControl.AgentStatus";
  private CancellationTokenSource? _agentStatusCts;

  private void InitializeAgentStatusListener()
  {
    _agentStatusCts = new CancellationTokenSource();
    _ = Task.Run(() => AgentStatusAcceptLoopAsync(_agentStatusCts.Token));
  }

  private async Task AgentStatusAcceptLoopAsync(CancellationToken token)
  {
    while (!token.IsCancellationRequested)
    {
      try
      {
        using var pipe = new NamedPipeServerStream(
          AgentStatusPipeName,
          PipeDirection.In,
          4,
          PipeTransmissionMode.Byte,
          PipeOptions.Asynchronous);
        await pipe.WaitForConnectionAsync(token);
        using var reader = new StreamReader(
          pipe,
          new UTF8Encoding(false),
          false,
          1024,
          true);
        string? status = await reader.ReadLineAsync(token);
        if (status is "已链接" or "已断开" or "未链接")
          SetConnectionStatus(status);
      }
      catch (OperationCanceledException) { break; }
      catch
      {
        await Task.Delay(250, token).ContinueWith(_ => { });
      }
    }
  }

  private void QueueAgentStatus(string status)
  {
    if (!_sessionHelper) return;
    _ = Task.Run(async () =>
    {
      try
      {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var pipe = new NamedPipeClientStream(
          ".",
          AgentStatusPipeName,
          PipeDirection.Out,
          PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        using var writer = new StreamWriter(
          pipe,
          new UTF8Encoding(false),
          1024,
          true)
        {
          AutoFlush = true
        };
        await writer.WriteLineAsync(status);
      }
      catch { }
    });
  }

  private void DisposeAgentStatusPipe()
  {
    try { _agentStatusCts?.Cancel(); } catch { }
    _agentStatusCts?.Dispose();
    _agentStatusCts = null;
  }
}
