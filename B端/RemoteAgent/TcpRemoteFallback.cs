using RemoteControl.Shared;
using System.Net.Sockets;

namespace RemoteAgent;

public partial class MainWindow
{
  private readonly object _tcpVideoGate = new();
  private TcpClient? _tcpVideoFallbackClient;
  private NetworkStream? _tcpVideoFallbackStream;
  private readonly SemaphoreSlim _tcpVideoFallbackWriteLock = new(1, 1);
  private TcpClient? _tcpInputFallbackClient;
  private NetworkStream? _tcpInputFallbackStream;
  private readonly SemaphoreSlim _tcpInputFallbackWriteLock = new(1, 1);
  private long _tcpLastMouseMoveResultAt;

  private bool IsTcpVideoFallbackConnected
  {
    get { lock (_tcpVideoGate) return _tcpVideoFallbackStream is not null; }
  }

  private async Task TcpVideoFallbackConnectLoopAsync(
    string host,
    int port,
    CancellationToken token)
  {
    while (!token.IsCancellationRequested && _stream is not null)
    {
      TcpClient? client = null;
      NetworkStream? stream = null;
      try
      {
        client = new TcpClient
        {
          NoDelay = true,
          SendBufferSize = 256 * 1024,
          ReceiveBufferSize = 64 * 1024
        };
        await client.ConnectAsync(host, port, token);
        stream = client.GetStream();
        await WriteLogicalChannelHelloAsync(
          stream,
          LogicalChannelType.Video,
          token);
        await BinaryVideoProtocol.WriteHelloAsync(stream, _deviceId, token);
        lock (_tcpVideoGate)
        {
          _tcpVideoFallbackClient = client;
          _tcpVideoFallbackStream = stream;
        }

        await TcpVideoControlReadLoopAsync(stream, token);
      }
      catch (OperationCanceledException) when (token.IsCancellationRequested)
      {
        break;
      }
      catch
      {
        await Task.Delay(250, token).ContinueWith(_ => { });
      }
      finally
      {
        lock (_tcpVideoGate)
        {
          if (ReferenceEquals(_tcpVideoFallbackStream, stream))
          {
            _tcpVideoFallbackStream = null;
            _tcpVideoFallbackClient = null;
          }
        }
        try { stream?.Dispose(); client?.Close(); } catch { }
      }
    }
  }

  private async Task TcpVideoControlReadLoopAsync(
    NetworkStream stream,
    CancellationToken token)
  {
    while (!token.IsCancellationRequested)
    {
      ControlPacket? packet = await BinaryControlProtocol.ReadAsync(stream, token);
      if (packet is null) return;
      if (packet.Type != ControlPacketType.VideoFeedback) continue;
      VideoFeedbackPacket feedback =
        BinaryControlProtocol.ReadVideoFeedback(packet);
      Guid active;
      lock (_desktopSessionGate) active = _activeDesktopSession;
      if (active != Guid.Empty && feedback.SessionId == active)
        _videoQuality.OnFeedback(feedback);
    }
  }

  private async Task<bool> TrySendTcpVideoFrameAsync(
    RemoteVideoFrame frame,
    CancellationToken token)
  {
    NetworkStream? stream;
    lock (_tcpVideoGate) stream = _tcpVideoFallbackStream;
    if (stream is null) return false;

    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
    timeout.CancelAfter(TimeSpan.FromMilliseconds(1500));
    await _tcpVideoFallbackWriteLock.WaitAsync(timeout.Token);
    try
    {
      lock (_tcpVideoGate)
      {
        if (!ReferenceEquals(_tcpVideoFallbackStream, stream)) return false;
      }
      await BinaryVideoProtocol.WriteFrameAsync(stream, frame, timeout.Token);
      return true;
    }
    catch (OperationCanceledException) when (token.IsCancellationRequested)
    {
      throw;
    }
    catch
    {
      TcpClient? client = null;
      lock (_tcpVideoGate)
      {
        if (ReferenceEquals(_tcpVideoFallbackStream, stream))
        {
          client = _tcpVideoFallbackClient;
          _tcpVideoFallbackStream = null;
          _tcpVideoFallbackClient = null;
        }
      }
      try { client?.Close(); } catch { }
      return false;
    }
    finally { _tcpVideoFallbackWriteLock.Release(); }
  }

  private async Task TcpInputFallbackConnectLoopAsync(
    string host,
    int port,
    CancellationToken token)
  {
    while (!token.IsCancellationRequested && (_stream is not null || _inputOnly))
    {
      TcpClient? client = null;
      NetworkStream? stream = null;
      try
      {
        client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(host, port, token);
        stream = client.GetStream();
        await WriteLogicalChannelHelloAsync(
          stream,
          LogicalChannelType.Input,
          token);
        await BinaryControlProtocol.WriteHelloAsync(stream, _deviceId, token);
        _tcpInputFallbackClient = client;
        _tcpInputFallbackStream = stream;
        if (_inputOnly) QueueAgentStatus("已链接");

        while (!token.IsCancellationRequested &&
               ReferenceEquals(_tcpInputFallbackStream, stream))
        {
          ControlPacket? packet = await BinaryControlProtocol.ReadAsync(stream, token);
          if (packet is null) break;
          await HandleTcpFallbackControlAsync(packet, token);
        }
      }
      catch (OperationCanceledException) when (token.IsCancellationRequested)
      {
        break;
      }
      catch
      {
        if (_inputOnly) QueueAgentStatus("已断开");
        await Task.Delay(250, token).ContinueWith(_ => { });
      }
      finally
      {
        try
        {
          await _inputDispatcher.ReleaseAllAsync(
            Interlocked.Read(ref _desktopEnvironmentGeneration),
            CancellationToken.None);
        }
        catch { }
        if (ReferenceEquals(_tcpInputFallbackStream, stream))
        {
          _tcpInputFallbackStream = null;
          _tcpInputFallbackClient = null;
        }
        try { stream?.Dispose(); client?.Close(); } catch { }
      }
    }
  }

  private async Task HandleTcpFallbackControlAsync(
    ControlPacket packet,
    CancellationToken token)
  {
    bool inputPacket = packet.Type is
      ControlPacketType.MouseMove or
      ControlPacketType.MouseButton or
      ControlPacketType.MouseWheel or
      ControlPacketType.Key or
      ControlPacketType.ReleaseAll;
    try
    {
      if (inputPacket)
      {
        await _inputDispatcher.ExecuteAsync(
          packet,
          Interlocked.Read(ref _desktopEnvironmentGeneration),
          token);
      }
      else if (packet.Type == ControlPacketType.Ping)
      {
        await SendTcpInputFallbackPacketAsync(
          BinaryControlProtocol.Pong(BinaryControlProtocol.ReadInt64(packet)),
          token);
        return;
      }

      if (!inputPacket) return;
      if (packet.Type == ControlPacketType.MouseMove &&
          Environment.TickCount64 - Interlocked.Read(
            ref _tcpLastMouseMoveResultAt) < 500)
        return;
      if (packet.Type == ControlPacketType.MouseMove)
        Interlocked.Exchange(
          ref _tcpLastMouseMoveResultAt,
          Environment.TickCount64);
      await SendTcpInputFallbackPacketAsync(
        BinaryControlProtocol.InputResult(packet.Type, true),
        token);
    }
    catch (System.ComponentModel.Win32Exception ex) when (inputPacket)
    {
      await SendTcpInputFallbackPacketAsync(
        BinaryControlProtocol.InputResult(
          packet.Type,
          false,
          ex.NativeErrorCode),
        token);
    }
    catch (Exception ex) when (inputPacket)
    {
      await SendTcpInputFallbackPacketAsync(
        BinaryControlProtocol.InputResult(
          packet.Type,
          false,
          ex.HResult),
        token);
    }
  }

  private async Task SendTcpInputFallbackPacketAsync(
    ControlPacket packet,
    CancellationToken token)
  {
    NetworkStream? stream = _tcpInputFallbackStream;
    if (stream is null) return;
    await _tcpInputFallbackWriteLock.WaitAsync(token);
    try
    {
      if (ReferenceEquals(_tcpInputFallbackStream, stream))
        await BinaryControlProtocol.WriteAsync(stream, packet, token);
    }
    finally { _tcpInputFallbackWriteLock.Release(); }
  }

  private void CloseTcpDesktopFallbackChannels()
  {
    TcpClient? videoClient;
    lock (_tcpVideoGate)
    {
      videoClient = _tcpVideoFallbackClient;
      _tcpVideoFallbackClient = null;
      _tcpVideoFallbackStream = null;
    }
    try { videoClient?.Close(); } catch { }
    try { _tcpInputFallbackStream?.Dispose(); _tcpInputFallbackClient?.Close(); } catch { }
    _tcpInputFallbackStream = null;
    _tcpInputFallbackClient = null;
  }
}
