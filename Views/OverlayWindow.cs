using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Keyloom.Views;

/// <summary>Base for transparent, click-through, never-activated windows placed in physical screen pixels.</summary>
public abstract class OverlayWindow : Window
{
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int style);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    private Int32Rect _bounds;
    protected OverlayWindow()
    {
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true;
        Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false; ShowActivated = false; Focusable = false;
        IsHitTestVisible = false; Width = 1; Height = 1; Left = -32000; Top = -32000;
        // Windows resizes a window that moves to a monitor with another DPI; forget the placement so the next Place restores it.
        DpiChanged += (_, _) => _bounds = default;
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        // Transparent to hit testing, layered, tool window, no activation. Interactive overlays keep only the tool window style.
        SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | (ClickThrough ? 0x20 | 0x80000 | 0x80 | 0x08000000 : 0x80));
    }
    /// <summary>False for overlays you work in, like the builder: they take clicks and keyboard focus.</summary>
    protected virtual bool ClickThrough => true;
    /// <summary>Pixels per DIP for this window's current monitor.</summary>
    protected double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;
    /// <summary>Places the window over a rectangle in physical pixels; skipped when unchanged.</summary>
    protected void Place(int x, int y, int width, int height)
    {
        var bounds = new Int32Rect(x, y, Math.Max(1, width), Math.Max(1, height));
        if (bounds == _bounds) return;
        _bounds = bounds;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) SetWindowPos(hwnd, new IntPtr(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x10 | 0x40);
    }
    /// <summary>Size of the placed area in DIPs; valid immediately, before WPF re-runs layout.</summary>
    protected Size LocalSize => new(_bounds.Width / Scale, _bounds.Height / Scale);
    /// <summary>Converts a screen pixel to a position inside this window in DIPs.</summary>
    protected Point ToLocal(double x, double y) => new((x - _bounds.X) / Scale, (y - _bounds.Y) / Scale);
    /// <summary>Converts a position inside this window in DIPs back to a screen pixel.</summary>
    protected (int X, int Y) ToScreen(Point local) => ((int)Math.Round(_bounds.X + local.X * Scale), (int)Math.Round(_bounds.Y + local.Y * Scale));
}
