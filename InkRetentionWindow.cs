using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OfficeTouchBar.Infrastructure;
using Button = System.Windows.Controls.Button;
using Localization = OfficeTouchBar.Infrastructure.Localization;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;

namespace OfficeTouchBar;

internal sealed class InkRetentionWindow : Window
{
    private readonly DispatcherTimer _layerTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    internal bool KeepInk { get; private set; } = true;
    internal Button KeepButton { get; }
    internal Button DiscardButton { get; }
    internal Button CloseButton { get; }

    internal InkRetentionWindow(string presentationName, string language, Rect? presentationBounds = null, bool dailyCleanup = false)
    {
        Title = Localization.Get("保留墨迹？", language);
        Width = 460;
        SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(240, SystemParameters.WorkArea.Height - 40);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.White;
        UseLayoutRounding = true;
        var accent = new SolidColorBrush(Color.FromRgb(194, 59, 27));
        var body = new StackPanel { Margin = new Thickness(24, 18, 24, 20) };
        var heading = new Grid { Margin = new Thickness(0, 0, 0, 20) };
        heading.ColumnDefinitions.Add(new ColumnDefinition());
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock
        {
            Text = "OfficeTouchBar", FontSize = 20, FontWeight = FontWeights.SemiBold,
            Foreground = accent, VerticalAlignment = VerticalAlignment.Center
        });
        CloseButton = FlatButton("", Brushes.White, Brushes.Gray, Brushes.Transparent);
        CloseButton.Width = CloseButton.Height = 28;
        CloseButton.MinWidth = CloseButton.MinHeight = 28;
        CloseButton.Padding = new Thickness(6);
        CloseButton.ToolTip = Localization.Get("关闭", language);
        System.Windows.Automation.AutomationProperties.SetName(CloseButton, Localization.Get("关闭", language));
        CloseButton.Content = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 0,0 L 10,10 M 10,0 L 0,10"),
            Stroke = Brushes.Gray, StrokeThickness = 1.2, Width = 10, Height = 10
        };
        CloseButton.Click += (_, _) => Close();
        Grid.SetColumn(CloseButton, 1);
        heading.Children.Add(CloseButton);
        body.Children.Add(heading);

        var message = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        message.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        message.ColumnDefinitions.Add(new ColumnDefinition());
        message.Children.Add(new System.Windows.Controls.Image
        {
            Source = WarningImage(), Width = 28, Height = 28,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        });
        var question = new TextBlock
        {
            Text = Localization.Get("是否保留内置墨迹批注？", language),
            FontSize = 14, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(question, 1);
        message.Children.Add(question);
        body.Children.Add(message);
        body.Children.Add(new TextBlock
        {
            Text = presentationName, ToolTip = presentationName, FontSize = 12,
            Foreground = Brushes.DimGray, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(40, 0, 0, 8)
        });
        body.Children.Add(new TextBlock
        {
            Text = Localization.Get("墨迹单独保存，不写入 PPT。放弃将删除此文稿全部已存内置墨迹。", language)
                + (dailyCleanup ? "\n" + Localization.Get("已开启按日清理：保留的墨迹将在次日清除；程序关闭时顺延至下次启动。", language) : ""),
            FontSize = 12, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(40, 0, 0, 20)
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        KeepButton = FlatButton(Localization.Get("保留", language), accent, Brushes.White, accent);
        KeepButton.IsDefault = true;
        KeepButton.IsCancel = true;
        KeepButton.Click += (_, _) => Close();
        DiscardButton = FlatButton(Localization.Get("放弃", language), Brushes.White, Brushes.Black, Brushes.DarkGray);
        DiscardButton.Margin = new Thickness(8, 0, 0, 0);
        DiscardButton.Click += (_, _) => { KeepInk = false; Close(); };
        buttons.Children.Add(KeepButton);
        buttons.Children.Add(DiscardButton);
        body.Children.Add(buttons);
        Content = new Border
        {
            BorderBrush = Brushes.DarkGray, BorderThickness = new Thickness(1), Background = Brushes.White,
            Child = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        };
        ContentRendered += (_, _) =>
        {
            PositionNearPresentation(presentationBounds);
            MaintainLayer();
            Activate();
            KeepButton.Focus();
            _layerTimer.Start();
        };
        _layerTimer.Tick += (_, _) => MaintainLayer();
        Closed += (_, _) => _layerTimer.Stop();
    }

    private void MaintainLayer()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var foreground = NativeMethods.GetForegroundWindow();
        if (handle == 0 || (foreground != handle && !NativeMethods.IsPowerPointWindow(foreground))) return;
        WindowLayerPolicy.EnsurePromptAbove(handle, NativeMethods.FindPresentationWindows(), out _);
    }

    private void PositionNearPresentation(Rect? bounds)
    {
        if (bounds is not { IsEmpty: false } target || target.Width <= 0 || target.Height <= 0) return;
        var center = new System.Drawing.Point((int)(target.Left + target.Width / 2), (int)(target.Top + target.Height / 2));
        var area = System.Windows.Forms.Screen.FromPoint(center).WorkingArea;
        var scale = NativeMethods.DpiScaleAt(center.X, center.Y);
        var width = Math.Min(area.Width, (int)Math.Ceiling(ActualWidth * scale));
        var height = Math.Min(area.Height, (int)Math.Ceiling(ActualHeight * scale));
        var x = Math.Clamp(center.X - width / 2, area.Left, area.Right - width);
        var y = Math.Clamp(center.Y - height / 2, area.Top, area.Bottom - height);
        NativeMethods.SetWindowPos(new WindowInteropHelper(this).Handle, -1, x, y, width, height, 0x0210);
    }

    private static Button FlatButton(string text, Brush background, Brush foreground, Brush border)
    {
        var button = new Button
        {
            Content = text, MinWidth = 80, MinHeight = 30, Padding = new Thickness(12, 5, 12, 5),
            FontSize = 13, Background = background, Foreground = foreground, BorderBrush = border,
            BorderThickness = new Thickness(1)
        };
        var surface = new FrameworkElementFactory(typeof(Border));
        surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
        surface.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        surface.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
        surface.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
        surface.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        surface.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = surface };
        var hover = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(OpacityProperty, 0.8));
        template.Triggers.Add(hover);
        button.Template = template;
        return button;
    }

    private static BitmapImage WarningImage()
    {
        using var bitmap = System.Drawing.SystemIcons.Warning.ToBitmap();
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
