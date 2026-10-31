namespace OfficeTouchBar.Infrastructure;

internal sealed class WindowLayerPolicy
{
    private static readonly nint HwndTopmost = new(-1);
    private static readonly nint HwndNotTopmost = new(-2);
    private readonly Dictionary<nint, string> _promotedByUs = [];
    private const int ExtendedStyle = -20;
    private const long TopmostStyle = 0x8;
    private const uint NoMoveNoSizeNoActivate = 0x0001 | 0x0002 | 0x0010 | 0x0200;

    internal static bool IsStackCorrect(nint slideShow, nint ink, nint toolbar,
        IReadOnlyList<nint>? editors = null)
    {
        if (slideShow == 0 || ink == 0 || toolbar == 0) return false;
        if (!NativeMethods.IsWindow(slideShow) || !NativeMethods.IsWindow(ink) || !NativeMethods.IsWindow(toolbar))
            return false;
        return IsTopmost(ink) && IsTopmost(toolbar)
            && NativeMethods.IsAbove(toolbar, ink)
            && NativeMethods.IsAbove(ink, slideShow)
            && (editors ?? NativeMethods.FindOverlappingEditors(slideShow))
                .All(editor => NativeMethods.IsAbove(slideShow, editor));
    }

    internal bool EnsureSlideShowStack(nint slideShow, nint ink, nint toolbar, out bool repaired,
        IReadOnlyList<nint>? editors = null)
    {
        repaired = false;
        if (slideShow == 0 || ink == 0 || toolbar == 0 || !NativeMethods.IsWindow(slideShow)
            || !NativeMethods.IsWindow(ink) || !NativeMethods.IsWindow(toolbar)) return false;
        var overlappingEditors = editors ?? NativeMethods.FindOverlappingEditors(slideShow);
        // A promotion is a temporary resource owned by this controller, not a
        // permanent property imposed on the Office process.
        if (_promotedByUs.ContainsKey(slideShow) && !overlappingEditors.Any(IsTopmost))
            repaired = ReleaseSlideShow(slideShow);
        if (IsStackCorrect(slideShow, ink, toolbar, overlappingEditors)) return true;

        repaired = true;
        DiagnosticLog.Ink($"stack-repair show={slideShow} ink={ink} toolbar={toolbar} foreground={NativeMethods.GetForegroundWindow()} showTopmost={IsTopmost(slideShow)} inkTopmost={IsTopmost(ink)} toolbarAboveInk={NativeMethods.IsAbove(toolbar, ink)} inkAboveShow={NativeMethods.IsAbove(ink, slideShow)}");
        // Keep Office in the normal Z-order band whenever possible. Making it
        // topmost lets a later foreground activation cover both overlays until
        // a repair, even when all three were ordered correctly beforehand.
        var blockedByEditor = overlappingEditors.Any(editor => !NativeMethods.IsAbove(slideShow, editor));
        var batch = NativeMethods.BeginDeferWindowPos(3);
        if (batch == 0) return false;
        var needsPromotion = blockedByEditor && !IsTopmost(slideShow);
        var identity = needsPromotion ? NativeMethods.ProcessIdentity(slideShow) : string.Empty;
        if (blockedByEditor)
            batch = NativeMethods.DeferWindowPos(batch, slideShow,
                needsPromotion ? HwndTopmost : 0, 0, 0, 0, 0, NoMoveNoSizeNoActivate);
        if (batch == 0) return false;
        batch = NativeMethods.DeferWindowPos(batch, ink, HwndTopmost, 0, 0, 0, 0, NoMoveNoSizeNoActivate);
        if (batch == 0) return false;
        batch = NativeMethods.DeferWindowPos(batch, toolbar, HwndTopmost, 0, 0, 0, 0, NoMoveNoSizeNoActivate);
        var committed = batch != 0 && NativeMethods.EndDeferWindowPos(batch);
        if (needsPromotion && IsTopmost(slideShow))
            _promotedByUs[slideShow] = identity;
        return committed && IsStackCorrect(slideShow, ink, toolbar, overlappingEditors);
    }

    internal bool ReleaseSlideShow(nint slideShow)
    {
        if (!_promotedByUs.TryGetValue(slideShow, out var identity)) return false;
        if (identity.Length == 0 || !NativeMethods.IsWindow(slideShow)
            || NativeMethods.ProcessIdentity(slideShow) != identity || !IsTopmost(slideShow))
        { _promotedByUs.Remove(slideShow); return false; }
        DiagnosticLog.Ink($"stack-release show={slideShow} foreground={NativeMethods.GetForegroundWindow()}");
        if (NativeMethods.SetWindowPos(slideShow, HwndNotTopmost, 0, 0, 0, 0, NoMoveNoSizeNoActivate))
        { _promotedByUs.Remove(slideShow); return true; }
        else
        {
            DiagnosticLog.Ink($"stack-release-failed show={slideShow}");
        }
        return false;
    }

    internal static bool EnsureToolbarAbove(nint target, nint toolbar)
    {
        if (target == 0 || toolbar == 0 || !NativeMethods.IsWindow(target) || !NativeMethods.IsWindow(toolbar))
            return false;
        if (IsTopmost(toolbar) && NativeMethods.IsAbove(toolbar, target)) return true;
        return NativeMethods.SetWindowPos(toolbar, HwndTopmost, 0, 0, 0, 0, NoMoveNoSizeNoActivate);
    }

    internal static bool EnsurePromptAbove(nint prompt, IReadOnlyList<nint> presentations, out bool repaired)
    {
        repaired = false;
        if (prompt == 0 || !NativeMethods.IsWindow(prompt)) return false;
        bool Correct() => IsTopmost(prompt) && presentations
            .Where(handle => handle != prompt && NativeMethods.IsWindow(handle))
            .All(handle => NativeMethods.IsAbove(prompt, handle));
        if (Correct()) return true;
        repaired = true;
        return NativeMethods.SetWindowPos(prompt, HwndTopmost, 0, 0, 0, 0, NoMoveNoSizeNoActivate)
            && Correct();
    }

    private static bool IsTopmost(nint handle) =>
        ((long)NativeMethods.GetWindowLongPtr(handle, ExtendedStyle) & TopmostStyle) != 0;
}
