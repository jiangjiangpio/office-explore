using Microsoft.Win32;

namespace OfficeTouchBar.Infrastructure;

public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "OfficeTouchBar";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue(AppName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true)
            ?? Registry.CurrentUser.CreateSubKey(RunKey, true);

        if (enabled)
        {
            var exe = Environment.ProcessPath ?? string.Empty;
            key.SetValue(AppName, $"\"{exe}\" --startup");
        }
        else
        {
            key.DeleteValue(AppName, false);
        }
    }
}
