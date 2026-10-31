using System.Windows.Ink;

namespace OfficeTouchBar.Models;

public enum InkEngine
{
    Office,
    BuiltIn
}

public sealed record InkPageKey(string PresentationIdentity, string PresentationName, int SlideNumber, int TotalSlides);

public sealed record InkPageData(StrokeCollection Strokes, double CanvasWidth, double CanvasHeight)
{
    public DateOnly? InkDay { get; init; }
    public static InkPageData Empty(double width = 0, double height = 0) => new([], width, height);
}
