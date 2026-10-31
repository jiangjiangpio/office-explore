namespace OfficeTouchBar.Infrastructure;

internal enum ExitResult { Cancelled, Failed, Sent }

internal sealed class ExitTransitionPolicy
{
    private nint _target;
    private DateTime _resumeAfter;
    private bool _allowResume;

    internal void Begin(nint target, DateTime now, bool allowResume = true)
    {
        _target = target;
        _resumeAfter = now.AddMilliseconds(1200);
        _allowResume = allowResume;
    }

    internal void Cancel() => _target = 0;

    internal ExitResult TryExit(Func<bool> confirm, Action prepare,
        Func<bool> sendEscape, Action afterSent, nint target, DateTime now, bool allowResume = true)
    {
        if (!confirm()) return ExitResult.Cancelled;
        // COM and modal dialogs may pump the dispatcher during the command.
        // Suppress stale snapshots before any hiding or native exit operation.
        Begin(target, now, allowResume);
        prepare();
        if (!sendEscape())
        {
            Cancel();
            return ExitResult.Failed;
        }
        afterSent();
        return ExitResult.Sent;
    }

    internal bool IsWaiting(nint foreground, DateTime now, Func<nint, bool> isSlideShow)
    {
        if (_target == 0) return false;
        if (!isSlideShow(_target) || (_allowResume && foreground == _target && now >= _resumeAfter))
        {
            Cancel();
            return false;
        }
        return true;
    }
}
