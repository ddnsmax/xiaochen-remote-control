using System.Buffers.Binary;
using System.Text;

namespace RemoteControl.Shared;

public enum UdpDesktopPacketKind : byte
{
  Hello = 1,
  HelloAck = 2,
  SessionStart = 3,
  SessionStop = 4,
  VideoFragment = 5,
  VideoFeedback = 6,
  Input = 7,
  InputAck = 8,
  Ping = 9,
  Pong = 10,
  VideoStatus = 11,
  VideoRetransmitRequest = 12,
  AudioFrame = 13
}

public enum UdpDesktopPeerRole : byte
{
  Controller = 1,
  VideoProducer = 2,
  InputExecutor = 3,
  AudioProducer = 4
}

[Flags]
public enum UdpDesktopFlags : ushort
{
  None = 0,
  KeyFrame = 1,
  AllowControl = 2,
  Reliable = 4
}

public sealed record UdpDesktopDatagram(
  UdpDesktopPacketKind Kind,
  UdpDesktopPeerRole Role,
  UdpDesktopFlags Flags,
  Guid SessionId,
  long Sequence,
  long FrameId,
  ushort FragmentIndex,
  ushort FragmentCount,
  int Width,
  int Height,
  int SourceWidth,
  int SourceHeight,
  int SourceX,
  int SourceY,
  long TimestampTicks,
  VideoCodec Codec,
  int TotalFrameLength,
  byte[] Payload);

public readonly record struct UdpInputAck(
  ControlPacketType SourceType,
  bool Success,
  int Win32Error);

public readonly record struct UdpPeerHello(
  string DeviceId,
  Guid InstanceId);

public readonly record struct UdpVideoRetransmitRequest(
  long FrameId,
  ushort[] MissingFragments);

public sealed record RemoteAudioFrame(
  Guid SessionId,
  long Sequence,
  long TimestampTicks,
  int SampleRate,
  int Channels,
  int FrameSamples,
  byte[] OpusData);

public static class UdpDesktopProtocol
{
  public const int HeaderBytes = 100;
  public const int MaxDatagramBytes = 1200;
  public const int MaxPayloadBytes = MaxDatagramBytes - HeaderBytes;
  public const int MaxFrameBytes = 32 * 1024 * 1024;
  private const int Version = 2;
  private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ADCUDP01");
  private static readonly uint[] Crc32Table = CreateCrc32Table();

  public static byte[] Hello(
    string deviceId,
    Guid instanceId,
    UdpDesktopPeerRole role,
    long sequence)
  {
    if (instanceId == Guid.Empty)
      throw new InvalidOperationException("Invalid UDP desktop instance id.");
    byte[] id = Encoding.UTF8.GetBytes(deviceId);
    if (id.Length is <= 0 or > 4096)
      throw new InvalidOperationException("Invalid UDP desktop device id.");
    byte[] payload = new byte[16 + id.Length];
    instanceId.TryWriteBytes(payload);
    id.CopyTo(payload, 16);
    return Serialize(New(UdpDesktopPacketKind.Hello, role, Guid.Empty, sequence, payload));
  }

  public static byte[] HelloAck(
    UdpDesktopPeerRole role,
    Guid instanceId,
    long sequence)
  {
    if (instanceId == Guid.Empty)
      throw new InvalidOperationException("Invalid UDP desktop instance id.");
    byte[] payload = new byte[17];
    payload[0] = (byte)role;
    instanceId.TryWriteBytes(payload.AsSpan(1));
    return Serialize(New(
      UdpDesktopPacketKind.HelloAck,
      UdpDesktopPeerRole.Controller,
      Guid.Empty,
      sequence,
      payload));
  }

  public static byte[] SessionStart(Guid sessionId, bool allowControl, long sequence) =>
    Serialize(New(
      UdpDesktopPacketKind.SessionStart,
      UdpDesktopPeerRole.Controller,
      sessionId,
      sequence,
      [],
      allowControl ? UdpDesktopFlags.AllowControl : UdpDesktopFlags.None));

  public static byte[] SessionStop(Guid sessionId, long sequence) =>
    Serialize(New(
      UdpDesktopPacketKind.SessionStop,
      UdpDesktopPeerRole.Controller,
      sessionId,
      sequence,
      []));

  public static byte[] VideoFeedback(Guid sessionId, ControlPacket packet, long sequence) =>
    Serialize(New(
      UdpDesktopPacketKind.VideoFeedback,
      UdpDesktopPeerRole.Controller,
      sessionId,
      sequence,
      EncodeControl(packet)));

  public static byte[] Input(
    Guid sessionId,
    ControlPacket packet,
    long sequence,
    bool reliable) =>
    Serialize(New(
      UdpDesktopPacketKind.Input,
      UdpDesktopPeerRole.Controller,
      sessionId,
      sequence,
      EncodeControl(packet),
      reliable ? UdpDesktopFlags.Reliable : UdpDesktopFlags.None));

  public static byte[] InputAck(
    Guid sessionId,
    long sequence,
    ControlPacketType sourceType,
    bool success,
    int win32Error) =>
    Serialize(New(
      UdpDesktopPacketKind.InputAck,
      UdpDesktopPeerRole.InputExecutor,
      sessionId,
      sequence,
      EncodeInputAck(new(sourceType, success, win32Error))));

  public static byte[] Ping(
    UdpDesktopPeerRole role,
    Guid sessionId,
    long sequence) =>
    Serialize(New(UdpDesktopPacketKind.Ping, role, sessionId, sequence, []));

  public static byte[] Pong(
    UdpDesktopPeerRole role,
    Guid sessionId,
    long sequence) =>
    Serialize(New(UdpDesktopPacketKind.Pong, role, sessionId, sequence, []));

  public static byte[] VideoStatus(Guid sessionId, string message, long sequence) =>
    Serialize(New(
      UdpDesktopPacketKind.VideoStatus,
      UdpDesktopPeerRole.VideoProducer,
      sessionId,
      sequence,
      Encoding.UTF8.GetBytes(message)));

  public static byte[] AudioFrame(
    Guid sessionId,
    long sequence,
    long timestampTicks,
    int sampleRate,
    int channels,
    int frameSamples,
    ReadOnlySpan<byte> opusData)
  {
    if (sessionId == Guid.Empty ||
        sampleRate <= 0 ||
        channels is < 1 or > 2 ||
        frameSamples <= 0 ||
        opusData.Length is <= 0 or > MaxPayloadBytes - 12)
      throw new InvalidOperationException("Invalid UDP audio frame.");
    byte[] payload = new byte[12 + opusData.Length];
    BinaryPrimitives.WriteInt32LittleEndian(payload, sampleRate);
    BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), channels);
    BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8), frameSamples);
    opusData.CopyTo(payload.AsSpan(12));
    UdpDesktopDatagram packet = New(
      UdpDesktopPacketKind.AudioFrame,
      UdpDesktopPeerRole.AudioProducer,
      sessionId,
      sequence,
      payload);
    return Serialize(packet with { TimestampTicks = timestampTicks });
  }

  public static byte[] VideoRetransmitRequest(
    Guid sessionId,
    long frameId,
    IReadOnlyList<ushort> missingFragments,
    long sequence)
  {
    if (sessionId == Guid.Empty || frameId <= 0)
      throw new InvalidOperationException("Invalid video retransmission request.");
    int count = Math.Min(
      missingFragments.Count,
      (MaxPayloadBytes - sizeof(long) - sizeof(ushort)) / sizeof(ushort));
    if (count <= 0)
      throw new InvalidOperationException("A retransmission request requires fragments.");
    byte[] payload = new byte[
      sizeof(long) + sizeof(ushort) + count * sizeof(ushort)];
    BinaryPrimitives.WriteInt64LittleEndian(payload, frameId);
    BinaryPrimitives.WriteUInt16LittleEndian(
      payload.AsSpan(sizeof(long)),
      checked((ushort)count));
    for (int index = 0; index < count; index++)
      BinaryPrimitives.WriteUInt16LittleEndian(
        payload.AsSpan(sizeof(long) + sizeof(ushort) + index * sizeof(ushort)),
        missingFragments[index]);
    return Serialize(New(
      UdpDesktopPacketKind.VideoRetransmitRequest,
      UdpDesktopPeerRole.Controller,
      sessionId,
      sequence,
      payload));
  }

  public static IEnumerable<byte[]> VideoFragments(
    Guid sessionId,
    RemoteVideoFrame frame,
    long sequence)
  {
    if (sessionId == Guid.Empty)
      throw new InvalidOperationException("A video frame requires a desktop session.");
    if (frame.Data.Length is <= 0 or > MaxFrameBytes)
      throw new InvalidOperationException("Invalid UDP video frame length.");
    int count = checked((frame.Data.Length + MaxPayloadBytes - 1) / MaxPayloadBytes);
    if (count > ushort.MaxValue)
      throw new InvalidOperationException("UDP video frame has too many fragments.");

    for (int index = 0, offset = 0; index < count; index++)
    {
      int length = Math.Min(MaxPayloadBytes, frame.Data.Length - offset);
      ReadOnlySpan<byte> payload = frame.Data.AsSpan(offset, length);
      byte[] data = new byte[HeaderBytes + length];
      Magic.CopyTo(data, 0);
      BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), Version);
      data[12] = (byte)UdpDesktopPacketKind.VideoFragment;
      data[13] = (byte)UdpDesktopPeerRole.VideoProducer;
      BinaryPrimitives.WriteUInt16LittleEndian(
        data.AsSpan(14),
        (ushort)(frame.KeyFrame ? UdpDesktopFlags.KeyFrame : UdpDesktopFlags.None));
      sessionId.TryWriteBytes(data.AsSpan(16, 16));
      BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(32), sequence);
      BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(40), frame.FrameId);
      BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(48), checked((ushort)index));
      BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(50), checked((ushort)count));
      BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(52), length);
      BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(56), frame.Width);
      BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(60), frame.Height);
      BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(64), frame.SourceWidth);
      BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(68), frame.SourceHeight);
      BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(72), frame.SourceX);
      BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(76), frame.SourceY);
      BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(80), frame.TimestampTicks);
      BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(88), (int)frame.Codec);
      BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(92), frame.Data.Length);
      BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(96), Crc32(payload));
      payload.CopyTo(data.AsSpan(HeaderBytes));
      yield return data;
      offset += length;
    }
  }

  public static UdpPeerHello ReadHello(UdpDesktopDatagram packet)
  {
    if (packet.Kind != UdpDesktopPacketKind.Hello)
      throw new InvalidOperationException("Not a UDP desktop hello packet.");
    if (packet.Payload.Length <= 16)
      throw new InvalidOperationException("Invalid UDP desktop hello payload.");
    Guid instanceId = new(packet.Payload.AsSpan(0, 16));
    string deviceId = Encoding.UTF8.GetString(packet.Payload.AsSpan(16));
    if (instanceId == Guid.Empty || string.IsNullOrWhiteSpace(deviceId))
      throw new InvalidOperationException("Invalid UDP desktop hello payload.");
    return new(deviceId, instanceId);
  }

  public static Guid ReadHelloAckInstanceId(UdpDesktopDatagram packet)
  {
    if (packet.Kind != UdpDesktopPacketKind.HelloAck ||
        packet.Payload.Length != 17)
      throw new InvalidOperationException("Invalid UDP desktop hello acknowledgement.");
    return new Guid(packet.Payload.AsSpan(1, 16));
  }

  public static string ReadVideoStatus(UdpDesktopDatagram packet) =>
    packet.Kind != UdpDesktopPacketKind.VideoStatus
      ? throw new InvalidOperationException("Not a UDP video status packet.")
      : Encoding.UTF8.GetString(packet.Payload);

  public static RemoteAudioFrame ReadAudioFrame(UdpDesktopDatagram packet)
  {
    if (packet.Kind != UdpDesktopPacketKind.AudioFrame ||
        packet.Role != UdpDesktopPeerRole.AudioProducer ||
        packet.SessionId == Guid.Empty ||
        packet.Payload.Length <= 12)
      throw new InvalidOperationException("Invalid UDP audio frame packet.");
    int sampleRate = BinaryPrimitives.ReadInt32LittleEndian(packet.Payload);
    int channels = BinaryPrimitives.ReadInt32LittleEndian(packet.Payload.AsSpan(4));
    int frameSamples = BinaryPrimitives.ReadInt32LittleEndian(packet.Payload.AsSpan(8));
    if (sampleRate <= 0 || channels is < 1 or > 2 || frameSamples <= 0)
      throw new InvalidOperationException("Invalid UDP audio format.");
    return new(
      packet.SessionId,
      packet.Sequence,
      packet.TimestampTicks,
      sampleRate,
      channels,
      frameSamples,
      packet.Payload.AsSpan(12).ToArray());
  }

  public static ControlPacket ReadControl(UdpDesktopDatagram packet) =>
    DecodeControl(packet.Payload);

  public static UdpInputAck ReadInputAck(UdpDesktopDatagram packet)
  {
    if (packet.Kind != UdpDesktopPacketKind.InputAck || packet.Payload.Length != 6)
      throw new InvalidOperationException("Invalid UDP input acknowledgement.");
    return new(
      (ControlPacketType)packet.Payload[0],
      packet.Payload[1] != 0,
      BinaryPrimitives.ReadInt32LittleEndian(packet.Payload.AsSpan(2)));
  }

  public static UdpVideoRetransmitRequest ReadVideoRetransmitRequest(
    UdpDesktopDatagram packet)
  {
    if (packet.Kind != UdpDesktopPacketKind.VideoRetransmitRequest ||
        packet.Payload.Length < sizeof(long) + sizeof(ushort))
      throw new InvalidOperationException(
        "Invalid video retransmission request packet.");
    long frameId = BinaryPrimitives.ReadInt64LittleEndian(packet.Payload);
    int count = BinaryPrimitives.ReadUInt16LittleEndian(
      packet.Payload.AsSpan(sizeof(long)));
    int expected = sizeof(long) + sizeof(ushort) + count * sizeof(ushort);
    if (frameId <= 0 || count <= 0 || packet.Payload.Length != expected)
      throw new InvalidOperationException(
        "Invalid video retransmission request payload.");
    var missing = new ushort[count];
    for (int index = 0; index < count; index++)
      missing[index] = BinaryPrimitives.ReadUInt16LittleEndian(
        packet.Payload.AsSpan(
          sizeof(long) + sizeof(ushort) + index * sizeof(ushort)));
    return new(frameId, missing);
  }

  public static byte[] Serialize(UdpDesktopDatagram packet)
  {
    if (packet.Payload.Length > MaxPayloadBytes)
      throw new InvalidOperationException("UDP desktop payload is too large.");
    byte[] data = new byte[HeaderBytes + packet.Payload.Length];
    Magic.CopyTo(data, 0);
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), Version);
    data[12] = (byte)packet.Kind;
    data[13] = (byte)packet.Role;
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), (ushort)packet.Flags);
    packet.SessionId.TryWriteBytes(data.AsSpan(16, 16));
    BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(32), packet.Sequence);
    BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(40), packet.FrameId);
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(48), packet.FragmentIndex);
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(50), packet.FragmentCount);
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(52), packet.Payload.Length);
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(56), packet.Width);
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(60), packet.Height);
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(64), packet.SourceWidth);
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(68), packet.SourceHeight);
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(72), packet.SourceX);
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(76), packet.SourceY);
    BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(80), packet.TimestampTicks);
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(88), (int)packet.Codec);
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(92), packet.TotalFrameLength);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(96), Crc32(packet.Payload));
    packet.Payload.CopyTo(data, HeaderBytes);
    return data;
  }

  public static bool TryParse(
    ReadOnlySpan<byte> data,
    out UdpDesktopDatagram? packet)
  {
    packet = null;
    if (data.Length < HeaderBytes || data.Length > MaxDatagramBytes ||
        !data[..8].SequenceEqual(Magic) ||
        BinaryPrimitives.ReadInt32LittleEndian(data[8..]) != Version)
      return false;
    int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(data[52..]);
    if (payloadLength < 0 || payloadLength > MaxPayloadBytes ||
        data.Length != HeaderBytes + payloadLength)
      return false;
    byte[] payload = data.Slice(HeaderBytes, payloadLength).ToArray();
    if (BinaryPrimitives.ReadUInt32LittleEndian(data[96..]) != Crc32(payload))
      return false;

    var kind = (UdpDesktopPacketKind)data[12];
    var role = (UdpDesktopPeerRole)data[13];
    if (!Enum.IsDefined(kind) || !Enum.IsDefined(role)) return false;
    packet = new UdpDesktopDatagram(
      kind,
      role,
      (UdpDesktopFlags)BinaryPrimitives.ReadUInt16LittleEndian(data[14..]),
      new Guid(data.Slice(16, 16)),
      BinaryPrimitives.ReadInt64LittleEndian(data[32..]),
      BinaryPrimitives.ReadInt64LittleEndian(data[40..]),
      BinaryPrimitives.ReadUInt16LittleEndian(data[48..]),
      BinaryPrimitives.ReadUInt16LittleEndian(data[50..]),
      BinaryPrimitives.ReadInt32LittleEndian(data[56..]),
      BinaryPrimitives.ReadInt32LittleEndian(data[60..]),
      BinaryPrimitives.ReadInt32LittleEndian(data[64..]),
      BinaryPrimitives.ReadInt32LittleEndian(data[68..]),
      BinaryPrimitives.ReadInt32LittleEndian(data[72..]),
      BinaryPrimitives.ReadInt32LittleEndian(data[76..]),
      BinaryPrimitives.ReadInt64LittleEndian(data[80..]),
      (VideoCodec)BinaryPrimitives.ReadInt32LittleEndian(data[88..]),
      BinaryPrimitives.ReadInt32LittleEndian(data[92..]),
      payload);
    return true;
  }

  private static UdpDesktopDatagram New(
    UdpDesktopPacketKind kind,
    UdpDesktopPeerRole role,
    Guid sessionId,
    long sequence,
    byte[] payload,
    UdpDesktopFlags flags = UdpDesktopFlags.None) =>
    new(
      kind,
      role,
      flags,
      sessionId,
      sequence,
      0,
      0,
      0,
      0,
      0,
      0,
      0,
      0,
      0,
      DateTime.UtcNow.Ticks,
      VideoCodec.H264,
      0,
      payload);

  private static byte[] EncodeControl(ControlPacket packet)
  {
    if (packet.Payload.Length >= MaxPayloadBytes)
      throw new InvalidOperationException("UDP control packet is too large.");
    byte[] data = new byte[packet.Payload.Length + 1];
    data[0] = (byte)packet.Type;
    packet.Payload.CopyTo(data, 1);
    return data;
  }

  private static ControlPacket DecodeControl(byte[] data)
  {
    if (data.Length == 0)
      throw new InvalidOperationException("Empty UDP control packet.");
    var type = (ControlPacketType)data[0];
    if (!Enum.IsDefined(type))
      throw new InvalidOperationException("Unknown UDP control packet.");
    return new(type, data.AsSpan(1).ToArray());
  }

  private static byte[] EncodeInputAck(UdpInputAck ack)
  {
    byte[] data = new byte[6];
    data[0] = (byte)ack.SourceType;
    data[1] = ack.Success ? (byte)1 : (byte)0;
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(2), ack.Win32Error);
    return data;
  }

  private static uint Crc32(ReadOnlySpan<byte> data)
  {
    uint crc = uint.MaxValue;
    foreach (byte value in data)
      crc = (crc >> 8) ^ Crc32Table[(int)((crc ^ value) & 0xFF)];
    return ~crc;
  }

  private static uint[] CreateCrc32Table()
  {
    var table = new uint[256];
    for (uint index = 0; index < table.Length; index++)
    {
      uint value = index;
      for (int bit = 0; bit < 8; bit++)
        value = (value >> 1) ^ (0xEDB88320u & unchecked((uint)-(int)(value & 1)));
      table[index] = value;
    }
    return table;
  }
}

public sealed class UdpVideoFrameAssembler
{
  private readonly object _gate = new();
  private readonly Dictionary<(Guid SessionId, long FrameId), FrameState> _frames = [];
  private readonly TimeSpan _maximumAge;
  private readonly int _maximumPendingFrames;
  private readonly Dictionary<Guid, SortedSet<long>> _completedFrames = [];

  public UdpVideoFrameAssembler(
    TimeSpan? maximumAge = null,
    int maximumPendingFrames = 4)
  {
    _maximumAge = maximumAge ?? TimeSpan.FromMilliseconds(250);
    _maximumPendingFrames = Math.Max(2, maximumPendingFrames);
  }

  public bool TryAdd(
    UdpDesktopDatagram packet,
    out Guid sessionId,
    out RemoteVideoFrame? frame)
  {
    sessionId = packet.SessionId;
    frame = null;
    if (packet.Kind != UdpDesktopPacketKind.VideoFragment ||
        packet.SessionId == Guid.Empty ||
        packet.FrameId <= 0 ||
        packet.FragmentCount == 0 ||
        packet.FragmentIndex >= packet.FragmentCount ||
        packet.TotalFrameLength is <= 0 or > UdpDesktopProtocol.MaxFrameBytes ||
        packet.Width <= 0 || packet.Height <= 0 ||
        packet.SourceWidth <= 0 || packet.SourceHeight <= 0)
      return false;

    lock (_gate)
    {
      PruneExpired();
      if (_completedFrames.TryGetValue(packet.SessionId, out SortedSet<long>? completed) &&
          completed.Contains(packet.FrameId))
        return false;
      var key = (packet.SessionId, packet.FrameId);
      if (!_frames.TryGetValue(key, out FrameState? state))
      {
        if (_frames.Count >= _maximumPendingFrames)
        {
          var oldest = _frames.MinBy(pair => pair.Value.CreatedUtc).Key;
          _frames.Remove(oldest);
        }
        state = new FrameState(packet);
        _frames[key] = state;
      }
      if (!state.Matches(packet))
      {
        _frames.Remove(key);
        return false;
      }
      state.Add(packet.FragmentIndex, packet.Payload);
      if (!state.Complete) return false;
      _frames.Remove(key);
      byte[] data = state.Combine();
      if (data.Length != state.TotalFrameLength) return false;
      RememberCompleted(packet.SessionId, packet.FrameId);
      frame = new RemoteVideoFrame(
        state.Codec,
        data,
        state.Width,
        state.Height,
        state.SourceWidth,
        state.SourceHeight,
        state.SourceX,
        state.SourceY,
        state.FrameId,
        state.KeyFrame,
        state.TimestampTicks,
        packet.SessionId);
      return true;
    }
  }

  public void Reset()
  {
    lock (_gate)
    {
      _frames.Clear();
      _completedFrames.Clear();
    }
  }

  public IReadOnlyList<UdpVideoRetransmitRequest> CollectRetransmissionRequests(
    DateTime utcNow,
    int maximumRequests = 2)
  {
    lock (_gate)
    {
      PruneExpired();
      var requests = new List<UdpVideoRetransmitRequest>();
      foreach (FrameState state in _frames.Values
                 .OrderBy(value => value.CreatedUtc))
      {
        if (requests.Count >= Math.Max(1, maximumRequests)) break;
        if (!state.ShouldRequestRetransmission(utcNow)) continue;
        ushort[] missing = state.MissingFragments(
          (UdpDesktopProtocol.MaxPayloadBytes - sizeof(long) - sizeof(ushort)) /
          sizeof(ushort));
        if (missing.Length == 0) continue;
        state.MarkRetransmissionRequested(utcNow);
        requests.Add(new(state.FrameId, missing));
      }
      return requests;
    }
  }

  private void RememberCompleted(Guid sessionId, long frameId)
  {
    if (!_completedFrames.TryGetValue(sessionId, out SortedSet<long>? completed))
    {
      completed = [];
      _completedFrames[sessionId] = completed;
    }
    completed.Add(frameId);

    // UDP frames can finish out of order.  Keep a bounded duplicate window
    // instead of a high-water mark; otherwise completing frame N+1 first
    // permanently discards a valid frame N and breaks the H.264 reference
    // chain even though all of its fragments arrived.
    long cutoff = frameId - 256;
    while (completed.Count > 0 && completed.Min < cutoff)
      completed.Remove(completed.Min);
  }

  private void PruneExpired()
  {
    DateTime cutoff = DateTime.UtcNow - _maximumAge;
    foreach (var key in _frames
               .Where(pair => pair.Value.CreatedUtc < cutoff)
               .Select(pair => pair.Key)
               .ToArray())
      _frames.Remove(key);
  }

  private sealed class FrameState
  {
    private readonly byte[] _data;
    private readonly bool[] _parts;
    private int _received;
    private DateTime _lastRetransmissionRequestUtc;
    private int _retransmissionRequests;

    public FrameState(UdpDesktopDatagram packet)
    {
      SessionId = packet.SessionId;
      FrameId = packet.FrameId;
      Width = packet.Width;
      Height = packet.Height;
      SourceWidth = packet.SourceWidth;
      SourceHeight = packet.SourceHeight;
      SourceX = packet.SourceX;
      SourceY = packet.SourceY;
      TimestampTicks = packet.TimestampTicks;
      Codec = packet.Codec;
      TotalFrameLength = packet.TotalFrameLength;
      KeyFrame = packet.Flags.HasFlag(UdpDesktopFlags.KeyFrame);
      _data = new byte[packet.TotalFrameLength];
      _parts = new bool[packet.FragmentCount];
    }

    public Guid SessionId { get; }
    public long FrameId { get; }
    public int Width { get; }
    public int Height { get; }
    public int SourceWidth { get; }
    public int SourceHeight { get; }
    public int SourceX { get; }
    public int SourceY { get; }
    public long TimestampTicks { get; }
    public VideoCodec Codec { get; }
    public int TotalFrameLength { get; }
    public bool KeyFrame { get; }
    public DateTime CreatedUtc { get; } = DateTime.UtcNow;
    public bool Complete => _received == _parts.Length;

    public bool Matches(UdpDesktopDatagram packet) =>
      SessionId == packet.SessionId &&
      FrameId == packet.FrameId &&
      Width == packet.Width &&
      Height == packet.Height &&
      SourceWidth == packet.SourceWidth &&
      SourceHeight == packet.SourceHeight &&
      SourceX == packet.SourceX &&
      SourceY == packet.SourceY &&
      TimestampTicks == packet.TimestampTicks &&
      Codec == packet.Codec &&
      TotalFrameLength == packet.TotalFrameLength &&
      KeyFrame == packet.Flags.HasFlag(UdpDesktopFlags.KeyFrame) &&
      _parts.Length == packet.FragmentCount;

    public void Add(ushort index, byte[] payload)
    {
      if (_parts[index]) return;
      int offset = index * UdpDesktopProtocol.MaxPayloadBytes;
      int expected = Math.Min(
        UdpDesktopProtocol.MaxPayloadBytes,
        TotalFrameLength - offset);
      if (offset < 0 || expected <= 0 || payload.Length != expected) return;
      payload.CopyTo(_data, offset);
      _parts[index] = true;
      _received++;
    }

    public byte[] Combine() => Complete ? _data : [];

    public bool ShouldRequestRetransmission(DateTime utcNow) =>
      !Complete &&
      _received > 0 &&
      _retransmissionRequests < 5 &&
      utcNow - CreatedUtc >= TimeSpan.FromMilliseconds(10) &&
      (_lastRetransmissionRequestUtc == default ||
       utcNow - _lastRetransmissionRequestUtc >=
       TimeSpan.FromMilliseconds(18 + _retransmissionRequests * 8));

    public ushort[] MissingFragments(int maximumCount)
    {
      var missing = new List<ushort>(Math.Min(maximumCount, _parts.Length));
      for (int index = 0;
           index < _parts.Length && missing.Count < maximumCount;
           index++)
      {
        if (!_parts[index]) missing.Add(checked((ushort)index));
      }
      return [.. missing];
    }

    public void MarkRetransmissionRequested(DateTime utcNow)
    {
      _lastRetransmissionRequestUtc = utcNow;
      _retransmissionRequests++;
    }
  }
}
