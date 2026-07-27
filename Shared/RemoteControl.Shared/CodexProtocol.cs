using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;

namespace RemoteControl.Shared;

public enum CodexPacketType
{
  Request = 1,
  Output = 2,
  Result = 3,
  Completed = 4,
  Error = 5,
  Cancel = 6
}

public sealed record CodexPacket(
  CodexPacketType Type,
  string RequestId,
  string Operation,
  string WorkingDirectory,
  string Path,
  string DestinationPath,
  string Shell,
  string Command,
  string Text,
  byte[] Data,
  long Sequence,
  int ExitCode,
  bool Success,
  string Message,
  int TimeoutSeconds = 300);

public static class BinaryCodexProtocol
{
  private static readonly JsonSerializerOptions Json =
    new(JsonSerializerDefaults.Web);
  private const int MaxPacketBytes = 96 * 1024 * 1024;

  public static async Task WriteAsync(
    NetworkStream stream,
    CodexPacket packet,
    CancellationToken token)
  {
    byte[] body = JsonSerializer.SerializeToUtf8Bytes(packet, Json);
    if (body.Length is <= 0 or > MaxPacketBytes)
      throw new InvalidOperationException("Invalid Codex packet.");
    byte[] length = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(length, body.Length);
    await stream.WriteAsync(length, token);
    await stream.WriteAsync(body, token);
    await stream.FlushAsync(token);
  }

  public static async Task<CodexPacket?> ReadAsync(
    NetworkStream stream,
    CancellationToken token)
  {
    byte[] length = new byte[4];
    if (!await ReadExactAsync(stream, length, token)) return null;
    int size = BinaryPrimitives.ReadInt32LittleEndian(length);
    if (size is <= 0 or > MaxPacketBytes)
      throw new InvalidOperationException("Invalid Codex packet length.");
    byte[] body = new byte[size];
    if (!await ReadExactAsync(stream, body, token)) return null;
    return JsonSerializer.Deserialize<CodexPacket>(body, Json)
      ?? throw new InvalidOperationException("Invalid Codex packet.");
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

  public static CodexPacket Request(
    string operation,
    string requestId = "",
    string workingDirectory = "",
    string path = "",
    string destinationPath = "",
    string shell = "",
    string command = "",
    string text = "",
    byte[]? data = null,
    int timeoutSeconds = 300) =>
    new(
      CodexPacketType.Request,
      requestId.Length == 0 ? Guid.NewGuid().ToString("N") : requestId,
      operation,
      workingDirectory,
      path,
      destinationPath,
      shell,
      command,
      text,
      data ?? [],
      0,
      0,
      true,
      "",
      timeoutSeconds);
}
