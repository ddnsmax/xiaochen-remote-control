using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using RemoteControl.Shared;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace RemoteAgent;

public partial class MainWindow
{
  private TcpClient? _terminalClient;
  private TcpClient? _registryClient;

  private async Task TerminalConnectLoopAsync(string host, int port, CancellationToken token)
  {
    while (!token.IsCancellationRequested && _stream is not null)
    {
      TcpClient? client = null;
      try
      {
        client = new TcpClient { NoDelay = true, SendBufferSize = 64 * 1024 };
        await client.ConnectAsync(host, port, token);
        _terminalClient = client;
        NetworkStream stream = client.GetStream();
        await WriteLogicalChannelHelloAsync(stream, LogicalChannelType.Terminal, token);
        await BinaryTerminalProtocol.WriteHelloAsync(stream, _deviceId, token);
        await RunTerminalChannelAsync(stream, token);
      }
      catch (OperationCanceledException) { break; }
      catch (Exception)
      {
        await Task.Delay(TimeSpan.FromSeconds(2), token).ContinueWith(_ => { });
      }
      finally
      {
        try { client?.Close(); } catch { }
        if (ReferenceEquals(_terminalClient, client)) _terminalClient = null;
      }
    }
  }

  private async Task RunTerminalChannelAsync(NetworkStream stream, CancellationToken token)
  {
    var sendLock = new SemaphoreSlim(1, 1);
    var commands = new ConcurrentDictionary<string, StreamingCommand>();
    var commandTasks = new ConcurrentDictionary<string, Task>();

    async Task SendAsync(TerminalPacket packet, CancellationToken sendToken)
    {
      await sendLock.WaitAsync(sendToken);
      try { await BinaryTerminalProtocol.WriteAsync(stream, packet, sendToken); }
      finally { sendLock.Release(); }
    }

    try
    {
      while (!token.IsCancellationRequested)
      {
        TerminalPacket? packet = await BinaryTerminalProtocol.ReadAsync(stream, token);
        if (packet is null) break;

        if (packet.Type == TerminalPacketType.Start)
        {
          var command = new StreamingCommand(packet, SendAsync);
          if (!commands.TryAdd(packet.CommandId, command))
          {
            await SendAsync(FailedTerminalPacket(packet.CommandId, "命令标识重复。"), token);
            continue;
          }

          Task running = Task.Run(async () =>
          {
            try { await command.RunAsync(token); }
            finally
            {
              commands.TryRemove(packet.CommandId, out _);
              commandTasks.TryRemove(packet.CommandId, out _);
              command.Dispose();
            }
          }, CancellationToken.None);
          commandTasks[packet.CommandId] = running;
        }
        else if (packet.Type == TerminalPacketType.Cancel &&
                 commands.TryGetValue(packet.CommandId, out StreamingCommand? command))
        {
          command.Cancel();
        }
        else if (packet.Type == TerminalPacketType.Cancel)
        {
          await SendAsync(
            new TerminalPacket(
              TerminalPacketType.Cancelled,
              packet.CommandId,
              string.Empty,
              string.Empty,
              string.Empty,
              0,
              "任务已经结束或不属于当前终端连接。",
              -1,
              DateTime.UtcNow.Ticks),
            token);
        }
        else if (packet.Type == TerminalPacketType.Ping)
        {
          await SendAsync(packet with
          {
            Type = TerminalPacketType.Pong,
            TimestampUtcTicks = DateTime.UtcNow.Ticks
          }, token);
        }
      }
    }
    finally
    {
      foreach (StreamingCommand command in commands.Values) command.Cancel();
      try { await Task.WhenAll(commandTasks.Values); } catch { }
      foreach (StreamingCommand command in commands.Values) command.Dispose();
      sendLock.Dispose();
    }
  }

  private static TerminalPacket FailedTerminalPacket(string commandId, string message) =>
    new(
      TerminalPacketType.Failed,
      commandId,
      string.Empty,
      string.Empty,
      string.Empty,
      0,
      message,
      -1,
      DateTime.UtcNow.Ticks);

  private async Task RegistryConnectLoopAsync(string host, int port, CancellationToken token)
  {
    while (!token.IsCancellationRequested && _stream is not null)
    {
      TcpClient? client = null;
      try
      {
        client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(host, port, token);
        _registryClient = client;
        NetworkStream stream = client.GetStream();
        await WriteLogicalChannelHelloAsync(stream, LogicalChannelType.Registry, token);
        await FramedJsonTransport.WriteAsync(
          stream,
          new RemoteMessage
          {
            Type = MessageType.Hello,
            DeviceId = _deviceId,
            DeviceName = Environment.MachineName,
            Payload = MessagePayload.ToElement(new HelloPayload(
              _deviceId,
              Environment.MachineName,
              WindowsAgentEnvironment.GetInteractiveUserName(),
              Environment.MachineName,
              Environment.OSVersion.ToString(),
              "3.2.0-streaming-terminal-registry",
              ProtocolVersions.Current))
          },
          token);
        await RunRegistryChannelAsync(stream, token);
      }
      catch (OperationCanceledException) { break; }
      catch (Exception)
      {
        await Task.Delay(TimeSpan.FromSeconds(2), token).ContinueWith(_ => { });
      }
      finally
      {
        try { client?.Close(); } catch { }
        if (ReferenceEquals(_registryClient, client)) _registryClient = null;
      }
    }
  }

  private async Task RunRegistryChannelAsync(NetworkStream stream, CancellationToken token)
  {
    var writeLock = new SemaphoreSlim(1, 1);
    CancellationTokenSource? watchCts = null;

    async Task ReplyAsync(RemoteMessage request, MessageType type, object payload)
    {
      await writeLock.WaitAsync(token);
      try
      {
        await FramedJsonTransport.WriteAsync(
          stream,
          new RemoteMessage
          {
            RequestId = request.RequestId,
            Type = type,
            DeviceId = _deviceId,
            DeviceName = Environment.MachineName,
            Payload = MessagePayload.ToElement(payload)
          },
          token);
      }
      finally { writeLock.Release(); }
    }

    try
    {
      while (!token.IsCancellationRequested)
      {
        RemoteMessage? request = await FramedJsonTransport.ReadAsync(stream, token);
        if (request is null) break;
        try
        {
          // Registry mutations and watch changes are state transitions. Keep
          // them in receive order so create/rename/delete cannot overtake one
          // another on the same connection.
          switch (request.Type)
          {
            case MessageType.RegistryReadRequest:
              await ReplyAsync(
                request,
                MessageType.RegistryReadResponse,
                ReadRegistry(request.Payload.As<RegistryReadPayload>()
                  ?? throw new InvalidOperationException("注册表读取参数无效。")));
              break;

            case MessageType.RegistryMutationRequest:
              RegistryMutationPayload mutation = request.Payload.As<RegistryMutationPayload>()
                ?? throw new InvalidOperationException("注册表修改参数无效。");
              await ReplyAsync(
                request,
                MessageType.RegistryMutationResponse,
                MutateRegistry(mutation));
              break;

            case MessageType.RegistryWatchRequest:
              RegistryWatchPayload watch = request.Payload.As<RegistryWatchPayload>()
                ?? throw new InvalidOperationException("注册表监视参数无效。");
              watchCts?.Cancel();
              watchCts?.Dispose();
              watchCts = CancellationTokenSource.CreateLinkedTokenSource(token);
              await ReplyAsync(
                request,
                MessageType.RegistryWatchRequest,
                new OperationResultPayload(true, "注册表监视已更新"));
              CancellationToken watchToken = watchCts.Token;
              _ = Task.Run(
                () => PollRegistryChangesAsync(stream, writeLock, watch, watchToken),
                CancellationToken.None);
              break;
          }
        }
        catch (Exception ex)
        {
          try { await ReplyAsync(request, MessageType.Error, new ErrorPayload(ex.Message)); }
          catch { }
        }
      }
    }
    finally
    {
      watchCts?.Cancel();
      watchCts?.Dispose();
      writeLock.Dispose();
    }
  }

  private async Task PollRegistryChangesAsync(
    NetworkStream stream,
    SemaphoreSlim writeLock,
    RegistryWatchPayload watch,
    CancellationToken token)
  {
    string previous = RegistrySnapshotFingerprint(watch);
    while (!token.IsCancellationRequested)
    {
      await Task.Delay(TimeSpan.FromSeconds(1), token);
      string current;
      try { current = RegistrySnapshotFingerprint(watch); }
      catch { continue; }
      if (string.Equals(previous, current, StringComparison.Ordinal)) continue;
      previous = current;

      await writeLock.WaitAsync(token);
      try
      {
        await FramedJsonTransport.WriteAsync(
          stream,
          new RemoteMessage
          {
            Type = MessageType.RegistryChanged,
            DeviceId = _deviceId,
            DeviceName = Environment.MachineName,
            Payload = MessagePayload.ToElement(
              new RegistryChangedPayload(watch.Hive, watch.SubKey, watch.View))
          },
          token);
      }
      finally { writeLock.Release(); }
    }
  }

  private static string RegistrySnapshotFingerprint(RegistryWatchPayload watch)
  {
    RegistryReadResponsePayload snapshot =
      ReadRegistry(new RegistryReadPayload(watch.Hive, watch.SubKey, watch.View));
    var builder = new StringBuilder();
    foreach (string key in snapshot.SubKeys) builder.Append("K:").Append(key).Append('\n');
    foreach (RegistryValuePayload value in snapshot.Values)
      builder.Append("V:").Append(value.Name).Append('|').Append(value.Type).Append('|').Append(value.Data).Append('\n');
    return Convert.ToHexString(
      System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
  }

  private static OperationResultPayload MutateRegistry(RegistryMutationPayload mutation)
  {
    using RegistryKey hive = OpenRegistryHive(mutation.Hive, mutation.View);
    switch (mutation.Kind)
    {
      case RegistryMutationKind.CreateKey:
      {
        using RegistryKey parent = OpenWritableKey(hive, mutation.SubKey);
        using RegistryKey? created = parent.CreateSubKey(
          RequireRegistryName(mutation.Name),
          writable: true);
        return new(created is not null, created is null ? "创建注册表项失败。" : "注册表项已创建。");
      }

      case RegistryMutationKind.RenameKey:
      {
        using RegistryKey parent = OpenWritableKey(hive, mutation.SubKey);
        string oldName = RequireRegistryName(mutation.Name);
        string newName = RequireRegistryName(mutation.NewName);
        int result = RegRenameKey(parent.Handle, oldName, newName);
        if (result != 0) throw new InvalidOperationException(new System.ComponentModel.Win32Exception(result).Message);
        return new(true, "注册表项已重命名。");
      }

      case RegistryMutationKind.DeleteKey:
      {
        using RegistryKey parent = OpenWritableKey(hive, mutation.SubKey);
        parent.DeleteSubKeyTree(RequireRegistryName(mutation.Name), throwOnMissingSubKey: true);
        return new(true, "注册表项已删除。");
      }

      case RegistryMutationKind.CreateValue:
      case RegistryMutationKind.SetValue:
      {
        using RegistryKey key = OpenWritableKey(hive, mutation.SubKey);
        RegistryValueKind kind = ParseRegistryValueKind(mutation.ValueKind);
        key.SetValue(
          NormalizeRegistryValueName(mutation.Name),
          MaterializeRegistryValue(mutation, kind),
          kind);
        return new(true, mutation.Kind == RegistryMutationKind.CreateValue
          ? "注册表值已创建。"
          : "注册表值已修改。");
      }

      case RegistryMutationKind.RenameValue:
      {
        using RegistryKey key = OpenWritableKey(hive, mutation.SubKey);
        string oldName = NormalizeRegistryValueName(mutation.Name);
        string newName = NormalizeRegistryValueName(mutation.NewName);
        RegistryValueKind kind = key.GetValueKind(oldName);
        object? value = key.GetValue(oldName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null) throw new InvalidOperationException("注册表值不存在。");
        if (!string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase) &&
            key.GetValue(newName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not null)
          throw new InvalidOperationException("目标注册表值名称已存在。");
        key.SetValue(newName, value, kind);
        if (!string.Equals(oldName, newName, StringComparison.Ordinal)) key.DeleteValue(oldName, true);
        return new(true, "注册表值已重命名。");
      }

      case RegistryMutationKind.DeleteValue:
      {
        using RegistryKey key = OpenWritableKey(hive, mutation.SubKey);
        key.DeleteValue(NormalizeRegistryValueName(mutation.Name), true);
        return new(true, "注册表值已删除。");
      }

      default:
        throw new InvalidOperationException("不支持的注册表操作。");
    }
  }

  private static RegistryKey OpenWritableKey(RegistryKey hive, string subKey) =>
    string.IsNullOrWhiteSpace(subKey)
      ? hive
      : hive.OpenSubKey(subKey, writable: true)
        ?? throw new InvalidOperationException("注册表项不存在或没有写入权限。");

  private static string RequireRegistryName(string name)
  {
    string result = name.Trim();
    if (result.Length == 0 || result.Contains('\\'))
      throw new InvalidOperationException("注册表项名称无效。");
    return result;
  }

  private static string NormalizeRegistryValueName(string name) => name;

  private static RegistryValueKind ParseRegistryValueKind(string kind) =>
    Enum.TryParse(kind, ignoreCase: true, out RegistryValueKind parsed)
      ? parsed
      : throw new InvalidOperationException("注册表值类型无效。");

  private static object MaterializeRegistryValue(
    RegistryMutationPayload mutation,
    RegistryValueKind kind) => kind switch
  {
    RegistryValueKind.String or RegistryValueKind.ExpandString =>
      mutation.StringValue ?? string.Empty,
    RegistryValueKind.MultiString =>
      mutation.MultiStringValue?.ToArray() ?? [],
    RegistryValueKind.Binary or RegistryValueKind.None =>
      mutation.BinaryValue ?? [],
    RegistryValueKind.DWord =>
      unchecked((int)(uint)(mutation.IntegerValue ?? 0)),
    RegistryValueKind.QWord =>
      mutation.IntegerValue ?? 0,
    _ => mutation.StringValue ?? string.Empty
  };

  [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
  private static extern int RegRenameKey(
    SafeRegistryHandle hKey,
    string? lpSubKeyName,
    string lpNewKeyName);

  private void CloseDedicatedChannels()
  {
    try { _terminalClient?.Close(); } catch { }
    try { _registryClient?.Close(); } catch { }
    _terminalClient = null;
    _registryClient = null;
  }

  private sealed class StreamingCommand : IDisposable
  {
    private readonly TerminalPacket _request;
    private readonly Func<TerminalPacket, CancellationToken, Task> _send;
    private readonly CancellationTokenSource _cancel = new();
    private Process? _process;
    private IntPtr _jobHandle;
    private long _sequence;
    private int _cancelRequested;

    public StreamingCommand(
      TerminalPacket request,
      Func<TerminalPacket, CancellationToken, Task> send)
    {
      _request = request;
      _send = send;
    }

    public async Task RunAsync(CancellationToken channelToken)
    {
      using var linked = CancellationTokenSource.CreateLinkedTokenSource(channelToken, _cancel.Token);
      CancellationToken token = linked.Token;
      try
      {
        token.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = CreateStartInfo(_request) };
        _process = process;
        if (!process.Start()) throw new InvalidOperationException("命令进程启动失败。");
        AttachKillOnCloseJob(process);

        await SendAsync(TerminalPacketType.Started, string.Empty, 0, token);
        Task stdout = PumpAsync(
          process.StandardOutput,
          TerminalPacketType.StandardOutput,
          token);
        Task stderr = PumpAsync(
          process.StandardError,
          TerminalPacketType.StandardError,
          token);

        await process.WaitForExitAsync(token);
        await Task.WhenAll(stdout, stderr);
        await SendAsync(TerminalPacketType.Completed, string.Empty, process.ExitCode, token);
      }
      catch (OperationCanceledException)
      {
        KillProcessTree();
        try
        {
          await SendAsync(
            TerminalPacketType.Cancelled,
            "命令已终止。",
            -1,
            channelToken);
        }
        catch { }
      }
      catch (Exception ex)
      {
        KillProcessTree();
        try
        {
          await SendAsync(
            TerminalPacketType.Failed,
            ex.Message,
            -1,
            channelToken);
        }
        catch { }
      }
      finally
      {
        CloseProcessJob();
        _process = null;
      }
    }

    public void Cancel()
    {
      if (Interlocked.Exchange(ref _cancelRequested, 1) != 0) return;
      _cancel.Cancel();
      KillProcessTree();
    }

    private async Task PumpAsync(
      StreamReader reader,
      TerminalPacketType type,
      CancellationToken token)
    {
      char[] buffer = new char[4096];
      while (!token.IsCancellationRequested)
      {
        int read = await reader.ReadAsync(buffer.AsMemory(), token);
        if (read == 0) break;
        await SendAsync(type, new string(buffer, 0, read), 0, token);
      }
    }

    private Task SendAsync(
      TerminalPacketType type,
      string text,
      int exitCode,
      CancellationToken token) =>
      _send(
        new TerminalPacket(
          type,
          _request.CommandId,
          _request.Shell,
          _request.Command,
          _request.WorkingDirectory,
          Interlocked.Increment(ref _sequence),
          text,
          exitCode,
          DateTime.UtcNow.Ticks),
        token);

    private static ProcessStartInfo CreateStartInfo(TerminalPacket request)
    {
      bool cmd = string.Equals(request.Shell, "CMD", StringComparison.OrdinalIgnoreCase);
      var start = new ProcessStartInfo
      {
        FileName = cmd ? "cmd.exe" : "powershell.exe",
        WorkingDirectory = Directory.Exists(request.WorkingDirectory)
          ? request.WorkingDirectory
          : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        RedirectStandardInput = false,
        UseShellExecute = false,
        CreateNoWindow = true,
        StandardOutputEncoding = new UTF8Encoding(false),
        StandardErrorEncoding = new UTF8Encoding(false)
      };

      if (cmd)
      {
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/s");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("chcp 65001>nul & " + request.Command);
      }
      else
      {
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(
          "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);" +
          "$OutputEncoding=[Console]::OutputEncoding;" +
          request.Command);
      }

      return start;
    }

    private void KillProcessTree()
    {
      CloseProcessJob();
      try
      {
        if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
      }
      catch { }
    }

    private void AttachKillOnCloseJob(Process process)
    {
      IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
      if (job == IntPtr.Zero) return;
      try
      {
        var information = new JobObjectExtendedLimitInformation
        {
          BasicLimitInformation = new JobObjectBasicLimitInformation
          {
            LimitFlags = JobObjectLimitKillOnJobClose
          }
        };
        int length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        IntPtr buffer = Marshal.AllocHGlobal(length);
        try
        {
          Marshal.StructureToPtr(information, buffer, false);
          if (!SetInformationJobObject(
                job,
                JobObjectInfoClass.ExtendedLimitInformation,
                buffer,
                (uint)length) ||
              !AssignProcessToJobObject(job, process.Handle))
            return;
          _jobHandle = job;
          job = IntPtr.Zero;
        }
        finally
        {
          Marshal.FreeHGlobal(buffer);
        }
      }
      finally
      {
        if (job != IntPtr.Zero) CloseHandle(job);
      }
    }

    private void CloseProcessJob()
    {
      IntPtr job = Interlocked.Exchange(ref _jobHandle, IntPtr.Zero);
      if (job != IntPtr.Zero) CloseHandle(job);
    }

    public void Dispose()
    {
      KillProcessTree();
      _cancel.Dispose();
    }

    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private enum JobObjectInfoClass
    {
      ExtendedLimitInformation = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
      public long PerProcessUserTimeLimit;
      public long PerJobUserTimeLimit;
      public uint LimitFlags;
      public UIntPtr MinimumWorkingSetSize;
      public UIntPtr MaximumWorkingSetSize;
      public uint ActiveProcessLimit;
      public UIntPtr Affinity;
      public uint PriorityClass;
      public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
      public ulong ReadOperationCount;
      public ulong WriteOperationCount;
      public ulong OtherOperationCount;
      public ulong ReadTransferCount;
      public ulong WriteTransferCount;
      public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
      public JobObjectBasicLimitInformation BasicLimitInformation;
      public IoCounters IoInfo;
      public UIntPtr ProcessMemoryLimit;
      public UIntPtr JobMemoryLimit;
      public UIntPtr PeakProcessMemoryUsed;
      public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(
      IntPtr jobAttributes,
      string? name);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
      IntPtr job,
      JobObjectInfoClass informationClass,
      IntPtr information,
      uint informationLength);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(
      IntPtr job,
      IntPtr process);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
  }
}
