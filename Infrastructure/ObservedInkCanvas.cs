using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Threading;

namespace OfficeTouchBar.Infrastructure;

public sealed class ObservedInkCanvas : InkCanvas
{
    private StylusDevice? _contactStylus;
    private bool _mouseContact;
    private bool _hadCapture;
    private bool _awaitingRecovery;
    private bool _restarting;
    private bool _continuedContact;
    private Stroke? _contactStroke;
    private long _generation;
    private long _lastAttempt = long.MinValue;
    private DispatcherOperation? _recoveryOperation;

    // The owner knows whether the page and foreground window still belong to
    // this writing session. A lost capture alone is not permission to take it.
    internal Func<bool>? MayResumeContact { get; set; }
    internal void AddObserver(InkContactObserver observer) => StylusPlugIns.Insert(0, observer);

    public ObservedInkCanvas()
    {
        PreviewStylusDown += (_, e) =>
        {
            if ((_contactStylus is { InAir: false } other && other != e.StylusDevice)
                || (_mouseContact && Mouse.LeftButton == MouseButtonState.Pressed)
                || EditingMode != InkCanvasEditingMode.Ink || e.StylusDevice.Inverted) return;
            // A real Down is authoritative even if the previous Up was lost.
            ResetContactRecovery();
            _contactStylus = e.StylusDevice;
        };
        PreviewMouseDown += (_, e) =>
        {
            if (e.StylusDevice is not null || e.ChangedButton != MouseButton.Left
                || _contactStylus is { InAir: false } || ActiveEditingMode != InkCanvasEditingMode.Ink) return;
            ResetContactRecovery();
            _mouseContact = true;
        };
        // Up must end recovery even if another handler consumes the event.
        AddHandler(PreviewStylusUpEvent, new StylusEventHandler((_, e) =>
        {
            if (e.StylusDevice == _contactStylus) StopContactRecovery();
        }), true);
        AddHandler(PreviewMouseUpEvent, new MouseButtonEventHandler((_, e) =>
        {
            if (_mouseContact && e.StylusDevice is null && e.ChangedButton == MouseButton.Left)
                StopContactRecovery();
        }), true);
        StylusOutOfRange += (_, e) =>
        {
            if (e.StylusDevice == _contactStylus) StopContactRecovery();
        };
        GotStylusCapture += (_, e) =>
        {
            if (e.StylusDevice == _contactStylus && e.StylusDevice.Captured == this) _hadCapture = true;
        };
        GotMouseCapture += (_, _) =>
        {
            if (_mouseContact && Mouse.Captured == this) _hadCapture = true;
        };
        LostStylusCapture += (_, e) =>
        {
            if (e.OriginalSource == this && e.StylusDevice == _contactStylus) CaptureInterrupted();
        };
        LostMouseCapture += (_, e) =>
        {
            if (e.OriginalSource == this && _mouseContact) CaptureInterrupted();
        };
        PreviewStylusMove += (_, e) =>
        {
            if (e.StylusDevice == _contactStylus) TryResumeContact();
        };
        PreviewMouseMove += (_, e) =>
        {
            if (_mouseContact && e.StylusDevice is null) TryResumeContact();
        };
        IsVisibleChanged += (_, _) => { if (!IsVisible) ResetContactRecovery(); };
        IsEnabledChanged += (_, _) => { if (!IsEnabled) ResetContactRecovery(); };
        ActiveEditingModeChanged += (_, _) =>
        {
            if (ActiveEditingMode != InkCanvasEditingMode.Ink) StopContactRecovery();
        };
    }

    private bool HasPressedContact() => _contactStylus is { InAir: false }
        || (_mouseContact && Mouse.LeftButton == MouseButtonState.Pressed);

    // Keep the collected segment until WPF commits the rest during the Up /
    // explicit release event. The next physical Down always starts a new stroke.
    internal void StopContactRecovery()
    {
        _generation++;
        _recoveryOperation?.Abort();
        _recoveryOperation = null;
        _contactStylus = null;
        _mouseContact = false;
        _hadCapture = false;
        _awaitingRecovery = false;
    }

    internal void ResetContactRecovery()
    {
        StopContactRecovery();
        _continuedContact = false;
        _contactStroke = null;
        _lastAttempt = long.MinValue;
    }

    private void CaptureInterrupted()
    {
        if (!_hadCapture || _restarting) return;
        _hadCapture = false;
        if (!HasPressedContact()) { StopContactRecovery(); return; }
        _continuedContact = true;
        _awaitingRecovery = true;
        if (_recoveryOperation is not null) return;
        var generation = _generation;
        // InkCanvas's lost-capture handler still has to commit its partial
        // stroke and unregister the old editor. Never restart inside that event.
        _recoveryOperation = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            _recoveryOperation = null;
            if (generation == _generation) TryResumeContact();
        }));
    }

    private void TryResumeContact()
    {
        if (!_awaitingRecovery || _restarting || _recoveryOperation is not null) return;
        if (!HasPressedContact()) { StopContactRecovery(); return; }
        if (!IsVisible || !IsEnabled || !IsHitTestVisible || ActiveEditingMode != InkCanvasEditingMode.Ink)
        { StopContactRecovery(); return; }
        var stylus = _contactStylus;
        var owner = stylus is not null ? stylus.Captured : Mouse.Captured;
        if (owner is not null && owner != this)
        { StopContactRecovery(); return; } // Another control deliberately took capture.
        if (stylus is not null && Mouse.Captured is { } mouseOwner && mouseOwner != this)
        { StopContactRecovery(); return; }
        if (MayResumeContact?.Invoke() != true) return;
        // WPF's CaptureStylus uses the current device. Never substitute another
        // finger (or a hovering pen) for the one that began this stroke.
        if (stylus is not null && (Stylus.CurrentStylusDevice != stylus || stylus.Inverted)) return;
        var position = stylus is not null ? stylus.GetPosition(this) : Mouse.GetPosition(this);
        if (!new Rect(RenderSize).Contains(position)) return;
        var now = Environment.TickCount64;
        if (_lastAttempt != long.MinValue && now - _lastAttempt < 30) return;
        _lastAttempt = now;
        _restarting = true;
        try
        {
            // Re-capturing the device alone does NOT restart InkCanvas's editor.
            // Its promoted-MouseDown path initializes both the editor and the
            // DynamicRenderer for an already-down stylus. Raise only the bubble
            // event: no fake physical Down reaches our preview contact trackers.
            if (stylus is null)
                DynamicRenderer?.Reset(null, new StylusPointCollection([position]));
            RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left, stylus)
            { RoutedEvent = Mouse.MouseDownEvent, Source = this });
            var captured = stylus is not null ? stylus.Captured == this : Mouse.Captured == this;
            _awaitingRecovery = !captured;
            DiagnosticLog.Ink($"ink-capture-recovery device={stylus?.Id.ToString() ?? "mouse"} captured={captured}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // A device can leave range while the queued operation is pending.
            DiagnosticLog.Ink($"ink-capture-recovery deferred: {ex.GetType().Name}");
        }
        finally { _restarting = false; }
    }

    protected override void OnStrokeCollected(InkCanvasStrokeCollectedEventArgs e)
    {
        // Preserve one physical stroke (including whole-stroke erase semantics)
        // across editor restarts. Only the same contact may join these segments.
        if (_continuedContact)
        {
            if (_contactStroke is not null && Strokes.Contains(_contactStroke) && _contactStroke != e.Stroke)
            {
                var points = e.Stroke.StylusPoints.Reformat(_contactStroke.StylusPoints.Description);
                _contactStroke.StylusPoints.Add(points);
                Strokes.Remove(e.Stroke);
                e = new InkCanvasStrokeCollectedEventArgs(_contactStroke);
            }
            else _contactStroke = e.Stroke;
        }
        base.OnStrokeCollected(e);
    }
}
