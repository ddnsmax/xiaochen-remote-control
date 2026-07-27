using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace RemoteControl.Shared;

public static class NetworkDefaults
{
  public const int Port = 27183;
  public const string DefaultControllerHost = "chenyong.eu.org";
}

public enum LogicalChannelType : int
{
  Management = 1,
  Video = 2,
  Input = 3,
  Clipboard = 4,
  File = 5,
  Terminal = 6,
  Registry = 7,
  Codex = 8
}

public sealed record LogicalChannelHello(
  LogicalChannelType Channel,
  string DeviceId,
  Guid InstanceId,
  long Generation,
  int ProtocolVersion);

public static class LogicalChannelProtocol
{
  private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ADCCHN01");
  private const int MaxDeviceIdBytes = 4096;

  public static async Task WriteHelloAsync(
    NetworkStream stream,
    LogicalChannelType channel,
    string deviceId,
    Guid instanceId,
    long generation,
    CancellationToken token)
  {
    byte[] device = Encoding.UTF8.GetBytes(deviceId);
    if (device.Length is <= 0 or > MaxDeviceIdBytes)
      throw new InvalidOperationException("Invalid logical channel device id.");

    byte[] header = new byte[44];
    Magic.CopyTo(header, 0);
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), ProtocolVersions.Current);
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), (int)channel);
    instanceId.TryWriteBytes(header.AsSpan(16, 16));
    BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(32), generation);
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), device.Length);
    await stream.WriteAsync(header, token);
    await stream.WriteAsync(device, token);
    await stream.FlushAsync(token);
  }

  public static async Task<LogicalChannelHello?> ReadHelloAsync(
    NetworkStream stream,
    CancellationToken token)
  {
    byte[] header = new byte[44];
    if (!await ReadExactAsync(stream, header, token)) return null;
    if (!header.AsSpan(0, 8).SequenceEqual(Magic))
      throw new InvalidOperationException("Invalid logical channel handshake.");

    int version = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
    var channel = (LogicalChannelType)BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
    if (!Enum.IsDefined(channel))
      throw new InvalidOperationException("Unknown logical channel.");
    Guid instanceId = new(header.AsSpan(16, 16));
    long generation = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(32));
    int deviceLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(40));
    if (deviceLength is <= 0 or > MaxDeviceIdBytes)
      throw new InvalidOperationException("Invalid logical channel device id.");
    byte[] device = new byte[deviceLength];
    if (!await ReadExactAsync(stream, device, token)) return null;
    return new LogicalChannelHello(
      channel,
      Encoding.UTF8.GetString(device),
      instanceId,
      generation,
      version);
  }

  private static async Task<bool> ReadExactAsync(
    NetworkStream stream,
    byte[] buffer,
    CancellationToken token)
  {
    int offset = 0;
    while (offset < buffer.Length)
    {
      int read = await stream.ReadAsync(buffer.AsMemory(offset), token);
      if (read == 0) return false;
      offset += read;
    }
    return true;
  }
}
