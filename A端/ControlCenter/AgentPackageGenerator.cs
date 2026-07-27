using RemoteControl.Shared;
using System.IO;
using System.Reflection;

namespace ControlCenter;

internal static class AgentPackageGenerator
{
  private const string TemplateResourceName = "ControlCenter.AgentTemplate.bin";

  public static string Generate(string host, string destinationPath)
  {
    string normalized = GeneratedAgentConfiguration.NormalizeHost(host);
    string fullDestination = Path.GetFullPath(destinationPath);
    string? directory = Path.GetDirectoryName(fullDestination);
    if (string.IsNullOrWhiteSpace(directory))
      throw new InvalidOperationException("无法确定B端保存目录。");
    Directory.CreateDirectory(directory);
    string temporary = Path.Combine(
      directory,
      $".{Path.GetFileName(fullDestination)}.{Guid.NewGuid():N}.tmp");
    try
    {
      using Stream template = Assembly.GetExecutingAssembly()
        .GetManifestResourceStream(TemplateResourceName)
        ?? throw new InvalidOperationException(
          "当前A端没有内置B端模板，请使用正式交付版A端。");
      using (var output = new FileStream(
               temporary,
               FileMode.CreateNew,
               FileAccess.Write,
               FileShare.None))
        template.CopyTo(output);
      GeneratedAgentConfiguration.AppendToExecutable(temporary, normalized);
      if (!GeneratedAgentConfiguration.TryReadFromExecutable(
            temporary,
            out string verified) ||
          !string.Equals(verified, normalized, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("生成后的域名配置校验失败。");
      File.Move(temporary, fullDestination, overwrite: true);
      return normalized;
    }
    catch
    {
      try { File.Delete(temporary); } catch { }
      throw;
    }
  }
}
