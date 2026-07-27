using RemoteControl.Shared;

namespace RemoteAgent;

internal sealed class AdaptiveVideoController
{
  private const int NativeMaximumWidth = 7680;
  private const int NativeMaximumHeight = 4320;
  private const int DecisionIntervalMilliseconds = 3000;
  private static readonly (int Width, int Height, int Kilobits)[] BitratePresets =
  {
    (640, 480, 400),
    (800, 600, 500),
    (1024, 768, 800),
    (1280, 720, 1000),
    (1366, 768, 1100),
    (1440, 900, 1300),
    (1600, 900, 1500),
    (1920, 1080, 2073),
    (2048, 1080, 2200),
    (2560, 1440, 3000),
    (3440, 1440, 4000),
    (3840, 2160, 5000),
    (7680, 4320, 12000)
  };

  private readonly object _gate = new();
  private long _lastSentFrame;
  private long _lastAckFrame;
  private bool _requestKeyFrame;
  private long _encodeMs;
  private int _lastDecodeErrors;
  private long _lastEvaluatedFeedbackFrame;
  private bool _localNetworkMode;
  private long _networkRttMs;
  private double _averageInterFrameBytes;
  private long _lastSceneKeyFrameRequestAt;
  private long _lastDecisionAt = Environment.TickCount64;
  private int _targetFps = 30;
  private double _qualityRatio = 6.0;
  private int _frameWidth = 1920;
  private int _frameHeight = 1080;
  private int _freshMetricSamples;
  private long _decodeMillisecondsTotal;
  private long _renderMillisecondsTotal;
  private long _maximumDecoderQueue;
  private int _encodedInterFrames;

  public VideoProfile Current
  {
    get
    {
      lock (_gate)
        return CreateProfile(_frameWidth, _frameHeight);
    }
  }

  public VideoProfile ForFrameSize(int width, int height)
  {
    lock (_gate)
    {
      _frameWidth = Math.Clamp(width, 1, NativeMaximumWidth);
      _frameHeight = Math.Clamp(height, 1, NativeMaximumHeight);
      return CreateProfile(_frameWidth, _frameHeight);
    }
  }

  public void SetLocalNetworkMode(bool enabled)
  {
    lock (_gate)
    {
      _localNetworkMode = enabled;
      _targetFps = enabled ? 60 : 30;
      _qualityRatio = enabled ? 9.0 : 6.0;
      _lastAckFrame = _lastSentFrame;
      _encodeMs = 0;
      _networkRttMs = 0;
      _averageInterFrameBytes = 0;
      _lastSceneKeyFrameRequestAt = 0;
      ResetDecisionWindow(Environment.TickCount64);
      _requestKeyFrame = true;
    }
  }

  public void OnEncoded(
    long frameId,
    long milliseconds,
    bool keyFrame,
    int encodedBytes)
  {
    lock (_gate)
    {
      if (keyFrame) return;
      _encodedInterFrames++;
      _encodeMs = _encodeMs == 0
        ? milliseconds
        : (long)Math.Round(_encodeMs * 0.75 + milliseconds * 0.25);

      double baseline = _averageInterFrameBytes;
      long now = Environment.TickCount64;
      bool largeSceneChange =
        encodedBytes >= Math.Max(96 * 1024, baseline * 2.0) &&
        now - _lastSceneKeyFrameRequestAt >= 500;
      if (largeSceneChange)
      {
        _requestKeyFrame = true;
        _lastSceneKeyFrameRequestAt = now;
      }
      _averageInterFrameBytes = baseline <= 0
        ? encodedBytes
        : baseline * 0.90 + encodedBytes * 0.10;
    }
  }

  public void OnSent(long frameId, int bytes, long milliseconds)
  {
    lock (_gate) _lastSentFrame = frameId;
  }

  public void OnNetworkSample(long roundTripMilliseconds)
  {
    if (roundTripMilliseconds < 0) return;
    lock (_gate)
      _networkRttMs = _networkRttMs == 0
        ? roundTripMilliseconds
        : (long)Math.Round(_networkRttMs * 0.75 + roundTripMilliseconds * 0.25);
  }

  public void OnFeedback(VideoFeedbackPacket feedback)
  {
    lock (_gate)
    {
      _lastAckFrame = Math.Max(_lastAckFrame, feedback.LastReceivedFrameId);
      bool newDecodeError = feedback.DecodeErrors > _lastDecodeErrors;
      _lastDecodeErrors = Math.Max(_lastDecodeErrors, feedback.DecodeErrors);
      bool hasFreshFrameMetrics =
        feedback.LastRenderedFrameId > _lastEvaluatedFeedbackFrame;
      if (hasFreshFrameMetrics)
        _lastEvaluatedFeedbackFrame = feedback.LastRenderedFrameId;
      if (feedback.RequestKeyFrame || newDecodeError)
        _requestKeyFrame = true;

      if (hasFreshFrameMetrics)
      {
        _freshMetricSamples++;
        _decodeMillisecondsTotal += Math.Max(0, feedback.DecodeMilliseconds);
        _renderMillisecondsTotal += Math.Max(0, feedback.RenderMilliseconds);
        _maximumDecoderQueue = Math.Max(
          _maximumDecoderQueue,
          Math.Max(
            0,
            feedback.LastReceivedFrameId - feedback.LastRenderedFrameId));
      }

      long now = Environment.TickCount64;
      if (now - _lastDecisionAt >= DecisionIntervalMilliseconds)
        ApplyRustDeskStyleDecision(now);
    }
  }

  public bool ConsumeKeyFrameRequest()
  {
    lock (_gate)
    {
      bool value = _requestKeyFrame;
      _requestKeyFrame = false;
      return value;
    }
  }

  public void ResetForNewSession()
  {
    lock (_gate)
    {
      _lastAckFrame = _lastSentFrame;
      _targetFps = _localNetworkMode ? 60 : 30;
      _qualityRatio = _localNetworkMode ? 9.0 : 6.0;
      _encodeMs = 0;
      _networkRttMs = 0;
      _averageInterFrameBytes = 0;
      _lastSceneKeyFrameRequestAt = 0;
      _lastDecodeErrors = 0;
      _lastEvaluatedFeedbackFrame = 0;
      ResetDecisionWindow(Environment.TickCount64);
      _requestKeyFrame = true;
    }
  }

  private void ApplyRustDeskStyleDecision(long now)
  {
    int preferredFps = _localNetworkMode ? 60 : 30;
    double averageDecode = _freshMetricSamples == 0
      ? 0
      : _decodeMillisecondsTotal / (double)_freshMetricSamples;
    double averageRender = _freshMetricSamples == 0
      ? 0
      : _renderMillisecondsTotal / (double)_freshMetricSamples;
    double slowestStage = Math.Max(
      Math.Max(averageDecode, averageRender),
      _encodeMs);
    bool decoderPressure =
      _maximumDecoderQueue > Math.Max(2, _targetFps / 4) ||
      slowestStage > 1000.0 / Math.Max(1, _targetFps);

    int networkLimitedFps = preferredFps;
    if (_networkRttMs >= 500)
      networkLimitedFps = Math.Max(12, preferredFps / 2);
    else if (_networkRttMs >= 300)
      networkLimitedFps = Math.Max(15, preferredFps * 2 / 3);
    else if (_networkRttMs >= 200)
      networkLimitedFps = Math.Max(18, preferredFps * 4 / 5);
    else if (_networkRttMs >= 150)
      networkLimitedFps = Math.Max(20, preferredFps * 9 / 10);

    int desiredFps = networkLimitedFps;
    if (decoderPressure && slowestStage > 0)
    {
      int decoderCapacity = (int)Math.Floor(1000.0 / slowestStage);
      desiredFps = Math.Min(
        desiredFps,
        Math.Max(12, decoderCapacity * 9 / 10));
    }

    if (desiredFps < _targetFps)
      _targetFps = Math.Max(desiredFps, _targetFps - 8);
    else if (desiredFps > _targetFps)
      _targetFps = Math.Min(desiredFps, _targetFps + 3);

    long backlog = Math.Max(0, _lastSentFrame - _lastAckFrame);
    bool dynamicScreen = _encodedInterFrames >= 6;
    double factor = 1.0;
    if (backlog > Math.Max(8, _targetFps / 2))
      factor = 0.85;
    else if (_networkRttMs >= 500)
      factor = 0.80;
    else if (_networkRttMs >= 300)
      factor = 0.85;
    else if (_networkRttMs >= 200)
      factor = 0.90;
    else if (_networkRttMs >= 150)
      factor = 0.95;
    else if (dynamicScreen && _networkRttMs is > 0 and < 50)
      factor = 1.15;
    else if (dynamicScreen && _networkRttMs < 100)
      factor = 1.10;
    else if (dynamicScreen && _networkRttMs < 150)
      factor = 1.05;

    double minimumRatio = _localNetworkMode ? 3.0 : 1.2;
    double maximumRatio = _localNetworkMode ? 9.0 : 7.0;
    _qualityRatio = Math.Clamp(
      _qualityRatio * factor,
      minimumRatio,
      maximumRatio);
    ResetDecisionWindow(now);
  }

  private VideoProfile CreateProfile(int width, int height)
  {
    int bitrate = checked((int)Math.Clamp(
      Math.Round(BaseBitrateKilobits(width, height) * 1000.0 * _qualityRatio),
      2_000_000,
      60_000_000));
    return new(
      "Native adaptive",
      NativeMaximumWidth,
      NativeMaximumHeight,
      _targetFps,
      bitrate);
  }

  private static int BaseBitrateKilobits(int width, int height)
  {
    long pixels = Math.Max(1L, (long)width * height);
    var nearest = BitratePresets.MinBy(preset =>
      Math.Abs((long)preset.Width * preset.Height - pixels));
    long presetPixels = Math.Max(1L, (long)nearest.Width * nearest.Height);
    return Math.Max(
      400,
      (int)Math.Round(nearest.Kilobits * pixels / (double)presetPixels));
  }

  private void ResetDecisionWindow(long now)
  {
    _lastDecisionAt = now;
    _freshMetricSamples = 0;
    _decodeMillisecondsTotal = 0;
    _renderMillisecondsTotal = 0;
    _maximumDecoderQueue = 0;
    _encodedInterFrames = 0;
  }
}
