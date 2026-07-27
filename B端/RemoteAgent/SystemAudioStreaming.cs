using Concentus.Enums;
using Concentus;
using NAudio.Wave;
using RemoteControl.Shared;
using System.Net.Sockets;

namespace RemoteAgent;

public partial class MainWindow
{
  private const int AudioSampleRate = 48000;
  private const int AudioChannels = 2;
  private const int AudioFrameSamples = 960;
  private readonly object _audioGate = new();
  private readonly SemaphoreSlim _udpAudioSendLock = new(1, 1);
  private CancellationTokenSource? _audioCts;
  private Task? _audioTask;
  private UdpClient? _udpAudioClient;
  private Guid _audioSessionId;
  private long _udpAudioLastAckAt;
  private long _audioSequence;

  private OperationResultPayload StartSystemAudio(DesktopSessionPayload request)
  {
    if (!Guid.TryParseExact(request.SessionId, "N", out Guid sessionId))
      return new(false, "音频会话标识无效。");
    lock (_desktopSessionGate)
    {
      if (_activeDesktopSession != sessionId || !_videoStreaming)
        return new(false, "桌面会话已经结束。");
    }
    lock (_audioGate)
    {
      if (_audioSessionId == sessionId &&
          _audioTask is { IsCompleted: false })
        return new(true, "系统音频已经开始传输。");
      ForceStopSystemAudioCore();
      _audioSessionId = sessionId;
      _audioCts = _cts is null
        ? new CancellationTokenSource()
        : CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
      CancellationToken token = _audioCts.Token;
      _audioTask = Task.Run(
        () => UdpSystemAudioLoopAsync(
          _controllerHost,
          _controllerPort,
          sessionId,
          token),
        CancellationToken.None);
    }
    return new(true, "系统音频传输已启动。");
  }

  private OperationResultPayload StopSystemAudio(DesktopSessionPayload request)
  {
    if (!Guid.TryParseExact(request.SessionId, "N", out Guid sessionId))
      return new(false, "音频会话标识无效。");
    lock (_audioGate)
    {
      if (_audioSessionId != Guid.Empty && _audioSessionId != sessionId)
        return new(true, "过期音频会话已忽略。");
      ForceStopSystemAudioCore();
    }
    return new(true, "系统音频传输已停止。");
  }

  private void ForceStopSystemAudio()
  {
    lock (_audioGate) ForceStopSystemAudioCore();
  }

  private void ForceStopSystemAudioCore()
  {
    try { _audioCts?.Cancel(); } catch { }
    try { _udpAudioClient?.Dispose(); } catch { }
    _udpAudioClient = null;
    _audioCts?.Dispose();
    _audioCts = null;
    _audioTask = null;
    _audioSessionId = Guid.Empty;
    Interlocked.Exchange(ref _udpAudioLastAckAt, 0);
  }

  private async Task UdpSystemAudioLoopAsync(
    string host,
    int port,
    Guid sessionId,
    CancellationToken token)
  {
    UdpClient? udp = null;
    using var connectionCts =
      CancellationTokenSource.CreateLinkedTokenSource(token);
    try
    {
      udp = CreateConnectedUdpClient(host, port);
      _udpAudioClient = udp;
      Task receiveTask = Task.Run(
        () => UdpAudioReceiveLoopAsync(udp, connectionCts.Token),
        CancellationToken.None);
      Task helloTask = Task.Run(
        () => UdpHelloLoopAsync(
          udp,
          _udpAudioSendLock,
          UdpDesktopPeerRole.AudioProducer,
          connectionCts.Token),
        CancellationToken.None);

      while (!token.IsCancellationRequested &&
             Interlocked.Read(ref _udpAudioLastAckAt) == 0)
        await Task.Delay(20, token);

      await CaptureAndSendSystemAudioAsync(udp, sessionId, token);
      connectionCts.Cancel();
      try { await Task.WhenAll(receiveTask, helloTask); } catch { }
    }
    catch (OperationCanceledException) { }
    catch
    {
      if (!token.IsCancellationRequested)
        await Task.Delay(250, token).ContinueWith(_ => { });
    }
    finally
    {
      connectionCts.Cancel();
      try { udp?.Dispose(); } catch { }
      if (ReferenceEquals(_udpAudioClient, udp))
        _udpAudioClient = null;
    }
  }

  private async Task UdpAudioReceiveLoopAsync(
    UdpClient udp,
    CancellationToken token)
  {
    while (!token.IsCancellationRequested)
    {
      UdpReceiveResult received = await udp.ReceiveAsync(token);
      if (!UdpDesktopProtocol.TryParse(
            received.Buffer,
            out UdpDesktopDatagram? packet) ||
          packet is null ||
          packet.Role != UdpDesktopPeerRole.Controller)
        continue;
      if (packet.Kind == UdpDesktopPacketKind.HelloAck)
      {
        if (UdpDesktopProtocol.ReadHelloAckInstanceId(packet) != _instanceId)
          continue;
        Interlocked.Exchange(ref _udpAudioLastAckAt, Environment.TickCount64);
        await SendConnectedUdpAsync(
          udp,
          _udpAudioSendLock,
          UdpDesktopProtocol.Ping(
            UdpDesktopPeerRole.AudioProducer,
            Guid.Empty,
            Interlocked.Increment(ref _udpDesktopSequence)),
          token);
      }
      else if (packet.Kind == UdpDesktopPacketKind.Ping)
      {
        await SendConnectedUdpAsync(
          udp,
          _udpAudioSendLock,
          UdpDesktopProtocol.Pong(
            UdpDesktopPeerRole.AudioProducer,
            packet.SessionId,
            packet.Sequence),
          token);
      }
      else if (packet.Kind == UdpDesktopPacketKind.Pong)
      {
        Interlocked.Exchange(ref _udpAudioLastAckAt, Environment.TickCount64);
      }
    }
  }

  private async Task CaptureAndSendSystemAudioAsync(
    UdpClient udp,
    Guid sessionId,
    CancellationToken token)
  {
    using MultimediaThreadScope priority = MultimediaThreadScope.Enter("Audio");
    using var capture = new WasapiLoopbackCapture();
    var source = new BufferedWaveProvider(capture.WaveFormat)
    {
      BufferDuration = TimeSpan.FromMilliseconds(500),
      DiscardOnBufferOverflow = true,
      ReadFully = false
    };
    capture.DataAvailable += (_, e) => source.AddSamples(e.Buffer, 0, e.BytesRecorded);
    using var resampler = new MediaFoundationResampler(
      source,
      new WaveFormat(AudioSampleRate, 16, AudioChannels))
    {
      ResamplerQuality = 60
    };
    OpusCodecFactory.AttemptToUseNativeLibrary = false;
    IOpusEncoder encoder = OpusCodecFactory.CreateEncoder(
      AudioSampleRate,
      AudioChannels,
      OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
    encoder.Bitrate = 96000;
    encoder.Complexity = 5;

    byte[] pcmBytes = new byte[AudioFrameSamples * AudioChannels * sizeof(short)];
    short[] pcm = new short[AudioFrameSamples * AudioChannels];
    byte[] opus = new byte[1000];
    capture.StartRecording();
    try
    {
      while (!token.IsCancellationRequested)
      {
        int read = resampler.Read(pcmBytes, 0, pcmBytes.Length);
        if (read < pcmBytes.Length)
        {
          await Task.Delay(5, token);
          continue;
        }
        Buffer.BlockCopy(pcmBytes, 0, pcm, 0, pcmBytes.Length);
        int encoded = encoder.Encode(
          pcm.AsSpan(),
          AudioFrameSamples,
          opus.AsSpan(),
          opus.Length);
        if (encoded <= 0) continue;
        byte[] packet = UdpDesktopProtocol.AudioFrame(
          sessionId,
          Interlocked.Increment(ref _audioSequence),
          DateTime.UtcNow.Ticks,
          AudioSampleRate,
          AudioChannels,
          AudioFrameSamples,
          opus.AsSpan(0, encoded));
        await SendConnectedUdpAsync(
          udp,
          _udpAudioSendLock,
          packet,
          token);
      }
    }
    finally
    {
      try { capture.StopRecording(); } catch { }
    }
  }
}
