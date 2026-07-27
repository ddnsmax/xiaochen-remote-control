using H264Sharp;
using Lennox.LibYuvSharp;
using RemoteControl.Shared;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace RemoteAgent;

public partial class MainWindow
{
  private UdpClient? _udpVideoClient;
  private UdpClient? _udpInputClient;
  private readonly SemaphoreSlim _udpVideoSendLock = new(1, 1);
  private readonly SemaphoreSlim _udpInputSendLock = new(1, 1);
  private long _udpDesktopSequence;
  private long _udpVideoLastAckAt;
  private long _udpVideoPingSequence;
  private long _udpVideoPingSentAt;
  private long _udpInputLastAckAt;
  private Guid _udpInputSession;
  private bool _udpInputAllowed;
  private long _udpExpectedReliableInput = 1;
  private long _udpLastMouseSequence;
  private readonly SortedDictionary<long, UdpDesktopDatagram> _udpReliableInputBuffer = [];
  private readonly Dictionary<long, byte[]> _udpInputAckCache = [];
  private readonly UdpVideoRetransmitCache _udpVideoRetransmitCache = new();

  private async Task UdpH264VideoLoopAsync(
    string host,
    int port,
    CancellationToken token)
  {
    using MultimediaThreadScope multimediaPriority = MultimediaThreadScope.Enter("Capture");
    _videoQuality.SetLocalNetworkMode(IsLocalNetworkHost(host));
    Converter.SetOption(
      ConverterOption.NumThreads,
      Math.Clamp(Environment.ProcessorCount, 1, 16));
    DxgiDesktopCapture? capture = null;
    H264Encoder? encoder = null;
    YuvImage? yuvFrame = null;
    int encoderWidth = 0;
    int encoderHeight = 0;
    int encoderBitrate = 0;
    int encoderFps = 0;
    long frameId = 0;
    long desktopGeneration = Interlocked.Read(ref _desktopEnvironmentGeneration);
    long videoSessionGeneration = Interlocked.Read(ref _videoSessionGeneration);
    bool? lastSendWasUdp = null;

    while (!token.IsCancellationRequested && _stream is not null)
    {
      UdpClient? udp = null;
      using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(token);
      try
      {
        udp = CreateConnectedUdpClient(host, port);
        _udpVideoClient = udp;
        Interlocked.Exchange(ref _udpVideoLastAckAt, 0);
        Task receiveTask = Task.Run(
          () => UdpVideoReceiveLoopAsync(udp, connectionCts.Token),
          CancellationToken.None);
        Task helloTask = Task.Run(
          () => UdpHelloLoopAsync(
            udp,
            _udpVideoSendLock,
            UdpDesktopPeerRole.VideoProducer,
            connectionCts.Token),
          CancellationToken.None);

        while (!token.IsCancellationRequested)
        {
          long currentDesktopGeneration =
            Interlocked.Read(ref _desktopEnvironmentGeneration);
          long currentVideoSessionGeneration =
            Interlocked.Read(ref _videoSessionGeneration);
          bool inputDesktopChanged =
            capture is not null &&
            !WindowsInputDesktop.IsCurrentThreadOnInputDesktop();
          if (inputDesktopChanged)
          {
            currentDesktopGeneration = Interlocked.Increment(
              ref _desktopEnvironmentGeneration);
            _inputDispatcher.QueueReleaseAll(currentDesktopGeneration);
          }
          if (currentDesktopGeneration != desktopGeneration ||
              currentVideoSessionGeneration != videoSessionGeneration ||
              inputDesktopChanged)
          {
            desktopGeneration = currentDesktopGeneration;
            videoSessionGeneration = currentVideoSessionGeneration;
            capture?.Dispose();
            capture = null;
            encoder?.Dispose();
            encoder = null;
            yuvFrame?.Dispose();
            yuvFrame = null;
            encoderWidth = encoderHeight = encoderBitrate = encoderFps = 0;
            _udpVideoRetransmitCache.Clear();
            _videoQuality.ResetForNewSession();
          }

          long lastAck = Interlocked.Read(ref _udpVideoLastAckAt);
          bool udpReady = lastAck != 0 &&
                          Environment.TickCount64 - lastAck < 2500;
          bool tcpReady = IsTcpVideoFallbackConnected;
          if (!udpReady && !tcpReady)
          {
            await Task.Delay(20, token);
            continue;
          }

          Guid session;
          lock (_desktopSessionGate) session = _activeDesktopSession;
          if (!_videoStreaming || session == Guid.Empty)
          {
            await Task.Delay(20, token);
            continue;
          }

          if (capture is null)
          {
            WindowsInputDesktop.AttachCurrentThread(desktopGeneration);
            capture = new DxgiDesktopCapture();
          }
          VideoProfile profile = _videoQuality.Current;
          var loopWatch = Stopwatch.StartNew();
          RemoteVideoFrame? frame = null;
          bool useUdp = udpReady;
          bool transportChanged = lastSendWasUdp.HasValue &&
                                  lastSendWasUdp.Value != useUdp;
          bool forceKeyFrame = transportChanged ||
                               _videoQuality.ConsumeKeyFrameRequest();
          Func<int, int, Action<IntPtr, int, int, int, System.Drawing.Rectangle>, bool>
            captureFrame = forceKeyFrame
            ? capture.TryCaptureForced
            : capture.TryCapture;
          bool captured = captureFrame(
            profile.MaximumWidth,
            profile.MaximumHeight,
            (pointer, stride, width, height, bounds) =>
            {
              profile = _videoQuality.ForFrameSize(width, height);
              if (encoder is null ||
                  encoderWidth != width ||
                  encoderHeight != height)
              {
                encoder?.Dispose();
                encoder = new H264Encoder(
                  H264NativeResolver.Resolve(typeof(H264Encoder).Assembly));
                int initialized = InitializeLowLatencyEncoder(
                  encoder,
                  width,
                  height,
                  profile.Bitrate,
                  profile.Fps);
                if (initialized != 0)
                  throw new InvalidOperationException(
                    $"OpenH264编码器初始化失败：{initialized}");
                yuvFrame?.Dispose();
                yuvFrame = new YuvImage(width, height);
                encoderWidth = width;
                encoderHeight = height;
                encoderBitrate = profile.Bitrate;
                encoderFps = profile.Fps;
                encoder.ForceIntraFrame();
              }
              else
              {
                if (encoderBitrate != profile.Bitrate)
                {
                  encoder.SetMaxBitrate(profile.Bitrate);
                  encoderBitrate = profile.Bitrate;
                }
                if (encoderFps != profile.Fps)
                {
                  encoder.SetTargetFps(profile.Fps);
                  encoderFps = profile.Fps;
                }
              }

              if (forceKeyFrame)
                encoder.ForceIntraFrame();
              if (yuvFrame is null)
                throw new InvalidOperationException("YUV转换缓冲区尚未初始化。");
              unsafe
              {
                byte* y = (byte*)yuvFrame.ImageBytes;
                byte* u = y + yuvFrame.strideY * height;
                byte* v = u + yuvFrame.strideUV * ((height + 1) / 2);
                int converted = LibYuv.ARGBToJ420(
                  (byte*)pointer,
                  stride,
                  y,
                  yuvFrame.strideY,
                  u,
                  yuvFrame.strideUV,
                  v,
                  yuvFrame.strideUV,
                  width,
                  height);
                if (converted != 0)
                  throw new InvalidOperationException(
                    $"libyuv 全范围颜色转换失败：{converted}");
              }

              var encodeWatch = Stopwatch.StartNew();
              if (!encoder.Encode(yuvFrame, out var encoded)) return;
              encodeWatch.Stop();
              byte[] bytes = encoded.GetAllBytes();
              if (bytes.Length == 0)
              {
                if (!encoder.Encode(yuvFrame, out encoded)) return;
                bytes = encoded.GetAllBytes();
                if (bytes.Length == 0) return;
              }
              bool keyFrame = encoded.Any(
                layer => layer.FrameType is FrameType.IDR or FrameType.I);
              long id = Interlocked.Increment(ref frameId);
              frame = new RemoteVideoFrame(
                VideoCodec.H264,
                bytes,
                width,
                height,
                bounds.Width,
                bounds.Height,
                bounds.Left,
                bounds.Top,
                id,
                keyFrame,
                DateTime.UtcNow.Ticks,
                session);
              _videoQuality.OnEncoded(
                id,
                encodeWatch.ElapsedMilliseconds,
                keyFrame,
                bytes.Length);
            });

          if (!captured || frame is null)
          {
            await Task.Delay(1, token);
            continue;
          }

          Guid latestSession;
          lock (_desktopSessionGate) latestSession = _activeDesktopSession;
          if (!_videoStreaming || latestSession != session) continue;
          var sendWatch = Stopwatch.StartNew();
          bool sent;
          if (useUdp)
          {
            // IDR/SPS/PPS are reference-chain recovery data.  The already
            // authenticated TCP video channel delivers them completely and in
            // order; low-latency predictive frames remain on UDP.
            if (frame.KeyFrame && tcpReady)
            {
              sent = await TrySendTcpVideoFrameAsync(frame, token);
              if (!sent)
              {
                await SendUdpVideoFrameAsync(
                  udp,
                  session,
                  frame,
                  profile.Bitrate,
                  token);
                sent = true;
              }
            }
            else
            {
              await SendUdpVideoFrameAsync(
                udp,
                session,
                frame,
                profile.Bitrate,
                token);
              sent = true;
            }
          }
          else
          {
            sent = await TrySendTcpVideoFrameAsync(frame, token);
          }
          sendWatch.Stop();
          if (!sent)
          {
            await Task.Delay(10, token);
            continue;
          }
          lastSendWasUdp = useUdp;
          _videoQuality.OnSent(
            frame.FrameId,
            frame.Data.Length,
            sendWatch.ElapsedMilliseconds);

          int delay = Math.Max(
            0,
            1000 / Math.Max(1, profile.Fps) -
            (int)loopWatch.ElapsedMilliseconds);
          if (delay > 0) await Task.Delay(delay, token);
        }

        connectionCts.Cancel();
        try { await Task.WhenAll(receiveTask, helloTask); } catch { }
      }
      catch (OperationCanceledException) { break; }
      catch (Exception ex)
      {
        await TrySendUdpVideoStatusAsync(
          udp,
          $"B端UDP视频采集/编码失败：{ex.GetType().Name}: {ex.Message}",
          token);
        encoder?.Dispose();
        encoder = null;
        yuvFrame?.Dispose();
        yuvFrame = null;
        encoderWidth = encoderHeight = 0;
        await Task.Delay(300, token).ContinueWith(_ => { });
      }
      finally
      {
        connectionCts.Cancel();
        try { udp?.Dispose(); } catch { }
        if (ReferenceEquals(_udpVideoClient, udp)) _udpVideoClient = null;
      }
    }

    encoder?.Dispose();
    yuvFrame?.Dispose();
    capture?.Dispose();
  }

  private static bool IsLocalNetworkHost(string host)
  {
    try
    {
      IPAddress[] addresses = IPAddress.TryParse(host, out IPAddress? parsed)
        ? [parsed]
        : Dns.GetHostAddresses(host);
      return addresses.Any(address =>
      {
        if (IPAddress.IsLoopback(address)) return true;
        byte[] bytes = address.MapToIPv4().GetAddressBytes();
        return bytes[0] == 10 ||
               bytes[0] == 127 ||
               bytes[0] == 192 && bytes[1] == 168 ||
               bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
               bytes[0] == 169 && bytes[1] == 254;
      });
    }
    catch { return false; }
  }

  private async Task SendUdpVideoFrameAsync(
    UdpClient udp,
    Guid session,
    RemoteVideoFrame frame,
    int targetBitrate,
    CancellationToken token)
  {
    long sequence = Interlocked.Increment(ref _udpDesktopSequence);
    var pacing = Stopwatch.StartNew();
    long transmittedBytes = 0;
    int fragmentsInBurst = 0;
    byte[][] fragments = UdpDesktopProtocol.VideoFragments(
      session,
      frame,
      sequence).ToArray();
    _udpVideoRetransmitCache.Store(session, frame.FrameId, fragments);
    foreach (byte[] fragment in fragments)
    {
      token.ThrowIfCancellationRequested();
      int sent = udp.Client.Send(fragment, SocketFlags.None);
      if (sent != fragment.Length)
        throw new SocketException((int)SocketError.MessageSize);
      transmittedBytes += sent;
      if (++fragmentsInBurst < 8) continue;
      fragmentsInBurst = 0;

      double expectedMilliseconds =
        transmittedBytes * 8_000.0 / Math.Max(1, targetBitrate);
      int wait = (int)Math.Floor(
        expectedMilliseconds - pacing.Elapsed.TotalMilliseconds);
      if (wait > 0)
        await Task.Delay(Math.Min(wait, 4), token);
      else
        await Task.Yield();
    }
  }

  private async Task UdpVideoReceiveLoopAsync(
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
        long pingSequence = Interlocked.Increment(ref _udpDesktopSequence);
        Interlocked.Exchange(ref _udpVideoPingSequence, pingSequence);
        Interlocked.Exchange(ref _udpVideoPingSentAt, Environment.TickCount64);
        await SendConnectedUdpAsync(
          udp,
          _udpVideoSendLock,
          UdpDesktopProtocol.Ping(
            UdpDesktopPeerRole.VideoProducer,
            Guid.Empty,
            pingSequence),
          token);
        continue;
      }
      if (packet.Kind == UdpDesktopPacketKind.Pong)
      {
        long now = Environment.TickCount64;
        Interlocked.Exchange(ref _udpVideoLastAckAt, now);
        if (packet.Sequence == Interlocked.Read(ref _udpVideoPingSequence))
        {
          long sentAt = Interlocked.Read(ref _udpVideoPingSentAt);
          if (sentAt > 0 && now >= sentAt)
            _videoQuality.OnNetworkSample(now - sentAt);
        }
        continue;
      }
      if (packet.Kind == UdpDesktopPacketKind.Ping)
      {
        await SendConnectedUdpAsync(
          udp,
          _udpVideoSendLock,
          UdpDesktopProtocol.Pong(
            UdpDesktopPeerRole.VideoProducer,
            packet.SessionId,
            packet.Sequence),
          token);
        continue;
      }
      if (packet.Kind == UdpDesktopPacketKind.SessionStart)
      {
        // The ordered TCP management channel is authoritative for starting
        // capture. UDP start prepares the data path but must not race ahead of
        // the A-side receiver becoming session-ready.
        continue;
      }
      if (packet.Kind == UdpDesktopPacketKind.SessionStop)
      {
        lock (_desktopSessionGate)
        {
          if (_activeDesktopSession == packet.SessionId)
          {
            _videoStreaming = false;
            _activeDesktopSession = Guid.Empty;
          }
        }
        _udpVideoRetransmitCache.Clear();
        continue;
      }
      if (packet.Kind == UdpDesktopPacketKind.VideoRetransmitRequest)
      {
        Guid retransmitSession;
        lock (_desktopSessionGate) retransmitSession = _activeDesktopSession;
        if (retransmitSession == Guid.Empty ||
            packet.SessionId != retransmitSession)
          continue;
        UdpVideoRetransmitRequest request =
          UdpDesktopProtocol.ReadVideoRetransmitRequest(packet);
        foreach (byte[] fragment in _udpVideoRetransmitCache.Get(
                   retransmitSession,
                   request.FrameId,
                   request.MissingFragments))
        {
          token.ThrowIfCancellationRequested();
          udp.Client.Send(fragment, SocketFlags.None);
        }
        continue;
      }
      if (packet.Kind != UdpDesktopPacketKind.VideoFeedback) continue;
      ControlPacket feedbackPacket = UdpDesktopProtocol.ReadControl(packet);
      if (feedbackPacket.Type != ControlPacketType.VideoFeedback) continue;
      VideoFeedbackPacket feedback =
        BinaryControlProtocol.ReadVideoFeedback(feedbackPacket);
      Guid active;
      lock (_desktopSessionGate) active = _activeDesktopSession;
      if (feedback.SessionId == active && packet.SessionId == active)
        _videoQuality.OnFeedback(feedback);
    }
  }

  private sealed class UdpVideoRetransmitCache
  {
    private readonly object _gate = new();
    private readonly Dictionary<(Guid SessionId, long FrameId), CachedFrame>
      _frames = [];

    public void Store(Guid sessionId, long frameId, byte[][] fragments)
    {
      lock (_gate)
      {
        Prune();
        _frames[(sessionId, frameId)] = new(
          fragments,
          Environment.TickCount64);
        while (_frames.Count > 12)
        {
          var oldest = _frames.MinBy(pair => pair.Value.CreatedAt).Key;
          _frames.Remove(oldest);
        }
      }
    }

    public IReadOnlyList<byte[]> Get(
      Guid sessionId,
      long frameId,
      IReadOnlyList<ushort> missingFragments)
    {
      lock (_gate)
      {
        Prune();
        if (!_frames.TryGetValue((sessionId, frameId), out CachedFrame? frame))
          return [];
        var result = new List<byte[]>(missingFragments.Count);
        foreach (ushort index in missingFragments)
        {
          if (index < frame.Fragments.Length)
            result.Add(frame.Fragments[index]);
        }
        return result;
      }
    }

    public void Clear()
    {
      lock (_gate) _frames.Clear();
    }

    private void Prune()
    {
      long cutoff = Environment.TickCount64 - 750;
      foreach (var key in _frames
                 .Where(pair => pair.Value.CreatedAt < cutoff)
                 .Select(pair => pair.Key)
                 .ToArray())
        _frames.Remove(key);
    }

    private sealed record CachedFrame(byte[][] Fragments, long CreatedAt);
  }

  private async Task UdpInputLoopAsync(
    string host,
    int port,
    CancellationToken token)
  {
    while (!token.IsCancellationRequested && (_stream is not null || _inputOnly))
    {
      UdpClient? udp = null;
      using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(token);
      try
      {
        udp = CreateConnectedUdpClient(host, port);
        _udpInputClient = udp;
        Interlocked.Exchange(ref _udpInputLastAckAt, 0);
        Task helloTask = Task.Run(
          () => UdpHelloLoopAsync(
            udp,
            _udpInputSendLock,
            UdpDesktopPeerRole.InputExecutor,
            connectionCts.Token),
          CancellationToken.None);

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
            await SendConnectedUdpAsync(
              udp,
              _udpInputSendLock,
              UdpDesktopProtocol.Ping(
                UdpDesktopPeerRole.InputExecutor,
                Guid.Empty,
                Interlocked.Increment(ref _udpDesktopSequence)),
              token);
            continue;
          }
          if (packet.Kind == UdpDesktopPacketKind.Pong)
          {
            Interlocked.Exchange(ref _udpInputLastAckAt, Environment.TickCount64);
            if (_inputOnly) QueueAgentStatus("已链接");
            continue;
          }
          if (packet.Kind == UdpDesktopPacketKind.Ping)
          {
            await SendConnectedUdpAsync(
              udp,
              _udpInputSendLock,
              UdpDesktopProtocol.Pong(
                UdpDesktopPeerRole.InputExecutor,
                packet.SessionId,
                packet.Sequence),
              token);
            continue;
          }
          if (packet.Kind == UdpDesktopPacketKind.SessionStart)
          {
            bool changed = _udpInputSession != packet.SessionId;
            _udpInputSession = packet.SessionId;
            _udpInputAllowed = packet.Flags.HasFlag(
              UdpDesktopFlags.AllowControl);
            if (changed)
            {
              _udpExpectedReliableInput = 1;
              _udpLastMouseSequence = 0;
              _udpReliableInputBuffer.Clear();
              _udpInputAckCache.Clear();
              try
              {
                await _inputDispatcher.ReleaseAllAsync(
                  Interlocked.Read(ref _desktopEnvironmentGeneration),
                  token);
              }
              catch { }
            }
            continue;
          }
          if (packet.Kind == UdpDesktopPacketKind.SessionStop)
          {
            if (_udpInputSession == packet.SessionId)
            {
              _udpInputAllowed = false;
              _udpInputSession = Guid.Empty;
              _udpReliableInputBuffer.Clear();
              _udpInputAckCache.Clear();
              try
              {
                await _inputDispatcher.ReleaseAllAsync(
                  Interlocked.Read(ref _desktopEnvironmentGeneration),
                  token);
              }
              catch { }
            }
            continue;
          }
          if (packet.Kind != UdpDesktopPacketKind.Input ||
              packet.SessionId != _udpInputSession ||
              !_udpInputAllowed)
            continue;
          await HandleUdpInputAsync(udp, packet, token);
        }

        connectionCts.Cancel();
        try { await helloTask; } catch { }
      }
      catch (OperationCanceledException) { break; }
      catch
      {
        if (_inputOnly) QueueAgentStatus("已断开");
        await Task.Delay(200, token).ContinueWith(_ => { });
      }
      finally
      {
        connectionCts.Cancel();
        try
        {
          await _inputDispatcher.ReleaseAllAsync(
            Interlocked.Read(ref _desktopEnvironmentGeneration),
            CancellationToken.None);
        }
        catch { }
        try { udp?.Dispose(); } catch { }
        if (ReferenceEquals(_udpInputClient, udp)) _udpInputClient = null;
        _udpInputSession = Guid.Empty;
        _udpInputAllowed = false;
        _udpReliableInputBuffer.Clear();
        _udpInputAckCache.Clear();
      }
    }
  }

  private async Task HandleUdpInputAsync(
    UdpClient udp,
    UdpDesktopDatagram packet,
    CancellationToken token)
  {
    ControlPacket control = UdpDesktopProtocol.ReadControl(packet);
    bool reliable = packet.Flags.HasFlag(UdpDesktopFlags.Reliable);
    if (!reliable)
    {
      if (control.Type != ControlPacketType.MouseMove ||
          packet.Sequence <= _udpLastMouseSequence)
        return;
      _udpLastMouseSequence = packet.Sequence;
      await ExecuteAndAcknowledgeUdpInputAsync(udp, packet, control, token);
      return;
    }

    if (packet.Sequence < _udpExpectedReliableInput)
    {
      if (!_udpInputAckCache.TryGetValue(packet.Sequence, out byte[]? cached))
        cached = UdpDesktopProtocol.InputAck(
          packet.SessionId,
          packet.Sequence,
          control.Type,
          true,
          0);
      await SendConnectedUdpAsync(
        udp,
        _udpInputSendLock,
        cached,
        token);
      return;
    }
    if (packet.Sequence > _udpExpectedReliableInput)
    {
      if (_udpReliableInputBuffer.Count < 256)
        _udpReliableInputBuffer.TryAdd(packet.Sequence, packet);
      return;
    }

    await ExecuteAndAcknowledgeUdpInputAsync(udp, packet, control, token);
    _udpExpectedReliableInput++;
    while (_udpReliableInputBuffer.Remove(
             _udpExpectedReliableInput,
             out UdpDesktopDatagram? next))
    {
      ControlPacket nextControl = UdpDesktopProtocol.ReadControl(next);
      await ExecuteAndAcknowledgeUdpInputAsync(
        udp,
        next,
        nextControl,
        token);
      _udpExpectedReliableInput++;
    }
  }

  private async Task ExecuteAndAcknowledgeUdpInputAsync(
    UdpClient udp,
    UdpDesktopDatagram packet,
    ControlPacket control,
    CancellationToken token)
  {
    bool success = true;
    int error = 0;
    try
    {
      await _inputDispatcher.ExecuteAsync(
        control,
        Interlocked.Read(ref _desktopEnvironmentGeneration),
        token);
    }
    catch (System.ComponentModel.Win32Exception ex)
    {
      success = false;
      error = ex.NativeErrorCode;
    }
    catch (Exception ex)
    {
      success = false;
      error = ex.HResult;
    }

    byte[] ack = UdpDesktopProtocol.InputAck(
      packet.SessionId,
      packet.Sequence,
      control.Type,
      success,
      error);
    if (packet.Flags.HasFlag(UdpDesktopFlags.Reliable))
    {
      _udpInputAckCache[packet.Sequence] = ack;
      if (_udpInputAckCache.Count > 512)
      {
        foreach (long old in _udpInputAckCache.Keys
                   .OrderBy(value => value)
                   .Take(_udpInputAckCache.Count - 512)
                   .ToArray())
          _udpInputAckCache.Remove(old);
      }
    }
    await SendConnectedUdpAsync(
      udp,
      _udpInputSendLock,
      ack,
      token);
  }

  private async Task UdpHelloLoopAsync(
    UdpClient udp,
    SemaphoreSlim sendLock,
    UdpDesktopPeerRole role,
    CancellationToken token)
  {
    while (!token.IsCancellationRequested)
    {
      await SendConnectedUdpAsync(
        udp,
        sendLock,
        UdpDesktopProtocol.Hello(
          _deviceId,
          _instanceId,
          role,
          Interlocked.Increment(ref _udpDesktopSequence)),
        token);
      await Task.Delay(1000, token);
    }
  }

  private async Task TrySendUdpVideoStatusAsync(
    UdpClient? udp,
    string message,
    CancellationToken token)
  {
    if (udp is null) return;
    Guid session;
    lock (_desktopSessionGate) session = _activeDesktopSession;
    if (session == Guid.Empty) return;
    try
    {
      await SendConnectedUdpAsync(
        udp,
        _udpVideoSendLock,
        UdpDesktopProtocol.VideoStatus(
          session,
          message,
          Interlocked.Increment(ref _udpDesktopSequence)),
        token);
    }
    catch { }
  }

  private static UdpClient CreateConnectedUdpClient(string host, int port)
  {
    var udp = new UdpClient(AddressFamily.InterNetwork);
    udp.Client.ReceiveBufferSize = 4 * 1024 * 1024;
    udp.Client.SendBufferSize = 16 * 1024 * 1024;
    udp.Connect(host, port);
    return udp;
  }

  private static async Task SendConnectedUdpAsync(
    UdpClient udp,
    SemaphoreSlim sendLock,
    byte[] datagram,
    CancellationToken token)
  {
    await sendLock.WaitAsync(token);
    try { await udp.SendAsync(datagram.AsMemory(), token); }
    finally { sendLock.Release(); }
  }

  private void CloseUdpDesktopClients()
  {
    ForceStopSystemAudio();
    try { _udpVideoClient?.Dispose(); } catch { }
    try { _udpInputClient?.Dispose(); } catch { }
    _udpVideoClient = null;
    _udpInputClient = null;
    _udpInputSession = Guid.Empty;
    _udpInputAllowed = false;
    _udpReliableInputBuffer.Clear();
    _udpInputAckCache.Clear();
  }
}
