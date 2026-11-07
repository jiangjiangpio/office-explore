# Regression checks

Run `dotnet run --project Tests/RegressionChecks.csproj -c Release` from the repository root with .NET 10.

The executable links the production bridge and state policies directly. PlatformDoubles replaces the native window API, WPF value types and raw event entry points so the checks also run on Linux. The checks exercise missed Up reconciliation, stationary contact safety, independent contacts, bounded automatic preparation retries, transient NULL foreground handling, stable window stacking, delayed raw input events, COM failures and presentation/page transitions. A failed assertion exits nonzero.

The state checks do not emulate WPF rendering/capture or PowerPoint itself.

## Windows input integration checks

Run on an interactive Windows 10 (1809+) / Windows 11 / Windows Server desktop with .NET 10:

```powershell
dotnet run --project Tests/Windows/WindowsInputChecks.csproj -c Release
dotnet run --project Tests/Windows/WindowsInputChecks.csproj -c Release -- --pointer
```

These open the production ink overlay and use native input, rather than raising fake move events or replacing InkCanvas. The default run covers WPF's WISP mouse path. `--pointer` also enables WPF's WM_POINTER stack and injects touch through `CreateSyntheticPointerDevice`; a CI machine has no physical WISP tablet to turn injected pointer messages into legacy tablet packets. Production's choice of input stack is unchanged.

The checks hold a contact down, explicitly release its capture twice, keep moving without another Down, and verify both visible ink **before Up** (screen pixels) and the final stroke endpoints/attributes. They cover a stationary interval exceeding two seconds, highlighter, distinct successive contacts, deliberate cancellation, capture transfer, foreground handoff, page clear and replacement. All input-dependent checks require the initial contact to enter the actual WPF editor, so missing input cannot pass as successful cancellation. They move the mouse/touch on the test desktop; do not use that desktop during execution.

The pre-fix run at commit `b55c1d3` reproduces the original failure: the long mouse stroke stops at x=210 after capture is released at x=220. Recovery must restart both InkCanvas's editor and its DynamicRenderer; merely capturing the device again does not restore the editor's move subscription. The implementation uses InkCanvas's existing promoted-MouseDown path for an already-down stylus and joins collected segments only within the same physical contact. There is no periodic window activation or tool reset.

`Windows/SetupSmoke.ps1` runs only on a fresh disposable CI runner. It validates fresh/repeat installation, obsolete-payload cleanup, preservation of settings/ink/unlisted documents, self-contained application startup, and uninstall. The release workflow publishes Setup only after all checks succeed.

The automated checks cannot reproduce every physical touchscreen driver, multi-monitor setup or PowerPoint COM session. For hardware acceptance, verify both ink engines with the affected device:

- Repeated short strokes, long strokes with a pause, pen/finger release outside the screen, and pen inversion/eraser.
- Open/close the palette, switch colors and tools, then write immediately. Test automatic palette closing.
- Slide transitions, keyboard navigation and slideshow start/end; verify ink stays on its own slide and saves after restart.
- Switch between PowerPoint, a different application and Office dialogs. Verify the sidebar does not flash or cover unrelated windows.
- Test an overlapping editor, multiple presentations and monitors at different DPI settings.
- Upgrade over the installed previous version, retaining settings and saved ink.
