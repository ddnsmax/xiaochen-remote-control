using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RemoteControl.Shared;

public static class GeneratedAgentConfiguration
{
  private const int Version = 1;
  private const int HashBytes = 32;
  private const int FooterBytes = 16 + sizeof(int) + sizeof(int) + HashBytes;
  private static readonly byte[] Magic =
    Encoding.ASCII.GetBytes("ADC-AGENT-CFG-01");

  public static string NormalizeHost(string value)
  {
    string host = value.Trim().TrimEnd('.');
    if (host.Length == 0 ||
        host.Contains("://", StringComparison.Ordinal) ||
        host.IndexOfAny(['/', '\\', ':', '?', '#', '@', '"', '\'']) >= 0 ||
        host.Any(char.IsWhiteSpace))
      throw new InvalidOperationException("请输入不带协议、端口和路径的域名。");

    string ascii;
    try
    {
      ascii = new IdnMapping().GetAscii(host).ToLowerInvariant();
    }
    catch (ArgumentException)
    {
      throw new InvalidOperationException("域名格式无效。");
    }
    if (ascii.Length is <= 0 or > 253)
      throw new InvalidOperationException("域名长度无效。");
    string[] labels = ascii.Split('.');
    if (labels.Length < 2 ||
        labels.Any(label =>
          label.Length is <= 0 or > 63 ||
          label[0] == '-' ||
          label[^1] == '-' ||
          label.Any(character =>
            !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))))
      throw new InvalidOperationException("域名格式无效。");
    return ascii;
  }

  public static void AppendToExecutable(string executablePath, string host)
  {
    string normalized = NormalizeHost(host);
    byte[] payload = Encoding.UTF8.GetBytes(normalized);
    using var stream = new FileStream(
      executablePath,
      FileMode.Open,
      FileAccess.ReadWrite,
      FileShare.None);
    if (TryRead(stream, out _, out long configurationStart))
      stream.SetLength(configurationStart);
    stream.Position = stream.Length;
    stream.Write(payload);
    Span<byte> footer = stackalloc byte[FooterBytes];
    Magic.CopyTo(footer);
    BinaryPrimitives.WriteInt32LittleEndian(footer[16..], Version);
    BinaryPrimitives.WriteInt32LittleEndian(footer[20..], payload.Length);
    SHA256.HashData(payload, footer[24..]);
    stream.Write(footer);
    stream.Flush(flushToDisk: true);
  }

  public static bool TryReadFromExecutable(
    string executablePath,
    out string host)
  {
    host = string.Empty;
    try
    {
      using var stream = new FileStream(
        executablePath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete);
      return TryRead(stream, out host, out _);
    }
    catch
    {
      return false;
    }
  }

  private static bool TryRead(
    FileStream stream,
    out string host,
    out long configurationStart)
  {
    host = string.Empty;
    configurationStart = stream.Length;
    if (stream.Length < FooterBytes) return false;
    Span<byte> footer = stackalloc byte[FooterBytes];
    stream.Position = stream.Length - FooterBytes;
    stream.ReadExactly(footer);
    if (!footer[..16].SequenceEqual(Magic) ||
        BinaryPrimitives.ReadInt32LittleEndian(footer[16..]) != Version)
      return false;
    int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(footer[20..]);
    if (payloadLength is <= 0 or > 1024 ||
        stream.Length < FooterBytes + payloadLength)
      return false;
    configurationStart = stream.Length - FooterBytes - payloadLength;
    byte[] payload = new byte[payloadLength];
    stream.Position = configurationStart;
    stream.ReadExactly(payload);
    Span<byte> actualHash = stackalloc byte[HashBytes];
    SHA256.HashData(payload, actualHash);
    if (!actualHash.SequenceEqual(footer[24..]))
    {
      configurationStart = stream.Length;
      return false;
    }
    try
    {
      host = NormalizeHost(Encoding.UTF8.GetString(payload));
      return true;
    }
    catch
    {
      host = string.Empty;
      configurationStart = stream.Length;
      return false;
    }
  }
}
