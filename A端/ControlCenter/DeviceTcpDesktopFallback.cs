using RemoteControl.Shared;
using System.IO;
using System.Net.Sockets;

namespace ControlCenter;

public sealed partial class DeviceView
{
  private TcpClient? _tcpVideoClient;
  private NetworkStream? _tcpVideoStream;
  private CancellationTokenSource? _tcpVideoCts;
  private long _tcpVideoGeneration;
  private readonly SemaphoreSlim _tcpVideoControlWriteLock = new(1, 1);
  private TcpClient? _tcpInputClient;
  private NetworkStream? _tcpInputStream;
  private CancellationTokenSource? _tcpInputCts;
  private long _tcpInputGeneration;
  private readonly SemaphoreSlim _tcpInputWriteLock = new(1, 1);

  private bool IsTcpVideoConnected => _tcpVideoStream is not null;
  private bool IsTcpInputConnected => _tcpInputStream is not null;

  public void AttachTcpVideoClient(TcpClient client, CancellationToken parentToken)
  {
    CancellationTokenSource? previousCts = _tcpVideoCts;
    TcpClient? previousClient = _tcpVideoClient;
    try { previousCts?.Cancel(); previousClient?.Close(); } catch { }
    previousCts?.Dispose();

    client.NoDelay = true;
    _tcpVideoClient = client;
    _tcpVideoStream = client.GetStream();
    _tcpVideoCts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
    long generation = Interlocked.Increment(ref _tcpVideoGeneration);
    Changed(nameof(IsVideoConnected));
    _ = Task.Run(
      () => TcpVideoReadLoopAsync(client, generation, _tcpVideoCts.Token),
      CancellationToken.None);
  }

  private async Task TcpVideoReadLoopAsync(
    TcpClient client,
    long generation,
    CancellationToken token)
  {
    try
    {
      NetworkStream stream = client.GetStream();
      while (!token.IsCancellationRequested)
      {
        RemoteVideoFrame? frame = await BinaryVideoProtocol.ReadFrameAsync(stream, token);
        if (frame is null) break;
        VideoFrameReceived?.Invoke(frame);
      }
    }
    catch (OperationCanceledException) { }
    catch { }
    finally
    {
      try { client.Close(); } catch { }
      if (generation == Interlocked.Read(ref _tcpVideoGeneration) &&
          ReferenceEquals(_tcpVideoClient, client))
      {
        _tcpVideoStream = null;
        _tcpVideoClient = null;
        Changed(nameof(IsVideoConnected));
      }
    }
  }

  public void AttachTcpInputClient(TcpClient client, CancellationToken parentToken)
  {
    CancellationTokenSource? previousCts = _tcpInputCts;
    TcpClient? previousClient = _tcpInputClient;
    try { previousCts?.Cancel(); previousClient?.Close(); } catch { }
    previousCts?.Dispose();

    client.NoDelay = true;
    _tcpInputClient = client;
    _tcpInputStream = client.GetStream();
    _tcpInputCts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
    long generation = Interlocked.Increment(ref _tcpInputGeneration);
    Changed(nameof(IsInputConnected));
    _ = Task.Run(
      () => TcpInputReadLoopAsync(client, generation, _tcpInputCts.Token),
      CancellationToken.None);
  }

  private async Task TcpInputReadLoopAsync(
    TcpClient client,
    long generation,
    CancellationToken token)
  {
    try
    {
      NetworkStream stream = client.GetStream();
      while (!token.IsCancellationRequested)
      {
        ControlPacket? packet = await BinaryControlProtocol.ReadAsync(stream, token);
        if (packet is null) break;
        switch (packet.Type)
        {
          case ControlPacketType.InputResult:
            InputResultPacket result = BinaryControlProtocol.ReadInputResult(packet);
            LastInputResult = result;
            LastInputResultAt = DateTime.UtcNow;
            InputResultReceived?.Invoke(result);
            break;
          case ControlPacketType.VideoStatus:
            VideoStatusReceived?.Invoke(BinaryControlProtocol.ReadVideoStatus(packet));
            break;
          case ControlPacketType.Ping:
            await SendTcpInputControlAsync(
              BinaryControlProtocol.Pong(BinaryControlProtocol.ReadInt64(packet)),
              token);
            break;
        }
      }
    }
    catch (OperationCanceledException) { }
    catch { }
    finally
    {
      try { client.Close(); } catch { }
      if (generation == Interlocked.Read(ref _tcpInputGeneration) &&
          ReferenceEquals(_tcpInputClient, client))
      {
        _tcpInputStream = null;
        _tcpInputClient = null;
        Changed(nameof(IsInputConnected));
      }
    }
  }

  private Task SendTcpVideoControlAsync(
    ControlPacket packet,
    CancellationToken token) =>
    WriteTcpControlAsync(
      packet,
      _tcpVideoControlWriteLock,
      () => _tcpVideoStream,
      () => _tcpVideoClient,
      token,
      "TCP视频回退通道尚未连接。");

  private Task SendTcpInputControlAsync(
    ControlPacket packet,
    CancellationToken token) =>
    WriteTcpControlAsync(
      packet,
      _tcpInputWriteLock,
      () => _tcpInputStream,
      () => _tcpInputClient,
      token,
      "TCP输入回退通道尚未连接。");

  private async Task WriteTcpControlAsync(
    ControlPacket packet,
    SemaphoreSlim writeLock,
    Func<NetworkStream?> streamResolver,
    Func<TcpClient?> clientResolver,
    CancellationToken token,
    string unavailableMessage)
  {
    NetworkStream? stream = streamResolver();
    if (stream is null) throw new InvalidOperationException(unavailableMessage);
    await writeLock.WaitAsync(token);
    try
    {
      if (!ReferenceEquals(streamResolver(), stream))
        throw new IOException(unavailableMessage);
      await BinaryControlProtocol.WriteAsync(stream, packet, token);
    }
    catch
    {
      if (ReferenceEquals(streamResolver(), stream))
      {
        try { clientResolver()?.Close(); } catch { }
      }
      throw;
    }
    finally { writeLock.Release(); }
  }

  private void CloseTcpDesktopFallback()
  {
    CancellationTokenSource? videoCts = _tcpVideoCts;
    CancellationTokenSource? inputCts = _tcpInputCts;
    _tcpVideoCts = null;
    _tcpInputCts = null;
    try { videoCts?.Cancel(); inputCts?.Cancel(); } catch { }
    try { _tcpVideoStream?.Dispose(); _tcpVideoClient?.Close(); } catch { }
    try { _tcpInputStream?.Dispose(); _tcpInputClient?.Close(); } catch { }
    _tcpVideoStream = null;
    _tcpVideoClient = null;
    _tcpInputStream = null;
    _tcpInputClient = null;
    videoCts?.Dispose();
    inputCts?.Dispose();
  }
}
