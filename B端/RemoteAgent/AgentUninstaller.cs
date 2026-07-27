using RemoteControl.Shared;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;

namespace RemoteAgent;

public partial class MainWindow
{
  private int _uninstallScheduled;

  private OperationResultPayload ValidateAgentUninstall(
    AgentUninstallPayload request)
  {
    if (!string.Equals(
          request.DeviceId,
          _deviceId,
          StringComparison.OrdinalIgnoreCase))
      return new(false, "设备标识不匹配，未执行删除。");
    if (Interlocked.CompareExchange(ref _uninstallScheduled, 1, 0) != 0)
      return new(true, "B端删除任务已经启动。");
    return new(true, "B端删除任务已确认。");
  }

  private void ScheduleAgentUninstall()
  {
    _ = Task.Run(async () =>
    {
      await Task.Delay(350);
      try
      {
        AgentSettingsStore.DeleteForUninstall();
        WindowsAgentEnvironment.DeletePersistentIdentity();
        string executable = Path.GetFullPath(
          Environment.ProcessPath
          ?? Process.GetCurrentProcess().MainModule?.FileName
          ?? throw new InvalidOperationException("无法确定B端程序路径。"));
        string commonData = Path.Combine(
          Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
          "AuthorizedDeviceControl");
        string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        string script =
          "Start-Sleep -Seconds 2; " +
          $"& sc.exe stop {AgentServiceBootstrap.ServiceName} 2>$null | Out-Null; " +
          "Start-Sleep -Seconds 2; " +
          $"& sc.exe delete {AgentServiceBootstrap.ServiceName} 2>$null | Out-Null; " +
          $"Remove-Item -LiteralPath {Quote(Path.Combine(commonData, "agent-settings.json"))} -Force -ErrorAction SilentlyContinue; " +
          $"Remove-Item -LiteralPath {Quote(Path.Combine(commonData, "device.id"))} -Force -ErrorAction SilentlyContinue; " +
          $"Remove-Item -LiteralPath {Quote(executable)} -Force -ErrorAction SilentlyContinue";
        string encoded = Convert.ToBase64String(
          Encoding.Unicode.GetBytes(script));
        string command =
          $"powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}";
        LaunchDetachedSystemCommand(command);
      }
      catch
      {
        Interlocked.Exchange(ref _uninstallScheduled, 0);
        return;
      }
      try
      {
        ForceStopScreenStream();
        Disconnect();
      }
      catch { }
      Environment.Exit(0);
    });
  }

  private static void LaunchDetachedSystemCommand(string command)
  {
    using var processClass = new ManagementClass("Win32_Process");
    using ManagementBaseObject input = processClass.GetMethodParameters("Create");
    input["CommandLine"] = command;
    using ManagementBaseObject output = processClass.InvokeMethod(
      "Create",
      input,
      null)
      ?? throw new InvalidOperationException("无法创建B端清理任务。");
    uint result = Convert.ToUInt32(output["ReturnValue"]);
    if (result != 0)
      throw new InvalidOperationException($"创建B端清理任务失败：{result}");
  }
}
