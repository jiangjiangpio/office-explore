using System.Windows;
using System.Windows.Threading;
using OfficeTouchBar.Infrastructure;
using OfficeTouchBar.Models;
using Application = System.Windows.Application;
using Forms = System.Windows.Forms;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Localization = OfficeTouchBar.Infrastructure.Localization;

namespace OfficeTouchBar;

public sealed class MainController : IDisposable
{
    private static readonly System.Windows.Media.Color DefaultPenColor =
        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E53935");
    private readonly AppSettings _settings;
    private readonly PowerPointBridge _bridge = new();
    private SnapshotPoller? _snapshotPoller;
    private SnapshotPoller? _retiredPoller;
    private int _readerGeneration;
    private string _readerSession = "";
    private bool _usingNativeFallback;
    private readonly OverlayWindow _overlay = new();
    private readonly InkOverlayWindow _inkOverlay = new();
    private readonly InkStore _inkStore = new();
    private readonly DisplaySwitchService _displaySwitch = new();
    private readonly DispatcherTimer _dayTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private DateOnly _inkDay = DateOnly.FromDateTime(DateTime.Now);
    private DateOnly _lastCleanupDay;
    private string _nativeSession = "";
    private readonly InkInputProtection _inputProtection = new();
    private NativeInputProtection.ForegroundWatcher? _foregroundWatcher;
    private readonly WindowLayerPolicy _windowLayers = new();
    private readonly DispatcherTimer _protectionTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _inkSessionTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _inkSizeSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private sealed record InkSession(InkPageKey Page, Rect Bounds);
    private readonly Dictionary<nint, InkSession> _inkSessions = [];
    private readonly PresentationToolPolicy _presentationTools = new();
    private readonly ExitTransitionPolicy _exitTransition = new();
    private readonly OfficeExitOperation _officeExit = new();
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly Forms.ToolStripMenuItem _runningItem;
    private SettingsWindow? _settingsWindow;
    private InkRetentionWindow? _retentionWindow;
    private bool _disposed;
    private bool _running;
    private int _focusGeneration;
    private InkEngine _inkEngine = InkEngine.Office;
    private PointerTool _officeTool = PointerTool.Mouse;
    private PointerTool _builtInTool = PointerTool.Mouse;
    private System.Windows.Media.Color _penColor = DefaultPenColor;
    private InkPageKey? _activeInkPage;
    private nint _activeInkTarget;
    private readonly InkPageContinuityPolicy _pageContinuity = new();
    private readonly InkPreparationRetry _preparationRetry = new();
    private readonly ForegroundContinuityPolicy _foregroundContinuity = new();
    private readonly List<KeyboardMessage> _deferredKeys = [];
    private nint _deferredKeyTarget;
    private bool _keyBufferOverflow;
    private bool _positionPending;
    private bool _exportingDiagnostics;
    private bool _inkDirty;
    private long _inkRevision;
    private Task? _pendingInkSave;
    private long _pendingSaveRevision;
    private DateTime _inkChangedAt;
    private nint _preparedBuiltInTarget;
    private bool _restoreOfficeTool;
    private PowerPointSnapshot? _pendingSnapshot;
    private int _snapshotDispatchQueued;
    private string? _lastVisibilityReason;
    private Rect _lastOverlayFrame = Rect.Empty;
    private nint _lastOverlayTarget;
    private nint _promotedSlideShow;
    private bool _showingInkPrompt;
    private bool _exitInProgress;
    private DateTime _retryInkPromptAfter;
    private readonly HashSet<string> _retentionConfirmedPresentations =
        new(StringComparer.OrdinalIgnoreCase);

    public MainController()
    {
        _settings = AppSettings.Load();
        _inkStore.DeletePreviousDays = _settings.DeletePreviousDaysInk;
        _dayTimer.Tick += (_, _) => CleanExpiredInk();
        _inkOverlay.BeforeWriting += CleanExpiredInk;
        _inkOverlay.KeyboardHandoff = (message, key, detail) =>
        {
            if (!_running || _disposed || _showingInkPrompt || _exitInProgress
                || _settingsWindow is { IsVisible: true } || _inkEngine != InkEngine.BuiltIn) return false;
            if (key == 0x1b) return false;
            var target = _bridge.Snapshot.WindowHandle;
            _inkOverlay.TraceInput($"keyboard-message msg=0x{message:X} ownInjection={NativeMethods.IsOwnInputMessage()}");
            if (_inkOverlay.HasActiveContact || _deferredKeys.Count > 0 || _keyBufferOverflow)
            {
                if (_deferredKeys.Count == 0) _deferredKeyTarget = target;
                if (_deferredKeys.Count < 256 && !_keyBufferOverflow)
                    _deferredKeys.Add(new KeyboardMessage(message, key, detail));
                else
                {
                    _deferredKeys.Clear();
                    _keyBufferOverflow = true;
                    DiagnosticLog.Ink("keyboard deferred buffer overflow; cancelling unsent batch");
                }
                if (!_inkOverlay.HasActiveContact) FlushDeferredKeyboard();
                return true;
            }
            var result = NativeMethods.HandoffKeyboard(target, _inkOverlay.Handle, message, key, detail);
            if (result) { _focusGeneration++; PromoteSlideShow(target); PollOffice(); }
            return result;
        };
        _inkEngine = _settings.InkEngine;
        _bridge.PreferSlideShowForActivePresentation = _inkEngine == InkEngine.BuiltIn;
        _running = _settings.AutoRun;
        _protectionTimer.Tick += (_, _) =>
        {
            if (!_inkOverlay.IsWriting)
            {
                _overlay.CompleteDeferredPaletteLayout();
                if (_positionPending) { _positionPending = false; PositionOverlay(true); }
                FlushDeferredKeyboard();
            }
            UpdateInputProtection();
        };
        _inkOverlay.IsVisibleChanged += (_, _) => UpdateInputProtection();
        _inkOverlay.Deactivated += (_, _) => UpdateInputProtection();
        _timer.Tick += (_, _) => PollOffice();
        _inkSessionTimer.Tick += (_, _) => CheckEndedInkSessions();
        _inkSizeSaveTimer.Tick += (_, _) =>
        {
            _inkSizeSaveTimer.Stop();
            try { _settings.Save(); }
            catch (Exception ex) { DiagnosticLog.Write($"ink size save failed: {ex}"); }
        };
        _overlay.PreviousClicked += () => Navigate(_bridge.Previous, "上一项");
        _overlay.NextClicked += () => Navigate(_bridge.Next, "下一项");
        _overlay.PointerToolClicked += SelectPointerTool;
        _overlay.PenColorClicked += SelectPenColor;
        _overlay.InkSizesChanged += (pen, eraser) =>
        {
            _settings.PenSize = pen;
            ConfigureBuiltInTool();
            _inkSizeSaveTimer.Stop();
            _inkSizeSaveTimer.Start();
        };
        _overlay.PenButtonClicked += () =>
        {
            if (_inkEngine == InkEngine.BuiltIn)
                PromoteSlideShow(_bridge.Snapshot.WindowHandle);
        };
        _overlay.ClearInkClicked += ClearCurrentInk;
        _overlay.ExitClicked += ExitSlideShow;
        _inkOverlay.ExitRequested += ExitSlideShow;
        _overlay.LayoutChanged += () => PositionOverlay(true);
        _overlay.PositionChanged += SavePosition;
        _inkOverlay.InkChanged += () =>
        {
            _inkDirty = true;
            _inkRevision++;
            _inkChangedAt = DateTime.UtcNow;
        };
        _inkOverlay.WritingStarted += () =>
        {
            if (_settings.AutoClosePalette) _overlay.ClosePalette(deferWindowLayout: true);
        };
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = "OfficeTouchBar 0.3.21"
        };
        _trayIcon.DoubleClick += (_, _) => ShowSettings();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开设置", null, (_, _) => ShowSettings());
        _runningItem = new Forms.ToolStripMenuItem();
        _runningItem.Click += (_, _) => SetRunning(!_running);
        menu.Items.Add(_runningItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Application.Current.Shutdown());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("标记故障并导出诊断包", null, (_, _) => ExportDiagnostics());
        menu.Items.Add("打开日志与诊断文件夹", null, (_, _) => OpenDiagnosticsDirectory());
        _trayIcon.ContextMenuStrip = menu;
        UpdateTrayLanguage();
    }

    public void Start(bool startup = false)
    {
        CleanExpiredInk();
        _dayTimer.Start();
        _displaySwitch.Start(startup && _settings.DuplicateDisplayOnStartup);
        try { StartupManager.SetEnabled(_settings.StartWithWindows); }
        catch (Exception ex) { Report("开机自启动", ex.Message); }
        _overlay.ApplySettings(_settings);
        _overlay.SetPointerTool(_inkEngine == InkEngine.Office ? _officeTool : _builtInTool);
        _overlay.SetPenColor(_penColor);
        var overlayHandle = _overlay.Handle;
        var inkHandle = _inkOverlay.Handle;
        _bridge.OverlayHandle = overlayHandle;
        _bridge.InkOverlayHandle = inkHandle;
        _inkOverlay.ToolbarHandle = overlayHandle;
        DiagnosticLog.Ink($"startup version=0.3.21.0 settings={DiagnosticBundle.SettingsSummary(_settings)}");
        StartSnapshotPoller();
        _foregroundWatcher = new NativeInputProtection.ForegroundWatcher(UpdateInputProtection);
        _protectionTimer.Start();
        SetRunning(_running);
        Report("OfficeTouchBar", _running ? "已在系统托盘后台运行。" : "已启动，侧栏检测已暂停。", false);
    }

    private void StartSnapshotPoller()
    {
        var overlayHandle = _overlay.Handle;
        var inkHandle = _inkOverlay.Handle;
        var dispatcher = Application.Current.Dispatcher;
        var generation = ++_readerGeneration;
        _snapshotPoller = new SnapshotPoller(() => new PowerPointBridge
        {
            OverlayHandle = overlayHandle,
            InkOverlayHandle = inkHandle
        }, snapshot =>
        {
            if (generation != Volatile.Read(ref _readerGeneration)) return;
            Interlocked.Exchange(ref _pendingSnapshot, snapshot);
            if (Interlocked.Exchange(ref _snapshotDispatchQueued, 1) != 0) return;
            dispatcher.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref _snapshotDispatchQueued, 0);
                var latest = Interlocked.Exchange(ref _pendingSnapshot, null);
                if (generation == _readerGeneration && latest is not null) ApplySnapshot(latest);
            }, DispatcherPriority.Background);
        });
        _snapshotPoller.SetPreferSlideShow(_inkEngine == InkEngine.BuiltIn);
    }

    private void SetRunning(bool running)
    {
        _running = running;
        _runningItem.Text = Localization.Get(running ? "暂停侧栏检测" : "恢复侧栏检测", _settings.Language);
        _focusGeneration++;
        if (running) { _timer.Start(); PollOffice(); }
        else
        {
            _timer.Stop();
            _inputProtection.Release();
            _inkOverlay.CancelInput("sidebar paused");
            SaveBuiltInInk(true);
            _inkOverlay.HideForReason("sidebar paused");
            ReleasePromotedSlideShow();
            _overlay.Hide();
        }
    }

    private void PollOffice()
    {
        if (_settingsWindow is { IsVisible: true }) return;
        if (_disposed || !_running || _showingInkPrompt || _exitInProgress) return;
        if (_inkOverlay.IsVisible && _inkOverlay.IsWriting) return;
        var foreground = NativeMethods.GetForegroundWindow();
        var target = foreground == _overlay.Handle || foreground == _inkOverlay.Handle
            ? _bridge.Snapshot.WindowHandle : foreground;
        var isOffice = NativeMethods.IsPowerPointWindow(target);
        var session = isOffice ? NativeMethods.ProcessIdentity(target) : "";
        if (_snapshotPoller is { IsStalled: true })
        {
            if (!_usingNativeFallback)
                DiagnosticLog.Write($"snapshot stalled; native fallback target={target} session={session}");
            _usingNativeFallback = true;
            // At most one abandoned STA. A timeout does not cancel a COM call.
            // Only a different PowerPoint process justifies replacing its reader.
            if (session.Length > 0 && session != _readerSession
                && (_retiredPoller is null || _retiredPoller.HasStopped))
            {
                _retiredPoller = _snapshotPoller;
                _retiredPoller.Dispose();
                _readerSession = session;
                StartSnapshotPoller();
            }
            ApplySnapshot(new PowerPointSnapshot(false, isOffice, isOffice && NativeMethods.IsSlideShowWindow(target),
                0, 0, isOffice ? NativeMethods.Bounds(target) : Rect.Empty, isOffice ? target : 0,
                ObservedForegroundHandle: foreground));
        }
        else
        {
            _usingNativeFallback = false;
            if (session.Length > 0) _readerSession = session;
        }
        _snapshotPoller?.Request();
    }

    private void ApplySnapshot(PowerPointSnapshot snapshot)
    {
        if (_settingsWindow is { IsVisible: true }) return;
        if (_disposed || !_running || _showingInkPrompt || _exitInProgress) return;
        if (_inkOverlay.IsVisible && _inkOverlay.IsWriting) return;
        var foreground = NativeMethods.GetForegroundWindow();
        if (_foregroundContinuity.Defer(foreground, _overlay.IsVisible,
            NativeMethods.IsWindowVisible(_bridge.Snapshot.WindowHandle), Environment.TickCount64))
        { _snapshotPoller?.Request(); return; }
        if (snapshot.ObservedForegroundHandle != foreground)
        { _snapshotPoller?.Request(); return; }
        // A completed background COM read may describe a window that has
        // already closed/hidden. Never revive it with SetWindowPos(SHOWWINDOW).
        if (snapshot.IsSlideShow && (!NativeMethods.IsSlideShowWindow(snapshot.WindowHandle)
            || !NativeMethods.IsWindowVisible(snapshot.WindowHandle))) return;
        try
        {
            var session = NativeMethods.ProcessIdentity(snapshot.WindowHandle);
            if (session.Length > 0 && session != _nativeSession)
            {
                _nativeSession = session;
                _preparedBuiltInTarget = 0;
                _preparationRetry.Reset();
                _pageContinuity.Confirm();
                _lastOverlayTarget = 0;
                _lastOverlayFrame = Rect.Empty;
                _exitTransition.Cancel();
                if (_settings.ResetMouseOnPresentationChange)
                    ResetPresentationInteractionState("PowerPoint process session changed");
                else { _focusGeneration++; _restoreOfficeTool = true; }
            }
            _bridge.UseSnapshot(snapshot);
            if (_settingsWindow is not { IsVisible: true }) ObservePresentation(snapshot);
            _overlay.UpdateSnapshot(snapshot);
            SaveBuiltInInk(false);
            if (_settingsWindow is { IsVisible: true }
                || _exitTransition.IsWaiting(NativeMethods.GetForegroundWindow(), DateTime.UtcNow, NativeMethods.IsSlideShowWindow)
                || !InteractionPolicy.ShouldShow(snapshot, _settings, _running))
            {
                LogVisibility(_settingsWindow is { IsVisible: true } ? "settings"
                    : !snapshot.IsForeground ? "foreground changed" : "display policy or exit transition", snapshot);
                SaveBuiltInInk(true);
                _inkOverlay.HideForReason(_settingsWindow is { IsVisible: true } ? "settings" : "visibility policy or exit");
                ReleasePromotedSlideShow();
                _overlay.Hide();
                _focusGeneration++;
                return;
            }
            PrepareInkEngine(snapshot);
            SyncBuiltInInk(snapshot);
            if (!_overlay.IsDragging) PositionOverlay();
            if (!_overlay.IsVisible) _overlay.Show();
            LogVisibility("visible", snapshot);
            if (NeedsBuiltInLayerCheck(_inkEngine, _builtInTool) && _inkOverlay.IsVisible)
                // Page changes can reorder the same slideshow window above both
                // overlays. PromoteSlideShow first performs a read-only check and
                // only changes Z-order when the stack is actually broken.
                PromoteSlideShow(snapshot.WindowHandle);
            else WindowLayerPolicy.EnsureToolbarAbove(snapshot.WindowHandle, _overlay.Handle);
            UpdateInputProtection();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"poll failed: {ex}");
            _inputProtection.Release();
            _inkOverlay.HideForReason("snapshot application exception");
            ReleasePromotedSlideShow();
            _overlay.Hide();
        }
    }

    private void LogVisibility(string reason, PowerPointSnapshot snapshot)
    {
        var state = $"{reason} target={snapshot.WindowHandle}";
        if (_lastVisibilityReason == state) return;
        _lastVisibilityReason = state;
        DiagnosticLog.Write($"sidebar {state} show={snapshot.IsSlideShow} foreground={snapshot.IsForeground}");
    }

    private Rect AvailableBounds()
    {
        var snapshot = _bridge.Snapshot;
        var screen = Forms.Screen.FromHandle(snapshot.WindowHandle);
        var area = snapshot.IsSlideShow ? screen.Bounds : screen.WorkingArea;
        return new Rect(area.X, area.Y, area.Width, area.Height);
    }

    private void PositionOverlay(bool force = false)
    {
        if (_inkOverlay.IsWriting) { _positionPending |= force; return; }
        var snapshot = _bridge.Snapshot;
        if (_disposed || _overlay.IsDragging || snapshot.WindowBounds.IsEmpty) return;
        var available = AvailableBounds();
        var targetBounds = Rect.Intersect(snapshot.WindowBounds, available);
        if (targetBounds.IsEmpty) targetBounds = available;
        var dpi = NativeMethods.GetDpiForWindow(snapshot.WindowHandle);
        var scale = dpi > 0 ? dpi / 96.0 : 1;
        var fixedPosition = !_settings.FollowPowerPointWindow && double.IsFinite(_settings.Left) && double.IsFinite(_settings.Top);
        if (fixedPosition)
        {
            var screen = Forms.Screen.FromPoint(new System.Drawing.Point((int)_settings.Left, (int)_settings.Top));
            var rect = snapshot.IsSlideShow ? screen.Bounds : screen.WorkingArea;
            available = new Rect(rect.X, rect.Y, rect.Width, rect.Height);
            scale = NativeMethods.DpiScaleAt(available.Left + 1, available.Top + 1);
        }
        _overlay.FitToMonitor(new Size(available.Width / scale, available.Height / scale));
        var size = new Size(_overlay.Width * scale, _overlay.Height * scale);
        var mainWidth = _overlay.MainWidth * scale;
        var mainHeight = _overlay.MainHeight * scale;
        var left = _settings.DockSide == DockSide.Right
            ? targetBounds.Right - mainWidth - _settings.EdgeOffset * scale
            : targetBounds.Left + _settings.EdgeOffset * scale;
        var top = targetBounds.Top + Math.Max(0, targetBounds.Height - mainHeight) * _settings.RelativeTop;
        if (fixedPosition)
        {
            left = _settings.Left;
            top = _settings.Top;
        }
        var extra = size.Width - mainWidth;
        var expandLeft = _settings.DockSide == DockSide.Right;
        if (expandLeft && left - extra < available.Left) expandLeft = false;
        else if (!expandLeft && left + size.Width > available.Right) expandLeft = true;
        _overlay.SetExpansionSide(expandLeft ? DockSide.Right : DockSide.Left);
        var frame = InteractionPolicy.Clamp(
            new Point(expandLeft ? left - extra : left, top - (size.Height - mainHeight) / 2), size, available);
        var stableFrame = new Rect((int)Math.Round(frame.X), (int)Math.Round(frame.Y),
            (int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height));
        if (!InteractionPolicy.HasSignificantFrameChange(_lastOverlayFrame, stableFrame,
            _lastOverlayTarget, snapshot.WindowHandle, force)) return;
        DiagnosticLog.Ink($"toolbar-place target={snapshot.WindowHandle} frame={stableFrame} force={force} dpi={dpi} palette={_overlay.PaletteOpen}");
        if (!NativeMethods.SetWindowPos(_overlay.Handle, 0, (int)stableFrame.X, (int)stableFrame.Y,
            (int)stableFrame.Width, (int)stableFrame.Height, 0x0010 | 0x0004)) return;
        _lastOverlayFrame = stableFrame;
        _lastOverlayTarget = snapshot.WindowHandle;
    }

    private void SavePosition()
    {
        if (_settings.LockPosition) return;
        try
        {
            var bounds = _overlay.MainBounds;
            var target = _bridge.Snapshot.WindowBounds;
            var dpi = NativeMethods.GetDpiForWindow(_overlay.Handle);
            var scale = dpi > 0 ? dpi / 96.0 : 1;
            if (_settings.FollowPowerPointWindow && !target.IsEmpty)
            {
                var leftDistance = Math.Abs(bounds.Left - target.Left);
                var rightDistance = Math.Abs(target.Right - bounds.Right);
                _settings.DockSide = leftDistance < rightDistance ? DockSide.Left : DockSide.Right;
                var distance = Math.Min(leftDistance, rightDistance) / scale;
                _settings.EdgeOffset = _settings.SnapToEdge && distance <= 24 ? 8 : distance;
                _settings.RelativeTop = Math.Clamp((bounds.Top - target.Top) / Math.Max(1, target.Height - bounds.Height), 0, 1);
            }
            else
            {
                var screen = Forms.Screen.FromPoint(new System.Drawing.Point((int)bounds.Left, (int)bounds.Top));
                var work = _bridge.Snapshot.IsSlideShow ? screen.Bounds : screen.WorkingArea;
                var left = bounds.Left;
                if (_settings.SnapToEdge)
                {
                    if (Math.Abs(left - work.Left) <= 24 * scale) left = work.Left + 8 * scale;
                    if (Math.Abs(work.Right - bounds.Right) <= 24 * scale) left = work.Right - bounds.Width - 8 * scale;
                }
                _settings.Left = left;
                _settings.Top = bounds.Top;
            }
            _settings.Save();
            PositionOverlay(true);
        }
        catch (Exception ex) { Report("保存位置失败", ex.Message); }
    }

    private void ShowSettings()
    {
        if (_showingInkPrompt || _exitInProgress) { _retentionWindow?.Activate(); return; }
        _inkOverlay.TraceInput("show-settings");
        _deferredKeys.Clear();
        _keyBufferOverflow = false;
        _inputProtection.Release();
        if (_settingsWindow is { IsVisible: true }) { _settingsWindow.Activate(); return; }
        _focusGeneration++;
        _inkOverlay.CancelInput("settings opened");
        SaveBuiltInInk(false);
        _inkOverlay.HideForReason("settings opened");
        ReleasePromotedSlideShow();
        _overlay.Hide();
        _settingsWindow = new SettingsWindow(_settings);
        _settingsWindow.SwitchDisplayRequested += _displaySwitch.SwitchToDuplicate;
        _settingsWindow.CleanDisplayProcessesRequested += () => _displaySwitch.Cleanup(true);
        _settingsWindow.SettingsSaved += ApplySettings;
        _settingsWindow.Closed += (_, _) => { _settingsWindow = null; PollOffice(); };
        _settingsWindow.Show();
    }

    private void ApplySettings(AppSettings updated)
    {
        // Persist first; on failure the settings window remains open and reports the error.
        StartupManager.SetEnabled(updated.StartWithWindows);
        updated.Save();
        _settings.StartWithWindows = updated.StartWithWindows;
        _settings.DuplicateDisplayOnStartup = updated.DuplicateDisplayOnStartup;
        if (!updated.DuplicateDisplayOnStartup || !updated.StartWithWindows) _displaySwitch.CancelStartup();
        _settings.DeletePreviousDaysInk = updated.DeletePreviousDaysInk;
        _inkStore.DeletePreviousDays = updated.DeletePreviousDaysInk;
        _lastCleanupDay = default;
        CleanExpiredInk();
        _settings.Language = updated.Language;
        UpdateTrayLanguage();
        _settings.AutoRun = updated.AutoRun;
        _settings.ShowWhenPowerPointActive = updated.ShowWhenPowerPointActive;
        _settings.ShowOnlyDuringSlideShow = updated.ShowOnlyDuringSlideShow;
        _settings.FollowPowerPointWindow = updated.FollowPowerPointWindow;
        _settings.SnapToEdge = updated.SnapToEdge;
        _settings.ShowSlideBrowser = updated.ShowSlideBrowser;
        _settings.ShowPageNumber = updated.ShowPageNumber;
        _settings.ResetMouseOnPresentationChange = updated.ResetMouseOnPresentationChange;
        _settings.AutoClosePalette = updated.AutoClosePalette;
        _settings.ConfineCursorWhileInking = updated.ConfineCursorWhileInking;
        _settings.SuppressEdgeGesturesWhileInking = updated.SuppressEdgeGesturesWhileInking;
        _settings.LockPosition = updated.LockPosition;
        _settings.IncreaseOpacityOnInteraction = updated.IncreaseOpacityOnInteraction;
        _settings.InteractionOpacity = updated.InteractionOpacity;
        _settings.PenSize = updated.PenSize;
        _settings.EraserSize = updated.EraserSize;
        SelectInkEngine(updated.InkEngine);
        _settings.InkEngine = updated.InkEngine;
        _settings.DockSide = updated.DockSide;
        _settings.OverlayOpacity = updated.OverlayOpacity;
        _settings.ButtonScale = updated.ButtonScale;
        _settings.RelativeTop = updated.RelativeTop;
        _settings.EdgeOffset = updated.EdgeOffset;
        _settings.Left = updated.Left;
        _settings.Top = updated.Top;
        _overlay.ApplySettings(_settings);
        PositionOverlay(true);
    }

    private bool RunCommand(Func<bool> command, string name, bool reportFailure = true)
    {
        if (_disposed || !_running) return false;
        var success = command();
        DiagnosticLog.Write($"command={name} target={_bridge.Snapshot.WindowHandle} success={success}");
        if (!success && reportFailure)
            Report(name, "当前 PowerPoint 状态不支持此操作，或放映窗口未能获得焦点。");
        return success;
    }

    private void ObservePresentation(PowerPointSnapshot snapshot)
    {
        var closed = _presentationTools.ForgetClosedWindow(NativeMethods.IsWindow);
        if (closed) ResetPresentationInteractionState("closed slideshow window");
        if (!_presentationTools.Observe(snapshot, _settings.ResetMouseOnPresentationChange)) return;
        ResetPresentationInteractionState("presentation/session changed");
    }

    private void ResetPresentationInteractionState(string reason)
    {
        _officeTool = _builtInTool = PointerTool.Mouse;
        _penColor = DefaultPenColor;
        _restoreOfficeTool = true;
        _preparedBuiltInTarget = 0;
        _preparationRetry.Reset();
        _focusGeneration++;
        _overlay.SetPointerTool(PointerTool.Mouse);
        _overlay.SetPenColor(_penColor);
        _inkOverlay.ConfigureTool(PointerTool.Mouse, _penColor, _settings.PenSize, _settings.EraserSize);
        _overlay.ClosePalette();
        DiagnosticLog.Write($"presentation interaction reset reason={reason}");
    }

    private void SelectInkEngine(InkEngine engine)
    {
        if (_inkEngine == engine) return;
        if (_inkEngine == InkEngine.BuiltIn) SaveBuiltInInk(true);
        _inkEngine = engine;
        _bridge.PreferSlideShowForActivePresentation = engine == InkEngine.BuiltIn;
        _snapshotPoller?.SetPreferSlideShow(engine == InkEngine.BuiltIn);
        _overlay.SetPointerTool(engine == InkEngine.Office ? _officeTool : _builtInTool);
        _preparedBuiltInTarget = 0;
        _preparationRetry.Reset();
        if (engine == InkEngine.Office)
        {
            _inkOverlay.HideForReason("Office ink engine selected");
            ReleasePromotedSlideShow();
            _restoreOfficeTool = true;
            return;
        }
        _restoreOfficeTool = false;
        ConfigureBuiltInTool();
    }

    private void SelectPointerTool(PointerTool tool)
    {
        if (_inkEngine == InkEngine.Office)
        {
            RunTool(() => tool == PointerTool.Pen ? _bridge.SetPenColorAndPen(_penColor)
                : _bridge.SetPointerTool(tool), "Office 墨迹工具", () =>
            {
                _officeTool = tool;
                _restoreOfficeTool = false;
                _overlay.SetPointerTool(tool);
            });
            return;
        }

        _builtInTool = tool;
        _overlay.SetPointerTool(tool);
        ConfigureBuiltInTool();
        // Explicit tool choice may need to undo an Office tool changed by a
        // real keyboard shortcut. Automatic polls do not invalidate this cache.
        _preparedBuiltInTarget = 0;
        _preparationRetry.Reset();
        PrepareInkEngine(_bridge.Snapshot);
        if (tool == PointerTool.Mouse)
        {
            SaveBuiltInInk(true);
            SyncBuiltInInk(_bridge.Snapshot);
        }
        else SyncBuiltInInk(_bridge.Snapshot);
        ActivateInkInput();
        PromoteSlideShow(_bridge.Snapshot.WindowHandle);
    }

    private void SelectPenColor(System.Windows.Media.Color color)
    {
        if (_inkEngine == InkEngine.Office)
        {
            RunTool(() => _bridge.SetPenColorAndPen(color),
                "Office 笔颜色并启用普通笔", () =>
                {
                    _penColor = color;
                    _officeTool = PointerTool.Pen;
                    _restoreOfficeTool = false;
                    _overlay.SetPointerTool(PointerTool.Pen);
                    _overlay.SetPenColor(color);
                    if (_settings.AutoClosePalette) _overlay.ClosePalette();
                });
        }
        else
        {
            _penColor = color;
            _overlay.SetPenColor(color);
            _builtInTool = PointerTool.Pen;
            _overlay.SetPointerTool(PointerTool.Pen);
            ConfigureBuiltInTool();
            PrepareInkEngine(_bridge.Snapshot);
            SyncBuiltInInk(_bridge.Snapshot);
            ActivateInkInput();
            PromoteSlideShow(_bridge.Snapshot.WindowHandle);
        }
    }

    private void ClearCurrentInk()
    {
        if (_inkEngine == InkEngine.Office)
        {
            RunTool(_bridge.ClearCurrentInk, "擦除当前页 Office 墨迹");
            return;
        }
        _inkOverlay.ClearPage();
        SaveBuiltInInk(true);
    }

    private void SyncBuiltInInk(PowerPointSnapshot snapshot)
    {
        if (_inkEngine != InkEngine.BuiltIn || !snapshot.IsSlideShow)
        {
            _inkOverlay.HideForReason("not a built-in slideshow");
            return;
        }
        if (snapshot.CurrentSlide <= 0 || string.IsNullOrWhiteSpace(snapshot.PresentationIdentity))
        {
            var sameTarget = _activeInkTarget == snapshot.WindowHandle;
            if (!sameTarget && _activeInkPage is not null)
            {
                if (!SaveBuiltInInk(true)) return;
                _inkOverlay.LoadPage(InkPageData.Empty());
                _activeInkPage = null;
                _activeInkTarget = 0;
                _inkDirty = false;
            }
            var showLast = _pageContinuity.ShowLastFrame(snapshot.WindowHandle, _activeInkTarget,
                _activeInkPage is not null, Environment.TickCount64);
            _inkOverlay.Place(snapshot.WindowHandle, snapshot.WindowBounds, true);
            ConfigureBuiltInTool();
            _inkOverlay.SetPageAvailability(false, showLast);
            return;
        }

        var key = new InkPageKey(snapshot.PresentationIdentity, snapshot.PresentationName,
            snapshot.CurrentSlide, snapshot.TotalSlides);
        _inkSessions[snapshot.WindowHandle] = new InkSession(key, snapshot.WindowBounds);
        if (!_inkSessionTimer.IsEnabled) _inkSessionTimer.Start();
        const bool show = true;
        if (_activeInkTarget != snapshot.WindowHandle || !InkPageContinuityPolicy.SamePage(_activeInkPage, key))
        {
            if (!SaveBuiltInInk(true)) return;
            _inkOverlay.Place(snapshot.WindowHandle, snapshot.WindowBounds, false);
            var data = _inkStore.Load(key);
            _inkDay = data.InkDay ?? DateOnly.FromDateTime(DateTime.Now);
            _inkOverlay.LoadPage(data);
            _activeInkPage = key;
            _activeInkTarget = snapshot.WindowHandle;
            _inkDirty = false;
        }
        else _activeInkPage = key;
        _pageContinuity.Confirm();
        _inkOverlay.Place(snapshot.WindowHandle, snapshot.WindowBounds, show);
        ConfigureBuiltInTool();
        _inkOverlay.SetPageAvailability(_preparedBuiltInTarget == snapshot.WindowHandle, true);
    }

    private void ConfigureBuiltInTool() =>
        _inkOverlay.ConfigureTool(_builtInTool, _penColor, _settings.PenSize, _settings.EraserSize);

    private void PrepareInkEngine(PowerPointSnapshot snapshot)
    {
        if (_inkOverlay.HasActiveContact) return;
        if (!snapshot.IsSlideShow || snapshot.WindowHandle == 0) return;
        if (_inkEngine == InkEngine.BuiltIn)
        {
            if (_preparedBuiltInTarget == snapshot.WindowHandle) return;
            if (!_preparationRetry.CanAttempt(Environment.TickCount64)) return;
            var success = _bridge.PrepareBuiltInMouse();
            DiagnosticLog.Write($"prepare built-in ink target={snapshot.WindowHandle} success={success}");
            if (success) _preparedBuiltInTarget = snapshot.WindowHandle;
            else
            {
                var alreadyReported = _preparationRetry.Exhausted;
                _preparationRetry.Failed(Environment.TickCount64);
                DiagnosticLog.Ink($"prepare-failed target={snapshot.WindowHandle} exhausted={_preparationRetry.Exhausted} foreground={NativeMethods.GetForegroundWindow()}");
                if (_preparationRetry.Exhausted && !alreadyReported)
                    Report("内置笔初始化", "PowerPoint 暂未就绪，程序会在松开触控后自动重试。");
            }
            PromoteSlideShow(snapshot.WindowHandle);
            return;
        }
        if (!snapshot.IsConnected) return;
        if (!_restoreOfficeTool) return;
        var restored = _bridge.SetPointerTool(_officeTool);
        DiagnosticLog.Write($"restore Office ink target={snapshot.WindowHandle} tool={_officeTool} success={restored}");
        if (restored) _restoreOfficeTool = false;
    }

    private void PromoteSlideShow(nint target)
    {
        if (_inkOverlay.HasActiveContact) return;
        if (_showingInkPrompt || target == 0 || !_inkOverlay.IsVisible || !_overlay.IsVisible
            || _exitTransition.IsWaiting(NativeMethods.GetForegroundWindow(), DateTime.UtcNow, NativeMethods.IsSlideShowWindow))
            return;
        var targetChanged = _promotedSlideShow != target;
        if (_promotedSlideShow != 0 && targetChanged)
            _windowLayers.ReleaseSlideShow(_promotedSlideShow);
        _promotedSlideShow = target;
        var success = _windowLayers.EnsureSlideShowStack(
            target, _inkOverlay.Handle, _overlay.Handle, out var repaired);
        if (!success)
            DiagnosticLog.Write($"ensure slideshow stack failed target={target} ink={_inkOverlay.Handle} toolbar={_overlay.Handle}");
        else if (repaired)
            DiagnosticLog.Write($"slideshow stack repaired target={target} ink={_inkOverlay.Handle} toolbar={_overlay.Handle}");
    }

    internal static bool NeedsBuiltInLayerCheck(InkEngine engine, PointerTool tool) =>
        engine == InkEngine.BuiltIn;

    private void ActivateInkInput()
    {
        if (_inkOverlay.HasActiveContact) return;
        if (_builtInTool != PointerTool.Mouse && _inkOverlay.IsVisible && _inkOverlay.PageInputAvailable)
            _inkOverlay.Activate();
        UpdateInputProtection();
    }

    private void UpdateInputProtection()
    {
        var snapshot = _bridge.Snapshot;
        var enabled = !_disposed && _running && !_showingInkPrompt && !_exitInProgress
            && _settingsWindow is not { IsVisible: true }
            && _inkEngine == InkEngine.BuiltIn && _builtInTool != PointerTool.Mouse && _activeInkPage is not null
            && _inkOverlay.IsVisible && _inkOverlay.IsEnabled && _inkOverlay.PageInputAvailable && snapshot.IsSlideShow
            && NativeMethods.IsSlideShowWindow(snapshot.WindowHandle)
            && NativeMethods.IsWindowVisible(snapshot.WindowHandle);
        if (!enabled) { _inputProtection.Release(); return; }
        var bounds = NativeMethods.Bounds(snapshot.WindowHandle);
        var monitor = Forms.Screen.FromHandle(snapshot.WindowHandle).Bounds;
        var fullScreen = bounds == new Rect(monitor.X, monitor.Y, monitor.Width, monitor.Height);
        _inputProtection.Update(true, _settings.ConfineCursorWhileInking,
            _settings.SuppressEdgeGesturesWhileInking, fullScreen, bounds,
            NativeMethods.GetForegroundWindow(), snapshot.WindowHandle, _inkOverlay.Handle, _overlay.Handle);
    }

    private void ReleasePromotedSlideShow()
    {
        _windowLayers.ReleaseSlideShow(_promotedSlideShow);
        _promotedSlideShow = 0;
    }

    private async void ExitSlideShow()
    {
        if (_exitInProgress || _showingInkPrompt) return;
        if (_exitTransition.IsWaiting(NativeMethods.GetForegroundWindow(), DateTime.UtcNow,
            NativeMethods.IsSlideShowWindow)) return;
        _exitInProgress = true;
        _focusGeneration++; // Cancel delayed focus restoration from older tool clicks.
        var inkEnabled = _inkOverlay.IsEnabled;
        var toolbarEnabled = _overlay.IsEnabled;
        _inkOverlay.CancelInput("exit input handoff");
        _inkOverlay.IsEnabled = false;
        _overlay.IsEnabled = false;
        try { await ExitSlideShowCore(); }
        catch (Exception ex) { Report("退出幻灯片放映", ex.Message); }
        finally
        {
            _exitInProgress = false;
            if (!_disposed)
            {
                _inkOverlay.IsEnabled = inkEnabled;
                _overlay.IsEnabled = toolbarEnabled;
                PollOffice();
            }
        }
    }

    private async Task ExitSlideShowCore()
    {
        if (_inkEngine == InkEngine.BuiltIn) _inkOverlay.CancelInput("explicit slideshow exit");
        _inkOverlay.EndEraser();
        var target = _bridge.Snapshot.WindowHandle;
        string? confirmedPresentation = null;
        InkPageKey? discardAfterExit = null;
        nint promptHandle = 0;
        if (_inkEngine == InkEngine.BuiltIn)
        {
            var confirmation = await ConfirmRetentionBeforeExit();
            if (!confirmation.Success || _disposed) return;
            confirmedPresentation = confirmation.Presentation;
            discardAfterExit = confirmation.Discard;
            promptHandle = confirmation.Handle;
        }
        var snapshot = _bridge.Snapshot;
        _exitTransition.Begin(target, DateTime.UtcNow, allowResume: _inkEngine != InkEngine.BuiltIn);
        try
        {
            _inkOverlay.HideForReason("confirmed slideshow exit");
            ReleasePromotedSlideShow();
            _overlay.ClosePalette();
            _overlay.Hide();
            var sent = _inkEngine == InkEngine.BuiltIn
                ? await _officeExit.RunAsync(() =>
                {
                    using var bridge = new PowerPointBridge();
                    bridge.UseSnapshot(snapshot);
                    return bridge.ExitBuiltInSlideShowAfterConfirmation(target);
                }, TimeSpan.FromSeconds(5))
                : _bridge.ExitSlideShowAfterConfirmation(target, promptHandle);
            if (_disposed) return;
            var result = sent ? ExitResult.Sent : ExitResult.Failed;
            if (sent)
            {
                if (discardAfterExit is not null)
                    _inkStore.DiscardPresentation(discardAfterExit);
                if (confirmedPresentation is not null)
                    _retentionConfirmedPresentations.Add(confirmedPresentation);
            }
            else _exitTransition.Cancel();
            DiagnosticLog.Write($"command=退出幻灯片放映 target={target} result={result}");
            if (result == ExitResult.Sent)
                // Release before a later show can reuse the native handle.
                _exitTransition.IsWaiting(NativeMethods.GetForegroundWindow(), DateTime.UtcNow,
                    NativeMethods.IsSlideShowWindow);
            if (result == ExitResult.Failed)
                Report("退出幻灯片放映", "当前 PowerPoint 状态不支持此操作，或放映窗口未能获得焦点。");
        }
        catch { _exitTransition.Cancel(); throw; }
    }

    private async Task<(bool Success, string? Presentation, InkPageKey? Discard, nint Handle)> ConfirmRetentionBeforeExit()
    {
        if (!await SaveBuiltInInkAsync()) return (false, null, null, 0);

        var snapshot = _bridge.Snapshot;
        if (!snapshot.IsSlideShow || snapshot.WindowHandle == 0) return (true, null, null, 0);
        if (!_inkSessions.TryGetValue(snapshot.WindowHandle, out var session))
        {
            if (_activeInkPage is null) return (true, null, null, 0);
            session = new InkSession(_activeInkPage, snapshot.WindowBounds);
        }

        var anotherSession = _inkSessions.Any(other => other.Key != snapshot.WindowHandle
            && NativeMethods.IsSlideShowWindow(other.Key)
            && string.Equals(other.Value.Page.PresentationIdentity,
                session.Page.PresentationIdentity, StringComparison.OrdinalIgnoreCase));
        if (anotherSession || !_inkStore.HasPresentationInk(session.Page)) return (true, null, null, 0);
        var choice = await ShowRetentionPrompt(session, snapshot.WindowHandle);
        return (choice.Success, session.Page.PresentationIdentity, choice.Keep ? null : session.Page, choice.Handle);
    }

    private async Task<(bool Success, bool Keep, nint Handle)> ShowRetentionPrompt(InkSession session, nint owner)
    {
        _showingInkPrompt = true;
        _focusGeneration++;
        _inputProtection.Release();
        _inkOverlay.CancelInput("retention prompt opened");
        // Keep the slide and ink visible behind the asynchronous confirmation.
        // Removing the promoted stack here exposes the editor before a choice.
        var inkEnabled = _inkOverlay.IsEnabled;
        var overlayEnabled = _overlay.IsEnabled;
        _inkOverlay.IsEnabled = false;
        _overlay.IsEnabled = false;
        try
        {
            var prompt = new InkRetentionWindow(session.Page.PresentationName,
                _settings.Language, session.Bounds, _settings.DeletePreviousDaysInk);
            _retentionWindow = prompt;
            var helper = new System.Windows.Interop.WindowInteropHelper(prompt);
            var promptHandle = helper.EnsureHandle();
            var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            prompt.Closed += (_, _) => closed.TrySetResult(prompt.KeepInk);
            prompt.Show();
            var keepInk = await closed.Task;
            return (!_disposed, keepInk, promptHandle);
        }
        catch (Exception ex)
        {
            Report("处理墨迹失败", ex.Message);
            _retryInkPromptAfter = DateTime.UtcNow.AddSeconds(30);
            return (false, true, 0);
        }
        finally
        {
            _retentionWindow = null;
            if (!_disposed) { _inkOverlay.IsEnabled = inkEnabled; _overlay.IsEnabled = overlayEnabled; }
            _showingInkPrompt = false;
        }
    }

    private async Task<bool> SaveBuiltInInkAsync()
    {
        try
        {
            if (_pendingInkSave is { } pending)
            {
                await pending;
                _pendingInkSave = null;
                if (_inkRevision == _pendingSaveRevision) _inkDirty = false;
            }
            if (_disposed || !_inkDirty || _activeInkPage is null) return !_disposed;
            var page = _activeInkPage;
            var data = _inkOverlay.CapturePage() with { InkDay = _inkDay };
            _pendingSaveRevision = _inkRevision;
            _pendingInkSave = Task.Run(() => _inkStore.Save(page, data));
            await _pendingInkSave;
            _pendingInkSave = null;
            if (_inkRevision == _pendingSaveRevision) _inkDirty = false;
            return true;
        }
        catch (Exception ex) { _pendingInkSave = null; Report("保存内置笔迹失败", ex.Message); return false; }
    }

    private bool SaveBuiltInInk(bool force)
    {
        try
        {
            if (_pendingInkSave is { } pending)
            {
                if (!force && !pending.IsCompleted) return true;
                _pendingInkSave = null;
                pending.GetAwaiter().GetResult();
                if (_inkRevision == _pendingSaveRevision) _inkDirty = false;
            }
            if (!_inkDirty || _activeInkPage is null) return true;
            if (!force && DateTime.UtcNow - _inkChangedAt < TimeSpan.FromMilliseconds(800)) return true;
            var page = _activeInkPage;
            var data = _inkOverlay.CapturePage() with { InkDay = _inkDay };
            if (force)
            {
                _inkStore.Save(page, data);
                _inkDirty = false;
            }
            else
            {
                // The snapshot owns cloned strokes. Serialization and disk IO do
                // not repaint or stall the live InkCanvas between short strokes.
                _pendingSaveRevision = _inkRevision;
                _pendingInkSave = Task.Run(() => _inkStore.Save(page, data));
            }
            return true;
        }
        catch (Exception ex) { Report("保存内置笔迹失败", ex.Message); return false; }
    }

    private async void CheckEndedInkSessions()
    {
        if (_disposed || _showingInkPrompt || _exitInProgress || DateTime.UtcNow < _retryInkPromptAfter) return;
        if (_inkOverlay.IsWriting && NativeMethods.IsSlideShowWindow(_activeInkTarget)) return;
        foreach (var session in _inkSessions.ToArray())
        {
            // A background editor or a transient COM failure is not a slide-show exit.
            if (NativeMethods.IsSlideShowWindow(session.Key)) continue;
            var ownsCanvas = InkPageContinuityPolicy.EndedSessionOwnsCanvas(session.Key, _activeInkTarget);
            _showingInkPrompt = true;
            if (ownsCanvas) _inkOverlay.CancelInput("slideshow window closed");
            if (ownsCanvas && !await SaveBuiltInInkAsync())
            {
                _showingInkPrompt = false;
                _retryInkPromptAfter = DateTime.UtcNow.AddSeconds(30);
                return;
            }
            if (_disposed) { _showingInkPrompt = false; return; }
            _showingInkPrompt = true;
            _focusGeneration++;
            DiagnosticLog.Ink($"ended-session target={session.Key} active={_activeInkTarget} ownsCanvas={ownsCanvas}");
            if (ownsCanvas)
            {
                _inkOverlay.HideForReason("canvas session closed");
                _overlay.Hide();
                ReleasePromotedSlideShow();
            }
            try
            {
                var anotherSession = _inkSessions.Any(other => other.Key != session.Key
                    && NativeMethods.IsSlideShowWindow(other.Key)
                    && string.Equals(other.Value.Page.PresentationIdentity, session.Value.Page.PresentationIdentity,
                        StringComparison.OrdinalIgnoreCase));
                var alreadyConfirmed = _retentionConfirmedPresentations.Remove(
                    session.Value.Page.PresentationIdentity);
                if (!alreadyConfirmed && !anotherSession && _inkStore.HasPresentationInk(session.Value.Page))
                {
                    var choice = await ShowRetentionPrompt(session.Value, 0);
                    if (!choice.Success || _disposed) return;
                    if (!choice.Keep) _inkStore.DiscardPresentation(session.Value.Page);
                }
                _inkSessions.Remove(session.Key);
                if (ownsCanvas)
                {
                    _activeInkPage = null;
                    _activeInkTarget = 0;
                    _inkOverlay.LoadPage(InkPageData.Empty());
                    _inkDirty = false;
                }
                if (ownsCanvas) { _preparedBuiltInTarget = 0; _preparationRetry.Reset(); }
            }
            catch (Exception ex)
            {
                Report("处理墨迹失败", ex.Message);
                _retryInkPromptAfter = DateTime.UtcNow.AddSeconds(30);
            }
            finally { _showingInkPrompt = false; }
        }
        if (_inkSessions.Count == 0) _inkSessionTimer.Stop();
    }

    private void UpdateTrayLanguage()
    {
        var items = _trayIcon.ContextMenuStrip!.Items;
        items[0].Text = Localization.Get("打开设置", _settings.Language);
        _runningItem.Text = Localization.Get(_running ? "暂停侧栏检测" : "恢复侧栏检测", _settings.Language);
        items[3].Text = Localization.Get("退出", _settings.Language);
        items[5].Text = Localization.Get("标记故障并导出诊断包", _settings.Language);
        items[6].Text = Localization.Get("打开日志与诊断文件夹", _settings.Language);
    }

    private void FlushDeferredKeyboard()
    {
        if (_inkOverlay.HasActiveContact) return;
        if (_keyBufferOverflow) { _keyBufferOverflow = false; _deferredKeys.Clear(); return; }
        if (_deferredKeys.Count == 0) return;
        var messages = _deferredKeys.ToArray();
        _deferredKeys.Clear();
        if (_disposed || !_running || _showingInkPrompt || _exitInProgress
            || _settingsWindow is { IsVisible: true } || _inkEngine != InkEngine.BuiltIn
            || _deferredKeyTarget != _bridge.Snapshot.WindowHandle
            || NativeMethods.GetForegroundWindow() != _inkOverlay.Handle)
        {
            DiagnosticLog.Ink("keyboard deferred batch cancelled: interaction owner changed");
            return;
        }
        if (NativeMethods.HandoffKeyboardBatch(_deferredKeyTarget, _inkOverlay.Handle, messages))
        { _focusGeneration++; PromoteSlideShow(_deferredKeyTarget); PollOffice(); }
    }

    private async void ExportDiagnostics()
    {
        if (_exportingDiagnostics) return;
        _exportingDiagnostics = true;
        try
        {
            var displays = string.Join(Environment.NewLine, Forms.Screen.AllScreens.Select(screen =>
                $"Monitor primary={screen.Primary} bounds={screen.Bounds} dpiScale={NativeMethods.DpiScaleAt(screen.Bounds.Left + 1, screen.Bounds.Top + 1)}"));
            var path = await DiagnosticBundle.ExportAsync(DiagnosticBundle.SettingsSummary(_settings), displays);
            if (!_disposed) Report("诊断包已保存", path + "\n托盘菜单可打开日志与诊断文件夹。", false);
        }
        catch (Exception ex) { if (!_disposed) Report("导出诊断包失败", ex.Message); }
        finally { _exportingDiagnostics = false; }
    }

    private void OpenDiagnosticsDirectory()
    {
        try
        {
            System.IO.Directory.CreateDirectory(DiagnosticLog.RootDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DiagnosticLog.RootDirectory)
                { UseShellExecute = true });
        }
        catch (Exception ex) { Report("打开诊断文件夹失败", ex.Message); }
    }

    private async void RunTool(Func<bool> command, string name, Action? onSuccess = null)
    {
        var generation = ++_focusGeneration;
        var success = RunCommand(command, name, reportFailure: false);
        if (!success)
        {
            // PowerPoint may still be returning focus from the palette click.
            // Retry once without changing the UI state until the command succeeds.
            await Task.Delay(35);
            if (_disposed || !_running || generation != _focusGeneration) return;
            success = RunCommand(command, name, reportFailure: false);
        }
        if (!success)
        {
            if (_inkEngine == InkEngine.Office && _bridge.LastAppliedTool is { } actual)
            {
                _officeTool = actual;
                _overlay.SetPointerTool(actual);
            }
            Report(name, "当前 PowerPoint 状态不支持此操作，或放映窗口未能获得焦点。");
            return;
        }
        onSuccess?.Invoke();
        var target = _bridge.Snapshot.WindowHandle;
        NativeMethods.RestoreFocus(target, _overlay.Handle);
        await Task.Delay(40);
        if (_disposed || !_running || generation != _focusGeneration) return;
        if (InteractionPolicy.CanContinue(generation, _focusGeneration, target, _bridge.Snapshot, _disposed))
            NativeMethods.RestoreFocus(target, _overlay.Handle);
    }

    private void Navigate(Func<bool> command, string name)
    {
        if (_inkOverlay.HasActiveContact) _inkOverlay.CancelInput("explicit navigation");
        var before = _bridge.Snapshot;
        if (_disposed || !_running || !before.IsForeground || before.WindowHandle == 0) return;
        DiagnosticLog.Write($"navigation begin target={before.WindowHandle} page={before.CurrentSlide} foreground={NativeMethods.GetForegroundWindow()} inkVisible={_inkOverlay.IsVisible} strokes={_inkOverlay.StrokeCount} toolbarVisible={_overlay.IsVisible}");
        _focusGeneration++;
        if (!command())
        {
            Report(name, "当前 PowerPoint 状态不支持此操作，或放映窗口未能获得焦点。");
            return;
        }
        DiagnosticLog.Write($"command={name} target={before.WindowHandle} success=True");
        _pageContinuity.Navigation(before.WindowHandle);
        // Restore immediately after activation instead of waiting for the next
        // COM snapshot (which may be delayed during the first animation).
        PromoteSlideShow(before.WindowHandle);
        PollOffice();
    }

    private void CleanExpiredInk()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (!_settings.DeletePreviousDaysInk || _lastCleanupDay == today
            || _inkOverlay.IsWriting || _showingInkPrompt || _exitInProgress) return;
        try
        {
            // Drain the independent snapshot before clearing memory/disk.
            if (!SaveBuiltInInk(true)) return;
            if (_inkDay < today)
            {
                _inkOverlay.LoadPage(InkPageData.Empty());
                _inkDirty = false;
                _inkRevision++;
                _inkDay = today;
                _retentionConfirmedPresentations.Clear();
            }
            _inkStore.DeleteExpired();
            _lastCleanupDay = today;
        }
        catch (Exception ex) { DiagnosticLog.Write($"daily ink cleanup failed: {ex}"); }
    }

    private void Report(string title, string message, bool warning = true)
    {
        DiagnosticLog.Write($"{title}: {message}");
        _trayIcon.ShowBalloonTip(3000, Localization.Get(title, _settings.Language),
            Localization.Get(message, _settings.Language), warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _retentionWindow?.Close();
        _dayTimer.Stop();
        _displaySwitch.Dispose();
        _foregroundWatcher?.Dispose();
        _protectionTimer.Stop();
        _inputProtection.Dispose();
        _timer.Stop();
        _snapshotPoller?.Dispose();
        _inkSessionTimer.Stop();
        _inkSizeSaveTimer.Stop();
        try { _settings.Save(); }
        catch (Exception ex) { DiagnosticLog.Write($"settings save on exit failed: {ex}"); }
        _inkOverlay.CancelInput("application exit");
        SaveBuiltInInk(true);
        ReleasePromotedSlideShow();
        _settingsWindow?.Close();
        _inkOverlay.Close();
        _overlay.Close();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _bridge.Dispose();
    }
}
