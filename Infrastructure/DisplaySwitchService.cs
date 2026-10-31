using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using OfficeTouchBar.Models;

namespace OfficeTouchBar.Infrastructure;

// Route A: use the Windows display switch utility. No Office process is touched.
internal sealed class DisplaySwitchService : IDisposable
{
    internal sealed record OwnedProcess(int Pid, long StartedUtcTicks, string Path, DateTime CleanupAtUtc);
    private readonly string _path;
    private readonly string _journal;
    private readonly List<OwnedProcess> _owned = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(15) };
    private DateTime? _startupAt;
    private bool _disposed;
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;
    internal Func<string, OwnedProcess> Launch { get; set; }
    internal Func<OwnedProcess, bool> StopOwned { get; set; } = StopVerifiedProcess;

    internal DisplaySwitchService(string? journal = null)
    {
        _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DisplaySwitch.exe");
        _journal = journal ?? Path.Combine(AppSettings.DirectoryPath, "display-switch-processes.json");
        Launch = path =>
        {
            using var process = Process.Start(new ProcessStartInfo(path, "/clone")
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden })
                ?? throw new IOException("无法启动 Windows 显示切换工具。");
            return new OwnedProcess(process.Id, process.StartTime.ToUniversalTime().Ticks, path,
                UtcNow().AddMinutes(5));
        };
        try
        {
            if (File.Exists(_journal))
                _owned.AddRange((JsonSerializer.Deserialize<List<OwnedProcess>>(File.ReadAllText(_journal)) ?? [])
                    .Where(p => string.Equals(p.Path, _path, StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception ex) { DiagnosticLog.Write($"display journal read failed: {ex.Message}"); }
        _timer.Tick += (_, _) => Tick();
    }

    internal void Start(bool switchOnStartup)
    {
        // Let the login desktop and display driver settle. One request per login,
        // never periodically override a display mode subsequently chosen by the user.
        _startupAt = switchOnStartup ? UtcNow().AddSeconds(20) : null;
        _timer.Start();
        Tick();
    }

    internal void Tick()
    {
        if (_disposed) return;
        if (_startupAt is { } due && UtcNow() >= due)
        {
            _startupAt = null;
            DiagnosticLog.Write(SwitchToDuplicate());
        }
        Cleanup(false);
    }

    internal void CancelStartup() => _startupAt = null;

    internal string SwitchToDuplicate()
    {
        try
        {
            _startupAt = null; // A manual request also satisfies a pending startup request.
            var owned = Launch(_path);
            _owned.Add(owned);
            SaveJournal();
            DiagnosticLog.Write($"display clone requested pid={owned.Pid} cleanupAt={owned.CleanupAtUtc:O}");
            return "已请求切换为复制模式；残留进程将在约 5 分钟后清理。请确认屏幕显示效果。";
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"display clone failed: {ex}");
            return "显示切换失败：" + ex.Message;
        }
    }

    internal string Cleanup(bool immediate)
    {
        var count = 0;
        foreach (var owned in _owned.ToArray())
        {
            if (!immediate && UtcNow() < owned.CleanupAtUtc) continue;
            try
            {
                if (!StopOwned(owned)) continue;
                _owned.Remove(owned);
                count++;
            }
            catch (Exception ex) { DiagnosticLog.Write($"display cleanup pid={owned.Pid}: {ex.Message}"); }
        }
        if (count > 0) SaveJournal();
        return _owned.Count == 0 ? "本应用启动的显示切换进程已清理完毕（或已自行退出）。"
            : "部分显示切换进程尚未清理，稍后会重试。";
    }

    internal static bool Matches(OwnedProcess expected, int pid, long started, string? path) =>
        expected.Pid == pid && expected.StartedUtcTicks == started
        && string.Equals(expected.Path, path, StringComparison.OrdinalIgnoreCase);

    private static bool StopVerifiedProcess(OwnedProcess owned)
    {
        Process process;
        try { process = Process.GetProcessById(owned.Pid); }
        catch (ArgumentException) { return true; } // Already exited.
        using (process)
        {
            if (process.HasExited) return true;
            if (!Matches(owned, process.Id, process.StartTime.ToUniversalTime().Ticks,
                process.MainModule?.FileName)) return true; // PID reused; discard stale record only.
            process.Kill(); // The exact utility instance, never a process tree or name match.
            return true;
        }
    }

    private void SaveJournal()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_journal)!);
            File.WriteAllText(_journal + ".tmp", JsonSerializer.Serialize(_owned));
            File.Move(_journal + ".tmp", _journal, true);
        }
        catch (Exception ex) { DiagnosticLog.Write($"display journal save failed: {ex.Message}"); }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        // App exit cancels its timer; clean its utility now instead of leaving
        // an orphan until the next login. Failed cleanup stays in the journal.
        Cleanup(true);
    }
}
