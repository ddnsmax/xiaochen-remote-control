using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace RemoteControl.Shared;

public enum FileTransferPacketType
{
  ReadRangeRequest = 1,
  ReadRangeResponse = 2,
  WriteRangeRequest = 3,
  WriteRangeResponse = 4,
  Error = 5,
  Ping = 6,
  Pong = 7
}

public sealed record FileTransferPacket(
  FileTransferPacketType Type,
  string RequestId,
  string Path,
  string TransferId,
  long Offset,
  long TotalLength,
  int RequestedLength,
  bool Complete,
  byte[] Data,
  string Message);

public static class BinaryFileTransferProtocol
{
  private static readonly byte[] HelloMagic = Encoding.ASCII.GetBytes("ADCFILE1");
  private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
  public const int MaxChunkBytes = 2 * 1024 * 1024;
  private const int MaxMetadataBytes = 256 * 1024;

  public static async Task WriteHelloAsync(NetworkStream stream, string deviceId, CancellationToken token)
  {
    byte[] id = Encoding.UTF8.GetBytes(deviceId);
    if (id.Length is <= 0 or > 4096) throw new InvalidOperationException("Invalid file channel device id.");
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
      throw new InvalidOperationException("Invalid file channel handshake.");
    int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(HelloMagic.Length));
    if (length is <= 0 or > 4096) throw new InvalidOperationException("Invalid file channel device id.");
    byte[] id = new byte[length];
    if (!await ReadExactAsync(stream, id, token)) return null;
    return Encoding.UTF8.GetString(id);
  }

  public static async Task WriteAsync(NetworkStream stream, FileTransferPacket packet, CancellationToken token)
  {
    byte[] data = packet.Data ?? [];
    if (data.Length > MaxChunkBytes) throw new InvalidOperationException("File transfer chunk is too large.");
    var metadata = new FileTransferMetadata(
      packet.Type, packet.RequestId, packet.Path, packet.TransferId, packet.Offset,
      packet.TotalLength, packet.RequestedLength, packet.Complete, packet.Message);
    byte[] json = JsonSerializer.SerializeToUtf8Bytes(metadata, Json);
    if (json.Length <= 0 || json.Length > MaxMetadataBytes) throw new InvalidOperationException("Invalid file packet metadata.");
    byte[] header = new byte[8];
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), json.Length);
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), data.Length);
    await stream.WriteAsync(header, token);
    await stream.WriteAsync(json, token);
    if (data.Length > 0) await stream.WriteAsync(data, token);
    await stream.FlushAsync(token);
  }

  public static async Task<FileTransferPacket?> ReadAsync(NetworkStream stream, CancellationToken token)
  {
    byte[] header = new byte[8];
    if (!await ReadExactAsync(stream, header, token)) return null;
    int metadataLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
    int dataLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
    if (metadataLength is <= 0 or > MaxMetadataBytes || dataLength is < 0 or > MaxChunkBytes)
      throw new InvalidOperationException("Invalid file transfer packet.");
    byte[] metadataBytes = new byte[metadataLength];
    if (!await ReadExactAsync(stream, metadataBytes, token)) return null;
    var metadata = JsonSerializer.Deserialize<FileTransferMetadata>(metadataBytes, Json)
      ?? throw new InvalidOperationException("Invalid file transfer metadata.");
    byte[] data = dataLength == 0 ? [] : new byte[dataLength];
    if (dataLength > 0 && !await ReadExactAsync(stream, data, token)) return null;
    return new FileTransferPacket(
      metadata.Type, metadata.RequestId, metadata.Path ?? string.Empty,
      metadata.TransferId ?? string.Empty, metadata.Offset, metadata.TotalLength,
      metadata.RequestedLength, metadata.Complete, data, metadata.Message ?? string.Empty);
  }

  public static FileTransferPacket ReadRequest(string path, long offset, int length) =>
    new(FileTransferPacketType.ReadRangeRequest, Guid.NewGuid().ToString("N"), path, string.Empty,
      offset, 0, Math.Clamp(length, 1, MaxChunkBytes), false, [], string.Empty);

  public static FileTransferPacket WriteRequest(
    string path, string transferId, long offset, long totalLength, byte[] data, bool complete) =>
    new(FileTransferPacketType.WriteRangeRequest, Guid.NewGuid().ToString("N"), path, transferId,
      offset, totalLength, 0, complete, data, string.Empty);

  public static FileTransferPacket Error(FileTransferPacket request, string message) =>
    new(FileTransferPacketType.Error, request.RequestId, request.Path, request.TransferId,
      request.Offset, request.TotalLength, 0, false, [], message);

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

  private sealed record FileTransferMetadata(
    FileTransferPacketType Type,
    string RequestId,
    string Path,
    string TransferId,
    long Offset,
    long TotalLength,
    int RequestedLength,
    bool Complete,
    string Message);
}
