using RemoteControl.Shared;
using System.Collections.Concurrent;
using System.Net;

namespace ControlCenter;

public sealed partial class DeviceView
{
  private readonly object _udpGate = new();
  private Func<byte[], IPEndPoint, CancellationToken, Task>? _udpSender;
  private CancellationTokenSource? _udpTransportCts;
  private IPEndPoint? _udpVideoEndpoint;
  private IPEndPoint? _udpInputEndpoint;
  private IPEndPoint? _udpAudioEndpoint;
  private long _udpVideoSeenAt;
  private long _udpInputSeenAt;
  private long _udpAudioSeenAt;
  private Guid _udpSessionId;
  private bool _udpAllowControl;
  private long _udpSequence;
  private long _udpReliableInputSequence;
  private readonly UdpVideoFrameAssembler _udpVideoAssembler = new();
  private readonly ConcurrentDictionary<long, PendingUdpInput> _pendingUdpInput = [];

  public void ConfigureUdpTransport(
    Func<byte[], IPEndPoint, CancellationToken, Task> sender,
    CancellationToken parentToken)
  {
    lock (_udpGate)
    {
      _udpSender = sender;
      if (_udpTransportCts is not null) return;
      _udpTransportCts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
      _ = Task.Run(
        () => UdpInputRetryLoopAsync(_udpTransportCts.Token),
        CancellationToken.None);
      _ = Task.Run(
        () => UdpVideoRecoveryLoopAsync(_udpTransportCts.Token),
        CancellationToken.None);
    }
  }

  public async Task RegisterUdpPeerAsync(
    UdpDesktopPeerRole role,
    IPEndPoint endpoint,
    Guid instanceId,
    long helloSequence,
    CancellationToken token)
  {
    if (!AcceptsInstance(instanceId)) return;
    Func<byte[], IPEndPoint, CancellationToken, Task>? sender;
    Guid session;
    bool allowControl;
    lock (_udpGate)
    {
      sender = _udpSender;
      session = _udpSessionId;
      allowControl = _udpAllowControl;
      if (role == UdpDesktopPeerRole.VideoProducer)
      {
        bool changed = !Equals(_udpVideoEndpoint, endpoint);
        _udpVideoEndpoint = endpoint;
        if (changed) _udpVideoSeenAt = 0;
      }
      else if (role == UdpDesktopPeerRole.InputExecutor)
      {
        bool changed = !Equals(_udpInputEndpoint, endpoint);
        _udpInputEndpoint = endpoint;
        if (changed)
        {
          _udpInputSeenAt = 0;
          _udpReliableInputSequence = 0;
          _pendingUdpInput.Clear();
        }
      }
      else if (role == UdpDesktopPeerRole.AudioProducer)
      {
        bool changed = !Equals(_udpAudioEndpoint, endpoint);
        _udpAudioEndpoint = endpoint;
        if (changed) _udpAudioSeenAt = 0;
      }
    }
    Changed(nameof(IsVideoConnected));
    Changed(nameof(IsInputConnected));
    if (sender is null) return;
    await sender(
      UdpDesktopProtocol.HelloAck(role, instanceId, helloSequence),
      endpoint,
      token);
    if (session != Guid.Empty)
      await sender(
        UdpDesktopProtocol.SessionStart(
          session,
          allowControl,
          Interlocked.Increment(ref _udpSequence)),
        endpoint,
        token);
  }

  public async Task HandleUdpDesktopDatagramAsync(
    UdpDesktopDatagram packet,
    IPEndPoint endpoint,
    CancellationToken token)
  {
    Func<byte[], IPEndPoint, CancellationToken, Task>? sender;
    Guid activeSession;
    lock (_udpGate)
    {
      sender = _udpSender;
      activeSession = _udpSessionId;
      if (packet.Role == UdpDesktopPeerRole.VideoProducer)
      {
        if (!Equals(_udpVideoEndpoint, endpoint)) return;
        _udpVideoSeenAt = Environment.TickCount64;
      }
      else if (packet.Role == UdpDesktopPeerRole.InputExecutor)
      {
        if (!Equals(_udpInputEndpoint, endpoint)) return;
        _udpInputSeenAt = Environment.TickCount64;
      }
      else if (packet.Role == UdpDesktopPeerRole.AudioProducer)
      {
        if (!Equals(_udpAudioEndpoint, endpoint)) return;
        _udpAudioSeenAt = Environment.TickCount64;
      }
      else return;
    }

    if (packet.Kind is UdpDesktopPacketKind.Ping or UdpDesktopPacketKind.Pong)
      Changed(packet.Role == UdpDesktopPeerRole.VideoProducer
        ? nameof(IsVideoConnected)
        : nameof(IsInputConnected));
    if (packet.Kind == UdpDesktopPacketKind.Ping && sender is not null)
    {
      await sender(
        UdpDesktopProtocol.Pong(
          UdpDesktopPeerRole.Controller,
          packet.SessionId,
          packet.Sequence),
        endpoint,
        token);
      return;
    }
    if (packet.Kind == UdpDesktopPacketKind.Pong) return;
    if (activeSession == Guid.Empty || packet.SessionId != activeSession) return;

    switch (packet.Kind)
    {
      case UdpDesktopPacketKind.VideoFragment:
        if (_udpVideoAssembler.TryAdd(
              packet,
              out Guid frameSession,
              out RemoteVideoFrame? frame) &&
            frameSession == activeSession &&
            frame is not null)
          VideoFrameReceived?.Invoke(frame);
        break;

      case UdpDesktopPacketKind.VideoStatus:
        VideoStatusReceived?.Invoke(UdpDesktopProtocol.ReadVideoStatus(packet));
        break;

      case UdpDesktopPacketKind.InputAck:
        UdpInputAck ack = UdpDesktopProtocol.ReadInputAck(packet);
        _pendingUdpInput.TryRemove(packet.Sequence, out _);
        LastInputResult = new InputResultPacket(
          ack.SourceType,
          ack.Success,
          ack.Win32Error);
        LastInputResultAt = DateTime.UtcNow;
        InputResultReceived?.Invoke(LastInputResult.Value);
        break;

      case UdpDesktopPacketKind.AudioFrame:
        AudioFrameReceived?.Invoke(UdpDesktopProtocol.ReadAudioFrame(packet));
        break;
    }
  }

  public async Task StartUdpDesktopSessionAsync(
    Guid sessionId,
    bool allowControl,
    CancellationToken token = default)
  {
    if (sessionId == Guid.Empty)
      throw new InvalidOperationException("远控会话标识无效。");
    IPEndPoint? video;
    IPEndPoint? input;
    IPEndPoint? audio;
    Func<byte[], IPEndPoint, CancellationToken, Task>? sender;
    lock (_udpGate)
    {
      bool newSession = _udpSessionId != sessionId;
      _udpSessionId = sessionId;
      _udpAllowControl = allowControl;
      video = _udpVideoEndpoint;
      input = _udpInputEndpoint;
      audio = _udpAudioEndpoint;
      sender = _udpSender;
      if (newSession)
      {
        _udpReliableInputSequence = 0;
        _udpVideoAssembler.Reset();
        _pendingUdpInput.Clear();
      }
    }
    if (sender is null)
      throw new InvalidOperationException("UDP桌面通道尚未启动。");
    byte[] packet = UdpDesktopProtocol.SessionStart(
      sessionId,
      allowControl,
      Interlocked.Increment(ref _udpSequence));
    var sends = new List<Task>(3);
    if (video is not null) sends.Add(sender(packet, video, token));
    if (input is not null) sends.Add(sender(packet, input, token));
    if (audio is not null) sends.Add(sender(packet, audio, token));
    if (sends.Count > 0) await Task.WhenAll(sends);
  }

  public async Task StopUdpDesktopSessionAsync(
    Guid sessionId,
    CancellationToken token = default)
  {
    IPEndPoint? video;
    IPEndPoint? input;
    IPEndPoint? audio;
    Func<byte[], IPEndPoint, CancellationToken, Task>? sender;
    lock (_udpGate)
    {
      video = _udpVideoEndpoint;
      input = _udpInputEndpoint;
      audio = _udpAudioEndpoint;
      sender = _udpSender;
      if (_udpSessionId == sessionId)
      {
        _udpSessionId = Guid.Empty;
        _udpAllowControl = false;
      }
      _udpVideoAssembler.Reset();
      _pendingUdpInput.Clear();
    }
    if (sender is null || sessionId == Guid.Empty) return;
    for (int attempt = 0; attempt < 2; attempt++)
    {
      byte[] packet = UdpDesktopProtocol.SessionStop(
        sessionId,
        Interlocked.Increment(ref _udpSequence));
      var sends = new List<Task>(3);
      if (video is not null) sends.Add(sender(packet, video, token));
      if (input is not null) sends.Add(sender(packet, input, token));
      if (audio is not null) sends.Add(sender(packet, audio, token));
      if (sends.Count > 0) await Task.WhenAll(sends);
      if (attempt == 0) await Task.Delay(15, token);
    }
  }

  private async Task SendUdpControlAsync(
    ControlPacket packet,
    CancellationToken token)
  {
    Func<byte[], IPEndPoint, CancellationToken, Task>? sender;
    IPEndPoint? endpoint;
    Guid session;
    bool videoPacket = packet.Type == ControlPacketType.VideoFeedback;
    lock (_udpGate)
    {
      sender = _udpSender;
      endpoint = videoPacket ? _udpVideoEndpoint : _udpInputEndpoint;
      session = _udpSessionId;
    }
    if (sender is null || endpoint is null)
      throw new InvalidOperationException(
        videoPacket ? "UDP视频反馈通道尚未连接。" : "UDP输入通道尚未连接。");
    if (session == Guid.Empty)
      throw new InvalidOperationException("远控会话已经结束。");

    bool reliable = !videoPacket && packet.Type is
      ControlPacketType.MouseButton or
      ControlPacketType.MouseWheel or
      ControlPacketType.Key or
      ControlPacketType.ReleaseAll;
    long sequence = reliable
      ? Interlocked.Increment(ref _udpReliableInputSequence)
      : Interlocked.Increment(ref _udpSequence);
    if (videoPacket)
    {
      await sender(
        UdpDesktopProtocol.VideoFeedback(session, packet, sequence),
        endpoint,
        token);
      return;
    }

    byte[] datagram = UdpDesktopProtocol.Input(
      session,
      packet,
      sequence,
      reliable);
    if (reliable)
      _pendingUdpInput[sequence] = new(
        datagram,
        endpoint,
        packet.Type,
        Environment.TickCount64,
        Environment.TickCount64 + 35,
        1);
    await sender(datagram, endpoint, token);
  }

  private async Task UdpInputRetryLoopAsync(CancellationToken token)
  {
    while (!token.IsCancellationRequested)
    {
      long now = Environment.TickCount64;
      foreach ((long sequence, PendingUdpInput pending) in _pendingUdpInput)
      {
        if (now < pending.NextAttemptAt) continue;
        if (now - pending.CreatedAt >= 700 || pending.Attempts >= 8)
        {
          if (_pendingUdpInput.TryRemove(sequence, out _))
          {
            LastInputResult = new InputResultPacket(
              pending.SourceType,
              false,
              10060);
            LastInputResultAt = DateTime.UtcNow;
            InputResultReceived?.Invoke(LastInputResult.Value);
          }
          continue;
        }
        var next = pending with
        {
          NextAttemptAt = now + Math.Min(120, 30 + pending.Attempts * 10),
          Attempts = pending.Attempts + 1
        };
        if (!_pendingUdpInput.TryUpdate(sequence, next, pending)) continue;
        Func<byte[], IPEndPoint, CancellationToken, Task>? sender;
        lock (_udpGate) sender = _udpSender;
        if (sender is null) continue;
        try { await sender(next.Datagram, next.Endpoint, token); }
        catch when (!token.IsCancellationRequested) { }
      }
      await Task.Delay(15, token);
    }
  }

  private async Task UdpVideoRecoveryLoopAsync(CancellationToken token)
  {
    while (!token.IsCancellationRequested)
    {
      IReadOnlyList<UdpVideoRetransmitRequest> requests =
        _udpVideoAssembler.CollectRetransmissionRequests(DateTime.UtcNow);
      if (requests.Count == 0)
      {
        await Task.Delay(6, token);
        continue;
      }

      Func<byte[], IPEndPoint, CancellationToken, Task>? sender;
      IPEndPoint? endpoint;
      Guid session;
      lock (_udpGate)
      {
        sender = _udpSender;
        endpoint = _udpVideoEndpoint;
        session = _udpSessionId;
      }
      if (sender is null || endpoint is null || session == Guid.Empty)
      {
        await Task.Delay(6, token);
        continue;
      }

      foreach (UdpVideoRetransmitRequest request in requests)
      {
        try
        {
          await sender(
            UdpDesktopProtocol.VideoRetransmitRequest(
              session,
              request.FrameId,
              request.MissingFragments,
              Interlocked.Increment(ref _udpSequence)),
            endpoint,
            token);
        }
        catch when (!token.IsCancellationRequested) { }
      }
    }
  }

  private bool IsUdpPeerFresh(UdpDesktopPeerRole role)
  {
    lock (_udpGate)
    {
      long seen = role == UdpDesktopPeerRole.VideoProducer
        ? _udpVideoSeenAt
        : _udpInputSeenAt;
      IPEndPoint? endpoint = role == UdpDesktopPeerRole.VideoProducer
        ? _udpVideoEndpoint
        : _udpInputEndpoint;
      return endpoint is not null &&
             Environment.TickCount64 - seen < 6000;
    }
  }

  private void CloseUdpDesktop()
  {
    try { _udpTransportCts?.Cancel(); } catch { }
    _udpTransportCts?.Dispose();
    _udpTransportCts = null;
    lock (_udpGate)
    {
      _udpSender = null;
      _udpVideoEndpoint = null;
      _udpInputEndpoint = null;
      _udpAudioEndpoint = null;
      _udpSessionId = Guid.Empty;
      _udpAllowControl = false;
    }
    _udpVideoAssembler.Reset();
    _pendingUdpInput.Clear();
  }

  private sealed record PendingUdpInput(
    byte[] Datagram,
    IPEndPoint Endpoint,
    ControlPacketType SourceType,
    long CreatedAt,
    long NextAttemptAt,
    int Attempts);
}
