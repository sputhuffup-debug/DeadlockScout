namespace DeadlockScout.Core.Input;

/// <summary>
/// Turns key presses into double taps: a press within <see cref="Window"/> of the one before is a double tap; a slower
/// one starts over. After a double tap the next press starts a new pair, so tapping three times toggles once, not
/// twice. Only presses count (the key going down), so holding the key is one press. Pure, so tested.
/// </summary>
public sealed class DoubleTap
{
    private DateTimeOffset? _first;

    public DoubleTap(TimeSpan window) => Window = window;

    /// <summary>How quickly the second tap must follow the first.</summary>
    public TimeSpan Window { get; }

    /// <summary>The key went down at <paramref name="at"/>; true when that makes a double tap.</summary>
    public bool Press(DateTimeOffset at)
    {
        if (_first is { } first && at - first <= Window && at >= first)
        {
            _first = null;
            return true;
        }
        _first = at;
        return false;
    }

    /// <summary>Forgets a half-made pair (the game lost focus between taps).</summary>
    public void Reset() => _first = null;
}
