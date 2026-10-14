using System.Runtime.InteropServices;
using Keyloom.Engine;

namespace Keyloom.Views;

/// <summary>
/// Draws the planned route at its real screen position. Click-through; sends no input. The window covers only the
/// route's area (not every monitor), because a transparent window's whole surface is re-composed on each frame.
/// </summary>
public sealed class ScreenPreviewOverlay : OverlayWindow
{
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    private const int Edge = 40;
    private readonly PathPreview _preview = new();
    private readonly MacroTimeline _timeline;
    public ScreenPreviewOverlay(MacroTimeline timeline)
    {
        _timeline = timeline;
        Title = "Keyloom preview overlay";
        Content = _preview;
        SourceInitialized += (_, _) =>
        {
            PlaceOnScreens();
            _preview.Map = (x, y) => ToLocal(x, y);
            _preview.Timeline = timeline;
        };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(() => { PlaceOnScreens(); _preview.Redraw(); });
    }
    private void PlaceOnScreens()
    {
        int screenLeft = GetSystemMetrics(76), screenTop = GetSystemMetrics(77);
        int screenRight = screenLeft + GetSystemMetrics(78), screenBottom = screenTop + GetSystemMetrics(79);
        var (left, top, right, bottom) = _timeline.Bounds;
        int x = Math.Max(screenLeft, (int)left - Edge), y = Math.Max(screenTop, (int)top - Edge);
        int r = Math.Min(screenRight, (int)right + Edge), b = Math.Min(screenBottom, (int)bottom + Edge);
        if (r <= x || b <= y) { x = screenLeft; y = screenTop; r = x + 1; b = y + 1; }
        Place(x, y, r - x, b - y);
    }
    public double Time { set => _preview.Time = value; }
}
