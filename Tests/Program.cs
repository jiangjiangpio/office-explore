using OfficeTouchBar;
using OfficeTouchBar.Infrastructure;
using System.Runtime.InteropServices;

var passed = 0;
var failed = 0;
void Check(string name, Action test)
{
    try { test(); Console.WriteLine($"PASS {name}"); passed++; }
    catch (Exception ex) { Console.WriteLine($"FAIL {name}: {ex.Message}"); failed++; }
}
void Assert(bool value) { if (!value) throw new Exception("Assertion failed"); }

Check("missed mouse Up is reconciled without LostCapture", () => {
    var state = new InkContactState(); state.Begin("mouse", 0, 10);
    state.Sweep(20, (_, _) => true); Assert(!state.HasContacts);
    Assert(state.IsBusy(269)); Assert(!state.IsBusy(270));
});
Check("stationary pen remains down after capture loss", () => {
    var state = new InkContactState(); state.Begin("stylus", 1, 0); state.LostCapture("stylus", 1, 1);
    state.Sweep(5000, (_, _) => false); Assert(state.HasContacts);
    state.Sweep(5001, (_, _) => true); Assert(!state.HasContacts);
});
Check("missing device expires only after capture loss", () => {
    var state = new InkContactState(); state.Begin("touch", 1, 0);
    state.Sweep(5000, (_, _) => null); Assert(state.HasContacts);
    state.LostCapture("touch", 1, 5000);
    state.Sweep(6999, (_, _) => null); Assert(state.HasContacts);
    state.Sweep(7000, (_, _) => null); Assert(!state.HasContacts);
});
Check("one released finger does not end another contact", () => {
    var state = new InkContactState(); state.Begin("touch", 1, 0); state.Begin("touch", 2, 0);
    state.Sweep(10, (_, id) => id == 1); Assert(state.Contains("touch", 2)); Assert(!state.Contains("touch", 1));
});
Check("preparation retries recover after five failures without busy loop", () => {
    var retry = new InkPreparationRetry(); long now = 0;
    for (int i = 0; i < 10; i++) { Assert(retry.CanAttempt(now)); retry.Failed(now); Assert(!retry.CanAttempt(now + 1)); now += 4000; }
    Assert(retry.CanAttempt(now)); retry.Reset(); Assert(retry.CanAttempt(0)); Assert(!retry.Exhausted);
});
Check("NULL foreground gap is bounded; real other windows are immediate", () => {
    var p = new ForegroundContinuityPolicy(); Assert(p.Defer(0, true, true, 0));
    Assert(p.Defer(0, true, true, 499)); Assert(!p.Defer(0, true, true, 500));
    Assert(!p.Defer(99, true, true, 501)); Assert(p.Defer(0, true, true, 502));
    Assert(!p.Defer(0, true, false, 503)); Assert(!p.Defer(0, false, true, 504));
});
Check("normal editor is repaired without show promotion or repeated writes", () => {
    NativeMethods.Order = [3, 2, 4, 1]; NativeMethods.Topmost = [2, 3]; NativeMethods.Writes = 0;
    var p = new WindowLayerPolicy(); Assert(p.EnsureSlideShowStack(1, 2, 3, out var repaired)); Assert(repaired);
    Assert(!NativeMethods.Topmost.Contains(1)); var writes = NativeMethods.Writes;
    for (int i = 0; i < 100; i++) { Assert(p.EnsureSlideShowStack(1, 2, 3, out repaired)); Assert(!repaired); }
    Assert(NativeMethods.Writes == writes);
});
Check("topmost editor promotion is stable and released when no longer needed", () => {
    NativeMethods.Order = [4, 3, 2, 1]; NativeMethods.Topmost = [2, 3, 4]; NativeMethods.Writes = 0;
    var p = new WindowLayerPolicy(); Assert(p.EnsureSlideShowStack(1, 2, 3, out _)); Assert(NativeMethods.Topmost.Contains(1));
    var writes = NativeMethods.Writes; Assert(p.EnsureSlideShowStack(1, 2, 3, out var repaired)); Assert(!repaired);
    Assert(NativeMethods.Writes == writes);
    NativeMethods.SetWindowPos(4, -2, 0, 0, 0, 0, 0);
    Assert(p.EnsureSlideShowStack(1, 2, 3, out _)); Assert(!NativeMethods.Topmost.Contains(1));
});
Check("routed old Up cannot remove the next raw stroke", () => {
    var raw = new InkContactObserver(); raw.SetInputEnabled(true);
    raw.ObserveDown(1, 100); raw.ObserveUp(1, 110); Assert(!raw.HasContacts);
    raw.ObserveDown(1, 120); raw.ObserveUp(1, 110); Assert(raw.HasContacts);
    raw.ObserveUp(1, 130); Assert(!raw.HasContacts);
});
Check("raw timestamp reconciliation handles system tick wrap", () => {
    var raw = new InkContactObserver(); raw.SetInputEnabled(true);
    raw.ObserveDown(1, int.MaxValue - 1); raw.ObserveUp(1, int.MinValue + 1); Assert(!raw.HasContacts);
});
Check("missed raw Up recovers from physical state without ending a live stroke", () => {
    long now = 0;
    var raw = new InkContactObserver(() => now); raw.SetInputEnabled(true); raw.ObserveDown(1, 100);
    now = 49; raw.ReconcileReleased(_ => true); Assert(raw.HasContacts);
    now = 50; raw.ReconcileReleased(_ => false); Assert(raw.HasContacts);
    raw.ReconcileReleased(_ => true); Assert(!raw.HasContacts);
    raw.ObserveDown(1, 110); now = 100;
    raw.ReconcileReleased(_ => { raw.ObserveDown(1, 120); return true; }); Assert(raw.HasContacts);
});

foreach (var failure in new[] { "name", "identity", "slide" })
Check($"transient COM {failure} rejection preserves page and tool identity", () => {
    var app = new FakeOffice();
    using var bridge = new PowerPointBridge(() => app, () => 1, h => h == 1, NativeMethods.Bounds);
    bridge.Refresh(); Assert(bridge.Snapshot.IsConnected); Assert(bridge.Snapshot.CurrentSlide == 2);
    var tools = new PresentationToolPolicy(); Assert(tools.Observe(bridge.Snapshot, true));
    app.Presentation.Failure = failure; bridge.Refresh();
    Assert(bridge.Snapshot.PresentationIdentity == "lesson.pptx"); Assert(bridge.Snapshot.CurrentSlide == 2);
    Assert(!tools.Observe(bridge.Snapshot, true));
    app.Presentation.Failure = ""; app.Presentation.Page = 3; bridge.Refresh();
    Assert(bridge.Snapshot.CurrentSlide == 3); Assert(!tools.Observe(bridge.Snapshot, true));
    app.Presentation.File = "another.pptx"; bridge.Refresh(); Assert(tools.Observe(bridge.Snapshot, true));
});
Check("unknown page after navigation never reuses the previous frame", () => {
    var p = new InkPageContinuityPolicy(); Assert(p.ShowLastFrame(1, 1, true, 0));
    p.Navigation(1); Assert(!p.ShowLastFrame(1, 1, true, 1));
    p.Confirm(); Assert(!p.ShowLastFrame(2, 1, true, 2));
});
Console.WriteLine($"{passed} regression checks passed; {failed} failed.");
Environment.ExitCode = failed == 0 ? 0 : 1;

public sealed class FakeOffice
{
    public FakePresentation Presentation { get; } = new();
    public FakeCollection Windows => new([]);
    public FakeCollection SlideShowWindows => new([new FakeShow(Presentation)]);
    public FakePresentation ActivePresentation => Presentation;
}
public sealed class FakeCollection(object[] items)
{ public int Count => items.Length; public object this[int index] => items[index - 1]; }
public sealed class FakeShow(FakePresentation presentation)
{
    public int HWND => 1;
    public FakePresentation Presentation => presentation;
    public FakeView View => new(presentation);
}
public sealed class FakeView(FakePresentation presentation)
{ public FakeSlide Slide => presentation.Failure == "slide" ? throw FakePresentation.Busy() : new(presentation.Page); }
public sealed class FakeSlide(int page) { public int SlideIndex => page; }
public sealed class FakePresentation
{
    public string Failure = "";
    public int Page = 2;
    public string File = "lesson.pptx";
    public string Name => Failure == "name" ? throw Busy() : File;
    public string FullName => Failure == "identity" ? throw Busy() : File;
    public FakeCollection Slides => new(new object[10]);
    public static COMException Busy() => new("Office busy", unchecked((int)0x80010001));
}
