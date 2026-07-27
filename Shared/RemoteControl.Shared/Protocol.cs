using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace RemoteControl.Shared;

public enum MessageType
{
  Hello = 0,
  Heartbeat = 1,
  SystemInfoRequest = 2,
  SystemInfoResponse = 3,
  ScreenStreamStart = 4,
  ScreenStreamStop = 5,
  CommandRequest = 6,
  CommandResponse = 7,
  DrivesRequest = 8,
  DrivesResponse = 9,
  DirectoryRequest = 10,
  DirectoryResponse = 11,
  DeleteRequest = 12,
  DeleteResponse = 13,
  CreateDirectoryRequest = 14,
  CreateDirectoryResponse = 15,
  RenameRequest = 16,
  RenameResponse = 17,
  FilePropertiesRequest = 18,
  FilePropertiesResponse = 19,
  ThumbnailRequest = 20,
  ThumbnailResponse = 21,
  ProcessListRequest = 22,
  ProcessListResponse = 23,
  ProcessKillRequest = 24,
  ProcessKillResponse = 25,
  ServiceListRequest = 26,
  ServiceListResponse = 27,
  RegistryReadRequest = 28,
  RegistryReadResponse = 29,
  Error = 30,
  ProcessIconsRequest = 31,
  ProcessIconsResponse = 32,
  RegistryMutationRequest = 33,
  RegistryMutationResponse = 34,
  RegistryWatchRequest = 35,
  RegistryChanged = 36,
  ServiceControlRequest = 37,
  ServiceControlResponse = 38,
  ServiceDetailsRequest = 39,
  ServiceDetailsResponse = 40,
  PowerActionRequest = 41,
  PowerActionResponse = 42,
  AgentSettingsUpdateRequest = 43,
  AgentSettingsUpdateResponse = 44,
  AudioStreamStart = 45,
  AudioStreamStartResponse = 46,
  AudioStreamStop = 47,
  AudioStreamStopResponse = 48,
  AgentUninstallRequest = 49,
  AgentUninstallResponse = 50
}

public static class ProtocolVersions
{
  public const int Current = 16;
}

[Flags]
public enum DesktopTransportCapabilities
{
  None = 0,
  UdpH264 = 1,
  TcpH264Fallback = 2,
  UdpInput = 4,
  TcpInputFallback = 8,
  InstanceBoundSessions = 16,
  UdpOpusAudio = 32,
  Current = UdpH264 | TcpH264Fallback | UdpInput | TcpInputFallback |
            InstanceBoundSessions | UdpOpusAudio
}

public sealed class RemoteMessage
{
  public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
  public MessageType Type { get; set; }
  public string DeviceId { get; set; } = string.Empty;
  public string DeviceName { get; set; } = string.Empty;
  public JsonElement Payload { get; set; }
}

public sealed record HelloPayload(
  string DeviceId,
  string DeviceName,
  string UserName,
  string MachineName,
  string OperatingSystem,
  string AgentVersion,
  int ProtocolVersion = 0,
  DesktopTransportCapabilities DesktopCapabilities =
    DesktopTransportCapabilities.None,
  bool StartupEnabled = false,
  bool HideTray = false);
public sealed record SystemInfoPayload(string MachineName, string UserName, string OperatingSystem, string ProcessorCount, string WorkingSetMb, string CurrentDirectory, string LocalIpAddresses);
public sealed record DetailedSystemInfoPayload(string DeviceName, string Processor, string InstalledRam, string Graphics, string Storage, string DeviceId, string ProductId, string SystemType, string PenAndTouch, string WindowsEdition, string DisplayVersion, string InstallDate, string OsBuild, string ExperiencePack, string LocalIpAddresses);
public sealed record CommandRequestPayload(string FileName, string Arguments, string WorkingDirectory, int TimeoutSeconds);
public sealed record CommandResponsePayload(int ExitCode, string StandardOutput, string StandardError);
public sealed record DriveInfoPayload(
  string Name,
  string DriveType,
  string Format,
  long TotalSize,
  long AvailableFreeSpace,
  string VolumeLabel = "");
public sealed record DirectoryItemPayload(string Name, string FullPath, bool IsDirectory, long Length, DateTime LastWriteTime, string Extension);
public sealed record DirectoryResponsePayload(string Path, List<DirectoryItemPayload> Items);
public sealed record PathPayload(string Path);
public sealed record RenamePayload(string OldPath, string NewPath);
public sealed record OperationResultPayload(bool Success, string Message);
public enum PowerAction
{
  Lock = 1,
  Restart = 2,
  Shutdown = 3,
  SecureAttention = 4
}
public sealed record PowerActionPayload(PowerAction Action);
public sealed record AgentSettingsPayload(
  bool StartupEnabled,
  bool HideTray);
public sealed record FilePropertiesPayload(string Name, string FullPath, bool IsDirectory, long Length, DateTime CreationTime, DateTime LastWriteTime, string Attributes, string Extension);
public sealed record ThumbnailPayload(string Path, string Base64Png, bool Success, string Message);
public sealed record ProcessListRequestPayload(List<int> KnownIconProcessIds);
public sealed record ProcessInfoPayload(
  int Id,
  string Name,
  string MainWindowTitle,
  double CpuPercent,
  long WorkingSetMb,
  string IconBase64Png,
  bool IsApplication);
public sealed record ProcessIconsRequestPayload(List<int> ProcessIds);
public sealed record ProcessIconPayload(int Id, string IconBase64Png);
public sealed record ServiceInfoPayload(
  string ServiceName,
  string DisplayName,
  string Status,
  string StartType,
  bool CanStop = false,
  bool CanPauseAndContinue = false);

public enum ServiceControlAction
{
  Start = 1,
  Stop = 2,
  Restart = 3,
  Pause = 4,
  Continue = 5,
  SetStartType = 6
}

public sealed record ServiceControlPayload(
  string ServiceName,
  ServiceControlAction Action,
  string StartType = "");

public sealed record ServiceDetailsPayload(
  string ServiceName,
  string DisplayName,
  string Description,
  string Status,
  string StartType,
  string ExecutablePath,
  string Account,
  int ProcessId,
  List<string> Dependencies,
  List<string> DependentServices,
  bool CanStop,
  bool CanPauseAndContinue);

public sealed record DesktopSessionPayload(string SessionId);
public sealed record AgentUninstallPayload(string DeviceId);
public enum RegistryViewMode
{
  Default = 0,
  Registry64 = 1,
  Registry32 = 2
}

public enum RegistryMutationKind
{
  CreateKey = 1,
  RenameKey = 2,
  DeleteKey = 3,
  CreateValue = 4,
  SetValue = 5,
  RenameValue = 6,
  DeleteValue = 7
}

public sealed record RegistryReadPayload(
  string Hive,
  string SubKey,
  RegistryViewMode View = RegistryViewMode.Default);

public sealed record RegistryValuePayload(
  string Name,
  string Type,
  string Data,
  string? StringValue = null,
  List<string>? MultiStringValue = null,
  byte[]? BinaryValue = null,
  long? IntegerValue = null,
  string? RawName = null);

public sealed record RegistryReadResponsePayload(string KeyPath, List<string> SubKeys, List<RegistryValuePayload> Values);
public sealed record RegistryMutationPayload(
  RegistryMutationKind Kind,
  string Hive,
  string SubKey,
  string Name = "",
  string NewName = "",
  string ValueKind = "",
  string? StringValue = null,
  List<string>? MultiStringValue = null,
  byte[]? BinaryValue = null,
  long? IntegerValue = null,
  RegistryViewMode View = RegistryViewMode.Default);
public sealed record RegistryWatchPayload(
  string Hive,
  string SubKey,
  RegistryViewMode View = RegistryViewMode.Default);
public sealed record RegistryChangedPayload(string Hive, string SubKey, RegistryViewMode View);
public sealed record ErrorPayload(string Message);

public enum VideoCodec : int { H264 = 2 }
public sealed record RemoteVideoFrame(
  VideoCodec Codec,
  byte[] Data,
  int Width,
  int Height,
  int SourceWidth,
  int SourceHeight,
  int SourceX,
  int SourceY,
  long FrameId,
  bool KeyFrame,
  long TimestampTicks,
  Guid SessionId = default);

public static class BinaryVideoProtocol
{
  private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ADCVIDEO3");
  private const int MaxFrameBytes = 32 * 1024 * 1024;

  public static async Task WriteHelloAsync(NetworkStream stream, string deviceId, CancellationToken token)
  {
    byte[] id = Encoding.UTF8.GetBytes(deviceId);
    if (id.Length <= 0 || id.Length > 4096) throw new InvalidOperationException("Invalid video channel device id length.");
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
    if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic)) throw new InvalidOperationException("Invalid video channel handshake.");
    int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(Magic.Length));
    if (length <= 0 || length > 4096) throw new InvalidOperationException("Invalid video channel device id length.");
    byte[] id = new byte[length];
    if (!await ReadExactAsync(stream, id, token)) return null;
    return Encoding.UTF8.GetString(id);
  }

  public static async Task WriteFrameAsync(NetworkStream stream, RemoteVideoFrame frame, CancellationToken token)
  {
    if (frame.Data.Length <= 0 || frame.Data.Length > MaxFrameBytes) throw new InvalidOperationException("Invalid video frame.");
    byte[] header = new byte[68];
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0), (int)frame.Codec);
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), frame.Width);
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), frame.Height);
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), frame.SourceWidth);
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), frame.SourceHeight);
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), frame.SourceX);
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), frame.SourceY);
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), frame.KeyFrame ? 1 : 0);
    BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(32), frame.FrameId);
    BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(40), frame.TimestampTicks);
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(48), frame.Data.Length);
    frame.SessionId.TryWriteBytes(header.AsSpan(52, 16));
    await stream.WriteAsync(header, token);
    await stream.WriteAsync(frame.Data, token);
    await stream.FlushAsync(token);
  }

  public static async Task<RemoteVideoFrame?> ReadFrameAsync(NetworkStream stream, CancellationToken token)
  {
    byte[] header = new byte[68];
    if (!await ReadExactAsync(stream, header, token)) return null;
    var codec = (VideoCodec)BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0));
    int width = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
    int height = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
    int sourceWidth = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
    int sourceHeight = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16));
    int sourceX = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(20));
    int sourceY = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(24));
    bool keyFrame = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(28)) != 0;
    long frameId = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(32));
    long ticks = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(40));
    int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(48));
    Guid sessionId = new(header.AsSpan(52, 16));
    if (width <= 0 || height <= 0 || sourceWidth <= 0 || sourceHeight <= 0 || length <= 0 || length > MaxFrameBytes)
      throw new InvalidOperationException("Invalid video frame.");
    byte[] data = new byte[length];
    if (!await ReadExactAsync(stream, data, token)) return null;
    return new RemoteVideoFrame(codec, data, width, height, sourceWidth, sourceHeight, sourceX, sourceY, frameId, keyFrame, ticks, sessionId);
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
public static class MessagePayload
{
  private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = false };
  public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
  public static T? As<T>(this JsonElement element) => element.Deserialize<T>(Options);
}

public static class FramedJsonTransport
{
  private const int MaxMessageBytes = 96 * 1024 * 1024;
  private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

  public static async Task WriteAsync(NetworkStream stream, RemoteMessage message, CancellationToken cancellationToken)
  {
    byte[] json = JsonSerializer.SerializeToUtf8Bytes(message, Options);
    if (json.Length > MaxMessageBytes) throw new InvalidOperationException("消息过大。");
    byte[] length = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(length, json.Length);
    await stream.WriteAsync(length, cancellationToken);
    await stream.WriteAsync(json, cancellationToken);
    await stream.FlushAsync(cancellationToken);
  }

  public static async Task<RemoteMessage?> ReadAsync(NetworkStream stream, CancellationToken cancellationToken)
  {
    byte[] lengthBytes = new byte[4];
    if (!await ReadExactAsync(stream, lengthBytes, cancellationToken)) return null;
    int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
    if (length <= 0 || length > MaxMessageBytes) throw new InvalidOperationException("消息长度非法。");
    byte[] json = new byte[length];
    if (!await ReadExactAsync(stream, json, cancellationToken)) return null;
    return JsonSerializer.Deserialize<RemoteMessage>(json, Options);
  }

  private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
  {
    int offset = 0;
    while (offset < buffer.Length)
    {
      int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
      if (read == 0) return false;
      offset += read;
    }
    return true;
  }
}

