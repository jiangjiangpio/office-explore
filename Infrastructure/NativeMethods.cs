using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace OfficeTouchBar.Infrastructure;

internal static class NativeMethods
{
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hWnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint hWnd, out RECT rect);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint hWnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] internal static extern nint BeginDeferWindowPos(int count);
    [DllImport("user32.dll")] internal static extern nint DeferWindowPos(nint batch, nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] internal static extern bool EndDeferWindowPos(nint batch);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hWnd, uint command);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hWnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] internal static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder name, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int capacity);
    internal static string WindowTitle(nint hwnd)
    {
        var text = new StringBuilder(1024);
        GetWindowText(hwnd, text, text.Capacity);
        return text.ToString();
    }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, nint parameter);
    private delegate bool EnumWindowProc(nint hwnd, nint parameter);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern nint GetMessageExtraInfo();
    private static readonly nuint InputMarker = new(0x4f544231);
    internal static bool IsOwnInputMessage() => (nuint)GetMessageExtraInfo() == InputMarker;
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(POINT point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [DllImport("oleaut32.dll", PreserveSig = false)]
    private static extern void GetActiveObject(ref Guid clsid, nint reserved, [MarshalAs(UnmanagedType.Interface)] out object result);

    internal static object? GetRunningComObject(string progId)
    {
        try
        {
            var type = Type.GetTypeFromProgID(progId);
            if (type is null) return null;
            var clsid = type.GUID;
            GetActiveObject(ref clsid, 0, out var result);
            return result;
        }
        catch { return null; }
    }

    internal static bool IsPowerPointWindow(nint hwnd)
    {
        if (hwnd == 0 || !IsWindow(hwnd)) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName.Equals("POWERPNT", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    internal static string ProcessIdentity(nint hwnd)
    {
        if (!IsWindow(hwnd)) return "";
        GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return $"{pid}:{process.StartTime.ToUniversalTime().Ticks}";
        }
        catch { return ""; }
    }

    // Transfer ownership once, then let PowerPoint receive physical key-up,
    // repeat and subsequent keys normally. Never synthesize an early key-up.
    internal static bool HandoffKeyboard(nint target, nint ink, int message, nint key, nint detail)
    {
        return KeyboardTransfer.Transfer(message, key, detail, ink, target,
            GetForegroundWindow, IsSlideShowWindow, RestoreFocus,
            vk => (GetAsyncKeyState(vk) & 0x8000) != 0, packets =>
            {
                var inputs = packets.Select(packet => new INPUT
                {
                    Type = 1,
                    Data = new INPUTUNION { Keyboard = new KEYBDINPUT
                    {
                        VirtualKey = packet.ScanCode != 0 ? (ushort)0 : packet.VirtualKey,
                        ScanCode = packet.ScanCode,
                        ExtraInfo = InputMarker,
                        Flags = (packet.KeyUp ? 2u : 0) | (packet.Extended ? 1u : 0)
                            | (packet.ScanCode != 0 ? 4u : 0) // KEYEVENTF_SCANCODE preserves left/right keys.
                    } }
                }).ToArray();
                return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            });
    }

    internal static bool HandoffKeyboardBatch(nint target, nint ink, IReadOnlyList<KeyboardMessage> messages) =>
        KeyboardTransfer.TransferBatch(messages, ink, target, GetForegroundWindow, IsSlideShowWindow,
            RestoreFocus, vk => (GetAsyncKeyState(vk) & 0x8000) != 0, SendKeyboardPackets);

    private static uint SendKeyboardPackets(KeyboardPacket[] packets)
    {
        var inputs = packets.Select(packet => new INPUT
        {
            Type = 1,
            Data = new INPUTUNION { Keyboard = new KEYBDINPUT
            {
                VirtualKey = packet.ScanCode != 0 ? (ushort)0 : packet.VirtualKey,
                ScanCode = packet.ScanCode,
                ExtraInfo = InputMarker,
                Flags = (packet.KeyUp ? 2u : 0) | (packet.Extended ? 1u : 0) | (packet.ScanCode != 0 ? 4u : 0)
            } }
        }).ToArray();
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            var releases = packets.Where(p => !p.KeyUp).DistinctBy(p => p.VirtualKey)
                .Select(p => p with { KeyUp = true }).ToArray();
            if (releases.Length > 0)
            {
                var releaseInputs = releases.Select(p => new INPUT { Type = 1, Data = new INPUTUNION
                    { Keyboard = new KEYBDINPUT { VirtualKey = p.ScanCode == 0 ? p.VirtualKey : (ushort)0,
                        ScanCode = p.ScanCode, ExtraInfo = InputMarker,
                        Flags = 2u | (p.Extended ? 1u : 0) | (p.ScanCode != 0 ? 4u : 0) } } }).ToArray();
                SendInput((uint)releaseInputs.Length, releaseInputs, Marshal.SizeOf<INPUT>());
            }
        }
        return sent;
    }

    internal static bool IsSlideShowWindow(nint hwnd)
    {
        var name = new StringBuilder(128);
        GetClassName(hwnd, name, name.Capacity);
        return name.ToString().Equals("screenClass", StringComparison.OrdinalIgnoreCase)
            && IsPowerPointWindow(hwnd);
    }

    internal static bool IsPowerPointEditorWindow(nint hwnd)
    {
        var name = new StringBuilder(128);
        GetClassName(hwnd, name, name.Capacity);
        return name.ToString().Equals("PPTFrameClass", StringComparison.OrdinalIgnoreCase)
            && IsPowerPointWindow(hwnd);
    }

    internal static nint FindSlideShowWindow(nint relatedWindow)
    {
        GetWindowThreadProcessId(relatedWindow, out var pid);
        nint result = 0;
        var count = 0;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var candidatePid);
            if (candidatePid == pid && IsWindowVisible(hwnd) && IsSlideShowWindow(hwnd))
            {
                result = hwnd;
                count++;
            }
            return true;
        }, 0);
        return count == 1 ? result : 0;
    }

    internal static IReadOnlyList<nint> FindOverlappingEditors(nint slideShow)
    {
        GetWindowThreadProcessId(slideShow, out var pid);
        var bounds = Bounds(slideShow);
        var result = new List<nint>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var candidatePid);
            if (hwnd == slideShow || candidatePid != pid || !IsWindowVisible(hwnd)) return true;
            var name = new StringBuilder(128);
            GetClassName(hwnd, name, name.Capacity);
            if (name.ToString().Equals("PPTFrameClass", StringComparison.OrdinalIgnoreCase)
                && bounds.IntersectsWith(Bounds(hwnd))) result.Add(hwnd);
            return true;
        }, 0);
        return result;
    }

    internal static IReadOnlyList<nint> FindPresentationWindows()
    {
        var result = new List<nint>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            var name = new StringBuilder(128);
            GetClassName(hwnd, name, name.Capacity);
            if ((name.ToString().Equals("screenClass", StringComparison.OrdinalIgnoreCase)
                || name.ToString().Equals("PPTFrameClass", StringComparison.OrdinalIgnoreCase))
                && IsPowerPointWindow(hwnd)) result.Add(hwnd);
            return true;
        }, 0);
        return result;
    }

    // Keep window and monitor coordinates in physical pixels, including negative origins.
    internal static Rect Bounds(nint hwnd) => GetWindowRect(hwnd, out var r)
        ? new Rect(r.Left, r.Top, Math.Max(0, r.Right - r.Left), Math.Max(0, r.Bottom - r.Top))
        : Rect.Empty;

    internal static double DpiScaleAt(double x, double y)
    {
        var monitor = MonitorFromPoint(new POINT { X = (int)x, Y = (int)y }, 2);
        return GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1;
    }

    internal static bool IsAbove(nint candidate, nint reference)
    {
        if (candidate == 0 || reference == 0 || candidate == reference) return false;
        var current = reference;
        for (var i = 0; i < 1024; i++)
        {
            current = GetWindow(current, 3); // GW_HWNDPREV walks toward the top of the Z order.
            if (current == 0) return false;
            if (current == candidate) return true;
        }
        return false;
    }

    internal static bool CanRestoreFocus(nint target, nint overlay)
    {
        if (!IsPowerPointWindow(target)) return false;
        var foreground = GetForegroundWindow();
        return foreground == overlay || foreground == target || IsPowerPointWindow(foreground);
    }

    internal static bool RestoreFocus(nint target, nint overlay)
    {
        if (!CanRestoreFocus(target, overlay)) return false;
        if (GetForegroundWindow() != target)
        {
            DiagnosticLog.Ink($"focus-request from={GetForegroundWindow()} target={target} allowedOverlay={overlay}");
            SetForegroundWindow(target);
        }
        return GetForegroundWindow() == target;
    }

    internal static bool SendKey(nint hwnd, byte key, nint overlay = default) => SendKeys(hwnd, 0, key, overlay);
    internal static bool SendKeyAfterDialog(nint hwnd, byte key, nint dialog = default) =>
        SendKeys(hwnd, 0, key, 0, dialog);
    internal static bool SendChord(nint hwnd, byte modifier, byte key, nint overlay = default) => SendKeys(hwnd, modifier, key, overlay);

    private static bool SendKeys(nint hwnd, byte modifier, byte key, nint overlay,
        nint additionalAllowedForeground = default)
    {
        // Reject a busy modifier before changing focus, rather than stealing
        // focus and only then discovering that the command cannot be sent.
        if (new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0))
            return false;
        if (!RestoreFocus(hwnd, overlay)
            && GetForegroundWindow() != additionalAllowedForeground)
            return false;
        if (GetForegroundWindow() != hwnd) SetForegroundWindow(hwnd);
        if (GetForegroundWindow() != hwnd) return false;
        var inputs = new List<INPUT>();
        if (modifier != 0) inputs.Add(KeyInput(modifier, false));
        inputs.Add(KeyInput(key, false));
        inputs.Add(KeyInput(key, true));
        if (modifier != 0) inputs.Add(KeyInput(modifier, true));
        var sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        if (sent == inputs.Count) return true;
        // A partial injection must not leave our modifier down.
        var release = new List<INPUT> { KeyInput(key, true) };
        if (modifier != 0) release.Add(KeyInput(modifier, true));
        SendInput((uint)release.Count, release.ToArray(), Marshal.SizeOf<INPUT>());
        DiagnosticLog.Write($"SendInput failed target={hwnd} sent={sent} error={Marshal.GetLastWin32Error()}");
        return false;
    }

    private static INPUT KeyInput(ushort key, bool up) => new()
    {
        Type = 1,
        Data = new INPUTUNION { Keyboard = new KEYBDINPUT { VirtualKey = key, Flags = up ? 2u : 0, ExtraInfo = InputMarker } }
    };

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint Type; public INPUTUNION Data; }
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
        [FieldOffset(0)] public MOUSEINPUT Mouse;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort VirtualKey, ScanCode; public uint Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int X, Y; public uint MouseData, Flags, Time; public nuint ExtraInfo; }
}
