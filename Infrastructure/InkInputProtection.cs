using System.Runtime.InteropServices;
using System.Windows;

namespace OfficeTouchBar.Infrastructure;

// No registry or global gesture settings: only our active, full-screen ink window.
internal sealed class InkInputProtection : IDisposable
{
    private readonly Func<Rect?, bool> _clip;
    private readonly Func<Rect> _readClip;
    private readonly Func<nint, bool, bool> _edge;
    private Rect? _ownedClip;
    private nint _edgeWindow;
    private nint _edgeAttempt;

    internal InkInputProtection(Func<Rect?, bool>? clip = null, Func<Rect>? readClip = null,
        Func<nint, bool, bool>? edge = null)
    {
        _clip = clip ?? NativeInputProtection.Clip;
        _readClip = readClip ?? NativeInputProtection.ReadClip;
        _edge = edge ?? NativeInputProtection.SetEdgeGesturesDisabled;
    }

    internal void Update(bool enabled, bool confineMouse, bool blockEdges,
        bool fullScreen, Rect bounds, nint foreground, nint show, nint ink, nint toolbar)
    {
        var active = enabled && ink != 0 && show != 0 && !bounds.IsEmpty
            && bounds.Width > 0 && bounds.Height > 0
            && (foreground == ink || foreground == show || foreground == toolbar);
        if (!active) { Release(); return; }

        if (confineMouse)
        {
            if (_ownedClip != bounds)
            {
                ReleaseClip();
                if (_ownedClip is not null) return;
                if (_clip(bounds)) _ownedClip = bounds;
                else DiagnosticLog.Write("mouse confinement failed");
            }
        }
        else ReleaseClip();

        if (blockEdges && fullScreen)
        {
            if (_edgeAttempt != ink)
            {
                ReleaseEdges();
                if (_edgeWindow != 0) return;
                _edgeAttempt = ink;
                if (_edge(ink, true)) _edgeWindow = ink;
                else DiagnosticLog.Write("full-screen edge gesture protection unavailable");
            }
        }
        else ReleaseEdges();
    }

    private void ReleaseClip()
    {
        if (_ownedClip is not { } owned) return;
        // Do not undo a different application's newer cursor restriction.
        if (_readClip() == owned && !_clip(null))
        {
            DiagnosticLog.Write("mouse confinement release failed; will retry");
            return;
        }
        _ownedClip = null;
    }

    private void ReleaseEdges()
    {
        if (_edgeWindow != 0 && !_edge(_edgeWindow, false))
        {
            DiagnosticLog.Write("edge gesture protection release failed; will retry");
            return;
        }
        _edgeWindow = _edgeAttempt = 0;
    }

    internal void Release() { ReleaseClip(); ReleaseEdges(); }
    public void Dispose() => Release();
}

internal static class NativeInputProtection
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "ClipCursor")]
    private static extern bool ClipRect(ref NativeRect rect);
    [DllImport("user32.dll", EntryPoint = "ClipCursor")]
    private static extern bool Unclip(nint rect);
    [DllImport("user32.dll")]
    private static extern bool GetClipCursor(out NativeRect rect);

    internal static bool Clip(Rect? bounds)
    {
        if (bounds is not { } area) return Unclip(0);
        var rect = new NativeRect { Left = (int)area.Left, Top = (int)area.Top,
            Right = (int)area.Right, Bottom = (int)area.Bottom };
        return ClipRect(ref rect);
    }
    internal static Rect ReadClip() => GetClipCursor(out var r)
        ? new Rect(r.Left, r.Top, Math.Max(0, r.Right - r.Left), Math.Max(0, r.Bottom - r.Top)) : Rect.Empty;

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropertyValue
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public short Boolean;
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropertyValue value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropertyValue value);
        [PreserveSig] int Commit();
    }
    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHGetPropertyStoreForWindow(nint hwnd, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    internal static bool SetEdgeGesturesDisabled(nint hwnd, bool disabled)
    {
        if (!NativeMethods.IsWindow(hwnd)) return !disabled;
        IPropertyStore? store = null;
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out store) < 0) return false;
            var key = new PropertyKey { Format = new Guid("32CE38B2-2C9A-41B1-9BC5-B3784394AA44"), Id = 2 };
            var value = new PropertyValue { Type = 11, Boolean = disabled ? (short)-1 : (short)0 };
            return store.SetValue(ref key, ref value) >= 0;
        }
        catch (Exception ex) { DiagnosticLog.Write($"edge gesture property failed: {ex.Message}"); return false; }
        finally { if (store is not null) Marshal.ReleaseComObject(store); }
    }

    internal sealed class ForegroundWatcher : IDisposable
    {
        private delegate void WinEventProc(nint hook, uint eventType, nint hwnd,
            int idObject, int idChild, uint eventThread, uint eventTime);
        [DllImport("user32.dll")]
        private static extern nint SetWinEventHook(uint min, uint max, nint module,
            WinEventProc callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(nint hook);
        private readonly WinEventProc _callback;
        private readonly nint _hook;
        internal ForegroundWatcher(Action changed)
        {
            _callback = (_, _, _, _, _, _, _) =>
            {
                try { changed(); }
                catch (Exception ex) { DiagnosticLog.Write($"foreground protection update failed: {ex.Message}"); }
            };
            _hook = SetWinEventHook(3, 3, 0, _callback, 0, 0, 0); // OUTOFCONTEXT, no DLL injection.
        }
        public void Dispose() { if (_hook != 0) UnhookWinEvent(_hook); GC.KeepAlive(_callback); }
    }
}
