using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace RemoteControl.Shared;

public enum ControlPacketType : byte
{
  MouseMove = 1,
  MouseButton = 2,
  MouseWheel = 3,
  Key = 4,
  ReleaseAll = 5,
  ClipboardText = 6,
  VideoFeedback = 7,
  Ping = 8,
  Pong = 9,
  InputResult = 10,
  VideoStatus = 11
}

public sealed record ControlPacket(ControlPacketType Type, byte[] Payload);

public readonly record struct MouseMovePacket(int X, int Y);
public readonly record struct MouseButtonPacket(int X, int Y, byte Button, bool Down, byte ClickCount);
public readonly record struct MouseWheelPacket(int X, int Y, int Delta);
public readonly record struct KeyPacket(ushort VirtualKey, ushort ScanCode, bool Down, bool Extended);
public readonly record struct InputResultPacket(
  ControlPacketType SourceType,
  bool Success,
  int Win32Error);
public readonly record struct VideoFeedbackPacket(
  Guid SessionId,
  long LastReceivedFrameId,
  long LastRenderedFrameId,
  int DecodeMilliseconds,
  int RenderMilliseconds,
  int FramesReceived,
  int DecodeErrors,
  bool RequestKeyFrame);

public static class BinaryControlProtocol
{
  private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ADCINPUT1");
  private const int MaxPacketBytes = 16 * 1024 * 1024;

  public static async Task WriteHelloAsync(NetworkStream stream, string deviceId, CancellationToken token)
  {
    byte[] id = Encoding.UTF8.GetBytes(deviceId);
    if (id.Length is <= 0 or > 4096) throw new InvalidOperationException("Invalid input channel device id length.");
    byte[] header = new byte[Magic.Length + 4];
    Magic.CopyTo(header, 0);
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(Magic.Length), id.Length);
    await stream.WriteAsync(header, token);
    await stream.WriteAsync(id, token);
    await stream.FlushAsync(token);
  }

  public static async Task<string?> ReadHelloAsync(NetworkStream stream, CancellationToken token)
  {
    byte[] header = new byte[Magic.Length + 4];
    if (!await ReadExactAsync(stream, header, token)) return null;
    if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic)) throw new InvalidOperationException("Invalid input channel handshake.");
    int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(Magic.Length));
    if (length is <= 0 or > 4096) throw new InvalidOperationException("Invalid input channel device id length.");
    byte[] id = new byte[length];
    if (!await ReadExactAsync(stream, id, token)) return null;
    return Encoding.UTF8.GetString(id);
  }

  public static async Task WriteAsync(NetworkStream stream, ControlPacket packet, CancellationToken token)
  {
    int bodyLength = checked(packet.Payload.Length + 1);
    if (bodyLength < 1 || bodyLength > MaxPacketBytes) throw new InvalidOperationException("Invalid input packet length.");
    byte[] header = new byte[5];
    BinaryPrimitives.WriteInt32LittleEndian(header, bodyLength);
    header[4] = (byte)packet.Type;
    await stream.WriteAsync(header, token);
    if (packet.Payload.Length > 0) await stream.WriteAsync(packet.Payload, token);
    await stream.FlushAsync(token);
  }

  public static async Task<ControlPacket?> ReadAsync(NetworkStream stream, CancellationToken token)
  {
    byte[] lengthBytes = new byte[4];
    if (!await ReadExactAsync(stream, lengthBytes, token)) return null;
    int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
    if (length < 1 || length > MaxPacketBytes) throw new InvalidOperationException("Invalid input packet length.");
    byte[] body = new byte[length];
    if (!await ReadExactAsync(stream, body, token)) return null;
    return new ControlPacket((ControlPacketType)body[0], body.AsSpan(1).ToArray());
  }

  public static ControlPacket MouseMove(int x, int y)
  {
    byte[] data = new byte[8];
    BinaryPrimitives.WriteInt32LittleEndian(data, x);
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), y);
    return new(ControlPacketType.MouseMove, data);
  }

  public static MouseMovePacket ReadMouseMove(ControlPacket packet) =>
    new(BinaryPrimitives.ReadInt32LittleEndian(packet.Payload), BinaryPrimitives.ReadInt32LittleEndian(packet.Payload.AsSpan(4)));

  public static ControlPacket MouseButton(int x, int y, byte button, bool down, byte clickCount)
  {
    byte[] data = new byte[11];
    BinaryPrimitives.WriteInt32LittleEndian(data, x);
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), y);
    data[8] = button; data[9] = down ? (byte)1 : (byte)0; data[10] = clickCount;
    return new(ControlPacketType.MouseButton, data);
  }

  public static MouseButtonPacket ReadMouseButton(ControlPacket packet) =>
    new(BinaryPrimitives.ReadInt32LittleEndian(packet.Payload), BinaryPrimitives.ReadInt32LittleEndian(packet.Payload.AsSpan(4)), packet.Payload[8], packet.Payload[9] != 0, packet.Payload[10]);

  public static ControlPacket MouseWheel(int x, int y, int delta)
  {
    byte[] data = new byte[12];
    BinaryPrimitives.WriteInt32LittleEndian(data, x);
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), y);
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), delta);
    return new(ControlPacketType.MouseWheel, data);
  }

  public static MouseWheelPacket ReadMouseWheel(ControlPacket packet) =>
    new(BinaryPrimitives.ReadInt32LittleEndian(packet.Payload), BinaryPrimitives.ReadInt32LittleEndian(packet.Payload.AsSpan(4)), BinaryPrimitives.ReadInt32LittleEndian(packet.Payload.AsSpan(8)));

  public static ControlPacket Key(ushort virtualKey, ushort scanCode, bool down, bool extended)
  {
    byte[] data = new byte[6];
    BinaryPrimitives.WriteUInt16LittleEndian(data, virtualKey);
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), scanCode);
    data[4] = down ? (byte)1 : (byte)0; data[5] = extended ? (byte)1 : (byte)0;
    return new(ControlPacketType.Key, data);
  }

  public static KeyPacket ReadKey(ControlPacket packet) =>
    new(BinaryPrimitives.ReadUInt16LittleEndian(packet.Payload), BinaryPrimitives.ReadUInt16LittleEndian(packet.Payload.AsSpan(2)), packet.Payload[4] != 0, packet.Payload[5] != 0);

  public static ControlPacket ReleaseAll() => new(ControlPacketType.ReleaseAll, Array.Empty<byte>());
  public static ControlPacket ClipboardText(string text) => new(ControlPacketType.ClipboardText, Encoding.UTF8.GetBytes(text));
  public static string ReadClipboardText(ControlPacket packet) => Encoding.UTF8.GetString(packet.Payload);

  public static ControlPacket VideoFeedback(VideoFeedbackPacket feedback)
  {
    byte[] data = new byte[53];
    feedback.SessionId.TryWriteBytes(data);
    BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(16), feedback.LastReceivedFrameId);
    BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(24), feedback.LastRenderedFrameId);
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(32), feedback.DecodeMilliseconds);
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(36), feedback.RenderMilliseconds);
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(40), feedback.FramesReceived);
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(44), feedback.DecodeErrors);
    data[48] = feedback.RequestKeyFrame ? (byte)1 : (byte)0;
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(49), Environment.TickCount);
    return new(ControlPacketType.VideoFeedback, data);
  }

  public static VideoFeedbackPacket ReadVideoFeedback(ControlPacket packet) =>
    packet.Payload.Length < 53
      ? throw new InvalidOperationException("Invalid video feedback packet.")
      : new(
        new Guid(packet.Payload.AsSpan(0, 16)),
        BinaryPrimitives.ReadInt64LittleEndian(packet.Payload.AsSpan(16)),
        BinaryPrimitives.ReadInt64LittleEndian(packet.Payload.AsSpan(24)),
        BinaryPrimitives.ReadInt32LittleEndian(packet.Payload.AsSpan(32)),
        BinaryPrimitives.ReadInt32LittleEndian(packet.Payload.AsSpan(36)),
        BinaryPrimitives.ReadInt32LittleEndian(packet.Payload.AsSpan(40)),
        BinaryPrimitives.ReadInt32LittleEndian(packet.Payload.AsSpan(44)),
        packet.Payload[48] != 0);

  public static ControlPacket Ping(long value) => Int64Packet(ControlPacketType.Ping, value);
  public static ControlPacket Pong(long value) => Int64Packet(ControlPacketType.Pong, value);
  public static long ReadInt64(ControlPacket packet) => BinaryPrimitives.ReadInt64LittleEndian(packet.Payload);
  public static ControlPacket InputResult(
    ControlPacketType sourceType,
    bool success,
    int win32Error = 0)
  {
    byte[] data = new byte[6];
    data[0] = (byte)sourceType;
    data[1] = success ? (byte)1 : (byte)0;
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(2), win32Error);
    return new(ControlPacketType.InputResult, data);
  }

  public static InputResultPacket ReadInputResult(ControlPacket packet) =>
    packet.Payload.Length != 6
      ? throw new InvalidOperationException("Invalid input result packet.")
      : new(
        (ControlPacketType)packet.Payload[0],
        packet.Payload[1] != 0,
        BinaryPrimitives.ReadInt32LittleEndian(packet.Payload.AsSpan(2)));

  public static ControlPacket VideoStatus(string message) =>
    new(ControlPacketType.VideoStatus, Encoding.UTF8.GetBytes(message));

  public static string ReadVideoStatus(ControlPacket packet) =>
    packet.Type != ControlPacketType.VideoStatus
      ? throw new InvalidOperationException("Invalid video status packet.")
      : Encoding.UTF8.GetString(packet.Payload);

  private static ControlPacket Int64Packet(ControlPacketType type, long value)
  {
    byte[] data = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(data, value); return new(type, data);
  }

  private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken token)
  {
    int offset = 0;
    while (offset < buffer.Length)
    {
      int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), token);
      if (read == 0) return false;
      offset += read;
    }
    return true;
  }
}
