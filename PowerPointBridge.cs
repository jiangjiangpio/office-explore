using System.Runtime.InteropServices;
using System.Windows;
using OfficeTouchBar.Infrastructure;
using OfficeTouchBar.Models;

namespace OfficeTouchBar;

public sealed class PowerPointBridge : ISnapshotReader
{
    private dynamic? _application;
    private readonly Func<object?> _connect;
    private readonly Func<nint> _foreground;
    private readonly Func<nint, bool> _isPowerPoint;
    private readonly Func<nint, Rect> _bounds;
    private readonly Func<nint, nint, bool> _restoreFocus;
    private readonly Func<nint, byte, nint, bool> _sendKey;
    private readonly Func<nint, byte, nint, bool> _sendKeyAfterDialog;
    private readonly Func<nint, byte, byte, nint, bool> _sendChord;
    private readonly Func<nint, bool> _isSlideShow;
    private readonly Func<nint, bool> _isEditor;
    private readonly Func<nint, nint> _findSlideShow;
    private readonly Func<nint, string> _windowTitle;
    private DateTime _lastRead;
    internal PointerTool? LastAppliedTool { get; private set; }
    public nint OverlayHandle { get; set; }
    public nint InkOverlayHandle { get; set; }
    public bool PreferSlideShowForActivePresentation { get; set; }
    public PowerPointSnapshot Snapshot { get; private set; } = new(false, false, false, 0, 0, Rect.Empty);

    private string _connectionSession = "";
    internal Func<nint, string> SessionIdentity { get; set; } = NativeMethods.ProcessIdentity;

    private void SynchronizeSession(nint target)
    {
        var session = target == 0 ? "" : SessionIdentity(target);
        if (session == _connectionSession) return;
        DiagnosticLog.Write($"COM session changed old={_connectionSession} new={session} target={target}");
        ReleaseComObject();
        _connectionSession = session;
        LastAppliedTool = null;
    }

    internal void UseSnapshot(PowerPointSnapshot snapshot)
    {
        SynchronizeSession(snapshot.WindowHandle);
        if (Snapshot.WindowHandle != snapshot.WindowHandle
            || !string.Equals(Snapshot.PresentationIdentity, snapshot.PresentationIdentity,
                StringComparison.OrdinalIgnoreCase)) ReleaseComObject();
        Snapshot = snapshot;
    }

    public PowerPointBridge() : this(
        () => NativeMethods.GetRunningComObject("PowerPoint.Application"),
        NativeMethods.GetForegroundWindow, NativeMethods.IsPowerPointWindow, NativeMethods.Bounds) { }

    internal PowerPointBridge(Func<object?> connect, Func<nint> foreground,
        Func<nint, bool> isPowerPoint, Func<nint, Rect> bounds,
        Func<nint, nint, bool>? restoreFocus = null,
        Func<nint, byte, nint, bool>? sendKey = null,
        Func<nint, byte, nint, bool>? sendKeyAfterDialog = null,
        Func<nint, byte, byte, nint, bool>? sendChord = null,
        Func<nint, bool>? isSlideShow = null,
        Func<nint, bool>? isEditor = null,
        Func<nint, nint>? findSlideShow = null,
        Func<nint, string>? windowTitle = null)
    {
        _connect = connect;
        _foreground = foreground;
        _isPowerPoint = isPowerPoint;
        _bounds = bounds;
        _restoreFocus = restoreFocus ?? NativeMethods.RestoreFocus;
        _sendKey = sendKey ?? NativeMethods.SendKey;
        _sendKeyAfterDialog = sendKeyAfterDialog ?? NativeMethods.SendKeyAfterDialog;
        _sendChord = sendChord ?? NativeMethods.SendChord;
        _isSlideShow = isSlideShow ?? NativeMethods.IsSlideShowWindow;
        _isEditor = isEditor ?? NativeMethods.IsPowerPointEditorWindow;
        _findSlideShow = findSlideShow ?? NativeMethods.FindSlideShowWindow;
        _windowTitle = windowTitle ?? NativeMethods.WindowTitle;
    }

    public void Refresh()
    {
        var foreground = _foreground();
        var isOffice = _isPowerPoint(foreground);
        var isOverlay = IsOverlayWindow(foreground);
        var previous = Snapshot;
        SynchronizeSession(isOffice ? foreground : previous.WindowHandle);
        // Modal Office prompts may reject COM calls; do not revive the old foreground
        // snapshot during that failure and accidentally cover the prompt again.
        if (isOffice && !_isSlideShow(foreground) && !_isEditor(foreground))
        {
            Snapshot = previous with { IsForeground = false, ObservedForegroundHandle = foreground };
            return;
        }
        if (previous.WindowHandle != 0 && !_isPowerPoint(previous.WindowHandle))
            ReleaseComObject();
        if (_application is null && (isOffice || isOverlay))
            _application = _connect();

        try
        {
            if (_application is not null)
            {
                using var scope = new ComScope();
                dynamic? target = SelectWindow(scope, foreground, previous.WindowHandle, out bool isShow);
                if (target is not null)
                {
                    dynamic presentation = scope.Track(target.Presentation);
                    var presentationName = ReadString(() => presentation.Name);
                    var presentationIdentity = ReadString(() => presentation.FullName);
                    // PowerPoint 2024 may throw while exposing SlideShowWindow.HWND.
                    // The native foreground/previous handle is already the authoritative
                    // window identity and keeps page reads independent of that COM quirk.
                    nint hwnd = 0;
                    try { hwnd = (nint)Convert.ToInt64(target.HWND); } catch { }
                    // SelectWindow resolved the foreground show first. Never substitute
                    // another still-open slideshow merely because its HWND is cached.
                    if (isShow && _isSlideShow(foreground)) hwnd = foreground;
                    if (hwnd == 0 && isShow && previous.IsSlideShow
                        && _isSlideShow(previous.WindowHandle)
                        && !string.IsNullOrWhiteSpace(presentationIdentity)
                        && string.Equals(presentationIdentity, previous.PresentationIdentity, StringComparison.OrdinalIgnoreCase))
                        hwnd = previous.WindowHandle;
                    if (hwnd == 0 && !isShow) hwnd = isOverlay ? previous.WindowHandle : foreground;
                    if (isShow && !_isSlideShow(hwnd))
                    {
                        var nativeShow = _findSlideShow(isOverlay ? previous.WindowHandle : foreground);
                        if (nativeShow != 0) hwnd = nativeShow;
                    }
                    if (hwnd == 0 || (isShow && !_isSlideShow(hwnd)))
                        throw new InvalidOperationException("No matching native PowerPoint window handle.");
                    dynamic slides = scope.Track(presentation.Slides);
                    dynamic view = scope.Track(target.View);
                    var current = 0;
                    try
                    {
                        dynamic slide = scope.Track(view.Slide);
                        current = Convert.ToInt32(slide.SlideIndex);
                    }
                    catch { /* Grid and transition views do not always expose a current slide. */ }
                    if (string.IsNullOrWhiteSpace(presentationIdentity))
                        presentationIdentity = $"unsaved:{presentationName}:{hwnd}";
                    var targetForeground = isOverlay || (isOffice && (foreground == hwnd
                        || (PreferSlideShowForActivePresentation && _isEditor(foreground))));
                    Snapshot = new(true, targetForeground, isShow, current,
                        Convert.ToInt32(slides.Count), _bounds(hwnd), hwnd, presentationIdentity, presentationName, foreground);
                    _lastRead = DateTime.UtcNow;
                    LogSnapshot();
                    return;
                }
            }
        }
        catch (COMException ex)
        {
            DiagnosticLog.Write($"COM refresh HRESULT=0x{ex.HResult:X8}: {ex.Message}");
            if (ex.HResult is not unchecked((int)0x80010001) and not unchecked((int)0x8001010A))
                ReleaseComObject();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"refresh failed: {ex}");
            ReleaseComObject();
        }

        if ((isOffice || isOverlay) && _isPowerPoint(previous.WindowHandle)
            && (isOverlay || foreground == previous.WindowHandle)
            && DateTime.UtcNow - _lastRead < TimeSpan.FromMilliseconds(700))
        {
            Snapshot = previous with { IsForeground = true, ObservedForegroundHandle = foreground };
            return;
        }
        var fallback = isOffice ? foreground : isOverlay ? previous.WindowHandle : 0;
        if (isOverlay && fallback != 0 && !_isSlideShow(fallback))
        {
            var show = _findSlideShow(fallback);
            if (show != 0) fallback = show;
        }
        Snapshot = new(false, (isOffice || isOverlay) && fallback != 0,
            _isSlideShow(fallback), 0, 0,
            fallback != 0 ? _bounds(fallback) : Rect.Empty, fallback,
            ObservedForegroundHandle: foreground);
        LogSnapshot();
    }

    private dynamic? SelectWindow(ComScope scope, nint foreground, nint previous, out bool isShow)
    {
        isShow = false;
        // An editor can remain open while a different presentation is being shown.
        // Do not attach its foreground state to the unrelated slideshow.
        if (!IsOverlayWindow(foreground))
        {
            try
            {
                dynamic documents = scope.Track(_application!.Windows);
                for (var i = 1; i <= Convert.ToInt32(documents.Count); i++)
                {
                    dynamic document = scope.Track(documents[i]);
                    if ((nint)Convert.ToInt64(document.HWND) != foreground) continue;
                    if (PreferSlideShowForActivePresentation)
                    {
                        dynamic documentPresentation = scope.Track(document.Presentation);
                        var identity = ReadString(() => documentPresentation.FullName);
                        dynamic activeShows = scope.Track(_application.SlideShowWindows);
                        for (var j = 1; j <= Convert.ToInt32(activeShows.Count); j++)
                        {
                            dynamic show = scope.Track(activeShows[j]);
                            dynamic showPresentation = scope.Track(show.Presentation);
                            if (!string.IsNullOrWhiteSpace(identity)
                                && string.Equals(identity, ReadString(() => showPresentation.FullName),
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                isShow = true;
                                return show;
                            }
                        }
                    }
                    return document;
                }
            }
            catch { }
        }
        dynamic shows = scope.Track(_application!.SlideShowWindows);
        dynamic? prior = null;
        dynamic? matchingPresentation = null;
        dynamic? titleMatch = null;
        dynamic? previousIdentityMatch = null;
        var titleMatches = 0;
        var foregroundShow = _isSlideShow(foreground);
        var foregroundTitle = foregroundShow ? _windowTitle(foreground) : "";
        dynamic? unique = null;
        string? activeName = null;
        try
        {
            dynamic active = scope.Track(_application.ActivePresentation);
            activeName = Convert.ToString(active.FullName);
        }
        catch { }
        var count = Convert.ToInt32(shows.Count);
        for (var i = 1; i <= count; i++)
        {
            dynamic window = scope.Track(shows[i]);
            nint hwnd;
            try { hwnd = (nint)Convert.ToInt64(window.HWND); }
            catch
            {
                // HWND is optional here; the active/previous presentation match below
                // can still select the correct slideshow without it.
                hwnd = 0;
            }
            if (hwnd != 0 && !_isPowerPoint(hwnd)) continue;
            if (hwnd != 0 && hwnd == foreground) { isShow = true; return window; }
            if (hwnd != 0 && hwnd == previous) prior = window;
            dynamic presentation = scope.Track(window.Presentation);
            var identity = ReadString(() => presentation.FullName);
            var name = ReadString(() => presentation.Name);
            if (string.IsNullOrEmpty(name)) name = System.IO.Path.GetFileName(identity);
            if (foregroundShow && !string.IsNullOrWhiteSpace(name)
                && foregroundTitle.Contains($"[{name}]", StringComparison.OrdinalIgnoreCase))
            { titleMatch = window; titleMatches++; }
            if (!string.IsNullOrWhiteSpace(Snapshot.PresentationIdentity)
                && string.Equals(identity, Snapshot.PresentationIdentity, StringComparison.OrdinalIgnoreCase))
                previousIdentityMatch = window;
            if (activeName is not null && Convert.ToString(presentation.FullName) == activeName)
                matchingPresentation = window;
            unique = window;
        }
        if (foregroundShow)
        {
            // When multiple COM HWNDs are unavailable, only an unambiguous
            // native title match may associate page/ink data with that window.
            var foregroundSelection = titleMatches == 1 ? titleMatch : count == 1 ? unique : null;
            isShow = foregroundSelection is not null;
            return foregroundSelection;
        }
        if (IsOverlayWindow(foreground))
        {
            var overlaySelection = prior ?? previousIdentityMatch ?? (count == 1 ? unique : null);
            isShow = overlaySelection is not null;
            return overlaySelection;
        }
        dynamic? selected = matchingPresentation;
        selected ??= matchingPresentation;
        selected ??= prior;
        if (selected is null && count == 1) selected = unique;
        if (selected is not null) { isShow = true; return selected; }
        try { return scope.Track(_application.ActiveWindow); }
        catch { return null; }
    }

    private bool WithView(Action<dynamic> command)
        => WithViewResult(view =>
        {
            command(view);
            return true;
        });

    private bool WithViewResult(Func<dynamic, bool> command)
    {
        if (!Snapshot.IsSlideShow) return false;
        SynchronizeSession(Snapshot.WindowHandle);
        _application ??= _connect();
        if (_application is null) return false;
        using var scope = new ComScope();
        // Commands must use the same HWND-tolerant selection as page refresh.
        dynamic? window = SelectWindow(scope, Snapshot.WindowHandle, Snapshot.WindowHandle, out bool isShow);
        if (window is null || !isShow) return false;
        if (!string.IsNullOrWhiteSpace(Snapshot.PresentationIdentity))
        {
            dynamic presentation = scope.Track(window.Presentation);
            if (!string.Equals(ReadString(() => presentation.FullName), Snapshot.PresentationIdentity,
                StringComparison.OrdinalIgnoreCase)) return false;
        }
        dynamic view = scope.Track(window.View);
        return command(view);
    }

    private bool Prepare() => Snapshot.IsForeground
        && _restoreFocus(Snapshot.WindowHandle, IsOverlayWindow(_foreground()) ? _foreground() : OverlayHandle);
    public bool Next() => Navigate(true);
    public bool Previous() => Navigate(false);

    private bool Navigate(bool forward)
    {
        if (!Snapshot.IsForeground) return false;
        if (Snapshot.IsSlideShow)
        {
            var foreground = _foreground();
            return _sendKey(Snapshot.WindowHandle, forward ? (byte)0x28 : (byte)0x26,
                IsOverlayWindow(foreground) ? foreground : OverlayHandle);
        }
        if (!Prepare()) return false;
        return InteractionPolicy.TryCommand(() =>
        {
            return ExecuteMso(forward ? "SlideNext" : "SlidePrevious");
        }, () => _sendKey(Snapshot.WindowHandle, forward ? (byte)0x28 : (byte)0x26, OverlayHandle));
    }

    public bool SetPointerTool(PointerTool tool)
    {
        LastAppliedTool = null;
        if (!Snapshot.IsSlideShow || !Prepare()) return false;
        var expected = tool switch
        {
            PointerTool.Mouse => 1,
            PointerTool.Pen => 2,
            PointerTool.Highlighter => 2,
            PointerTool.Eraser => 5, // ppSlideShowPointerEraser; 4 is AutoArrow.
            _ => throw new ArgumentOutOfRangeException(nameof(tool))
        };
        var chord = tool switch
        {
            PointerTool.Mouse => (byte)0x41,
            PointerTool.Pen => (byte)0x50,
            PointerTool.Highlighter => (byte)0x49,
            PointerTool.Eraser => (byte)0x45,
            _ => throw new ArgumentOutOfRangeException(nameof(tool))
        };
        // COM has no highlighter enum. Writing 2 while it is already 2 can
        // leave the highlighter subtype active. First leave ink altogether,
        // then issue the explicit native tool command (also on repeated clicks).
        var arrowReady = InteractionPolicy.TryCommand(() => SetPointerType(1),
            () => _sendChord(Snapshot.WindowHandle, 0x11, 0x41, OverlayHandle)
                && VerifyPointerTypeWithRetry(1));
        if (!arrowReady) return false;
        LastAppliedTool = PointerTool.Mouse;
        if (tool == PointerTool.Mouse) return true;
        var success = InteractionPolicy.TryCommand(
            () => _sendChord(Snapshot.WindowHandle, 0x11, chord, OverlayHandle)
                && VerifyPointerTypeWithRetry(expected), () => false);
        if (success) LastAppliedTool = tool;
        else LastAppliedTool = InteractionPolicy.TryCommand(() => VerifyPointerType(1), () => false)
            ? PointerTool.Mouse : null;
        DiagnosticLog.Write($"Office tool command target={Snapshot.WindowHandle} tool={tool} success={success} confirmed={LastAppliedTool}");
        return success;
    }

    // Built-in ink only needs the native arrow, not a synchronous COM readback
    // on the UI thread while Office is opening a new process or animating.
    internal bool PrepareBuiltInMouse() => Snapshot.IsSlideShow && Prepare()
        && _sendChord(Snapshot.WindowHandle, 0x11, 0x41,
            IsOverlayWindow(_foreground()) ? _foreground() : OverlayHandle);

    private bool VerifyPointerType(int expected) =>
        WithViewResult(view => Convert.ToInt32(view.PointerType) == expected);

    public bool SetPenColor(System.Windows.Media.Color color)
        => SetPenColorAndPen(color);

    public bool SetPenColorAndPen(System.Windows.Media.Color color)
    {
        if (!SetPointerTool(PointerTool.Pen)) return false;
        var rgb = color.R | color.G << 8 | color.B << 16;
        return InteractionPolicy.TryCommand(() => SetColor(rgb), () => false);
    }

    private bool SetPointerType(int expectedType) =>
        WithViewResult(view =>
        {
            view.PointerType = expectedType;
            return Convert.ToInt32(view.PointerType) == expectedType;
        });

    private bool SetColor(int rgb) =>
        WithViewResult(view =>
        {
            using var scope = new ComScope();
            dynamic pointerColor = scope.Track(view.PointerColor);
            pointerColor.RGB = rgb;
            return Convert.ToInt32(pointerColor.RGB) == rgb
                && Convert.ToInt32(view.PointerType) == 2;
        });

    private bool VerifyPointerTypeWithRetry(int expected)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (VerifyPointerType(expected)) return true;
            if (attempt < 2) Thread.Sleep(15);
        }
        return false;
    }

    public bool ClearCurrentInk() => Snapshot.IsSlideShow && Prepare()
        && _sendKey(Snapshot.WindowHandle, 0x45, OverlayHandle);

    public bool ExitSlideShow() => Snapshot.IsSlideShow && Prepare()
        && _sendKey(Snapshot.WindowHandle, 0x1B, OverlayHandle);

    internal bool ExitBuiltInSlideShowAfterConfirmation(nint target) =>
        target != 0 && target == Snapshot.WindowHandle && _isSlideShow(target)
        && InteractionPolicy.TryCommand(() => WithView(view => view.Exit()), () => false);

    internal bool ExitSlideShowAfterConfirmation(nint target, nint promptHandle) =>
        target != 0 && _isSlideShow(target)
        && (promptHandle != 0
            ? _sendKeyAfterDialog(target, 0x1B, promptHandle)
            : _sendKey(target, 0x1B, OverlayHandle));

    private bool ExecuteMso(string command)
    {
        _application ??= _connect();
        if (_application is null) return false;
        using var scope = new ComScope();
        dynamic bars = scope.Track(_application.CommandBars);
        bars.ExecuteMso(command);
        return true;
    }

    private void LogSnapshot() => DiagnosticLog.Write(
        $"state connected={Snapshot.IsConnected} foreground={Snapshot.IsForeground} show={Snapshot.IsSlideShow} hwnd={Snapshot.WindowHandle} page={Snapshot.CurrentSlide}/{Snapshot.TotalSlides}");

    private bool IsOverlayWindow(nint hwnd) => hwnd != 0 && (hwnd == OverlayHandle || hwnd == InkOverlayHandle);

    private static string ReadString(Func<object> read)
    {
        try { return Convert.ToString(read()) ?? ""; }
        catch { return ""; }
    }

    private void ReleaseComObject()
    {
        if (_application is not null && Marshal.IsComObject(_application))
            Marshal.ReleaseComObject(_application);
        _application = null;
    }
    public void Dispose() => ReleaseComObject();

    private sealed class ComScope : IDisposable
    {
        private readonly List<object> _objects = [];
        internal dynamic Track(object value)
        {
            if (Marshal.IsComObject(value)) _objects.Add(value);
            return value;
        }
        public void Dispose()
        {
            for (var i = _objects.Count - 1; i >= 0; i--)
                Marshal.ReleaseComObject(_objects[i]);
        }
    }
}
