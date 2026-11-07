using System.Collections.Concurrent;
using System.Windows.Input.StylusPlugIns;

namespace OfficeTouchBar.Infrastructure;

// The pen-input thread can start rendering before PreviewStylusDown reaches
// the Dispatcher. This observer never touches WPF visuals or changes packets.
internal sealed class InkContactObserver : StylusPlugIn
{
    private sealed record Contact(long LastSeen, bool Left, long Sequence, int Timestamp);
    private sealed record Verification(int Id, long Sequence);
    private readonly ConcurrentDictionary<int, Contact> _contacts = new();
    private readonly Func<long> _clock;
    private int _acceptInput;
    private long _lastMoveLog;
    private long _sequence;
    internal InkContactObserver(Func<long>? clock = null) =>
        _clock = clock ?? (() => Environment.TickCount64);
    internal void SetInputEnabled(bool value)
    {
        Volatile.Write(ref _acceptInput, value ? 1 : 0);
        if (!value) _contacts.Clear();
    }
    internal void Clear() => _contacts.Clear();
    internal void SuspectCaptureLoss()
    {
        foreach (var pair in _contacts)
            _contacts.TryUpdate(pair.Key, pair.Value with { Left = true, LastSeen = _clock() }, pair.Value);
    }
    internal void ReconcileReleased(Func<int, bool> released)
    {
        var now = _clock();
        foreach (var pair in _contacts)
        {
            // Allow routed device state to catch up with the input thread.
            // Conditional removal preserves any newer Down/Move for this ID.
            if (now - pair.Value.LastSeen >= 50 && released(pair.Key))
                ((ICollection<KeyValuePair<int, Contact>>)_contacts).Remove(pair);
        }
    }
    internal bool HasContacts
    {
        get
        {
            if (Volatile.Read(ref _acceptInput) == 0) return false;
            foreach (var pair in _contacts)
                if (pair.Value.Left && _clock() - pair.Value.LastSeen >= 2000)
                    ((ICollection<KeyValuePair<int, Contact>>)_contacts).Remove(pair);
            return !_contacts.IsEmpty;
        }
    }
    internal void ObserveDown(int id, int timestamp) => RecordDown(id, timestamp);
    private long RecordDown(int id, int timestamp)
    {
        if (Volatile.Read(ref _acceptInput) == 0) return 0;
        var sequence = Interlocked.Increment(ref _sequence);
        _contacts[id] = new(_clock(), false, sequence, timestamp);
        if (Volatile.Read(ref _acceptInput) == 0) _contacts.TryRemove(id, out _);
        DiagnosticLog.Ink($"raw-down id={id} inputTimestamp={timestamp}");
        return sequence;
    }
    internal void ObserveUp(int id, int timestamp)
    {
        // A routed Up may arrive after the input thread has started the next
        // stroke with the same device ID. Never erase that newer contact.
        if (_contacts.TryGetValue(id, out var contact) && unchecked(timestamp - contact.Timestamp) >= 0
            && ((ICollection<KeyValuePair<int, Contact>>)_contacts).Remove(new(id, contact)))
            DiagnosticLog.Ink($"raw-up id={id} inputTimestamp={timestamp}");
    }
    protected override void OnStylusDown(RawStylusInput input)
    {
        try
        {
            var sequence = RecordDown(input.StylusDeviceId, input.Timestamp);
            if (sequence != 0) input.NotifyWhenProcessed(new Verification(input.StylusDeviceId, sequence));
        }
        catch (Exception ex) { DiagnosticLog.Write($"raw down observer failed: {ex.GetType().Name}"); }
    }
    protected override void OnStylusDownProcessed(object data, bool targetVerified)
    {
        if (!targetVerified && data is Verification verification
            && _contacts.TryGetValue(verification.Id, out var contact) && contact.Sequence == verification.Sequence)
        {
            ((ICollection<KeyValuePair<int, Contact>>)_contacts).Remove(new(verification.Id, contact));
            DiagnosticLog.Ink($"raw-target-rejected id={verification.Id}");
        }
    }
    protected override void OnStylusMove(RawStylusInput input)
    {
        try
        {
            var now = _clock();
            if (_contacts.TryGetValue(input.StylusDeviceId, out var contact))
            {
                _contacts.TryUpdate(input.StylusDeviceId, contact with { LastSeen = now, Left = false }, contact);
                if (now - Interlocked.Read(ref _lastMoveLog) >= 250)
                {
                    Interlocked.Exchange(ref _lastMoveLog, now);
                    DiagnosticLog.Ink($"raw-move id={input.StylusDeviceId} inputTimestamp={input.Timestamp}");
                }
            }
        }
        catch (Exception ex) { DiagnosticLog.Write($"raw move observer failed: {ex.GetType().Name}"); }
    }
    protected override void OnStylusUp(RawStylusInput input)
    {
        try { ObserveUp(input.StylusDeviceId, input.Timestamp); }
        catch (Exception ex) { DiagnosticLog.Write($"raw up observer failed: {ex.GetType().Name}"); }
    }
    protected override void OnStylusLeave(RawStylusInput input, bool confirmed)
    {
        try
        {
            if (confirmed && _contacts.TryGetValue(input.StylusDeviceId, out var contact))
            {
                _contacts.TryUpdate(input.StylusDeviceId, contact with { Left = true, LastSeen = _clock() }, contact);
                DiagnosticLog.Ink($"raw-leave id={input.StylusDeviceId} inputTimestamp={input.Timestamp}");
            }
        }
        catch (Exception ex) { DiagnosticLog.Write($"raw leave observer failed: {ex.GetType().Name}"); }
    }
    protected override void OnRemoved() => Clear();
}
