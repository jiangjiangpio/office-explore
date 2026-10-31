using System.Windows;
using OfficeTouchBar.Models;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace OfficeTouchBar.Infrastructure;

internal static class InteractionPolicy
{
    internal static bool ShouldShow(PowerPointSnapshot snapshot, AppSettings settings, bool running) =>
        running && snapshot.IsForeground && snapshot.WindowHandle != 0
        && settings.ShowWhenPowerPointActive
        && (!settings.ShowOnlyDuringSlideShow || snapshot.IsSlideShow);

    internal static bool CanContinue(int generation, int currentGeneration, nint target,
        PowerPointSnapshot snapshot, bool disposed) =>
        !disposed && generation == currentGeneration && target != 0
        && target == snapshot.WindowHandle && snapshot.IsForeground;

    internal static Point Clamp(Point point, Size size, Rect bounds) => new(
        Math.Clamp(point.X, bounds.Left, Math.Max(bounds.Left, bounds.Right - size.Width)),
        Math.Clamp(point.Y, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - size.Height)));

    internal static bool HasSignificantFrameChange(Rect previous, Rect next,
        nint previousTarget, nint nextTarget, bool force, double tolerance = 2) =>
        force || previous.IsEmpty || previousTarget != nextTarget
        || Math.Abs(previous.X - next.X) > tolerance
        || Math.Abs(previous.Y - next.Y) > tolerance
        || Math.Abs(previous.Width - next.Width) > tolerance
        || Math.Abs(previous.Height - next.Height) > tolerance;

    internal static bool TryCommand(Func<bool> primary, Func<bool> fallback)
    {
        try { if (primary()) return true; }
        catch (Exception ex) { DiagnosticLog.Write($"COM command failed: {ex.Message}"); }
        return fallback();
    }
}
