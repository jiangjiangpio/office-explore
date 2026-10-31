using System.Windows;

namespace OfficeTouchBar.Models;

public sealed record PowerPointSnapshot(
    bool IsConnected,
    bool IsForeground,
    bool IsSlideShow,
    int CurrentSlide,
    int TotalSlides,
    Rect WindowBounds,
    nint WindowHandle = default,
    string PresentationIdentity = "",
    string PresentationName = "",
    nint ObservedForegroundHandle = default);
