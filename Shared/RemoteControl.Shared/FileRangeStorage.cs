namespace RemoteControl.Shared;

public static class FileRangeStorage
{
  public static async Task<FileTransferPacket> ReadRangeAsync(
    FileTransferPacket request, CancellationToken token)
  {
    var file = new FileInfo(request.Path);
    if (!file.Exists) throw new FileNotFoundException("远程文件不存在。", request.Path);
    long offset = Math.Clamp(request.Offset, 0, file.Length);
    int requested = Math.Clamp(request.RequestedLength, 1, BinaryFileTransferProtocol.MaxChunkBytes);
    int count = (int)Math.Min(requested, file.Length - offset);
    byte[] data = count == 0 ? [] : new byte[count];
    if (count > 0)
    {
      await using var stream = new FileStream(
        file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
        1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
      stream.Position = offset;
      int read = 0;
      while (read < count)
      {
        int n = await stream.ReadAsync(data.AsMemory(read, count - read), token);
        if (n == 0) break;
        read += n;
      }
      if (read != data.Length) Array.Resize(ref data, read);
    }
    long next = offset + data.LongLength;
    return new FileTransferPacket(
      FileTransferPacketType.ReadRangeResponse, request.RequestId, file.FullName, string.Empty,
      next, file.Length, 0, next >= file.Length, data, string.Empty);
  }

  public static async Task<FileTransferPacket> WriteRangeAsync(
    FileTransferPacket request, CancellationToken token)
  {
    if (string.IsNullOrWhiteSpace(request.Path)) throw new InvalidOperationException("目标路径为空。");
    if (!Guid.TryParseExact(request.TransferId, "N", out _)) throw new InvalidOperationException("无效的传输标识。");
    if (request.Offset < 0 || request.TotalLength < 0 ||
        request.Offset + request.Data.LongLength > request.TotalLength)
      throw new InvalidOperationException("无效的上传分块范围。");

    string fullPath = Path.GetFullPath(request.Path);
    string? directory = Path.GetDirectoryName(fullPath);
    if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("无效的目标目录。");
    Directory.CreateDirectory(directory);
    string temporary = fullPath + ".adc-upload-" + request.TransferId + ".part";
    CleanupExpiredUploadParts(directory, Path.GetFileName(fullPath), temporary);

    await using (var stream = new FileStream(
      temporary, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read,
      1024 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
    {
      if (request.Offset == 0 && stream.Length != 0) stream.SetLength(0);
      if (stream.Length < request.Offset)
        throw new InvalidOperationException($"上传偏移不连续，远端已有 {stream.Length} 字节。");
      stream.Position = request.Offset;
      if (request.Data.Length > 0) await stream.WriteAsync(request.Data, token);
      await stream.FlushAsync(token);
      long next = stream.Position;
      if (request.Complete)
      {
        if (next != request.TotalLength)
          throw new InvalidOperationException($"文件尚未完整上传：{next}/{request.TotalLength}。");
        stream.SetLength(request.TotalLength);
      }
    }

    long uploaded = new FileInfo(temporary).Length;
    if (request.Complete)
    {
      File.Move(temporary, fullPath, true);
      uploaded = request.TotalLength;
    }
    return new FileTransferPacket(
      FileTransferPacketType.WriteRangeResponse, request.RequestId, fullPath, request.TransferId,
      uploaded, request.TotalLength, 0, request.Complete, [], "OK");
  }

  private static void CleanupExpiredUploadParts(
    string directory,
    string targetFileName,
    string activeTemporary)
  {
    try
    {
      DateTime cutoff = DateTime.UtcNow.AddHours(-24);
      foreach (string candidate in Directory.EnumerateFiles(
                 directory,
                 targetFileName + ".adc-upload-*.part",
                 SearchOption.TopDirectoryOnly))
      {
        if (string.Equals(candidate, activeTemporary, StringComparison.OrdinalIgnoreCase))
          continue;
        try
        {
          if (File.GetLastWriteTimeUtc(candidate) < cutoff)
            File.Delete(candidate);
        }
        catch { }
      }
    }
    catch { }
  }
}
