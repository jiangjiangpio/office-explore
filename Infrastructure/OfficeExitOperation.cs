namespace OfficeTouchBar.Infrastructure;

// Owns a single STA command. A timeout releases the UI, not the COM call;
// another worker is never spawned while the previous call is still running.
internal sealed class OfficeExitOperation
{
    private int _busy;
    internal async Task<bool> RunAsync(Func<bool> command, TimeSpan timeout)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return false;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.TrySetResult(command()); }
            catch (Exception ex) { DiagnosticLog.Write($"exit command failed: {ex}"); completion.TrySetResult(false); }
            finally { Volatile.Write(ref _busy, 0); }
        }) { IsBackground = true, Name = "OfficeTouchBar.Exit" };
        try
        {
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch { Volatile.Write(ref _busy, 0); throw; }
        try { return await completion.Task.WaitAsync(timeout); }
        catch (TimeoutException) { DiagnosticLog.Write("exit command timed out; COM may still complete; ink retained"); return false; }
    }
}
