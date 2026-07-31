using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;

namespace RemoteControl.Shared;

public enum MessageType
{
  Hello = 0,
  Heartbeat = 1,
  SystemInfoRequest = 2,
  SystemInfoResponse = 3,
  ReservedLegacyScreenStreamStart = 4,
  ReservedLegacyScreenStreamStop = 5,
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
  ReservedLegacyAudioStreamStart = 45,
  ReservedLegacyAudioStreamStartResponse = 46,
  ReservedLegacyAudioStreamStop = 47,
  ReservedLegacyAudioStreamStopResponse = 48,
  AgentUninstallRequest = 49,
  AgentUninstallResponse = 50,
  RustDeskSessionStartRequest = 51,
  RustDeskSessionStartResponse = 52,
  RustDeskSessionStopRequest = 53,
  RustDeskSessionStopResponse = 54
}

public static class ProtocolVersions
{
  public const int Current = 17;
}

[Flags]
public enum RemoteDesktopCapabilities
{
  None = 0,
  RustDeskDirect = 1,
  SecureDesktop = 2,
  ViewOnly = 4,
  Clipboard = 8,
  Audio = 16,
  FileTransfer = 32,
  Current = RustDeskDirect | SecureDesktop | ViewOnly | Clipboard | Audio |
            FileTransfer
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
  RemoteDesktopCapabilities DesktopCapabilities =
    RemoteDesktopCapabilities.None,
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

public sealed record RustDeskSessionPayload(long SessionId, bool ViewOnly = false);
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

