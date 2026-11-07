using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OfficeTouchBar;
using OfficeTouchBar.Infrastructure;
using OfficeTouchBar.Models;

internal static class Program
{
    private static int _passed, _failed;
    private static InkOverlayWindow _window = null!;
    private static InkCanvas _canvas = null!;

    [STAThread]
    private static int Main(string[] args)
    {
        var pointer = args.Contains("--pointer");
        if (pointer)
        {
            AppContext.SetSwitch("Switch.System.Windows.Input.Stylus.EnablePointerSupport", true);
            NativeContact.InitializeTouch();
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try
            {
                _window = new InkOverlayWindow { Left = 40, Top = 40, Width = 800, Height = 500, Background = Brushes.White };
                _window.Show();
                _window.Activate();
                _canvas = (InkCanvas)_window.FindName("InkSurface");
                _canvas.Focus();
                await Task.Delay(300);
                // Synthetic pointer devices emit WM_POINTER. WISP on this
                // headless runner has no tablet service/device to convert them
                // into legacy packets; touch is exercised by the pointer run.
                Console.WriteLine(pointer ? "Windows pointer stack: native mouse and synthetic touch" : "Default WISP stack: native mouse");
                Console.WriteLine($"WPF tablet devices: {Tablet.TabletDevices.Count}");
                foreach (var touch in pointer ? new[] { false, true } : new[] { false })
                {
                    var name = touch ? "touch" : "mouse";
                    await Check($"{name}: uninterrupted long stroke", () => Stroke(touch, false, false));
                    await Check($"{name}: repeated capture loss during one contact", () => Stroke(touch, true, false));
                    await Check($"{name}: highlighter survives capture loss", () => Stroke(touch, true, true));
                    await Check($"{name}: explicit cancellation does not resume", () => Cancel(touch));
                    await Check($"{name}: a new contact does not join the old stroke", () => Separate(touch));
                    await Check($"{name}: clearing during contact keeps the page empty", () => ClearDuringContact(touch));
                    await Check($"{name}: another control's capture is respected", () => OtherCapture(touch));
                    await Check($"{name}: page replacement cannot inherit the old contact", () => ReplacePage(touch));
                    await Check($"{name}: foreground handoff cancels the old contact", () => ForegroundHandoff(touch));
                }
            }
            catch (Exception ex) { _failed++; Console.WriteLine($"FAIL test infrastructure: {ex}"); }
            finally
            {
                _window?.Close();
                await DiagnosticLog.FlushAsync();
                Console.WriteLine($"{_passed} Windows input checks passed; {_failed} failed.");
                app.Shutdown(_failed == 0 ? 0 : 1);
            }
        };
        return app.Run();
    }

    private static async Task Check(string name, Func<Task> test)
    {
        try { await test(); _passed++; Console.WriteLine($"PASS {name}"); }
        catch (Exception ex) { _failed++; Console.WriteLine($"FAIL {name}: {ex}"); }
        finally { _window.CancelInput("test cleanup"); await Task.Delay(100); }
    }

    private static void Assert(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    private static void Prepare(bool highlighter = false)
    {
        _window.CancelInput("test preparation");
        _window.ClearPage();
        _window.ConfigureTool(highlighter ? PointerTool.Highlighter : PointerTool.Pen, Colors.Red);
        _window.Activate();
        _canvas.Focus();
    }

    private static async Task Stroke(bool touch, bool loseCapture, bool highlighter)
    {
        Prepare(highlighter);
        using var input = new NativeContact(_canvas, touch);
        var stylusDowns = 0;
        StylusDownEventHandler observe = (_, _) => stylusDowns++;
        _canvas.PreviewStylusDown += observe;
        try
        {
            await input.Down(80, 140);
            Assert(touch ? _canvas.IsStylusCaptured : _canvas.IsMouseCaptured,
                "Native Down did not reach WPF's ink editor.");
            for (var x = 90; x <= 620; x += 10)
            {
                if (loseCapture && x is 220 or 420)
                {
                    if (touch) _canvas.ReleaseStylusCapture();
                    else _canvas.ReleaseMouseCapture();
                    // No second Down is injected. The contact stays physically pressed.
                    await Task.Delay(60);
                }
                if (x == 320) await Task.Delay(2200); // Exceed the stale-contact timeout without lifting.
                await input.Move(x, 140);
            }
            await Task.Delay(100);
            Assert(NativeContact.HasRedInk(_canvas, 570, 140), "The live stroke was invisible before Up.");
            await input.Up();
            Assert(_canvas.Strokes.Count == 1, $"Expected one physical stroke, got {_canvas.Strokes.Count}.");
            var stroke = _canvas.Strokes[0];
            var points = stroke.StylusPoints;
            Assert(points[0].X < 100 && points[^1].X > 600,
                $"Long stroke stopped early: {points[0].X:F1} .. {points[^1].X:F1}.");
            Assert(stroke.DrawingAttributes.IsHighlighter == highlighter, "Drawing attributes changed.");
            if (touch) Assert(stylusDowns == 1, $"Expected exactly one native touch Down, got {stylusDowns}.");
            Assert(!_window.HasActiveContact, "Input state remained busy after physical Up.");
        }
        finally { _canvas.PreviewStylusDown -= observe; }
    }

    private static async Task Cancel(bool touch)
    {
        Prepare();
        using var input = new NativeContact(_canvas, touch);
        await input.Down(80, 180);
        await input.Move(200, 180);
        _window.CancelInput("explicit navigation");
        await input.Move(400, 180);
        await input.Up();
        Assert(_canvas.Strokes.Count == 1, "Cancellation lost or duplicated the committed partial stroke.");
        Assert(_canvas.Strokes[0].StylusPoints[^1].X < 250, "Cancelled contact resumed unexpectedly.");
    }

    private static async Task Separate(bool touch)
    {
        Prepare();
        using var input = new NativeContact(_canvas, touch);
        await input.Down(80, 220);
        await input.Move(200, 220);
        if (touch) _canvas.ReleaseStylusCapture(); else _canvas.ReleaseMouseCapture();
        await input.Up();
        await input.Down(420, 260);
        await input.Move(600, 260);
        await input.Up();
        Assert(_canvas.Strokes.Count == 2, $"Two physical contacts became {_canvas.Strokes.Count} strokes.");
        Assert(_canvas.Strokes[1].StylusPoints[0].X > 400, "New contact joined the previous stroke.");
    }

    private static async Task ClearDuringContact(bool touch)
    {
        Prepare();
        using var input = new NativeContact(_canvas, touch);
        await input.Down(80, 220);
        await input.Move(200, 220);
        if (touch) _canvas.ReleaseStylusCapture(); else _canvas.ReleaseMouseCapture();
        _window.ClearPage();
        await input.Move(500, 220);
        await input.Up();
        Assert(_canvas.Strokes.Count == 0, "A cancelled stroke reappeared after page clear.");
    }

    private static async Task OtherCapture(bool touch)
    {
        Prepare();
        using var input = new NativeContact(_canvas, touch);
        await input.Down(80, 220);
        await input.Move(200, 220);
        var device = Stylus.CurrentStylusDevice;
        if (touch) Assert(device!.Capture(_window), "Cannot transfer test stylus capture.");
        else Assert(Mouse.Capture(_window), "Cannot transfer test mouse capture.");
        await input.Move(500, 220);
        Assert(touch ? device!.Captured == _window : Mouse.Captured == _window,
            "Recovery stole capture from another control.");
        await input.Up();
        if (touch) device!.Capture(null); else Mouse.Capture(null);
        Assert(_canvas.Strokes.Count == 1 && _canvas.Strokes[0].StylusPoints[^1].X < 250,
            "Recovery continued ink after deliberate capture transfer.");
    }

    private static async Task ReplacePage(bool touch)
    {
        Prepare();
        using var input = new NativeContact(_canvas, touch);
        await input.Down(80, 220);
        await input.Move(200, 220);
        _window.LoadPage(InkPageData.Empty());
        await input.Move(500, 220);
        await input.Up();
        Assert(_canvas.Strokes.Count == 0, "An old contact wrote onto the next page.");
    }

    private static async Task ForegroundHandoff(bool touch)
    {
        Prepare();
        using var input = new NativeContact(_canvas, touch);
        await input.Down(80, 220);
        await input.Move(200, 220);
        if (touch) _canvas.ReleaseStylusCapture(); else _canvas.ReleaseMouseCapture();
        var other = new Window { Left = 850, Top = 40, Width = 120, Height = 120, Topmost = true };
        try
        {
            other.Show();
            other.Activate();
            await Task.Delay(100);
            Assert(other.IsActive, "The test foreground window did not activate.");
            await input.Move(400, 220);
        }
        finally { other.Close(); }
        _window.Activate();
        await input.Move(600, 220);
        await input.Up();
        Assert(_canvas.Strokes.Count == 1 && _canvas.Strokes[0].StylusPoints[^1].X < 250,
            "A contact cancelled by a foreground handoff resumed on return.");
    }
}

internal sealed class NativeContact(FrameworkElement target, bool touch) : IDisposable
{
    private bool _down;
    private NativePoint _position;
    private static nint _touchDevice;

    internal static void InitializeTouch()
    {
        if (_touchDevice != 0) return;
        _touchDevice = CreateSyntheticPointerDevice(2, 1, 3);
        if (_touchDevice == 0) throw new System.ComponentModel.Win32Exception();
    }

    internal async Task Down(double x, double y)
    {
        Position(x, y);
        if (touch && _touchDevice == 0)
        {
            Console.WriteLine($"Touch injection: pointerSize={Marshal.SizeOf<PointerInfo>()} touchSize={Marshal.SizeOf<TouchInfo>()} screen={GetSystemMetrics(0)}x{GetSystemMetrics(1)} firstPoint={_position.X},{_position.Y}");
            InitializeTouch();
        }
        Send(0x10000 | 0x2 | 0x4, 0x2);
        _down = true;
        await Task.Delay(60);
        if (target is InkCanvas canvas && !(touch ? canvas.IsStylusCaptured : canvas.IsMouseCaptured))
            throw new InvalidOperationException($"Native {(touch ? "touch" : "mouse")} Down did not enter WPF ink collection.");
        if (touch && Stylus.CurrentStylusDevice is { } stylus)
            Console.WriteLine($"Touch Down: id={stylus.Id} inAir={stylus.InAir} inverted={stylus.Inverted} position={stylus.GetPosition(target)} points={stylus.GetStylusPoints(target).Count} mode={((InkCanvas)target).ActiveEditingMode}");
    }
    internal async Task Move(double x, double y)
    {
        Position(x, y);
        if (touch) Send(0x20000 | 0x2 | 0x4, 0);
        await Task.Delay(16);
    }
    internal async Task Up()
    {
        if (!_down) return;
        Send(0x40000, 0x4);
        _down = false;
        await Task.Delay(100);
    }
    private void Position(double x, double y)
    {
        var point = target.PointToScreen(new Point(x, y));
        _position = new() { X = (int)point.X, Y = (int)point.Y };
        if (!touch && !SetCursorPos(_position.X, _position.Y))
            throw new System.ComponentModel.Win32Exception();
    }
    private void Send(uint touchFlags, uint mouseFlags)
    {
        if (touch)
        {
            var info = new TouchInfo
            {
                Pointer = new() { Type = 2, Id = 1, Flags = touchFlags, Pixel = _position },
                Mask = 7, Orientation = 90, Pressure = 512,
                Contact = new() { Left = _position.X - 2, Right = _position.X + 2,
                    Top = _position.Y - 2, Bottom = _position.Y + 2 }
            };
            if (!InjectSyntheticPointerInput(_touchDevice, [new() { Type = 2, Touch = info }], 1))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                    $"InjectSyntheticPointerInput flags=0x{touchFlags:X} point={_position.X},{_position.Y}");
        }
        else
        {
            var input = new MouseInput { Type = 0, Flags = mouseFlags };
            if (SendInput(1, [input], Marshal.SizeOf<MouseInput>()) != 1)
                throw new System.ComponentModel.Win32Exception();
        }
    }
    public void Dispose()
    {
        if (!_down) return;
        Send(0x40000, 0x4);
        _down = false;
    }
    internal static bool HasRedInk(FrameworkElement target, double x, double y)
    {
        var point = target.PointToScreen(new Point(x, y));
        var dc = GetDC(0);
        if (dc == 0) throw new System.ComponentModel.Win32Exception();
        try
        {
            for (var dy = -3; dy <= 3; dy++)
            {
                var color = GetPixel(dc, (int)point.X, (int)point.Y + dy);
                var red = color & 255;
                var green = (color >> 8) & 255;
                var blue = (color >> 16) & 255;
                if (color != uint.MaxValue && red > green + 40 && red > blue + 40) return true;
            }
            return false;
        }
        finally { ReleaseDC(0, dc); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct PointerInfo
    {
        internal uint Type, Id, FrameId, Flags;
        internal nint Source, Target;
        internal NativePoint Pixel, Himetric, PixelRaw, HimetricRaw;
        internal uint Time, History;
        internal int InputData;
        internal uint KeyStates;
        internal ulong Performance;
        internal uint ButtonChange;
    }
    [StructLayout(LayoutKind.Sequential)] private struct TouchInfo
    {
        internal PointerInfo Pointer;
        internal uint Flags, Mask;
        internal NativeRect Contact, ContactRaw;
        internal uint Orientation, Pressure;
    }
    [StructLayout(LayoutKind.Explicit, Size = 152)] private struct PointerTypeInfo
    {
        [FieldOffset(0)] internal uint Type;
        [FieldOffset(8)] internal TouchInfo Touch;
    }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct MouseInput
    {
        [FieldOffset(0)] internal uint Type;
        [FieldOffset(8)] internal int X;
        [FieldOffset(12)] internal int Y;
        [FieldOffset(16)] internal uint Data;
        [FieldOffset(20)] internal uint Flags;
        [FieldOffset(24)] internal uint Time;
        [FieldOffset(32)] internal nuint Extra;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(nint dc, int x, int y);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, MouseInput[] inputs, int size);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint CreateSyntheticPointerDevice(uint type, uint count, uint mode);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool InjectSyntheticPointerInput(nint device, PointerTypeInfo[] inputs, uint count);
}
