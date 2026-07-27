namespace RemoteControl.Shared;

/// <summary>
/// Keeps predictive video frames in decode order.  Arbitrarily replacing a
/// pending H.264 P-frame makes every dependent frame unusable until the next
/// IDR, so overload is handled by entering key-frame recovery instead.
/// </summary>
public sealed class OrderedVideoFrameBuffer
{
  private readonly object _gate = new();
  private readonly SortedDictionary<long, RemoteVideoFrame> _frames = [];
  private readonly int _capacity;
  private readonly long _reorderGraceMilliseconds;
  private long _lastTakenFrameId;
  private long _gapObservedAt;
  private bool _awaitingKeyFrame = true;
  private bool _recoveryPending;

  public OrderedVideoFrameBuffer(
    int capacity = 90,
    int reorderGraceMilliseconds = 35)
  {
    _capacity = Math.Max(8, capacity);
    _reorderGraceMilliseconds = Math.Max(0, reorderGraceMilliseconds);
  }

  public int Count
  {
    get { lock (_gate) return _frames.Count; }
  }

  public bool AwaitingKeyFrame
  {
    get { lock (_gate) return _awaitingKeyFrame; }
  }

  public bool Enqueue(RemoteVideoFrame frame)
  {
    lock (_gate)
    {
      if (frame.FrameId <= _lastTakenFrameId || _frames.ContainsKey(frame.FrameId))
        return false;

      _frames.Add(frame.FrameId, frame);
      if (_frames.Count <= _capacity) return true;

      EnterRecoveryCore(keepNewestKeyFrame: true);
      return true;
    }
  }

  public bool TryTake(
    long nowMilliseconds,
    out RemoteVideoFrame? frame,
    out bool resetDecoder,
    out bool recoveryRequested)
  {
    lock (_gate)
    {
      frame = null;
      resetDecoder = false;
      recoveryRequested = _recoveryPending;
      _recoveryPending = false;

      if (_frames.Count == 0) return false;

      if (_awaitingKeyFrame)
      {
        KeyValuePair<long, RemoteVideoFrame>? keyFrame = _frames
          .Where(pair => pair.Value.KeyFrame && pair.Key > _lastTakenFrameId)
          .LastOrDefault();
        if (keyFrame is null || keyFrame.Value.Value is null) return false;

        RemoveBefore(keyFrame.Value.Key);
        frame = keyFrame.Value.Value;
        _frames.Remove(keyFrame.Value.Key);
        _lastTakenFrameId = keyFrame.Value.Key;
        _awaitingKeyFrame = false;
        _gapObservedAt = 0;
        resetDecoder = true;
        return true;
      }

      long expected = _lastTakenFrameId + 1;
      if (_frames.Remove(expected, out RemoteVideoFrame? next))
      {
        frame = next;
        _lastTakenFrameId = expected;
        _gapObservedAt = 0;
        return true;
      }

      KeyValuePair<long, RemoteVideoFrame>? replacementKeyFrame = _frames
        .Where(pair => pair.Key > expected && pair.Value.KeyFrame)
        .FirstOrDefault();
      if (replacementKeyFrame is not null && replacementKeyFrame.Value.Value is not null)
      {
        RemoveBefore(replacementKeyFrame.Value.Key);
        frame = replacementKeyFrame.Value.Value;
        _frames.Remove(replacementKeyFrame.Value.Key);
        _lastTakenFrameId = replacementKeyFrame.Value.Key;
        _gapObservedAt = 0;
        resetDecoder = true;
        return true;
      }

      long firstAvailable = _frames.First().Key;
      if (firstAvailable <= expected) return false;
      if (_gapObservedAt == 0)
      {
        _gapObservedAt = nowMilliseconds;
        return false;
      }
      if (nowMilliseconds - _gapObservedAt < _reorderGraceMilliseconds)
        return false;

      EnterRecoveryCore(keepNewestKeyFrame: true);
      recoveryRequested = true;
      return false;
    }
  }

  public void EnterRecovery(bool keepNewestKeyFrame = false)
  {
    lock (_gate) EnterRecoveryCore(keepNewestKeyFrame);
  }

  public void Reset()
  {
    lock (_gate)
    {
      _frames.Clear();
      _lastTakenFrameId = 0;
      _gapObservedAt = 0;
      _awaitingKeyFrame = true;
      _recoveryPending = false;
    }
  }

  private void EnterRecoveryCore(bool keepNewestKeyFrame)
  {
    RemoteVideoFrame? retained = keepNewestKeyFrame
      ? _frames.Values.LastOrDefault(value => value.KeyFrame)
      : null;
    _frames.Clear();
    if (retained is not null && retained.FrameId > _lastTakenFrameId)
      _frames[retained.FrameId] = retained;
    _gapObservedAt = 0;
    _awaitingKeyFrame = true;
    _recoveryPending = true;
  }

  private void RemoveBefore(long frameId)
  {
    foreach (long stale in _frames.Keys.Where(value => value < frameId).ToArray())
      _frames.Remove(stale);
  }
}
