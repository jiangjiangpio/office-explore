using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Cursor = System.Windows.Input.Cursor;

namespace OfficeTouchBar.Infrastructure;

internal static class InkCursors
{
    // A native cursor is moved by Windows, independently of InkCanvas repainting.
    // Keep the hotspot centered on the same contact used for whole-stroke erasing.
    internal static Cursor CreateEraser()
    {
        const int size = 24;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRoundedRectangle(Brushes.White, new Pen(Brushes.DimGray, 1.5),
                new Rect(5, 5, 14, 14), 2, 2);
            dc.DrawGeometry(Brushes.White, new Pen(Brushes.Black, 1.3),
                Geometry.Parse("M8,12 L12,8 L17,13 L13,17 L11,17 Z M10,10 L15,15"));
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[size * size * 4];
        bitmap.CopyPixels(pixels, size * 4, 0);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            var maskStride = ((size + 31) / 32) * 4;
            writer.Write((ushort)0); writer.Write((ushort)2); writer.Write((ushort)1);
            writer.Write((byte)size); writer.Write((byte)size); writer.Write((ushort)0);
            writer.Write((ushort)(size / 2)); writer.Write((ushort)(size / 2));
            writer.Write(40 + pixels.Length + maskStride * size); writer.Write(22);
            writer.Write(40); writer.Write(size); writer.Write(size * 2);
            writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write(0); writer.Write(pixels.Length); writer.Write(0); writer.Write(0);
            writer.Write(0); writer.Write(0);
            for (var y = size - 1; y >= 0; y--)
                writer.Write(pixels, y * size * 4, size * 4);
            for (var y = size - 1; y >= 0; y--)
            {
                var mask = new byte[maskStride];
                for (var x = 0; x < size; x++)
                    if (pixels[(y * size + x) * 4 + 3] == 0)
                        mask[x / 8] |= (byte)(0x80 >> (x % 8));
                writer.Write(mask);
            }
        }
        stream.Position = 0;
        return new Cursor(stream, scaleWithDpi: true);
    }
}
