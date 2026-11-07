# Regression checks

Run `dotnet run --project Tests/RegressionChecks.csproj -c Release` from the repository root with .NET 10.

The executable links the production bridge and state policies directly. PlatformDoubles replaces the native window API, WPF value types and raw event entry points so the checks also run on Linux. The checks exercise missed Up reconciliation, stationary contact safety, independent contacts, bounded automatic preparation retries, transient NULL foreground handling, stable window stacking, delayed raw input events, COM failures and presentation/page transitions. A failed assertion exits nonzero.

These tests do not emulate WPF rendering/capture or PowerPoint itself. Before release acceptance on Windows, verify both ink engines with the actual affected touch hardware:

- Repeated short strokes, long strokes with a pause, pen/finger release outside the screen, and pen inversion/eraser.
- Open/close the palette, switch colors and tools, then write immediately. Test automatic palette closing.
- Slide transitions, keyboard navigation and slideshow start/end; verify ink stays on its own slide and saves after restart.
- Switch between PowerPoint, a different application and Office dialogs. Verify the sidebar does not flash or cover unrelated windows.
- Test an overlapping editor, multiple presentations and monitors at different DPI settings.
- Fresh installation and upgrade over 0.3.19, retaining settings and saved ink; verify uninstall.
