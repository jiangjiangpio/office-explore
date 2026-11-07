// Platform boundary doubles for running the real state/COM-selection code on
// Linux. These do not simulate WPF rendering, drivers, or real Office COM.
namespace System.Windows
{
    public readonly record struct Rect(double X, double Y, double Width, double Height)
    { public static Rect Empty => default; }
}
namespace System.Windows.Media { public readonly record struct Color(byte R, byte G, byte B); }
namespace System.Windows.Controls
{
    public class InkCanvas
    { protected List<System.Windows.Input.StylusPlugIns.StylusPlugIn> StylusPlugIns { get; } = []; }
}
namespace System.Windows.Input.StylusPlugIns
{
    public class RawStylusInput
    {
        public int StylusDeviceId { get; init; }
        public int Timestamp { get; init; }
        public void NotifyWhenProcessed(object data) { }
    }
    public class StylusPlugIn
    {
        protected virtual void OnStylusDown(RawStylusInput input) { }
        protected virtual void OnStylusDownProcessed(object data, bool targetVerified) { }
        protected virtual void OnStylusMove(RawStylusInput input) { }
        protected virtual void OnStylusUp(RawStylusInput input) { }
        protected virtual void OnStylusLeave(RawStylusInput input, bool confirmed) { }
        protected virtual void OnRemoved() { }
    }
}
namespace OfficeTouchBar
{ public enum PointerTool { Mouse, Pen, Highlighter, Eraser } }
namespace OfficeTouchBar.Models
{ public sealed record InkPageKey(string PresentationIdentity, string PresentationName, int SlideNumber, int TotalSlides); }
namespace OfficeTouchBar.Infrastructure
{
    internal interface ISnapshotReader : IDisposable
    {
        bool PreferSlideShowForActivePresentation { get; set; }
        Models.PowerPointSnapshot Snapshot { get; }
        void Refresh();
    }
    internal static class DiagnosticLog
    { internal static void Write(string text) { } internal static void Ink(string text) { } }
    internal static class InteractionPolicy
    {
        internal static bool TryCommand(Func<bool> primary, Func<bool> fallback)
        { try { if (primary()) return true; } catch { } return fallback(); }
    }
    internal static class NativeMethods
    {
        internal static List<nint> Order = [];
        internal static HashSet<nint> Topmost = [];
        internal static int Writes;
        internal static bool IsWindow(nint h) => Order.Contains(h);
        internal static nint GetWindowLongPtr(nint h, int index) => Topmost.Contains(h) ? 8 : 0;
        internal static bool IsAbove(nint a, nint b) => Order.IndexOf(a) < Order.IndexOf(b);
        internal static IReadOnlyList<nint> FindOverlappingEditors(nint show) => [4];
        internal static nint BeginDeferWindowPos(int n) => 1;
        internal static nint DeferWindowPos(nint batch, nint h, nint after, int x, int y, int w, int height, uint flags)
        { SetWindowPos(h, after, x, y, w, height, flags); return batch; }
        internal static bool EndDeferWindowPos(nint batch) => true;
        internal static bool SetWindowPos(nint h, nint after, int x, int y, int w, int height, uint flags)
        {
            Writes++;
            if (after == -1) Topmost.Add(h);
            if (after == -2) Topmost.Remove(h);
            Order.Remove(h);
            Order.Insert(Topmost.Contains(h) ? 0 : Order.Count(Topmost.Contains), h);
            return true;
        }
        internal static string ProcessIdentity(nint h) => "office-process";
        internal static object? GetRunningComObject(string name) => throw new NotSupportedException();
        internal static nint GetForegroundWindow() => 1;
        internal static bool IsPowerPointWindow(nint h) => h == 1;
        internal static System.Windows.Rect Bounds(nint h) => new(0, 0, 1920, 1080);
        internal static bool RestoreFocus(nint h, nint overlay) => true;
        internal static bool SendKey(nint h, byte key, nint overlay) => true;
        internal static bool SendKeyAfterDialog(nint h, byte key, nint overlay) => true;
        internal static bool SendChord(nint h, byte modifier, byte key, nint overlay) => true;
        internal static bool IsSlideShowWindow(nint h) => h == 1;
        internal static bool IsPowerPointEditorWindow(nint h) => false;
        internal static nint FindSlideShowWindow(nint h) => 1;
        internal static string WindowTitle(nint h) => "Presentation";
    }
}
