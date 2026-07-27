using RemoteControl.Shared;
using System.IO;
using System.Net.Sockets;

namespace RemoteAgent;

public partial class MainWindow
{
  private TcpClient? _fileClient;
  private NetworkStream? _fileStream;

  private async Task FileTransferConnectLoopAsync(string host, int filePort, CancellationToken token)
  {
    while (!token.IsCancellationRequested && _stream is not null)
    {
      try
      {
        _fileClient = new TcpClient { NoDelay = true, ReceiveBufferSize = 512 * 1024, SendBufferSize = 512 * 1024 };
        await _fileClient.ConnectAsync(host, filePort, token);
        _fileStream = _fileClient.GetStream();
        await WriteLogicalChannelHelloAsync(_fileStream, LogicalChannelType.File, token);
        await BinaryFileTransferProtocol.WriteHelloAsync(_fileStream, _deviceId, token);
        while (!token.IsCancellationRequested && _fileStream is not null)
        {
          FileTransferPacket? packet = await BinaryFileTransferProtocol.ReadAsync(_fileStream, token);
          if (packet is null) break;
          FileTransferPacket response;
          try
          {
            response = packet.Type switch
            {
              FileTransferPacketType.ReadRangeRequest => await FileRangeStorage.ReadRangeAsync(packet, token),
              FileTransferPacketType.WriteRangeRequest => await FileRangeStorage.WriteRangeAsync(packet, token),
              FileTransferPacketType.Ping => packet with { Type = FileTransferPacketType.Pong },
              _ => BinaryFileTransferProtocol.Error(packet, "不支持的文件传输请求。")
            };
          }
          catch (Exception ex)
          {
            response = BinaryFileTransferProtocol.Error(packet, ex.Message);
          }
          await BinaryFileTransferProtocol.WriteAsync(_fileStream, response, token);
        }
      }
      catch (OperationCanceledException) { break; }
      catch (Exception)
      {
        await Task.Delay(500, token).ContinueWith(_ => { });
      }
      finally
      {
        try { _fileStream?.Dispose(); _fileClient?.Close(); } catch { }
        _fileStream = null;
        _fileClient = null;
      }
    }
  }

}
