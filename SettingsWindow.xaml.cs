using System.Windows;
using OfficeTouchBar.Models;
using OfficeTouchBar.Infrastructure;
using Localization = OfficeTouchBar.Infrastructure.Localization;

namespace OfficeTouchBar;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    public event Action<AppSettings>? SettingsSaved;
    public event Func<string>? SwitchDisplayRequested;
    public event Func<string>? CleanDisplayProcessesRequested;

    private void SwitchDisplay_OnClick(object sender, RoutedEventArgs e) =>
        DisplayStatus.Text = SwitchDisplayRequested?.Invoke() ?? "显示切换服务未连接。";
    private void CleanDisplay_OnClick(object sender, RoutedEventArgs e) =>
        DisplayStatus.Text = CleanDisplayProcessesRequested?.Invoke() ?? "显示切换服务未连接。";

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 40);
        _settings = Clone(settings);
        LanguageBox.ItemsSource = Localization.Names;
        LanguageBox.SelectedIndex = Math.Max(0, Array.IndexOf(Localization.Codes, settings.Language));
        LoadValues();
        Localization.Apply(this, settings.Language);
        LanguageBox.SelectionChanged += (_, _) =>
            Localization.Apply(this, Localization.Codes[Math.Max(0, LanguageBox.SelectedIndex)]);
        OpacitySlider.ValueChanged += (_, _) => OpacityValue.Text = $"{OpacitySlider.Value:P0}";
        ButtonScaleSlider.ValueChanged += (_, _) => ButtonScaleValue.Text = $"{ButtonScaleSlider.Value:P0}";
        InteractionOpacitySlider.ValueChanged += (_, _) => InteractionOpacityValue.Text = $"{InteractionOpacitySlider.Value:P0}";
        EdgeOffsetSlider.ValueChanged += (_, _) => EdgeOffsetValue.Text = $"{EdgeOffsetSlider.Value:0} DIP";
        RelativeTopSlider.ValueChanged += (_, _) => RelativeTopValue.Text = $"{RelativeTopSlider.Value:P0}";
    }

    private void LoadValues()
    {
        StartWithWindowsBox.IsChecked = _settings.StartWithWindows;
        DuplicateDisplayBox.IsChecked = _settings.DuplicateDisplayOnStartup;
        DeletePreviousDaysInkBox.IsChecked = _settings.DeletePreviousDaysInk;
        AutoRunBox.IsChecked = _settings.AutoRun;
        ShowWhenActiveBox.IsChecked = _settings.ShowWhenPowerPointActive;
        OnlyDuringSlideShowBox.IsChecked = _settings.ShowOnlyDuringSlideShow;
        FollowWindowBox.IsChecked = _settings.FollowPowerPointWindow;
        SnapToEdgeBox.IsChecked = _settings.SnapToEdge;
        ShowBrowserBox.IsChecked = _settings.ShowSlideBrowser;
        ShowPageNumberBox.IsChecked = _settings.ShowPageNumber;
        ResetMouseBox.IsChecked = _settings.ResetMouseOnPresentationChange;
        AutoClosePaletteBox.IsChecked = _settings.AutoClosePalette;
        ConfineCursorBox.IsChecked = _settings.ConfineCursorWhileInking;
        SuppressEdgeGesturesBox.IsChecked = _settings.SuppressEdgeGesturesWhileInking;
        LockPositionBox.IsChecked = _settings.LockPosition;
        InteractionOpacityBox.IsChecked = _settings.IncreaseOpacityOnInteraction;
        InteractionOpacitySlider.Value = 1 - _settings.InteractionOpacity;
        InteractionOpacityValue.Text = $"{InteractionOpacitySlider.Value:P0}";
        EdgeOffsetSlider.Maximum = Math.Max(200, _settings.EdgeOffset);
        EdgeOffsetSlider.Value = _settings.EdgeOffset;
        EdgeOffsetValue.Text = $"{EdgeOffsetSlider.Value:0} DIP";
        RelativeTopSlider.Value = _settings.RelativeTop;
        RelativeTopValue.Text = $"{RelativeTopSlider.Value:P0}";
        InkEngineBox.SelectedIndex = _settings.InkEngine == InkEngine.BuiltIn ? 1 : 0;
        DockSideBox.SelectedIndex = _settings.DockSide == DockSide.Right ? 1 : 0;
        OpacitySlider.Value = 1 - _settings.OverlayOpacity;
        ButtonScaleSlider.Value = _settings.ButtonScale;
        OpacityValue.Text = $"{OpacitySlider.Value:P0}";
        ButtonScaleValue.Text = $"{ButtonScaleSlider.Value:P0}";
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        _settings.StartWithWindows = StartWithWindowsBox.IsChecked == true;
        _settings.DuplicateDisplayOnStartup = DuplicateDisplayBox.IsChecked == true;
        _settings.DeletePreviousDaysInk = DeletePreviousDaysInkBox.IsChecked == true;
        _settings.Language = Localization.Codes[Math.Max(0, LanguageBox.SelectedIndex)];
        _settings.AutoRun = AutoRunBox.IsChecked == true;
        _settings.ShowWhenPowerPointActive = ShowWhenActiveBox.IsChecked == true;
        _settings.ShowOnlyDuringSlideShow = OnlyDuringSlideShowBox.IsChecked == true;
        _settings.FollowPowerPointWindow = FollowWindowBox.IsChecked == true;
        _settings.SnapToEdge = SnapToEdgeBox.IsChecked == true;
        _settings.ShowSlideBrowser = ShowBrowserBox.IsChecked == true;
        _settings.ShowPageNumber = ShowPageNumberBox.IsChecked == true;
        _settings.ResetMouseOnPresentationChange = ResetMouseBox.IsChecked == true;
        _settings.AutoClosePalette = AutoClosePaletteBox.IsChecked == true;
        _settings.ConfineCursorWhileInking = ConfineCursorBox.IsChecked == true;
        _settings.SuppressEdgeGesturesWhileInking = SuppressEdgeGesturesBox.IsChecked == true;
        _settings.LockPosition = LockPositionBox.IsChecked == true;
        _settings.IncreaseOpacityOnInteraction = InteractionOpacityBox.IsChecked == true;
        _settings.InteractionOpacity = 1 - InteractionOpacitySlider.Value;
        _settings.EdgeOffset = EdgeOffsetSlider.Value;
        _settings.RelativeTop = RelativeTopSlider.Value;
        _settings.InkEngine = InkEngineBox.SelectedIndex == 1 ? InkEngine.BuiltIn : InkEngine.Office;
        _settings.DockSide = DockSideBox.SelectedIndex == 1 ? DockSide.Right : DockSide.Left;
        _settings.OverlayOpacity = 1 - OpacitySlider.Value;
        _settings.ButtonScale = ButtonScaleSlider.Value;
        try { SettingsSaved?.Invoke(_settings); Close(); }
        catch (Exception ex)
        {
            Infrastructure.DiagnosticLog.Write($"settings save failed: {ex}");
            System.Windows.MessageBox.Show(this, ex.Message, Localization.Get("保存设置失败", _settings.Language), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private static AppSettings Clone(AppSettings value) => new()
    {
        SettingsVersion = value.SettingsVersion,
        ExtensionData = value.ExtensionData is null ? null : new(value.ExtensionData),
        AutoRun = value.AutoRun,
        StartWithWindows = value.StartWithWindows,
        DuplicateDisplayOnStartup = value.DuplicateDisplayOnStartup,
        DeletePreviousDaysInk = value.DeletePreviousDaysInk,
        ShowWhenPowerPointActive = value.ShowWhenPowerPointActive,
        ShowOnlyDuringSlideShow = value.ShowOnlyDuringSlideShow,
        FollowPowerPointWindow = value.FollowPowerPointWindow,
        SnapToEdge = value.SnapToEdge,
        ShowSlideBrowser = value.ShowSlideBrowser,
        ShowPageNumber = value.ShowPageNumber,
        ResetMouseOnPresentationChange = value.ResetMouseOnPresentationChange,
        AutoClosePalette = value.AutoClosePalette,
        ConfineCursorWhileInking = value.ConfineCursorWhileInking,
        SuppressEdgeGesturesWhileInking = value.SuppressEdgeGesturesWhileInking,
        LockPosition = value.LockPosition,
        IncreaseOpacityOnInteraction = value.IncreaseOpacityOnInteraction,
        InteractionOpacity = value.InteractionOpacity,
        PenSize = value.PenSize,
        EraserSize = value.EraserSize,
        InkEngine = value.InkEngine,
        Language = value.Language,
        DockSide = value.DockSide,
        OverlayOpacity = value.OverlayOpacity,
        ButtonScale = value.ButtonScale,
        Left = value.Left,
        Top = value.Top,
        RelativeTop = value.RelativeTop,
        EdgeOffset = value.EdgeOffset
    };
}
