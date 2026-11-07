namespace OfficeTouchBar.Infrastructure;

// GetForegroundWindow can briefly return NULL during an activation change.
// Keep an existing, live overlay for that gap only; an actual other window
// (including an Office dialog) must take effect immediately.
internal sealed class ForegroundContinuityPolicy
{
    private long _gapStarted = -1;

    internal bool Defer(nint foreground, bool visible, bool targetAlive, long now)
    {
        if (foreground != 0 || !visible || !targetAlive)
        {
            _gapStarted = -1;
            return false;
        }
        if (_gapStarted < 0) _gapStarted = now;
        return now - _gapStarted < 500;
    }
}
