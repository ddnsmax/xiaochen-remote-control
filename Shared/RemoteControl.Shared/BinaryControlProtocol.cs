using System.Buffers.Binary;

namespace RemoteControl.Shared;

public enum ControlPacketType : byte
{
  MouseMove = 1,
  MouseButton = 2,
  MouseWheel = 3,
  Key = 4,
  ReleaseAll = 5
}

public sealed record ControlPacket(ControlPacketType Type, byte[] Payload);

public readonly record struct MouseMovePacket(int X, int Y);
public readonly record struct MouseButtonPacket(int X, int Y, byte Button, bool Down, byte ClickCount);
public readonly record struct MouseWheelPacket(int X, int Y, int Delta);
public readonly record struct KeyPacket(ushort VirtualKey, ushort ScanCode, bool Down, bool Extended);

public static class BinaryControlProtocol
{
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
}
