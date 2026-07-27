using RemoteControl.Shared;
using System.Collections.Concurrent;
using System.Management;
using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace RemoteAgent;

public partial class MainWindow
{
  private static readonly ConcurrentDictionary<string, SemaphoreSlim> ServiceOperationLocks =
    new(StringComparer.OrdinalIgnoreCase);

  private async Task<OperationResultPayload> ControlServiceAsync(
    ServiceControlPayload request,
    CancellationToken token)
  {
    if (string.IsNullOrWhiteSpace(request.ServiceName))
      throw new InvalidOperationException("服务名称为空。");

    SemaphoreSlim operationLock = ServiceOperationLocks.GetOrAdd(
      request.ServiceName,
      _ => new SemaphoreSlim(1, 1));
    await operationLock.WaitAsync(token);
    try
    {
      return await Task.Run(
        () => ControlServiceCore(request, token),
        token);
    }
    finally
    {
      operationLock.Release();
    }
  }

  private static OperationResultPayload ControlServiceCore(
    ServiceControlPayload request,
    CancellationToken token)
  {
    token.ThrowIfCancellationRequested();
    using var service = new ServiceController(request.ServiceName);
    service.Refresh();

    switch (request.Action)
    {
      case ServiceControlAction.Start:
        if (service.Status == ServiceControllerStatus.Running)
          return new(true, "服务已经在运行。");
        service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        return new(true, "服务已启动。");

      case ServiceControlAction.Stop:
        if (service.Status == ServiceControllerStatus.Stopped)
          return new(true, "服务已经停止。");
        if (!service.CanStop) throw new InvalidOperationException("该服务不接受停止控制。");
        service.Stop();
        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
        return new(true, "服务已停止。");

      case ServiceControlAction.Restart:
        if (service.Status != ServiceControllerStatus.Stopped)
        {
          if (!service.CanStop) throw new InvalidOperationException("该服务不接受停止控制，无法重启。");
          service.Stop();
          service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
        }
        token.ThrowIfCancellationRequested();
        service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        return new(true, "服务已重启。");

      case ServiceControlAction.Pause:
        if (!service.CanPauseAndContinue) throw new InvalidOperationException("该服务不支持暂停。");
        if (service.Status == ServiceControllerStatus.Paused)
          return new(true, "服务已经暂停。");
        service.Pause();
        service.WaitForStatus(ServiceControllerStatus.Paused, TimeSpan.FromSeconds(30));
        return new(true, "服务已暂停。");

      case ServiceControlAction.Continue:
        if (!service.CanPauseAndContinue) throw new InvalidOperationException("该服务不支持继续。");
        if (service.Status == ServiceControllerStatus.Running)
          return new(true, "服务已经在运行。");
        service.Continue();
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        return new(true, "服务已继续运行。");

      case ServiceControlAction.SetStartType:
        SetServiceStartType(request.ServiceName, request.StartType);
        return new(true, "服务启动类型已修改。");

      default:
        throw new InvalidOperationException("不支持的服务操作。");
    }
  }

  private static ServiceDetailsPayload GetServiceDetails(string serviceName)
  {
    using var service = new ServiceController(serviceName);
    service.Refresh();
    string description = string.Empty;
    string executablePath = string.Empty;
    string account = string.Empty;
    int processId = 0;

    try
    {
      string escaped = serviceName.Replace("\\", "\\\\").Replace("'", "\\'");
      using var searcher = new ManagementObjectSearcher(
        $"SELECT Description,PathName,StartName,ProcessId FROM Win32_Service WHERE Name='{escaped}'");
      using ManagementObject? item = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
      if (item is not null)
      {
        description = Convert.ToString(item["Description"]) ?? string.Empty;
        executablePath = Convert.ToString(item["PathName"]) ?? string.Empty;
        account = Convert.ToString(item["StartName"]) ?? string.Empty;
        processId = Convert.ToInt32(item["ProcessId"] ?? 0);
      }
    }
    catch { }

    return new ServiceDetailsPayload(
      service.ServiceName,
      service.DisplayName,
      description,
      service.Status.ToString(),
      ReadServiceStartType(service.ServiceName, service.StartType.ToString()),
      executablePath,
      account,
      processId,
      service.ServicesDependedOn.Select(item => item.DisplayName).ToList(),
      service.DependentServices.Select(item => item.DisplayName).ToList(),
      service.CanStop,
      service.CanPauseAndContinue);
  }

  private static string ReadServiceStartType(string serviceName, string fallback)
  {
    try
    {
      using Microsoft.Win32.RegistryKey? key =
        Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
          $@"SYSTEM\CurrentControlSet\Services\{serviceName}");
      int start = Convert.ToInt32(key?.GetValue("Start", -1));
      int delayed = Convert.ToInt32(key?.GetValue("DelayedAutoStart", 0));
      return start switch
      {
        2 when delayed != 0 => "AutomaticDelayed",
        2 => "Automatic",
        3 => "Manual",
        4 => "Disabled",
        _ => fallback
      };
    }
    catch { return fallback; }
  }

  private static void SetServiceStartType(string serviceName, string startType)
  {
    uint nativeStartType;
    bool delayed;
    switch (startType)
    {
      case "AutomaticDelayed":
        nativeStartType = ServiceAutoStart;
        delayed = true;
        break;
      case "Automatic":
        nativeStartType = ServiceAutoStart;
        delayed = false;
        break;
      case "Manual":
        nativeStartType = ServiceDemandStart;
        delayed = false;
        break;
      case "Disabled":
        nativeStartType = ServiceDisabled;
        delayed = false;
        break;
      default:
        throw new InvalidOperationException("不支持的服务启动类型。");
    }

    IntPtr manager = OpenSCManager(null, null, ScManagerConnect);
    if (manager == IntPtr.Zero) throw new InvalidOperationException(Win32Error("无法打开服务控制管理器"));
    try
    {
      IntPtr service = OpenService(manager, serviceName, ServiceChangeConfig);
      if (service == IntPtr.Zero) throw new InvalidOperationException(Win32Error("无法打开服务配置"));
      try
      {
        if (!ChangeServiceConfig(
              service,
              ServiceNoChange,
              nativeStartType,
              ServiceNoChange,
              null,
              null,
              IntPtr.Zero,
              null,
              null,
              null,
              null))
          throw new InvalidOperationException(Win32Error("修改服务启动类型失败"));

        var delayedInfo = new ServiceDelayedAutoStartInfo { DelayedAutoStart = delayed };
        if (!ChangeServiceConfig2(
              service,
              ServiceConfigDelayedAutoStartInfo,
              ref delayedInfo))
          throw new InvalidOperationException(Win32Error("修改延迟启动设置失败"));
      }
      finally { CloseServiceHandle(service); }
    }
    finally { CloseServiceHandle(manager); }
  }

  private static string Win32Error(string prefix) =>
    $"{prefix}：{new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message}";

  private const uint ScManagerConnect = 0x0001;
  private const uint ServiceChangeConfig = 0x0002;
  private const uint ServiceNoChange = 0xFFFFFFFF;
  private const uint ServiceAutoStart = 2;
  private const uint ServiceDemandStart = 3;
  private const uint ServiceDisabled = 4;
  private const uint ServiceConfigDelayedAutoStartInfo = 3;

  [StructLayout(LayoutKind.Sequential)]
  private struct ServiceDelayedAutoStartInfo
  {
    [MarshalAs(UnmanagedType.Bool)]
    public bool DelayedAutoStart;
  }

  [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern IntPtr OpenSCManager(
    string? machineName,
    string? databaseName,
    uint desiredAccess);

  [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern IntPtr OpenService(
    IntPtr serviceManager,
    string serviceName,
    uint desiredAccess);

  [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool ChangeServiceConfig(
    IntPtr service,
    uint serviceType,
    uint startType,
    uint errorControl,
    string? binaryPathName,
    string? loadOrderGroup,
    IntPtr tagId,
    string? dependencies,
    string? serviceStartName,
    string? password,
    string? displayName);

  [DllImport("advapi32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool ChangeServiceConfig2(
    IntPtr service,
    uint infoLevel,
    ref ServiceDelayedAutoStartInfo info);

  [DllImport("advapi32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool CloseServiceHandle(IntPtr handle);
}
