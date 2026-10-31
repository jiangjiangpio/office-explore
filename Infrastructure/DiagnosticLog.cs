using System.IO;
using System.Text;
using System.Threading.Channels;

namespace OfficeTouchBar.Infrastructure;

internal static class DiagnosticLog
{
    private sealed record Entry(string? Text, bool Ink, TaskCompletionSource<bool>? Barrier = null);
    private static readonly Channel<Entry> Queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(4096)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private static readonly object Gate = new();
    private static string? _lastMessage;
    private static string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OfficeTouchBar");
    private static long _dropped;
    private static long _failedWrites;
    private static readonly string Run = $"{Environment.ProcessId}-{Guid.NewGuid():N}";
    private static readonly Task Writer = Task.Run(WriteLoop);
    internal static string RootDirectory => _root;
    internal static string InkDirectory => Path.Combine(_root, "Logs");
    internal static string BundleDirectory => Path.Combine(_root, "Diagnostics");
    internal static long FailedWrites => Interlocked.Read(ref _failedWrites);
    internal static void UseTestDirectory(string root) => _root = Path.GetFullPath(root);

    internal static void Write(string message)
    {
        lock (Gate)
        {
            if (_lastMessage == message) return;
            _lastMessage = message;
        }
        Enqueue(message, false);
    }

    internal static void Ink(string message) => Enqueue(message, true);

    private static void Enqueue(string message, bool ink)
    {
        if (ink) message = message.Replace("\r", "\\r").Replace("\n", "\\n");
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} tick={Environment.TickCount64} run={Run} thread={Environment.CurrentManagedThreadId} {message}{Environment.NewLine}";
        if (!Queue.Writer.TryWrite(new Entry(line, ink))) Interlocked.Increment(ref _dropped);
    }

    internal static async Task<bool> FlushAsync()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await Queue.Writer.WriteAsync(new Entry(null, false, completion), timeout.Token);
            return await completion.Task.WaitAsync(timeout.Token);
        }
        catch { return false; }
    }

    private static async Task WriteLoop()
    {
        var normal = new StringBuilder();
        var ink = new StringBuilder();
        await foreach (var first in Queue.Reader.ReadAllAsync())
        {
            var entry = first;
            var count = 0;
            do
            {
                if (entry.Barrier is not null)
                    entry.Barrier.TrySetResult(FlushBuffers(normal, ink));
                else (entry.Ink ? ink : normal).Append(entry.Text);
                count++;
            } while (count < 128 && Queue.Reader.TryRead(out entry!));
            FlushBuffers(normal, ink);
        }
    }

    private static bool FlushBuffers(StringBuilder normal, StringBuilder ink)
    {
        try
        {
            Directory.CreateDirectory(_root);
            var dropped = Interlocked.Exchange(ref _dropped, 0);
            if (dropped > 0) normal.AppendLine($"{DateTime.Now:O} diagnostics queue overflow dropped={dropped}");
            if (normal.Length > 0) Append(Path.Combine(_root, "diagnostic.log"), normal.ToString(), 2 * 1024 * 1024, 1);
            if (ink.Length > 0)
            {
                Directory.CreateDirectory(InkDirectory);
                Append(Path.Combine(InkDirectory, $"ink-events-{DateTime.Now:yyyyMMdd}.log"), ink.ToString(), 4 * 1024 * 1024, 2);
                foreach (var file in Directory.EnumerateFiles(InkDirectory, "ink-events-*.log*"))
                    if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-7)) File.Delete(file);
            }
            return true;
        }
        catch { Interlocked.Increment(ref _failedWrites); return false; }
        finally { normal.Clear(); ink.Clear(); }
    }

    private static void Append(string path, string text, long limit, int copies)
    {
        if (File.Exists(path) && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(text) > limit)
        {
            for (var i = copies; i >= 1; i--)
            {
                var source = i == 1 ? path : path + "." + (i - 1);
                if (File.Exists(source)) File.Move(source, path + "." + i, overwrite: true);
            }
        }
        File.AppendAllText(path, text, Encoding.UTF8);
    }
}
