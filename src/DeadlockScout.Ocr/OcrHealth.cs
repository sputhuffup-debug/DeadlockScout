namespace DeadlockScout.Ocr;

/// <summary>Parse success rate over a sliding window; the source is healthy above the configured fraction.</summary>
public sealed class OcrHealth
{
    private readonly Queue<(DateTimeOffset At, bool Ok)> _window = new();
    private readonly TimeSpan _span;
    private readonly double _minHealthy;
    private readonly int _minSamples;

    public OcrHealth(TimeSpan? span = null, double minHealthy = 0.8, int minSamples = 5)
    {
        _span = span ?? TimeSpan.FromSeconds(30);
        _minHealthy = minHealthy;
        _minSamples = minSamples;
    }

    public void Record(DateTimeOffset at, bool ok)
    {
        _window.Enqueue((at, ok));
        while (_window.Count > 0 && at - _window.Peek().At > _span) _window.Dequeue();
    }

    public double SuccessRate => _window.Count == 0 ? 0 : _window.Count(w => w.Ok) / (double)_window.Count;

    public bool IsHealthy => _window.Count >= _minSamples && SuccessRate >= _minHealthy;
}
