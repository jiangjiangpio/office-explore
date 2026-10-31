using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace OfficeTouchBar.Models;

public enum DockSide
{
    Left,
    Right
}

public sealed class AppSettings
{
    public const int CurrentVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public int SettingsVersion { get; set; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    public bool AutoRun { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public bool DuplicateDisplayOnStartup { get; set; } = true;
    public bool DeletePreviousDaysInk { get; set; } = true;
    public bool ShowWhenPowerPointActive { get; set; } = true;
    public bool ShowOnlyDuringSlideShow { get; set; } = true;
    public bool FollowPowerPointWindow { get; set; } = true;
    public bool SnapToEdge { get; set; } = true;
    public bool ShowSlideBrowser { get; set; } = true;
    public bool ShowPageNumber { get; set; } = true;
    public bool ResetMouseOnPresentationChange { get; set; } = true;
    public bool AutoClosePalette { get; set; } = true;
    public bool ConfineCursorWhileInking { get; set; } = true;
    public bool SuppressEdgeGesturesWhileInking { get; set; } = true;
    public bool LockPosition { get; set; }
    public bool IncreaseOpacityOnInteraction { get; set; }
    public double InteractionOpacity { get; set; } = 0.85;
    public InkEngine InkEngine { get; set; } = InkEngine.Office;
    public string Language { get; set; } = "zh-CN";
    public DockSide DockSide { get; set; } = DockSide.Right;
    public double OverlayOpacity { get; set; } = 0.20;
    public double ButtonScale { get; set; } = 1.0;
    public double PenSize { get; set; } = 3.2;
    public double EraserSize { get; set; } = 30;
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double RelativeTop { get; set; } = 0.5;
    public double EdgeOffset { get; set; } = 8;

    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OfficeTouchBar");

    public static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    public static AppSettings Load(string? filePath = null)
    {
        filePath ??= FilePath;
        try
        {
            if (File.Exists(filePath))
            {
                var json = File.ReadAllText(filePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
                var changed = false;
                if (settings.SettingsVersion < 1)
                {
                    if (!File.Exists(filePath + ".pre-v1.bak"))
                        File.Copy(filePath, filePath + ".pre-v1.bak", overwrite: false);
                    settings.ShowOnlyDuringSlideShow = true;
                    settings.OverlayOpacity = 0.20;
                    changed = true;
                }
                if (settings.SettingsVersion < 2)
                {
                    if (!File.Exists(filePath + ".pre-v2.bak"))
                        File.Copy(filePath, filePath + ".pre-v2.bak", overwrite: false);
                    settings.InkEngine = InkEngine.Office;
                    changed = true;
                }
                if (changed)
                {
                    settings.SettingsVersion = CurrentVersion;
                    settings.Normalize();
                    settings.Save(filePath);
                }
                settings.Normalize();
                return settings;
            }
        }
        catch (Exception ex)
        {
            Infrastructure.DiagnosticLog.Write($"settings load failed: {ex}");
        }

        return new AppSettings { SettingsVersion = CurrentVersion };
    }

    public void Normalize()
    {
        OverlayOpacity = double.IsFinite(OverlayOpacity) ? Math.Clamp(OverlayOpacity, 0.1, 1) : 0.2;
        ButtonScale = double.IsFinite(ButtonScale) ? Math.Clamp(ButtonScale, 0.4, 2.0) : 1;
        PenSize = double.IsFinite(PenSize) ? Math.Clamp(PenSize, 1, 20) : 3.2;
        EraserSize = double.IsFinite(EraserSize) ? Math.Clamp(EraserSize, 8, 64) : 30;
        InteractionOpacity = double.IsFinite(InteractionOpacity) ? Math.Clamp(InteractionOpacity, 0.1, 1) : 0.85;
        RelativeTop = double.IsFinite(RelativeTop) ? Math.Clamp(RelativeTop, 0, 1) : 0.5;
        EdgeOffset = double.IsFinite(EdgeOffset) ? Math.Max(0, EdgeOffset) : 8;
        if (!Enum.IsDefined(DockSide)) DockSide = DockSide.Right;
        if (!Enum.IsDefined(InkEngine)) InkEngine = InkEngine.Office;
        if (!Infrastructure.Localization.Codes.Contains(Language)) Language = "zh-CN";
        if (!double.IsFinite(Left) || !double.IsFinite(Top)) Left = Top = double.NaN;
    }

    public void Save(string? filePath = null)
    {
        filePath ??= FilePath;
        Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var json = JsonSerializer.Serialize(this, JsonOptions);
        var temporary = filePath + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, filePath, overwrite: true);
    }
}
