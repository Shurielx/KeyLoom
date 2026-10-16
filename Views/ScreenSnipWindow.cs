using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Keyloom.Engine;

namespace Keyloom.Views;

/// <summary>What was taken from the screen: a picture and where it was, or one color and where it was.</summary>
public sealed record ScreenSnip(PixelImage? Picture, uint Color, Int32Rect Bounds);

/// <summary>
/// Freezes the monitor under the pointer and lets the person drag around a picture, or click a color.
/// The frozen copy is what gets saved, so menus and hover effects that vanish on a click can still be captured.
/// </summary>
public sealed class ScreenSnipWindow : OverlayWindow
{
    private static readonly Brush Accent = Frozen(Color.FromRgb(15, 108, 189));
    private readonly bool _color;
    private readonly PixelImage _shot;
    private readonly Int32Rect _monitor;
    private readonly Canvas _canvas = new();
    private readonly Rectangle _selection = new() { Stroke = Accent, StrokeThickness = 2, Fill = Frozen(Color.FromArgb(0x22, 15, 108, 189)), Visibility = Visibility.Collapsed };
    private readonly Border _label;
    private readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold, FontSize = 12.5, Foreground = Frozen(Color.FromRgb(25, 54, 75)) };
    private readonly TextBlock _detail = new() { FontSize = 11, Foreground = Frozen(Color.FromRgb(90, 113, 132)), Margin = new(0, 2, 0, 0) };
    private readonly Border _swatch = new() { Width = 18, Height = 18, CornerRadius = new(4), BorderBrush = Brushes.White, BorderThickness = new(1.5), Margin = new(0, 0, 8, 0) };
    private readonly TaskCompletionSource<ScreenSnip?> _done = new();
    private Point? _from;
    protected override bool ClickThrough => false;

    private ScreenSnipWindow(bool color, PixelImage shot, Int32Rect monitor)
    {
        _color = color; _shot = shot; _monitor = monitor;
        Title = "Keyloom screen capture";
        ShowActivated = true; Focusable = true; IsHitTestVisible = true; Cursor = Cursors.Cross;
        var image = new Image { Source = ToBitmap(shot), Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        _swatch.Visibility = color ? Visibility.Visible : Visibility.Collapsed;
        _label = new()
        {
            Background = Frozen(Color.FromArgb(0xF2, 0xFB, 0xFD, 0xFF)), BorderBrush = Frozen(Color.FromArgb(0x33, 15, 53, 80)), BorderThickness = new(1),
            CornerRadius = new(8), Padding = new(10, 6, 12, 7), IsHitTestVisible = false,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _swatch, new StackPanel { Children = { _title, _detail } } } }
        };
        _canvas.Children.Add(_selection); _canvas.Children.Add(_label);
        Content = new Grid
        {
            Background = Brushes.Black,
            Children = { image, new Border { Background = Frozen(Color.FromArgb(color ? (byte)0 : (byte)0x40, 0, 0, 0)), IsHitTestVisible = false }, _canvas }
        };
        ShowLabel(new Point(24, 24), color ? "Click the color to find" : "Drag around what to find",
            color ? "Esc cancels" : "Keep it small and distinctive, like a button or an icon · Esc cancels");
        Loaded += (_, _) => { Place(_monitor.X, _monitor.Y, _monitor.Width, _monitor.Height); Activate(); Focus(); };
        // Moving onto a monitor with another scale makes Windows resize the window; put it back over the monitor.
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(() => Place(_monitor.X, _monitor.Y, _monitor.Width, _monitor.Height));
        Closed += (_, _) => _done.TrySetResult(null);
    }

    /// <summary>Captures the monitor under the pointer and waits for a picture or a color. Null when cancelled.</summary>
    public static Task<ScreenSnip?> TakeAsync(bool color)
    {
        var at = System.Windows.Forms.Cursor.Position;
        var bounds = System.Windows.Forms.Screen.FromPoint(at).Bounds;
        var monitor = new Int32Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        var shot = ScreenCapture.Instance.Capture(monitor.X, monitor.Y, monitor.Width, monitor.Height);
        var window = new ScreenSnipWindow(color, shot, monitor);
        window.Show();
        return window._done.Task;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Finish(null); }
        base.OnKeyDown(e);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var at = e.GetPosition(_canvas);
        if (_color)
        {
            var (x, y) = Pixel(at);
            Finish(new(null, _shot[x, y], new(_monitor.X + x, _monitor.Y + y, 1, 1)));
            return;
        }
        _from = at; CaptureMouse();
        Update(at);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        var at = e.GetPosition(_canvas);
        if (_color)
        {
            var (x, y) = Pixel(at);
            uint c = _shot[x, y];
            _swatch.Background = Frozen(Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c));
            ShowLabel(at, TargetPicture.ColorText(c), "Click to use this color · Esc cancels");
        }
        else if (_from != null) Update(at);
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_color || _from is not { } from) return;
        ReleaseMouseCapture();
        var (x1, y1) = Pixel(from);
        var (x2, y2) = Pixel(e.GetPosition(_canvas));
        int left = Math.Min(x1, x2), top = Math.Min(y1, y2), width = Math.Abs(x2 - x1) + 1, height = Math.Abs(y2 - y1) + 1;
        _from = null;
        if (width < 4 || height < 4) { _selection.Visibility = Visibility.Collapsed; ShowLabel(e.GetPosition(_canvas), "Drag a bigger box", "At least 4 by 4 pixels · Esc cancels"); return; }
        width = Math.Min(width, Models.ScreenTarget.MaxPictureSide); height = Math.Min(height, Models.ScreenTarget.MaxPictureSide);
        var pixels = new uint[width * height];
        for (int row = 0; row < height; row++) Array.Copy(_shot.Pixels, (top + row) * _shot.Width + left, pixels, row * width, width);
        Finish(new(new PixelImage(width, height, pixels), 0, new(_monitor.X + left, _monitor.Y + top, width, height)));
    }
    private void Update(Point at)
    {
        if (_from is not { } from) return;
        double left = Math.Min(from.X, at.X), top = Math.Min(from.Y, at.Y);
        _selection.Visibility = Visibility.Visible;
        Canvas.SetLeft(_selection, left); Canvas.SetTop(_selection, top);
        _selection.Width = Math.Abs(at.X - from.X); _selection.Height = Math.Abs(at.Y - from.Y);
        var (x1, y1) = Pixel(from); var (x2, y2) = Pixel(at);
        ShowLabel(at, $"{Math.Abs(x2 - x1) + 1} × {Math.Abs(y2 - y1) + 1} px", "Let go to use this picture");
    }
    private (int X, int Y) Pixel(Point local)
    {
        var (x, y) = ToScreen(local);
        return (Math.Clamp(x - _monitor.X, 0, _shot.Width - 1), Math.Clamp(y - _monitor.Y, 0, _shot.Height - 1));
    }
    private void ShowLabel(Point at, string title, string detail)
    {
        _title.Text = title; _detail.Text = detail;
        _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = LocalSize;
        double left = at.X + 16, top = at.Y + 18;
        if (size.Width > 0 && left + _label.DesiredSize.Width > size.Width) left = Math.Max(0, at.X - 16 - _label.DesiredSize.Width);
        if (size.Height > 0 && top + _label.DesiredSize.Height > size.Height) top = Math.Max(0, at.Y - 18 - _label.DesiredSize.Height);
        Canvas.SetLeft(_label, left); Canvas.SetTop(_label, top);
    }
    private void Finish(ScreenSnip? snip)
    {
        _done.TrySetResult(snip);
        Close();
    }
    public static BitmapSource ToBitmap(PixelImage image)
    {
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, (int[])(object)image.Pixels, image.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }
    private static Brush Frozen(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
}
