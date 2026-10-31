using OfficeTouchBar.Models;

namespace OfficeTouchBar.Infrastructure;

internal sealed class PresentationToolPolicy
{
    private string _identity = "";
    private nint _window;
    private bool _slideShow;

    internal bool Observe(PowerPointSnapshot snapshot, bool resetEnabled)
    {
        if (!snapshot.IsConnected || !snapshot.IsForeground || snapshot.WindowHandle == 0
            || string.IsNullOrWhiteSpace(snapshot.PresentationIdentity)) return false;
        var changed = !string.Equals(_identity, snapshot.PresentationIdentity, StringComparison.OrdinalIgnoreCase)
            || _window != snapshot.WindowHandle || _slideShow != snapshot.IsSlideShow;
        _identity = snapshot.PresentationIdentity;
        _window = snapshot.WindowHandle;
        _slideShow = snapshot.IsSlideShow;
        return changed && resetEnabled;
    }

    internal bool ForgetClosedWindow(Func<nint, bool> isWindow)
    {
        if (_window == 0 || isWindow(_window)) return false;
        _identity = "";
        _window = 0;
        _slideShow = false;
        return true;
    }
}
