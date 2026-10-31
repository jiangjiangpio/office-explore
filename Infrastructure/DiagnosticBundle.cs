using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using OfficeTouchBar.Models;

namespace OfficeTouchBar.Infrastructure;

internal static class DiagnosticBundle
{
    internal static string SettingsSummary(AppSettings settings) => JsonSerializer.Serialize(new
    {
        settings.SettingsVersion, settings.InkEngine, settings.AutoRun, settings.ResetMouseOnPresentationChange,
        settings.AutoClosePalette, settings.ConfineCursorWhileInking, settings.SuppressEdgeGesturesWhileInking,
        settings.DeletePreviousDaysInk, settings.DuplicateDisplayOnStartup, settings.ShowOnlyDuringSlideShow,
        settings.FollowPowerPointWindow, settings.ButtonScale, settings.PenSize, settings.Language
    }, new JsonSerializerOptions { WriteIndented = true });

    internal static async Task<string> ExportAsync(string settingsSummary, string displaySummary)
    {
        DiagnosticLog.Ink("USER-FAULT-MARKER diagnostic export requested");
        var flushed = await DiagnosticLog.FlushAsync();
        return await Task.Run(() => Export(settingsSummary, displaySummary, flushed));
    }

    private static string Export(string settingsSummary, string displaySummary, bool flushed)
    {
        Directory.CreateDirectory(DiagnosticLog.BundleDirectory);
        var path = Path.Combine(DiagnosticLog.BundleDirectory,
            $"OfficeTouchBar-Diagnostics-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}.zip");
        var report = new StringBuilder();
        report.AppendLine($"ExportedLocal={DateTime.Now:O}\nExportedUtc={DateTime.UtcNow:O}\nLogsFlushed={flushed}\nFailedLogWrites={DiagnosticLog.FailedWrites}");
        report.AppendLine($"OfficeTouchBar={typeof(DiagnosticBundle).Assembly.GetName().Version}");
        report.AppendLine($"OS={RuntimeInformation.OSDescription}\nRuntime={RuntimeInformation.FrameworkDescription}\nArchitecture={RuntimeInformation.ProcessArchitecture}");
        report.AppendLine(displaySummary);
        // Read executable versions only; never open Office COM, PPT or ink files.
        foreach (var process in Process.GetProcessesByName("POWERPNT"))
        {
            using (process)
            {
                try { report.AppendLine($"PowerPoint pid={process.Id} version={process.MainModule?.FileVersionInfo.FileVersion}"); }
                catch (Exception ex) { report.AppendLine($"PowerPoint version unavailable: {ex.GetType().Name}"); }
            }
        }
        try
        {
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            AddText(archive, "settings-summary.json", settingsSummary);
            AddText(archive, "README.txt", "本包包含诊断日志、已知设置与环境信息，不包含 PPT、截图或内置笔迹文件。\nUSER-FAULT-MARKER 标记导出操作的时间，便于定位之前的断笔。\n");
            var files = new List<string>();
            foreach (var name in new[] { "diagnostic.log", "diagnostic.log.1" })
                if (File.Exists(Path.Combine(DiagnosticLog.RootDirectory, name))) files.Add(Path.Combine(DiagnosticLog.RootDirectory, name));
            if (Directory.Exists(DiagnosticLog.InkDirectory))
                files.AddRange(Directory.EnumerateFiles(DiagnosticLog.InkDirectory, "ink-events-*.log*"));
            foreach (var file in files)
            {
                try
                {
                    using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var target = archive.CreateEntry("logs/" + Path.GetFileName(file), CompressionLevel.Fastest).Open();
                    source.CopyTo(target);
                }
                catch (IOException ex) { report.AppendLine($"Log skipped {Path.GetFileName(file)}: {ex.GetType().Name}"); }
            }
            AddText(archive, "environment.txt", report.ToString());
            return path;
        }
        catch
        {
            try { File.Delete(path); } catch { }
            throw;
        }
    }

    private static void AddText(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
