using OfficeTouchBar.Models;

namespace OfficeTouchBar.Infrastructure;

// Tracks a physical contact separately from its temporary WPF capture owner.
internal sealed class InkContactState
{
    private sealed record Contact(long LastSeen, bool CaptureLost);
    private readonly Dictionary<(string Kind, int Id), Contact> _contacts = [];
    private long _lastEnded = long.MinValue;
    internal void Begin(string kind, int id, long now) => _contacts[(kind, id)] = new(now, false);
    internal void Move(string kind, int id, long now)
    {
        if (_contacts.TryGetValue((kind, id), out var contact))
            _contacts[(kind, id)] = contact with { LastSeen = now };
    }
    internal void End(string kind, int id, long now)
    {
        if (_contacts.Remove((kind, id))) _lastEnded = now;
    }
    internal void LostCapture(string kind, int id, long now)
    {
        if (_contacts.ContainsKey((kind, id))) _contacts[(kind, id)] = new(now, true);
    }
    internal void Sweep(long now, Func<string, int, bool?> released)
    {
        foreach (var pair in _contacts.ToArray())
        {
            var isReleased = released(pair.Key.Kind, pair.Key.Id);
            // Up can be handled by WPF or lost outside the HWND. Reconcile all
            // contacts, but never time out a device known to still be down.
            if (isReleased == true || (isReleased is null && pair.Value.CaptureLost
                && now - pair.Value.LastSeen >= 2000))
            {
                End(pair.Key.Kind, pair.Key.Id, now);
                DiagnosticLog.Ink($"contact-reconciled kind={pair.Key.Kind} id={pair.Key.Id} quietMs={now - pair.Value.LastSeen}");
            }
        }
    }
    internal bool HasContacts => _contacts.Count > 0;
    internal bool Contains(string kind, int id) => _contacts.ContainsKey((kind, id));
    internal bool IsBusy(long now) => HasContacts
        || (_lastEnded != long.MinValue && now - _lastEnded < 250);
    internal void Clear() { _contacts.Clear(); _lastEnded = long.MinValue; }
}

internal sealed class InkPageContinuityPolicy
{
    private long _unknownSince = -1;
    private nint _navigationTarget;
    internal void Navigation(nint target) => _navigationTarget = target;
    internal void Confirm() { _unknownSince = -1; _navigationTarget = 0; }
    internal bool ShowLastFrame(nint target, nint activeTarget, bool hasPage, long now)
    {
        if (_unknownSince < 0) _unknownSince = now;
        return hasPage && target == activeTarget && _navigationTarget != target
            && now - _unknownSince < 1200;
    }
    internal static bool SamePage(InkPageKey? left, InkPageKey right) => left is not null
        && left.SlideNumber == right.SlideNumber
        && string.Equals(left.PresentationIdentity, right.PresentationIdentity, StringComparison.OrdinalIgnoreCase);
    internal static bool EndedSessionOwnsCanvas(nint ended, nint active) => ended != 0 && ended == active;
}

internal sealed class InkPreparationRetry
{
    private int _attempts;
    private long _retryAt;
    internal bool CanAttempt(long now) => now >= _retryAt;
    internal void Failed(long now)
    {
        _attempts = Math.Min(5, _attempts + 1);
        _retryAt = now + Math.Min(4000, 350L << Math.Min(_attempts - 1, 4));
    }
    internal void Reset() { _attempts = 0; _retryAt = 0; }
    internal bool Exhausted => _attempts >= 5;
}
