using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;
using OfficeTouchBar.Infrastructure;
using OfficeTouchBar.Models;
using WpfButton = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Localization = OfficeTouchBar.Infrastructure.Localization;
using Brushes = System.Windows.Media.Brushes;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace OfficeTouchBar;

public enum PointerTool { Mouse, Pen, Highlighter, Eraser }

public partial class OverlayWindow : Window
{
    private TouchDevice? _dragTouch;
    private Point _touchStart;
    private Point _windowStart;
    private double _scale = 1;
    private double _fitScale = 1;
    private DockSide _dockSide = DockSide.Right;
    private PointerTool _pointerTool = PointerTool.Mouse;
    private bool _lockPosition;
    private bool _increaseOpacityOnInteraction;
    private double _restOpacity = 0.2;
    private double _interactionOpacity = 0.85;
    private bool _settingInkSizes;
    private bool _deferredPaletteLayout;
    private Size _lastFitArea = Size.Empty;
    private System.Windows.Media.Color _selectedColor =
        (System.Windows.Media.Color)ColorConverter.ConvertFromString("#E53935");
    public bool IsDragging { get; private set; }
    public nint Handle => new WindowInteropHelper(this).EnsureHandle();
    public double MainWidth => 58 * _scale * _fitScale;
    public double MainHeight => RootBorder.DesiredSize.Height * _scale * _fitScale;
    public Rect MainBounds => new(RootBorder.PointToScreen(new Point(0, 0)),
        RootBorder.PointToScreen(new Point(RootBorder.ActualWidth, RootBorder.ActualHeight)));
    public bool PaletteOpen => PenPalette.Visibility == Visibility.Visible;

    public event Action? PreviousClicked;
    public event Action? NextClicked;
    public event Action? ExitClicked;
    public event Action<PointerTool>? PointerToolClicked;
    public event Action<System.Windows.Media.Color>? PenColorClicked;
    public event Action? ClearInkClicked;
    public event Action? PenButtonClicked;
    public event Action<double, double>? InkSizesChanged;
    public event Action? PositionChanged;
    public event Action? LayoutChanged;

    public OverlayWindow()
    {
        InitializeComponent();
        MouseEnter += (_, _) => UpdateInteractionOpacity(true);
        MouseLeave += (_, _) => UpdateInteractionOpacity(false);
        AddHandler(TouchDownEvent, new EventHandler<TouchEventArgs>((_, _) => UpdateInteractionOpacity(true)), true);
        AddHandler(TouchUpEvent, new EventHandler<TouchEventArgs>((_, _) => UpdateInteractionOpacity(IsMouseOver)), true);
        IsVisibleChanged += (_, _) => UpdateInteractionOpacity(false);
        SourceInitialized += (_, _) =>
        {
            NativeMethods.SetWindowLongPtr(Handle, -20,
                NativeMethods.GetWindowLongPtr(Handle, -20) | 0x08000000 | 0x80);
            HwndSource.FromHwnd(Handle)?.AddHook(WindowHook);
        };
    }

    private nint WindowHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x02E0) _lastFitArea = Size.Empty; // WM_DPICHANGED
        if (message == 0x0047 && lParam != 0) // WM_WINDOWPOSCHANGED
        {
            var position = Marshal.PtrToStructure<WindowPosition>(lParam);
            if ((position.Flags & 0x3) != 0x3 || (position.Flags & (0x0040 | 0x0080)) != 0
                || (position.Flags & 0x0004) == 0)
                DiagnosticLog.Ink($"toolbar-window-pos hwnd={hwnd} after={position.InsertAfter} x={position.X} y={position.Y} width={position.Width} height={position.Height} flags=0x{position.Flags:X} foreground={NativeMethods.GetForegroundWindow()}");
        }
        if (message == 0x21) { handled = true; return 3; } // MA_NOACTIVATE still delivers the click.
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition
    {
        public nint Window, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }

    public void ApplySettings(AppSettings settings)
    {
        Localization.Apply(this, settings.Language);
        _scale = settings.ButtonScale;
        SetInkSizes(settings.PenSize, settings.EraserSize);
        SetInkEngine(settings.InkEngine);
        _dockSide = settings.DockSide;
        _lockPosition = settings.LockPosition;
        _restOpacity = settings.OverlayOpacity;
        _interactionOpacity = settings.InteractionOpacity;
        _increaseOpacityOnInteraction = settings.IncreaseOpacityOnInteraction;
        UpdateInteractionOpacity(false);
        var brush = Brushes.White;
        RootBorder.Background = PenPalette.Background = brush;
        foreach (var button in Descendants<WpfButton>(RootBorder).Concat(Descendants<WpfButton>(PenPalette)))
            if (button.Tag is not string tag || !tag.StartsWith('#')) button.Background = brush;
        ExitButton.Visibility = settings.ShowSlideBrowser ? Visibility.Visible : Visibility.Collapsed;
        PageButton.Visibility = settings.ShowPageNumber ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelectionVisuals();
        SetExpansionSide(_dockSide);
        UpdateDimensions();
    }

    internal void UpdateInteractionOpacity(bool interacting) =>
        ContentRoot.Opacity = interacting && _increaseOpacityOnInteraction
            ? Math.Max(_restOpacity, _interactionOpacity) : _restOpacity;

    public void SetExpansionSide(DockSide side)
    {
        RootBorder.HorizontalAlignment = side == DockSide.Right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        PenPalette.HorizontalAlignment = side == DockSide.Right ? HorizontalAlignment.Left : HorizontalAlignment.Right;
    }

    public void FitToMonitor(Size availableDip)
    {
        if (_lastFitArea == availableDip) return;
        var raw = BaseSize();
        _fitScale = Math.Min(1, Math.Min(availableDip.Width / (raw.Width * _scale), availableDip.Height / (raw.Height * _scale)));
        UpdateDimensions();
        _lastFitArea = availableDip;
    }

    private Size BaseSize()
    {
        RootBorder.Measure(new Size(58, double.PositiveInfinity));
        PenPalette.Measure(new Size(PenPalette.Width, double.PositiveInfinity));
        return new Size(PaletteOpen ? PenPalette.Width + 64 : 58,
            Math.Max(RootBorder.DesiredSize.Height, PaletteOpen ? PenPalette.DesiredSize.Height : 0));
    }

    private void UpdateDimensions()
    {
        _lastFitArea = Size.Empty;
        var size = BaseSize();
        ContentScale.ScaleX = ContentScale.ScaleY = _scale * _fitScale;
        Width = size.Width * _scale * _fitScale;
        Height = size.Height * _scale * _fitScale;
    }

    public void UpdateSnapshot(PowerPointSnapshot snapshot)
    {
        PageText.Text = snapshot.TotalSlides > 0
            ? $"{(snapshot.CurrentSlide > 0 ? snapshot.CurrentSlide.ToString() : "--")}/{snapshot.TotalSlides}" : "--/--";
        PenPalette.IsEnabled = snapshot.IsSlideShow;
    }

    public void SetPointerTool(PointerTool tool)
    {
        _pointerTool = tool;
        UpdateSelectionVisuals();
    }

    public void SetInkEngine(InkEngine engine)
    {
        BuiltInSizePanel.Visibility = engine == InkEngine.BuiltIn ? Visibility.Visible : Visibility.Collapsed;
        UpdateDimensions();
    }

    public void SetInkSizes(double pen, double eraser)
    {
        _settingInkSizes = true;
        PenSizeSlider.Value = pen;
        _settingInkSizes = false;
        UpdateInkSizeLabels();
    }

    private void SizeSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PenSizeValue is null) return;
        UpdateInkSizeLabels();
        if (!_settingInkSizes) InkSizesChanged?.Invoke(PenSizeSlider.Value, InkOverlayWindow.EraserContactSize);
    }

    private void UpdateInkSizeLabels()
    {
        PenSizeValue.Text = $"{PenSizeSlider.Value:0.#}";
    }

    private void UpdateSelectionVisuals()
    {
        foreach (var pair in new[]
        {
            (MouseToolButton, PointerTool.Mouse), (PenToolButton, PointerTool.Pen),
            (HighlighterToolButton, PointerTool.Highlighter), (EraserToolButton, PointerTool.Eraser)
        })
            MarkSelected(pair.Item1, pair.Item2 == _pointerTool);
        MarkSelected(PenButton, _pointerTool != PointerTool.Mouse);
        foreach (WpfButton button in ColorGrid.Children)
            MarkSelected(button, button.Tag is string hex
                && (System.Windows.Media.Color)ColorConverter.ConvertFromString(hex) == _selectedColor);
    }

    public void SetPenColor(System.Windows.Media.Color color)
    {
        _selectedColor = color;
        UpdateSelectionVisuals();
    }

    private static void MarkSelected(WpfButton button, bool selected)
    {
        button.BorderBrush = selected ? Brushes.Black :
            new SolidColorBrush(System.Windows.Media.Color.FromArgb(85, 144, 150, 158));
        button.BorderThickness = new Thickness(selected ? 3 : 1);
    }

    public void ClosePalette(bool deferWindowLayout = false)
    {
        if (!PaletteOpen) return;
        PenPalette.Visibility = Visibility.Collapsed;
        if (deferWindowLayout) { _deferredPaletteLayout = true; return; }
        UpdateDimensions();
        LayoutChanged?.Invoke();
    }

    internal void CompleteDeferredPaletteLayout()
    {
        if (!_deferredPaletteLayout) return;
        _deferredPaletteLayout = false;
        UpdateDimensions();
        LayoutChanged?.Invoke();
    }

    private void PreviousButton_OnClick(object sender, RoutedEventArgs e) => PreviousClicked?.Invoke();
    private void NextButton_OnClick(object sender, RoutedEventArgs e) => NextClicked?.Invoke();
    private void ExitButton_OnClick(object sender, RoutedEventArgs e) => ExitClicked?.Invoke();
    private void ClearInkButton_OnClick(object sender, RoutedEventArgs e) => ClearInkClicked?.Invoke();
    private void PenButton_OnClick(object sender, RoutedEventArgs e)
    {
        PenPalette.Visibility = PaletteOpen ? Visibility.Collapsed : Visibility.Visible;
        UpdateDimensions();
        LayoutChanged?.Invoke();
        PenButtonClicked?.Invoke();
    }
    private void ClosePaletteButton_OnClick(object sender, RoutedEventArgs e) => ClosePalette();
    private void PointerToolButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton { Tag: string tag } && Enum.TryParse<PointerTool>(tag, out var tool))
            PointerToolClicked?.Invoke(tool);
    }
    private void ColorButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton { Tag: string hex })
            PenColorClicked?.Invoke((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
    }
    private void RootBorder_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_lockPosition || e.StylusDevice is not null || FindAncestor<WpfButton>(e.OriginalSource as DependencyObject) is not null) return;
        IsDragging = true;
        try { DragMove(); }
        catch (InvalidOperationException) { }
        finally { IsDragging = false; PositionChanged?.Invoke(); }
        e.Handled = true;
    }
    private void RootBorder_OnTouchDown(object sender, TouchEventArgs e)
    {
        if (_lockPosition || _dragTouch is not null || FindAncestor<WpfButton>(e.OriginalSource as DependencyObject) is not null) return;
        _dragTouch = e.TouchDevice;
        _touchStart = PointToScreen(e.GetTouchPoint(this).Position);
        var bounds = NativeMethods.Bounds(Handle);
        _windowStart = bounds.TopLeft;
        IsDragging = true;
        RootBorder.CaptureTouch(e.TouchDevice);
        e.Handled = true;
    }
    private void RootBorder_OnTouchMove(object sender, TouchEventArgs e)
    {
        if (_dragTouch != e.TouchDevice) return;
        var offset = PointToScreen(e.GetTouchPoint(this).Position) - _touchStart;
        NativeMethods.SetWindowPos(Handle, 0, (int)(_windowStart.X + offset.X), (int)(_windowStart.Y + offset.Y), 0, 0, 0x0015);
        e.Handled = true;
    }
    private void RootBorder_OnTouchUp(object sender, TouchEventArgs e)
    {
        if (_dragTouch != e.TouchDevice) return;
        _dragTouch = null;
        IsDragging = false;
        RootBorder.ReleaseTouchCapture(e.TouchDevice);
        PositionChanged?.Invoke();
        e.Handled = true;
    }
    private void RootBorder_OnLostTouchCapture(object sender, TouchEventArgs e)
    {
        if (_dragTouch != e.TouchDevice) return;
        _dragTouch = null;
        IsDragging = false;
        PositionChanged?.Invoke();
    }
    private static T? FindAncestor<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T target) return target;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
