using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace RemoteControl.Shared;

public enum TerminalPacketType
{
  Start = 1,
  Started = 2,
  StandardOutput = 3,
  StandardError = 4,
  Completed = 5,
  Cancel = 6,
  Cancelled = 7,
  Failed = 8,
  Ping = 9,
  Pong = 10
}

public sealed record TerminalPacket(
  TerminalPacketType Type,
  string CommandId,
  string Shell,
  string Command,
  string WorkingDirectory,
  long Sequence,
  string Text,
  int ExitCode,
  long TimestampUtcTicks);

public static class BinaryTerminalProtocol
{
  private static readonly byte[] HelloMagic = Encoding.ASCII.GetBytes("ADCTERM1");
  private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
  private const int MaxPacketBytes = 2 * 1024 * 1024;

  public static async Task WriteHelloAsync(
    NetworkStream stream,
    string deviceId,
    CancellationToken token)
  {
    byte[] id = Encoding.UTF8.GetBytes(deviceId);
    if (id.Length is <= 0 or > 4096)
      throw new InvalidOperationException("Invalid terminal channel device id.");

    byte[] header = new byte[HelloMagic.Length + 4];
    HelloMagic.CopyTo(header, 0);
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(HelloMagic.Length), id.Length);
    await stream.WriteAsync(header, token);
    await stream.WriteAsync(id, token);
    await stream.FlushAsync(token);
  }

  public static async Task<string?> ReadHelloAsync(NetworkStream stream, CancellationToken token)
  {
    byte[] header = new byte[HelloMagic.Length + 4];
    if (!await ReadExactAsync(stream, header, token)) return null;
    if (!header.AsSpan(0, HelloMagic.Length).SequenceEqual(HelloMagic))
      throw new InvalidOperationException("Invalid terminal channel handshake.");

    int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(HelloMagic.Length));
    if (length is <= 0 or > 4096)
      throw new InvalidOperationException("Invalid terminal channel device id.");

    byte[] id = new byte[length];
    if (!await ReadExactAsync(stream, id, token)) return null;
    return Encoding.UTF8.GetString(id);
  }

  public static async Task WriteAsync(
    NetworkStream stream,
    TerminalPacket packet,
    CancellationToken token)
  {
    byte[] body = JsonSerializer.SerializeToUtf8Bytes(packet, Json);
    if (body.Length is <= 0 or > MaxPacketBytes)
      throw new InvalidOperationException("Invalid terminal packet.");

    byte[] length = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(length, body.Length);
    await stream.WriteAsync(length, token);
    await stream.WriteAsync(body, token);
    await stream.FlushAsync(token);
  }

  public static async Task<TerminalPacket?> ReadAsync(
    NetworkStream stream,
    CancellationToken token)
  {
    byte[] lengthBytes = new byte[4];
    if (!await ReadExactAsync(stream, lengthBytes, token)) return null;
    int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
    if (length is <= 0 or > MaxPacketBytes)
      throw new InvalidOperationException("Invalid terminal packet length.");

    byte[] body = new byte[length];
    if (!await ReadExactAsync(stream, body, token)) return null;
    return JsonSerializer.Deserialize<TerminalPacket>(body, Json)
      ?? throw new InvalidOperationException("Invalid terminal packet body.");
  }

  public static TerminalPacket Start(
    string commandId,
    string shell,
    string command,
    string workingDirectory) =>
    new(
      TerminalPacketType.Start,
      commandId,
      shell,
      command,
      workingDirectory,
      0,
      string.Empty,
      0,
      DateTime.UtcNow.Ticks);

  public static TerminalPacket Cancel(string commandId) =>
    new(
      TerminalPacketType.Cancel,
      commandId,
      string.Empty,
      string.Empty,
      string.Empty,
      0,
      string.Empty,
      0,
      DateTime.UtcNow.Ticks);

  private static async Task<bool> ReadExactAsync(
    NetworkStream stream,
    byte[] buffer,
    CancellationToken token)
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
