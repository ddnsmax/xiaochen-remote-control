using RemoteControl.Shared;
using System.IO;
using System.Net.Sockets;

namespace ControlCenter;

public sealed partial class DeviceView
{
  private readonly object _fileGate = new();
  private readonly SemaphoreSlim _fileRequestLock = new(1, 1);
  private TcpClient? _fileClient;
  private NetworkStream? _fileStream;
  private CancellationTokenSource? _fileCts;
  private long _fileGeneration;

  public bool IsFileConnected
  {
    get { lock (_fileGate) return _fileStream is not null; }
  }

  public void AttachFileClient(TcpClient client, CancellationToken parentToken)
  {
    NetworkStream stream = client.GetStream();
    TcpClient? previous;
    lock (_fileGate)
    {
      previous = _fileClient;
      try { _fileCts?.Cancel(); } catch { }
      _fileClient = client;
      _fileStream = stream;
      _fileCts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
      _fileGeneration++;
    }
    try { previous?.Close(); } catch { }
    Changed(nameof(IsFileConnected));
  }

  public async Task<FileTransferPacket> ReadFileRangeAsync(
    string path, long offset, int length, CancellationToken token = default)
  {
    FileTransferPacket response = await SendFileRequestAsync(
      BinaryFileTransferProtocol.ReadRequest(path, offset, length), token);
    if (response.Type != FileTransferPacketType.ReadRangeResponse)
      throw new IOException(response.Message.Length > 0 ? response.Message : "远端返回了无效的读取响应。");
    return response;
  }

  public async Task<long> WriteFileRangeAsync(
    string path, string transferId, long offset, long totalLength, byte[] data, bool complete,
    CancellationToken token = default)
  {
    FileTransferPacket response = await SendFileRequestAsync(
      BinaryFileTransferProtocol.WriteRequest(path, transferId, offset, totalLength, data, complete), token);
    if (response.Type != FileTransferPacketType.WriteRangeResponse)
      throw new IOException(response.Message.Length > 0 ? response.Message : "远端返回了无效的写入响应。");
    return response.Offset;
  }

  public async Task DownloadFileAsync(
    string remotePath, string localPath, long expectedLength,
    IProgress<FileTransferProgress>? progress = null, CancellationToken token = default)
  {
    string partial = localPath + ".adc-part";
    string? directory = Path.GetDirectoryName(localPath);
    if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
    long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
    if (expectedLength >= 0 && offset > expectedLength)
    {
      File.Delete(partial);
      offset = 0;
    }

    await using var output = new FileStream(
      partial, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read,
      1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    output.Position = offset;
    long total = expectedLength;
    var started = DateTime.UtcNow;
    while (total < 0 || offset < total)
    {
      token.ThrowIfCancellationRequested();
      FileTransferPacket response = await ReadFileRangeAsync(
        remotePath, offset, BinaryFileTransferProtocol.MaxChunkBytes, token);
      total = response.TotalLength;
      if (response.Data.Length == 0 && !response.Complete)
        throw new IOException("远端未返回文件数据。");
      if (response.Data.Length > 0)
      {
        await output.WriteAsync(response.Data, token);
        offset += response.Data.LongLength;
      }
      progress?.Report(FileTransferProgress.Create(offset, total, started));
      if (response.Complete) break;
    }
    await output.FlushAsync(token);
    output.Close();
    File.Move(partial, localPath, true);
  }

  public async Task UploadFileAsync(
    string localPath, string remotePath,
    IProgress<FileTransferProgress>? progress = null, CancellationToken token = default)
  {
    var info = new FileInfo(localPath);
    if (!info.Exists) throw new FileNotFoundException("本地文件不存在。", localPath);
    string transferId = Guid.NewGuid().ToString("N");
    long offset = 0;
    var started = DateTime.UtcNow;
    await using var input = new FileStream(
      info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
      1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    byte[] buffer = new byte[BinaryFileTransferProtocol.MaxChunkBytes];
    if (info.Length == 0)
    {
      await WriteFileRangeAsync(remotePath, transferId, 0, 0, [], true, token);
      progress?.Report(FileTransferProgress.Create(0, 0, started));
      return;
    }
    while (offset < info.Length)
    {
      token.ThrowIfCancellationRequested();
      int read = await input.ReadAsync(buffer, token);
      if (read == 0) throw new EndOfStreamException("本地文件在上传过程中被截断。");
      byte[] chunk = buffer.AsSpan(0, read).ToArray();
      bool complete = offset + read == info.Length;
      long acknowledged = await WriteFileRangeAsync(
        remotePath, transferId, offset, info.Length, chunk, complete, token);
      if (acknowledged != offset + read) throw new IOException("远端上传确认偏移不一致。");
      offset = acknowledged;
      progress?.Report(FileTransferProgress.Create(offset, info.Length, started));
    }
  }

  public async Task CopyRemoteFileToStreamAsync(
    string remotePath, Stream destination, long expectedLength,
    IProgress<FileTransferProgress>? progress = null, CancellationToken token = default)
  {
    long offset = 0;
    long total = expectedLength;
    var started = DateTime.UtcNow;
    while (total < 0 || offset < total)
    {
      FileTransferPacket response = await ReadFileRangeAsync(
        remotePath, offset, BinaryFileTransferProtocol.MaxChunkBytes, token);
      total = response.TotalLength;
      if (response.Data.Length > 0)
      {
        await destination.WriteAsync(response.Data, token);
        offset += response.Data.LongLength;
      }
      progress?.Report(FileTransferProgress.Create(offset, total, started));
      if (response.Complete) break;
      if (response.Data.Length == 0) throw new EndOfStreamException("远端文件流提前结束。");
    }
  }

  private async Task<FileTransferPacket> SendFileRequestAsync(
    FileTransferPacket request, CancellationToken token)
  {
    await _fileRequestLock.WaitAsync(token);
    try
    {
      NetworkStream stream;
      long generation;
      lock (_fileGate)
      {
        stream = _fileStream ?? throw new IOException("文件通道尚未连接，请稍后重试。");
        generation = _fileGeneration;
      }
      try
      {
        await BinaryFileTransferProtocol.WriteAsync(stream, request, token);
        FileTransferPacket response = await BinaryFileTransferProtocol.ReadAsync(stream, token)
          ?? throw new EndOfStreamException("文件通道已断开。");
        if (!string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal))
          throw new IOException("文件通道响应序号不匹配。");
        if (response.Type == FileTransferPacketType.Error)
          throw new IOException(response.Message.Length > 0 ? response.Message : "远端文件操作失败。");
        return response;
      }
      catch
      {
        lock (_fileGate)
        {
          if (generation == _fileGeneration)
          {
            try { _fileStream?.Dispose(); _fileClient?.Close(); } catch { }
            _fileStream = null;
            _fileClient = null;
          }
        }
        Changed(nameof(IsFileConnected));
        throw;
      }
    }
    finally { _fileRequestLock.Release(); }
  }

  private void CloseFileChannel()
  {
    lock (_fileGate)
    {
      try { _fileCts?.Cancel(); _fileStream?.Dispose(); _fileClient?.Close(); } catch { }
      _fileCts = null;
      _fileStream = null;
      _fileClient = null;
    }
  }
}

public sealed record FileTransferProgress(long Transferred, long Total, double BytesPerSecond)
{
  public static FileTransferProgress Create(long transferred, long total, DateTime started)
  {
    double seconds = Math.Max(0.001, (DateTime.UtcNow - started).TotalSeconds);
    return new FileTransferProgress(transferred, total, transferred / seconds);
  }
}
