using OfficeTouchBar.Models;

namespace OfficeTouchBar.Infrastructure;

internal interface ISnapshotReader : IDisposable
{
    bool PreferSlideShowForActivePresentation { get; set; }
    PowerPointSnapshot Snapshot { get; }
    void Refresh();
}

internal sealed class SnapshotPoller : IDisposable
{
    private readonly Func<ISnapshotReader> _createBridge;
    private readonly Action<PowerPointSnapshot> _publish;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private int _requested;
    private int _disposed;
    private bool _preferSlideShow;
    private readonly Func<long> _clock;
    private long _readStarted = -1;
    // An idle reader is healthy, including the intentional pause while inking.
    internal bool IsStalled => Volatile.Read(ref _disposed) == 0 &&
        (HasStopped || (Interlocked.Read(ref _readStarted) is var started && started >= 0
            && _clock() - started > 3000));
    internal bool IsReadInProgress => Interlocked.Read(ref _readStarted) >= 0;
    internal bool HasStopped => !_thread.IsAlive;

    internal SnapshotPoller(Func<ISnapshotReader> createBridge, Action<PowerPointSnapshot> publish,
        Func<long>? clock = null)
    {
        _createBridge = createBridge;
        _publish = publish;
        _clock = clock ?? (() => Environment.TickCount64);
        _thread = new Thread(Run) { IsBackground = true, Name = "PowerPoint snapshot reader" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    internal void SetPreferSlideShow(bool value) => Volatile.Write(ref _preferSlideShow, value);

    internal void Request()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (Interlocked.Exchange(ref _requested, 1) == 0) _wake.Set();
    }

    private void Run()
    {
        ISnapshotReader? bridge = null;
        try
        {
            while (true)
            {
                _wake.WaitOne();
                if (Volatile.Read(ref _disposed) != 0) break;
                if (Interlocked.Exchange(ref _requested, 0) == 0) continue;
                try
                {
                    Interlocked.Exchange(ref _readStarted, _clock());
                    bridge ??= _createBridge();
                    bridge.PreferSlideShowForActivePresentation = Volatile.Read(ref _preferSlideShow);
                    bridge.Refresh();
                    if (Volatile.Read(ref _disposed) == 0) _publish(bridge.Snapshot);
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write($"snapshot read failed; retry on next poll: {ex}");
                    try { bridge?.Dispose(); } catch { }
                    bridge = null;
                }
                finally { Interlocked.Exchange(ref _readStarted, -1); }
            }
        }
        catch (Exception ex) { DiagnosticLog.Write($"snapshot worker failed: {ex}"); }
        finally { try { bridge?.Dispose(); } catch { } }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _wake.Set();
        // A busy Office COM call must never hold up the UI thread during shutdown.
        // The background STA disposes its own COM objects after that call returns.
    }
}
