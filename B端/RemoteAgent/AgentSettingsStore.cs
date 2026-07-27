using RemoteControl.Shared;
using System.IO;
using System.Text.Json;

namespace RemoteAgent;

internal static class AgentSettingsStore
{
  private static readonly object Gate = new();
  private static readonly string DirectoryPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "AuthorizedDeviceControl");
  private static readonly string FilePath = Path.Combine(
    DirectoryPath,
    "agent-settings.json");

  public static AgentSettingsPayload Load()
  {
    lock (Gate)
    {
      try
      {
        if (!File.Exists(FilePath)) return new(false, false);
        return JsonSerializer.Deserialize<AgentSettingsPayload>(
                 File.ReadAllText(FilePath))
               ?? new AgentSettingsPayload(false, false);
      }
      catch
      {
        return new(false, false);
      }
    }
  }

  public static AgentSettingsPayload Save(AgentSettingsPayload settings)
  {
    lock (Gate)
    {
      Directory.CreateDirectory(DirectoryPath);
      string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
      File.WriteAllText(
        temporary,
        JsonSerializer.Serialize(settings));
      File.Move(temporary, FilePath, true);
      AgentServiceBootstrap.ConfigureStartup(settings.StartupEnabled);
      return settings;
    }
  }

  public static void DeleteForUninstall()
  {
    lock (Gate)
    {
      try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }
  }
}
