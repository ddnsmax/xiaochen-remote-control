using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace RemoteControl.Shared;

public enum ClipboardPacketType : byte
{
  Text = 1,
  FilesBegin = 2,
  FileChunk = 3,
  FilesComplete = 4,
  FilesCancel = 5
}

public sealed record ClipboardPacket(ClipboardPacketType Type, byte[] Payload);
public sealed record ClipboardFileEntry(string RelativePath, bool IsDirectory, long Length, long LastWriteUtcTicks);
public sealed record ClipboardFilesManifest(Guid TransferId, List<ClipboardFileEntry> Entries, List<string> TopLevelPaths);

public sealed class ClipboardTransferPlan
{
  public ClipboardFilesManifest Manifest { get; }
  public IReadOnlyDictionary<int, string> SourceFiles { get; }

  private ClipboardTransferPlan(ClipboardFilesManifest manifest, IReadOnlyDictionary<int, string> sourceFiles)
  {
    Manifest = manifest;
    SourceFiles = sourceFiles;
  }

  public static ClipboardTransferPlan Create(IEnumerable<string> sourcePaths)
  {
    var manifest = new ClipboardFilesManifest(Guid.NewGuid(), new(), new());
    var sourceFiles = new Dictionary<int, string>();
    var usedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (string candidate in sourcePaths.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
    {
      string fullPath;
      try { fullPath = Path.GetFullPath(candidate); }
      catch { continue; }
      if (!File.Exists(fullPath) && !Directory.Exists(fullPath)) continue;

      string rawRoot = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
      if (string.IsNullOrWhiteSpace(rawRoot)) rawRoot = new DirectoryInfo(fullPath).Name.Replace(':', '_');
      string root = MakeUniqueRoot(SanitizePart(rawRoot), usedRoots);
      manifest.TopLevelPaths.Add(root);

      if (File.Exists(fullPath))
      {
        AddFile(fullPath, root, manifest, sourceFiles);
        continue;
      }

      AddDirectory(fullPath, root, manifest);
      var pending = new Stack<(string Source, string Relative)>();
      pending.Push((fullPath, root));
      while (pending.Count > 0)
      {
        var current = pending.Pop();
        IEnumerable<string> directories;
        IEnumerable<string> files;
        try
        {
          directories = Directory.EnumerateDirectories(current.Source).ToArray();
          files = Directory.EnumerateFiles(current.Source).ToArray();
        }
        catch { continue; }

        foreach (string directory in directories)
        {
          try
          {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            string relative = CombineRelative(current.Relative, Path.GetFileName(directory));
            AddDirectory(directory, relative, manifest);
            pending.Push((directory, relative));
          }
          catch { }
        }

        foreach (string file in files)
        {
          string relative = CombineRelative(current.Relative, Path.GetFileName(file));
          AddFile(file, relative, manifest, sourceFiles);
        }
      }
    }

    if (manifest.Entries.Count == 0) throw new InvalidOperationException("剪贴板中没有可传输的文件或文件夹。");
    return new ClipboardTransferPlan(manifest, sourceFiles);
  }

  private static void AddDirectory(string source, string relative, ClipboardFilesManifest manifest)
  {
    long ticks = 0;
    try { ticks = Directory.GetLastWriteTimeUtc(source).Ticks; } catch { }
    manifest.Entries.Add(new ClipboardFileEntry(relative, true, 0, ticks));
  }

  private static void AddFile(string source, string relative, ClipboardFilesManifest manifest, Dictionary<int, string> sourceFiles)
  {
    try
    {
      var info = new FileInfo(source);
      int index = manifest.Entries.Count;
      manifest.Entries.Add(new ClipboardFileEntry(relative, false, info.Length, info.LastWriteTimeUtc.Ticks));
      sourceFiles[index] = source;
    }
    catch { }
  }

  private static string CombineRelative(string left, string right) => left.TrimEnd('\\') + "\\" + SanitizePart(right);
  private static string SanitizePart(string value)
  {
    foreach (char c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
    return string.IsNullOrWhiteSpace(value) ? "未命名" : value;
  }

  private static string MakeUniqueRoot(string value, HashSet<string> used)
  {
    string result = value;
    int suffix = 2;
    while (!used.Add(result)) result = $"{value} ({suffix++})";
    return result;
  }
}

public static class BinaryClipboardProtocol
{
  private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ADCCLIP2");
  private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
  private const int MaxPacketBytes = 16 * 1024 * 1024;
  public const int ChunkSize = 256 * 1024;

  public static async Task WriteHelloAsync(NetworkStream stream, string deviceId, CancellationToken token)
  {
    byte[] id = Encoding.UTF8.GetBytes(deviceId);
    if (id.Length is <= 0 or > 4096) throw new InvalidOperationException("Invalid clipboard channel device id length.");
    byte[] header = new byte[Magic.Length + 4];
    Magic.CopyTo(header, 0);
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(Magic.Length), id.Length);
    await stream.WriteAsync(header, token);
    await stream.WriteAsync(id, token);
  }

  public static async Task<string?> ReadHelloAsync(NetworkStream stream, CancellationToken token)
  {
    byte[] header = new byte[Magic.Length + 4];
    if (!await ReadExactAsync(stream, header, token)) return null;
    if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic)) throw new InvalidOperationException("Invalid clipboard channel handshake.");
    int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(Magic.Length));
    if (length is <= 0 or > 4096) throw new InvalidOperationException("Invalid clipboard channel device id length.");
    byte[] id = new byte[length];
    if (!await ReadExactAsync(stream, id, token)) return null;
    return Encoding.UTF8.GetString(id);
  }

  public static async Task WriteAsync(NetworkStream stream, ClipboardPacket packet, CancellationToken token)
  {
    int bodyLength = checked(packet.Payload.Length + 1);
    if (bodyLength < 1 || bodyLength > MaxPacketBytes) throw new InvalidOperationException("Invalid clipboard packet length.");
    byte[] header = new byte[5];
    BinaryPrimitives.WriteInt32LittleEndian(header, bodyLength);
    header[4] = (byte)packet.Type;
    await stream.WriteAsync(header, token);
    if (packet.Payload.Length > 0) await stream.WriteAsync(packet.Payload, token);
  }

  public static async Task<ClipboardPacket?> ReadAsync(NetworkStream stream, CancellationToken token)
  {
    byte[] lengthBytes = new byte[4];
    if (!await ReadExactAsync(stream, lengthBytes, token)) return null;
    int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
    if (length < 1 || length > MaxPacketBytes) throw new InvalidOperationException("Invalid clipboard packet length.");
    byte[] body = new byte[length];
    if (!await ReadExactAsync(stream, body, token)) return null;
    return new ClipboardPacket((ClipboardPacketType)body[0], body.AsSpan(1).ToArray());
  }

  public static ClipboardPacket Text(string text) => new(ClipboardPacketType.Text, Encoding.UTF8.GetBytes(text));
  public static string ReadText(ClipboardPacket packet) => Encoding.UTF8.GetString(packet.Payload);
  public static ClipboardPacket FilesBegin(ClipboardFilesManifest manifest) => new(ClipboardPacketType.FilesBegin, JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
  public static ClipboardFilesManifest ReadManifest(ClipboardPacket packet) => JsonSerializer.Deserialize<ClipboardFilesManifest>(packet.Payload, Json) ?? throw new InvalidOperationException("Invalid clipboard manifest.");
  public static ClipboardPacket FilesComplete(Guid transferId) => new(ClipboardPacketType.FilesComplete, transferId.ToByteArray());
  public static ClipboardPacket FilesCancel(Guid transferId) => new(ClipboardPacketType.FilesCancel, transferId.ToByteArray());

  public static ClipboardPacket FileChunk(Guid transferId, int entryIndex, long offset, ReadOnlySpan<byte> data)
  {
    byte[] payload = new byte[28 + data.Length];
    transferId.TryWriteBytes(payload);
    BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(16), entryIndex);
    BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(20), offset);
    data.CopyTo(payload.AsSpan(28));
    return new ClipboardPacket(ClipboardPacketType.FileChunk, payload);
  }

  public static (Guid TransferId, int EntryIndex, long Offset, ReadOnlyMemory<byte> Data) ReadFileChunk(ClipboardPacket packet)
  {
    if (packet.Payload.Length < 28) throw new InvalidOperationException("Invalid clipboard file chunk.");
    return (new Guid(packet.Payload.AsSpan(0, 16)), BinaryPrimitives.ReadInt32LittleEndian(packet.Payload.AsSpan(16, 4)), BinaryPrimitives.ReadInt64LittleEndian(packet.Payload.AsSpan(20, 8)), packet.Payload.AsMemory(28));
  }

  public static Guid ReadTransferId(ClipboardPacket packet)
  {
    if (packet.Payload.Length != 16) throw new InvalidOperationException("Invalid clipboard transfer id.");
    return new Guid(packet.Payload);
  }

  public static async Task SendFilesAsync(NetworkStream stream, SemaphoreSlim writeLock, IEnumerable<string> sourcePaths, CancellationToken token, Action<long, long>? progress = null)
  {
    ClipboardTransferPlan plan = await Task.Run(() => ClipboardTransferPlan.Create(sourcePaths), token);
    long total = plan.Manifest.Entries.Where(x => !x.IsDirectory).Sum(x => x.Length);
    long sent = 0;
    await writeLock.WaitAsync(token);
    try
    {
      await WriteAsync(stream, FilesBegin(plan.Manifest), token);
      byte[] buffer = new byte[ChunkSize];
      foreach (var pair in plan.SourceFiles.OrderBy(x => x.Key))
      {
        await using var file = new FileStream(pair.Value, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        long offset = 0;
        while (true)
        {
          int read = await file.ReadAsync(buffer, token);
          if (read == 0) break;
          await WriteAsync(stream, FileChunk(plan.Manifest.TransferId, pair.Key, offset, buffer.AsSpan(0, read)), token);
          offset += read;
          sent += read;
          progress?.Invoke(sent, total);
        }
      }
      await WriteAsync(stream, FilesComplete(plan.Manifest.TransferId), token);
    }
    catch
    {
      try { await WriteAsync(stream, FilesCancel(plan.Manifest.TransferId), CancellationToken.None); } catch { }
      throw;
    }
    finally { writeLock.Release(); }
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

public sealed class ClipboardFileReceiver : IDisposable
{
  private readonly string _cacheRoot;
  private ClipboardFilesManifest? _manifest;
  private string? _transferRoot;
  private int _currentEntry = -1;
  private FileStream? _currentFile;

  public ClipboardFileReceiver(string cacheRoot)
  {
    _cacheRoot = Path.GetFullPath(cacheRoot);
    Directory.CreateDirectory(_cacheRoot);
    CleanupOldTransfers();
  }

  public async Task<IReadOnlyList<string>?> ProcessAsync(ClipboardPacket packet, CancellationToken token)
  {
    switch (packet.Type)
    {
      case ClipboardPacketType.FilesBegin:
        Begin(BinaryClipboardProtocol.ReadManifest(packet));
        break;
      case ClipboardPacketType.FileChunk:
        await WriteChunkAsync(BinaryClipboardProtocol.ReadFileChunk(packet), token);
        break;
      case ClipboardPacketType.FilesComplete:
        return Complete(BinaryClipboardProtocol.ReadTransferId(packet));
      case ClipboardPacketType.FilesCancel:
        Cancel(BinaryClipboardProtocol.ReadTransferId(packet));
        break;
    }
    return null;
  }

  private void Begin(ClipboardFilesManifest manifest)
  {
    Reset(false);
    if (manifest.Entries.Count is <= 0 or > 100_000 || manifest.TopLevelPaths.Count is <= 0 or > 10_000)
      throw new InvalidOperationException("Invalid clipboard file manifest.");
    _manifest = manifest;
    _transferRoot = Path.Combine(_cacheRoot, manifest.TransferId.ToString("N"));
    Directory.CreateDirectory(_transferRoot);
    foreach (var entry in manifest.Entries.Where(x => x.IsDirectory)) Directory.CreateDirectory(SafePath(entry.RelativePath));
    foreach (var entry in manifest.Entries.Where(x => !x.IsDirectory && x.Length == 0))
    {
      string path = SafePath(entry.RelativePath);
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      using var _ = File.Create(path);
    }
  }

  private async Task WriteChunkAsync((Guid TransferId, int EntryIndex, long Offset, ReadOnlyMemory<byte> Data) chunk, CancellationToken token)
  {
    if (_manifest is null || _transferRoot is null || chunk.TransferId != _manifest.TransferId) throw new InvalidOperationException("Clipboard transfer is not active.");
    if (chunk.EntryIndex < 0 || chunk.EntryIndex >= _manifest.Entries.Count) throw new InvalidOperationException("Invalid clipboard entry index.");
    ClipboardFileEntry entry = _manifest.Entries[chunk.EntryIndex];
    if (entry.IsDirectory) throw new InvalidOperationException("Clipboard chunk targets a directory.");

    if (_currentEntry != chunk.EntryIndex)
    {
      CloseCurrentFile();
      string path = SafePath(entry.RelativePath);
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      _currentFile = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, BinaryClipboardProtocol.ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
      _currentEntry = chunk.EntryIndex;
    }
    if (_currentFile is null || _currentFile.Position != chunk.Offset) throw new InvalidOperationException("Out-of-order clipboard file chunk.");
    await _currentFile.WriteAsync(chunk.Data, token);
  }

  private IReadOnlyList<string> Complete(Guid transferId)
  {
    if (_manifest is null || _transferRoot is null || transferId != _manifest.TransferId) throw new InvalidOperationException("Clipboard transfer id mismatch.");
    CloseCurrentFile();
    foreach (var entry in _manifest.Entries.Where(x => !x.IsDirectory))
    {
      string path = SafePath(entry.RelativePath);
      if (!File.Exists(path) || new FileInfo(path).Length != entry.Length)
      {
        Reset(true);
        throw new InvalidOperationException($"Clipboard file transfer is incomplete: {entry.RelativePath}");
      }
    }
    foreach (var entry in _manifest.Entries)
    {
      if (entry.LastWriteUtcTicks <= 0) continue;
      try
      {
        string path = SafePath(entry.RelativePath);
        DateTime timestamp = new(entry.LastWriteUtcTicks, DateTimeKind.Utc);
        if (entry.IsDirectory) Directory.SetLastWriteTimeUtc(path, timestamp); else File.SetLastWriteTimeUtc(path, timestamp);
      }
      catch { }
    }
    var result = _manifest.TopLevelPaths.Select(SafePath).ToArray();
    _manifest = null;
    _transferRoot = null;
    return result;
  }

  private void Cancel(Guid transferId)
  {
    if (_manifest?.TransferId == transferId) Reset(true);
  }

  private string SafePath(string relative)
  {
    if (_transferRoot is null) throw new InvalidOperationException("Clipboard transfer is not active.");
    string root = Path.GetFullPath(_transferRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    string result = Path.GetFullPath(Path.Combine(root, relative));
    if (!result.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe clipboard path.");
    return result;
  }

  private void CloseCurrentFile()
  {
    _currentFile?.Dispose();
    _currentFile = null;
    _currentEntry = -1;
  }

  private void Reset(bool deleteFiles)
  {
    CloseCurrentFile();
    string? root = _transferRoot;
    _manifest = null;
    _transferRoot = null;
    if (deleteFiles && root is not null) try { Directory.Delete(root, true); } catch { }
  }

  private void CleanupOldTransfers()
  {
    try
    {
      foreach (string directory in Directory.EnumerateDirectories(_cacheRoot))
        if (Directory.GetCreationTimeUtc(directory) < DateTime.UtcNow.AddDays(-7)) Directory.Delete(directory, true);
    }
    catch { }
  }

  public void Dispose() => Reset(true);
}
