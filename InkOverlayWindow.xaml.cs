using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using OfficeTouchBar.Infrastructure;
using OfficeTouchBar.Models;
using Point = System.Windows.Point;
using Cursor = System.Windows.Input.Cursor;

namespace OfficeTouchBar;

public partial class InkOverlayWindow : Window
{
    private bool _loading;
    private PointerTool _tool = PointerTool.Pen;
    private PointerTool? _configuredTool;
    private System.Windows.Media.Color? _configuredColor;
    private double _configuredPenSize;
    internal const double EraserContactSize = 12;
    private readonly Cursor _eraserCursor = InkCursors.CreateEraser();
    private bool _showContactIndicator;
    private long _lastEraserContact = long.MinValue;
    private readonly List<Stroke> _pendingEraseStrokes = [];
    private readonly HashSet<Stroke> _pendingEraseSet = [];
    private readonly List<Point> _eraserPath = [];
    private TouchDevice? _eraserTouch;
    private StylusDevice? _eraserStylus;
    private bool _eraserMouse;
    private bool _renderSubscribed;
    private DispatcherOperation? _eraseOperation;
    private readonly Dictionary<(int X, int Y), HashSet<Stroke>> _eraserGrid = [];
    private readonly Dictionary<Stroke, (int MinX, int MinY, int MaxX, int MaxY)> _eraserStrokeCells = [];
    private const double EraserGridCellSize = 192;
    private Point? _lastEraserPoint;
    private bool _eraserIndexReady;
    private bool _eraseChanged;
    private nint _targetHandle;
    private bool? _lastAboveTarget;
    private bool _inputLogged;
    private bool _changeLogged;
    private readonly InkContactState _contacts = new();
    private readonly InkContactObserver _rawContacts = new();
    private readonly Dictionary<int, StylusDevice> _writingStyluses = [];
    private readonly Dictionary<int, TouchDevice> _writingTouches = [];
    private long _lastMoveLog;
    private bool _pageInputAvailable = true;
    private bool _pageInkVisible = true;
    private Rect _lastPhysicalBounds = Rect.Empty;
    private nint _keyboardHwnd;
    private (PointerTool Tool, System.Windows.Media.Color Color, double Size)? _pendingTool;
    private readonly DispatcherTimer _toolTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };

    public event Action? InkChanged;
    internal int StrokeCount => InkSurface.Strokes.Count;
    public event Action? WritingStarted;
    internal event Action? BeforeWriting;
    public event Action? ExitRequested;
    public nint Handle => new WindowInteropHelper(this).EnsureHandle();
    internal bool HasActiveContact
    {
        get
        {
            _contacts.Sweep(Environment.TickCount64, ContactReleased);
            foreach (var id in _writingStyluses.Keys.ToArray())
                if (!_contacts.Contains("stylus", id)) _writingStyluses.Remove(id);
            foreach (var id in _writingTouches.Keys.ToArray())
                if (!_contacts.Contains("touch", id)) _writingTouches.Remove(id);
            return _contacts.HasContacts || _rawContacts.HasContacts;
        }
    }
    internal bool IsWriting => HasActiveContact || _contacts.IsBusy(Environment.TickCount64)
        || (_tool == PointerTool.Eraser && _lastEraserContact != long.MinValue
            && Environment.TickCount64 - _lastEraserContact < 250);
    internal nint ToolbarHandle { get; set; }
    internal bool PageInputAvailable => _pageInputAvailable;
    internal bool IsInputTransparent => (NativeMethods.GetWindowLongPtr(Handle, -20) & (nint)0x20) != 0;
    internal bool CanReceiveInkInput => Focusable
        && (NativeMethods.GetWindowLongPtr(Handle, -20) & (nint)0x08000000) == 0
        && !IsInputTransparent;
    internal bool IsTopmost => (NativeMethods.GetWindowLongPtr(Handle, -20) & (nint)0x8) != 0;
    internal bool IsAboveTarget => NativeMethods.IsAbove(Handle, _targetHandle);
    public double SurfaceWidth => SurfaceSize(InkSurface.ActualWidth, Width);
    public double SurfaceHeight => SurfaceSize(InkSurface.ActualHeight, Height);

    public InkOverlayWindow()
    {
        InitializeComponent();
        _toolTimer.Tick += (_, _) => ApplyPendingTool();
        InkSurface.AddObserver(_rawContacts);
        InkSurface.StrokeCollected += InkSurface_OnChanged;
        InkSurface.StrokeErased += InkSurface_OnChanged;
        InkSurface.PreviewMouseDown += Input_OnMouseDown;
        InkSurface.PreviewMouseUp += Input_OnMouseUp;
        InkSurface.PreviewMouseMove += Input_OnMouseMove;
        InkSurface.MouseLeave += Input_OnMouseLeave;
        InkSurface.PreviewStylusDown += Input_OnStylusDown;
        InkSurface.PreviewStylusMove += Input_OnStylusMove;
        InkSurface.PreviewStylusUp += Input_OnStylusUp;
        InkSurface.PreviewTouchDown += Input_OnTouchDown;
        InkSurface.PreviewTouchMove += Input_OnTouchMove;
        InkSurface.PreviewTouchUp += Input_OnTouchUp;
        Stylus.SetIsPressAndHoldEnabled(InkSurface, false);
        Stylus.SetIsFlicksEnabled(InkSurface, false);
        Stylus.SetIsTapFeedbackEnabled(InkSurface, false);
        Stylus.SetIsTouchFeedbackEnabled(InkSurface, false);
        SourceInitialized += (_, _) =>
        {
            NativeMethods.SetWindowLongPtr(Handle, -20,
                (NativeMethods.GetWindowLongPtr(Handle, -20) | 0x80) & ~(nint)0x08000000);
            HwndSource.FromHwnd(Handle)?.AddHook(WindowHook);
            _keyboardHwnd = Handle;
            ComponentDispatcher.ThreadFilterMessage += FilterKeyboardMessage;
        };
        ConfigureTool(PointerTool.Pen, Colors.Red);
        InkSurface.LostMouseCapture += (_, _) =>
        {
            TraceInput("lost-mouse-capture");
            _contacts.LostCapture("mouse", 0, Environment.TickCount64);
            if (_eraserMouse) EndEraser();
        };
        InkSurface.LostStylusCapture += (_, e) =>
        {
            TraceInput($"lost-stylus-capture id={e.StylusDevice.Id} inAir={e.StylusDevice.InAir} source={e.OriginalSource?.GetType().Name} newOwner={e.StylusDevice.Captured?.GetType().Name}");
            _contacts.LostCapture("stylus", e.StylusDevice.Id, Environment.TickCount64);
            if (_eraserStylus == e.StylusDevice) EndEraser();
        };
        InkSurface.LostTouchCapture += (_, e) =>
        {
            TraceInput($"lost-touch-capture id={e.TouchDevice.Id} source={e.OriginalSource?.GetType().Name} newOwner={e.TouchDevice.Captured?.GetType().Name}");
            _contacts.LostCapture("touch", e.TouchDevice.Id, Environment.TickCount64);
            if (_eraserTouch == e.TouchDevice) EndEraser();
        };
        InkSurface.StylusOutOfRange += (_, e) =>
        {
            _contacts.End("stylus", e.StylusDevice.Id, Environment.TickCount64);
            _writingStyluses.Remove(e.StylusDevice.Id);
            TraceInput($"stylus-out-of-range id={e.StylusDevice.Id}");
        };
        Closed += (_, _) =>
        {
            _toolTimer.Stop();
            ComponentDispatcher.ThreadFilterMessage -= FilterKeyboardMessage;
            _keyboardHwnd = 0;
            ResetContacts();
            _eraserCursor.Dispose();
        };
        Deactivated += (_, _) =>
        {
            var foreground = NativeMethods.GetForegroundWindow();
            TraceInput("deactivated");
            _rawContacts.SuspectCaptureLoss();
            // A focus bounce to our slideshow/toolbar is not a physical Up.
            if (foreground != 0 && foreground != _targetHandle && foreground != ToolbarHandle && foreground != Handle)
                CancelInput("another application activated");
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) ResetContacts();
            UpdateRawInputObserver();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            ExitRequested?.Invoke();
        };
    }

    internal Func<int, nint, nint, bool>? KeyboardHandoff { get; set; }

    private void FilterKeyboardMessage(ref MSG message, ref bool handled)
    {
        if (handled || _keyboardHwnd == 0 || message.hwnd != _keyboardHwnd) return;
        if (message.message is 0x100 or 0x101 or 0x104 or 0x105)
            handled = KeyboardHandoff?.Invoke(message.message, message.wParam, message.lParam) == true;
    }

    private nint WindowHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message is 0x0006 or 0x001F or 0x0215 or 0x024C)
            TraceInput($"window-message msg=0x{message:X} wParam={wParam} lParam={lParam}");
        if (message != 0x84) return 0;
        handled = true;
        return _tool == PointerTool.Mouse ? -1 : 1;
    }

    public void ConfigureTool(PointerTool tool, System.Windows.Media.Color color,
        double penSize = 3.2, double eraserSize = 30)
    {
        penSize = double.IsFinite(penSize) ? Math.Clamp(penSize, 1, 20) : 3.2;
        if (HasActiveContact)
        {
            _pendingTool = (tool, color, penSize);
            _toolTimer.Start();
            return;
        }
        _pendingTool = null;
        _toolTimer.Stop();
        if (_configuredTool == tool && _configuredColor == color
            && _configuredPenSize == penSize) return;
        var modeChanged = _configuredTool != tool;
        TraceInput($"configure-tool from={_configuredTool} to={tool} modeChanged={modeChanged}");
        if (modeChanged) ResetContacts();
        _tool = tool;
        if (modeChanged)
        {
            InkSurface.EditingMode = tool switch
            {
                PointerTool.Eraser => InkCanvasEditingMode.None,
                PointerTool.Mouse => InkCanvasEditingMode.None,
                _ => InkCanvasEditingMode.Ink
            };
            // Hardware eraser tips use whole-stroke erasing too; never fall
            // back to WPF's default, expensive point eraser.
            InkSurface.EditingModeInverted = tool is PointerTool.Pen or PointerTool.Highlighter
                ? InkCanvasEditingMode.EraseByStroke : InkCanvasEditingMode.None;
            InkSurface.Cursor = tool == PointerTool.Eraser
                ? _eraserCursor : System.Windows.Input.Cursors.Pen;
            SetInputTransparent(tool == PointerTool.Mouse);
        }
        if (modeChanged)
        {
            EndEraser();
            InkSurface.EraserShape = new EllipseStylusShape(EraserContactSize, EraserContactSize);
        }
        if (tool is PointerTool.Pen or PointerTool.Highlighter
            && (modeChanged || _configuredColor != color || _configuredPenSize != penSize))
            InkSurface.DefaultDrawingAttributes = new DrawingAttributes
            {
                Color = color,
                Width = tool == PointerTool.Highlighter ? penSize * 5.625 : penSize,
                Height = tool == PointerTool.Highlighter ? penSize * 5.625 : penSize,
                IsHighlighter = tool == PointerTool.Highlighter,
                FitToCurve = false,
                IgnorePressure = tool == PointerTool.Highlighter,
                StylusTip = StylusTip.Ellipse
            };
        if (tool != PointerTool.Eraser) EraserIndicator.Visibility = Visibility.Collapsed;
        _configuredTool = tool;
        _configuredColor = color;
        _configuredPenSize = penSize;
        UpdateRawInputObserver();
    }

    internal void ApplyPendingTool()
    {
        if (HasActiveContact) return;
        if (_pendingTool is { } pending) ConfigureTool(pending.Tool, pending.Color, pending.Size);
        else _toolTimer.Stop();
    }

    public void Place(nint targetHandle, Rect physicalBounds, bool show)
    {
        if (physicalBounds.IsEmpty) return;
        var targetChanged = _targetHandle != targetHandle;
        _targetHandle = targetHandle;
        var dpi = NativeMethods.GetDpiForWindow(Handle);
        var scale = dpi > 0 ? dpi / 96.0 : NativeMethods.DpiScaleAt(physicalBounds.Left + 1, physicalBounds.Top + 1);
        var oldWidth = SurfaceWidth;
        var oldHeight = SurfaceHeight;
        var newWidth = Math.Max(1, physicalBounds.Width / scale);
        var newHeight = Math.Max(1, physicalBounds.Height / scale);
        if (oldWidth > 0 && oldHeight > 0 && InkSurface.Strokes.Count > 0
            && (Math.Abs(oldWidth - newWidth) > 0.5 || Math.Abs(oldHeight - newHeight) > 0.5))
        {
            var matrix = new Matrix(newWidth / oldWidth, 0, 0, newHeight / oldHeight, 0, 0);
            foreach (var stroke in InkSurface.Strokes) stroke.Transform(matrix, true);
            InvalidateEraserIndex();
        }
        if (Math.Abs(Width - newWidth) > 0.01) Width = newWidth;
        if (Math.Abs(Height - newHeight) > 0.01) Height = newHeight;
        var becameVisible = show && !IsVisible;
        if (targetChanged || becameVisible)
        {
            TraceInput($"place targetChanged={targetChanged} show={show} becameVisible={becameVisible}");
            // A reused HWND or a hidden/re-shown overlay must not inherit a stale
            // hit-test style, capture state, or cached drawing configuration.
            ResetContacts();
            _configuredTool = null;
            _configuredColor = null;
            _configuredPenSize = 0;
            _lastAboveTarget = null;
        }
        if (becameVisible) Show();

        var roundedBounds = new Rect(
            Math.Round(physicalBounds.Left),
            Math.Round(physicalBounds.Top),
            Math.Ceiling(physicalBounds.Width),
            Math.Ceiling(physicalBounds.Height));
        var currentBounds = NativeMethods.Bounds(Handle);
        var positionChanged = targetChanged || _lastPhysicalBounds != roundedBounds || currentBounds != roundedBounds;
        if (!positionChanged && !becameVisible) return;

        TraceInput($"place-window bounds={roundedBounds}");
        NativeMethods.SetWindowPos(Handle, 0, (int)Math.Round(physicalBounds.Left), (int)Math.Round(physicalBounds.Top),
            (int)Math.Ceiling(physicalBounds.Width), (int)Math.Ceiling(physicalBounds.Height),
            0x0010 | 0x0004 | (show ? 0x0040u : 0u));
        _lastPhysicalBounds = roundedBounds;
        LogZOrder();
    }

    public void RaiseAboveTarget()
    {
        if (!IsVisible || _targetHandle == 0) return;
        NativeMethods.SetWindowPos(Handle, -1, 0, 0, 0, 0, 0x0013);
        LogZOrder();
    }

    public void LoadPage(InkPageData page)
    {
        TraceInput($"load-page fromStrokes={InkSurface.Strokes.Count} toStrokes={page.Strokes.Count}");
        ResetContacts();
        _loading = true;
        _inputLogged = false;
        _changeLogged = false;
        try
        {
            InkSurface.Strokes = page.Strokes;
            InvalidateEraserIndex();
            var width = SurfaceWidth;
            var height = SurfaceHeight;
            if (page.CanvasWidth > 0 && page.CanvasHeight > 0 && width > 0 && height > 0
                && (Math.Abs(page.CanvasWidth - width) > 0.5 || Math.Abs(page.CanvasHeight - height) > 0.5))
            {
                var matrix = new Matrix(width / page.CanvasWidth, 0, 0, height / page.CanvasHeight, 0, 0);
                foreach (var stroke in InkSurface.Strokes) stroke.Transform(matrix, true);
                InvalidateEraserIndex();
            }
        }
        finally { _loading = false; }
    }

    public InkPageData CapturePage()
    {
        FlushEraserFrame();
        // Clone once; the store serializes once. Avoid encode/decode/encode
        // of every point on the UI thread at each autosave.
        return new InkPageData(InkSurface.Strokes.Clone(), SurfaceWidth, SurfaceHeight);
    }

    public void ClearPage()
    {
        ResetContacts();
        InkSurface.Strokes.Clear();
        InvalidateEraserIndex();
        InkChanged?.Invoke();
    }

    internal void EraseAt(Point point)
    {
        if (_tool != PointerTool.Eraser) ConfigureTool(PointerTool.Eraser, _configuredColor ?? Colors.Red,
            _configuredPenSize > 0 ? _configuredPenSize : 3.2);
        BeginEraser(point);
        EndEraser();
    }

    private void InkSurface_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        TraceInput($"stroke-committed event={e.RoutedEvent?.Name} strokes={InkSurface.Strokes.Count}");
        InvalidateEraserIndex();
        if (!_changeLogged)
        {
            DiagnosticLog.Write($"built-in ink changed tool={_tool} strokes={InkSurface.Strokes.Count}");
            _changeLogged = true;
        }
        InkChanged?.Invoke();
    }

    private void ShowEraser(Point point)
    {
        if (_tool != PointerTool.Eraser || !_showContactIndicator) return;
        var left = point.X - EraserIndicator.Width / 2;
        var top = point.Y - EraserIndicator.Height / 2;
        if (EraserIndicator.Visibility == Visibility.Visible
            && EraserTranslation.X == left && EraserTranslation.Y == top) return;
        EraserIndicator.Visibility = Visibility.Visible;
        EraserTranslation.X = left;
        EraserTranslation.Y = top;
    }

    private void HideEraser()
    {
        if (_tool == PointerTool.Eraser) EraserIndicator.Visibility = Visibility.Collapsed;
    }

    private void ResetContacts(string reason = "explicit input reset")
    {
        if (_contacts.HasContacts) TraceInput($"contacts-reset reason={reason}");
        EndEraser();
        _contacts.Clear();
        _rawContacts.Clear();
        _writingStyluses.Clear();
        _writingTouches.Clear();
        _lastEraserContact = long.MinValue;
    }

    private bool ContactReleased(string kind, int id)
    {
        try
        {
            if (kind == "stylus" && _writingStyluses.TryGetValue(id, out var stylus)) return stylus.InAir;
            if (kind == "touch" && _writingTouches.TryGetValue(id, out var touch))
                return touch.GetTouchPoint(InkSurface).Action == TouchAction.Up;
        }
        catch { /* Missing device state is not evidence of a physical Up. */ }
        return false;
    }

    internal void CancelInput(string reason)
    {
        // WPF commits the existing partial stroke on capture release. Do this
        // before explicit settings/exit/navigation saves, rather than losing it.
        TraceInput($"cancel-input reason={reason}");
        InkSurface.ReleaseStylusCapture();
        InkSurface.ReleaseMouseCapture();
        InkSurface.ReleaseAllTouchCaptures();
        ResetContacts(reason);
    }

    internal void HideForReason(string reason)
    {
        CancelInput(reason);
        TraceInput($"hide-window reason={reason}");
        Hide();
    }

    internal void SetPageAvailability(bool inputAvailable, bool showInk)
    {
        if (_pageInputAvailable == inputAvailable && _pageInkVisible == showInk) return;
        if (!inputAvailable && HasActiveContact) return;
        TraceInput($"page-availability input={inputAvailable} showInk={showInk}");
        _pageInputAvailable = inputAvailable;
        _pageInkVisible = showInk;
        // Keep the HWND and strokes; the root still intercepts touches while
        // metadata is unknown, so a rejected stroke cannot turn into a PPT tap.
        InkSurface.IsHitTestVisible = inputAvailable;
        InkSurface.Opacity = showInk ? 1 : 0;
        UpdateRawInputObserver();
    }

    private void UpdateRawInputObserver() =>
        _rawContacts.SetInputEnabled(IsVisible && _pageInputAvailable && _tool != PointerTool.Mouse);

    internal void TraceInput(string message) => DiagnosticLog.Ink(
        $"{message} ink={_keyboardHwnd} target={_targetHandle} foreground={NativeMethods.GetForegroundWindow()} tool={_tool} contacts={_contacts.HasContacts} rawContacts={_rawContacts.HasContacts} visible={IsVisible} stylusCaptured={InkSurface.IsStylusCaptured} mouseCaptured={InkSurface.IsMouseCaptured} pageReady={_pageInputAvailable}");

    private void TraceMove(string kind, int id)
    {
        var now = Environment.TickCount64;
        _contacts.Move(kind, id, now);
        if (now - _lastMoveLog < 250) return;
        _lastMoveLog = now;
        TraceInput($"contact-move kind={kind} id={id}");
    }

    private void SetInputTransparent(bool transparent)
    {
        var style = NativeMethods.GetWindowLongPtr(Handle, -20);
        style = transparent ? style | (nint)0x20 : style & ~(nint)0x20;
        NativeMethods.SetWindowLongPtr(Handle, -20, style);
    }

    private void LogInput(string source)
    {
        if (_tool == PointerTool.Eraser) _lastEraserContact = Environment.TickCount64;
        if (_tool != PointerTool.Mouse) WritingStarted?.Invoke();
        if (_inputLogged) return;
        DiagnosticLog.Write($"built-in ink input source={source} tool={_tool} hwnd={Handle} target={_targetHandle}");
        _inputLogged = true;
    }

    internal void BeginEraser(Point point, bool showContactIndicator = true)
    {
        if (_tool != PointerTool.Eraser) return;
        EndEraser();
        _showContactIndicator = showContactIndicator;
        InkSurface.Cursor = showContactIndicator ? System.Windows.Input.Cursors.None : _eraserCursor;
        _lastEraserPoint = point;
        _eraserPath.Add(point);
        CompositionTarget.Rendering += Eraser_OnRendering;
        _renderSubscribed = true;
        ShowEraser(point);
    }

    internal void AddEraserPoint(Point point, bool updateIndicator = true)
    {
        if (_tool != PointerTool.Eraser || _lastEraserPoint is null) return;
        if (_eraserPath.Count == 0) _eraserPath.Add(_lastEraserPoint.Value);
        if (_eraserPath[^1] != point) _eraserPath.Add(point);
        _lastEraserPoint = point;
        _lastEraserContact = Environment.TickCount64;
        if (updateIndicator) ShowEraser(point);
    }

    private void AddEraserPoints(StylusPointCollection points)
    {
        foreach (var point in points) AddEraserPoint(point.ToPoint(), updateIndicator: false);
        if (points.Count > 0) ShowEraser(points[^1].ToPoint());
    }

    private void Eraser_OnRendering(object? sender, EventArgs e)
    {
        if (_eraserPath.Count == 0 || _eraseOperation is not null) return;
        // Let WPF submit the latest contact indicator before doing hit tests.
        // Only one work item may be queued, even if rendering/input outpace it.
        _eraseOperation = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _eraseOperation = null;
            FlushEraserFrame();
        }));
    }

    internal void FlushEraserFrame()
    {
        if (_eraserPath.Count == 0) return;
        ProcessEraserPoints(_eraserPath);
        _eraserPath.Clear();
    }

    internal void EndEraser()
    {
        if (_renderSubscribed) CompositionTarget.Rendering -= Eraser_OnRendering;
        _renderSubscribed = false;
        _eraseOperation?.Abort();
        _eraseOperation = null;
        FlushEraserFrame();
        _lastEraserPoint = null;
        var touch = _eraserTouch;
        var stylus = _eraserStylus;
        var mouse = _eraserMouse;
        _eraserTouch = null;
        _eraserStylus = null;
        _eraserMouse = false;
        if (touch?.Captured == InkSurface) touch.Capture(null);
        if (stylus?.Captured == InkSurface) stylus.Capture(null);
        if (mouse && Mouse.Captured == InkSurface) InkSurface.ReleaseMouseCapture();
        HideEraser();
        _showContactIndicator = false;
        if (_tool == PointerTool.Eraser) InkSurface.Cursor = _eraserCursor;
        if (_eraseChanged)
        {
            _eraseChanged = false;
            InkChanged?.Invoke();
        }
    }

    private void ProcessEraserPoints(IReadOnlyList<Point> path)
    {
        if (path.Count == 0 || InkSurface.Strokes.Count == 0) return;
        EnsureEraserIndex();
        var candidates = FindEraserCandidates(path);
        foreach (var stroke in candidates)
        {
            if (_pendingEraseSet.Contains(stroke) || !stroke.HitTest(path, InkSurface.EraserShape)) continue;
            _pendingEraseSet.Add(stroke);
            _pendingEraseStrokes.Add(stroke);
        }
        FlushPendingErasures();
    }

    private void FlushPendingErasures()
    {
        if (_pendingEraseStrokes.Count == 0) return;

        InkSurface.Strokes.Remove(new StrokeCollection(_pendingEraseStrokes));
        if (_eraserIndexReady)
        {
            foreach (var stroke in _pendingEraseStrokes) RemoveFromEraserIndex(stroke);
        }
        _pendingEraseStrokes.Clear();
        _pendingEraseSet.Clear();
        _eraseChanged = true;
    }

    private void InvalidateEraserIndex()
    {
        _eraserIndexReady = false;
        _eraserGrid.Clear();
        _eraserStrokeCells.Clear();
    }

    private void EnsureEraserIndex()
    {
        if (_eraserIndexReady) return;
        _eraserGrid.Clear();
        _eraserStrokeCells.Clear();
        foreach (var stroke in InkSurface.Strokes) AddToEraserIndex(stroke);
        _eraserIndexReady = true;
    }

    private HashSet<Stroke> FindEraserCandidates(IReadOnlyList<Point> path)
    {
        var result = new HashSet<Stroke>();
        var visited = new HashSet<(int X, int Y)>();
        var radiusX = InkSurface.EraserShape.Width / 2;
        var radiusY = InkSurface.EraserShape.Height / 2;
        // Query each segment, not the bounding rectangle of a whole curved path.
        for (var i = 0; i < path.Count; i++)
        {
            var a = path[Math.Max(0, i - 1)];
            var b = path[i];
            var bounds = new Rect(Math.Min(a.X, b.X) - radiusX, Math.Min(a.Y, b.Y) - radiusY,
                Math.Abs(a.X - b.X) + radiusX * 2, Math.Abs(a.Y - b.Y) + radiusY * 2);
            foreach (var cell in CellsFor(bounds))
                if (visited.Add(cell) && _eraserGrid.TryGetValue(cell, out var strokes))
                    result.UnionWith(strokes);
        }
        return result;
    }

    private void AddToEraserIndex(Stroke stroke)
    {
        var bounds = stroke.GetBounds();
        if (bounds.IsEmpty || !double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Top)
            || !double.IsFinite(bounds.Right) || !double.IsFinite(bounds.Bottom)) return;

        var minX = CellCoordinate(bounds.Left);
        var minY = CellCoordinate(bounds.Top);
        var maxX = CellCoordinate(bounds.Right);
        var maxY = CellCoordinate(bounds.Bottom);
        _eraserStrokeCells[stroke] = (minX, minY, maxX, maxY);
        for (var x = minX; x <= maxX; x++)
        for (var y = minY; y <= maxY; y++)
        {
            var cell = (x, y);
            if (!_eraserGrid.TryGetValue(cell, out var strokes))
                _eraserGrid[cell] = strokes = [];
            strokes.Add(stroke);
        }
    }

    private void RemoveFromEraserIndex(Stroke stroke)
    {
        if (!_eraserStrokeCells.Remove(stroke, out var cells)) return;
        for (var x = cells.MinX; x <= cells.MaxX; x++)
        for (var y = cells.MinY; y <= cells.MaxY; y++)
        {
            var cell = (x, y);
            if (!_eraserGrid.TryGetValue(cell, out var strokes)) continue;
            strokes.Remove(stroke);
            if (strokes.Count == 0) _eraserGrid.Remove(cell);
        }
    }

    private static IEnumerable<(int X, int Y)> CellsFor(Rect bounds)
    {
        var minX = CellCoordinate(bounds.Left);
        var minY = CellCoordinate(bounds.Top);
        var maxX = CellCoordinate(bounds.Right);
        var maxY = CellCoordinate(bounds.Bottom);
        for (var x = minX; x <= maxX; x++)
        for (var y = minY; y <= maxY; y++)
            yield return (x, y);
    }

    private static int CellCoordinate(double value) =>
        (int)Math.Floor(value / EraserGridCellSize);

    private void LogZOrder()
    {
        var above = IsAboveTarget;
        if (_lastAboveTarget == above) return;
        DiagnosticLog.Write($"built-in ink z-order hwnd={Handle} target={_targetHandle} topmost={IsTopmost} above={above}");
        _lastAboveTarget = above;
    }

    private void Input_OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.StylusDevice is not null || e.ChangedButton != MouseButton.Left) return;
        BeforeWriting?.Invoke();
        _contacts.Begin("mouse", 0, Environment.TickCount64);
        TraceInput("contact-down kind=mouse id=0");
        LogInput("mouse");
        if (_tool == PointerTool.Eraser)
        {
            e.Handled = true;
            if (_lastEraserPoint is not null) return;
            BeginEraser(e.GetPosition(InkSurface), showContactIndicator: false);
            _eraserMouse = true;
            if (!InkSurface.CaptureMouse()) { _contacts.LostCapture("mouse", 0, Environment.TickCount64); EndEraser(); }
        }
    }
    private void Input_OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.StylusDevice is not null || e.ChangedButton != MouseButton.Left) return;
        _contacts.End("mouse", 0, Environment.TickCount64);
        TraceInput("contact-up kind=mouse id=0");
        if (_eraserMouse)
        {
            AddEraserPoint(e.GetPosition(InkSurface));
            EndEraser();
            e.Handled = true;
        }
    }
    private void Input_OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.StylusDevice is null && e.LeftButton == MouseButtonState.Pressed) TraceMove("mouse", 0);
        if (e.StylusDevice is null && _eraserMouse && e.LeftButton == MouseButtonState.Pressed)
        {
            AddEraserPoint(e.GetPosition(InkSurface));
            e.Handled = true;
        }
    }
    private void Input_OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // Promoted mouse leave events must not hide an active touch/pen indicator.
        if (_lastEraserPoint is null) HideEraser();
    }
    private void Input_OnStylusDown(object sender, StylusDownEventArgs e)
    {
        if (_tool == PointerTool.Eraser && e.StylusDevice.TabletDevice.Type == TabletDeviceType.Touch) return;
        BeforeWriting?.Invoke();
        _writingStyluses[e.StylusDevice.Id] = e.StylusDevice;
        _contacts.Begin("stylus", e.StylusDevice.Id, Environment.TickCount64);
        TraceInput($"contact-down kind=stylus id={e.StylusDevice.Id} tablet={e.StylusDevice.TabletDevice.Type} inAir={e.StylusDevice.InAir}");
        LogInput("stylus");
        if (_tool == PointerTool.Eraser)
        {
            e.Handled = true;
            if (_lastEraserPoint is not null) return;
            BeginEraser(e.GetPosition(InkSurface));
            _eraserStylus = e.StylusDevice;
            if (!e.StylusDevice.Capture(InkSurface))
            { _contacts.LostCapture("stylus", e.StylusDevice.Id, Environment.TickCount64); EndEraser(); }
        }
    }
    private void Input_OnStylusMove(object sender, StylusEventArgs e)
    {
        if (!e.StylusDevice.InAir) TraceMove("stylus", e.StylusDevice.Id);
        if (_eraserStylus != e.StylusDevice) return;
        AddEraserPoints(e.GetStylusPoints(InkSurface));
        e.Handled = true;
    }
    private void Input_OnStylusUp(object sender, StylusEventArgs e)
    {
        if (_tool == PointerTool.Eraser && e.StylusDevice.TabletDevice.Type == TabletDeviceType.Touch) return;
        _contacts.End("stylus", e.StylusDevice.Id, Environment.TickCount64);
        _writingStyluses.Remove(e.StylusDevice.Id);
        TraceInput($"contact-up kind=stylus id={e.StylusDevice.Id}");
        if (_eraserStylus != e.StylusDevice) return;
        AddEraserPoints(e.GetStylusPoints(InkSurface));
        EndEraser();
        e.Handled = true;
    }
    private void Input_OnTouchDown(object? sender, TouchEventArgs e)
    {
        BeforeWriting?.Invoke();
        _writingTouches[e.TouchDevice.Id] = e.TouchDevice;
        _contacts.Begin("touch", e.TouchDevice.Id, Environment.TickCount64);
        TraceInput($"contact-down kind=touch id={e.TouchDevice.Id}");
        LogInput("touch");
        if (_tool == PointerTool.Eraser)
        {
            e.Handled = true;
            if (_lastEraserPoint is not null) return;
            BeginEraser(e.GetTouchPoint(InkSurface).Position);
            _eraserTouch = e.TouchDevice;
            if (!e.TouchDevice.Capture(InkSurface))
            { _contacts.LostCapture("touch", e.TouchDevice.Id, Environment.TickCount64); EndEraser(); }
        }
    }
    private void Input_OnTouchMove(object? sender, TouchEventArgs e)
    {
        TraceMove("touch", e.TouchDevice.Id);
        if (_tool != PointerTool.Eraser) return;
        e.Handled = true;
        if (_eraserTouch != e.TouchDevice) return;
        foreach (var point in e.GetIntermediateTouchPoints(InkSurface)) AddEraserPoint(point.Position, updateIndicator: false);
        AddEraserPoint(e.GetTouchPoint(InkSurface).Position);
    }
    private void Input_OnTouchUp(object? sender, TouchEventArgs e)
    {
        _contacts.End("touch", e.TouchDevice.Id, Environment.TickCount64);
        _writingTouches.Remove(e.TouchDevice.Id);
        TraceInput($"contact-up kind=touch id={e.TouchDevice.Id}");
        if (_tool != PointerTool.Eraser) return;
        e.Handled = true;
        if (_eraserTouch != e.TouchDevice) return;
        AddEraserPoint(e.GetTouchPoint(InkSurface).Position);
        EndEraser();
    }

    private static double SurfaceSize(double actual, double requested) => actual > 0 && double.IsFinite(actual)
        ? actual : double.IsFinite(requested) && requested > 0 ? requested : 0;
}
