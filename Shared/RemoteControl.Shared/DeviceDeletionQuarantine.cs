using System.Collections.Concurrent;

namespace RemoteControl.Shared;

public sealed class DeviceDeletionQuarantine
{
  private readonly ConcurrentDictionary<string, BlockedInstance> _blocked =
    new(StringComparer.OrdinalIgnoreCase);

  public void Block(
    string deviceId,
    Guid instanceId,
    DateTimeOffset expiresAt) =>
    _blocked[deviceId] = new(instanceId, expiresAt);

  public bool ShouldReject(
    string deviceId,
    Guid instanceId,
    DateTimeOffset now)
  {
    if (!_blocked.TryGetValue(deviceId, out BlockedInstance? blocked))
      return false;
    if (blocked.ExpiresAt > now && blocked.InstanceId == instanceId)
      return true;
    _blocked.TryRemove(deviceId, out _);
    return false;
  }

  public void Clear(string deviceId) => _blocked.TryRemove(deviceId, out _);

  private sealed record BlockedInstance(
    Guid InstanceId,
    DateTimeOffset ExpiresAt);
}
