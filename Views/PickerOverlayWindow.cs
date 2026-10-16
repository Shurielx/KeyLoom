using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Keyloom.Views;

/// <summary>Highlights the window under the pointer while choosing a point. Purely visual; input passes through.</summary>
public sealed class PickerOverlayWindow : OverlayWindow
{
    private static readonly Brush Accent = Frozen(Color.FromRgb(15, 108, 189));
    private readonly Border _frame = new() { BorderBrush = Accent, BorderThickness = new(2), CornerRadius = new(4), Background = Frozen(Color.FromArgb(0x1A, 15, 108, 189)) };
    private readonly Canvas _canvas = new();
    private readonly Line _horizontal = new() { Stroke = Accent, StrokeThickness = 1, StrokeDashArray = new() { 3, 3 } };
    private readonly Line _vertical = new() { Stroke = Accent, StrokeThickness = 1, StrokeDashArray = new() { 3, 3 } };
    private readonly Ellipse _dot = new() { Width = 10, Height = 10, Stroke = Brushes.White, StrokeThickness = 2, Fill = Accent };
    private readonly Border _label;
    private readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold, FontSize = 12.5, Foreground = Frozen(Color.FromRgb(25, 54, 75)), MaxWidth = 320, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _detail = new() { FontSize = 11, Foreground = Frozen(Color.FromRgb(90, 113, 132)), Margin = new(0, 2, 0, 0) };
    public PickerOverlayWindow()
    {
        Title = L.T("Keyloom point picker");
        _label = new()
        {
            Background = Frozen(Color.FromArgb(0xF2, 0xFB, 0xFD, 0xFF)), BorderBrush = Frozen(Color.FromArgb(0x33, 15, 53, 80)), BorderThickness = new(1),
            CornerRadius = new(8), Padding = new(10, 6, 12, 7), Child = new StackPanel { Children = { _title, _detail } }
        };
        _canvas.Children.Add(_horizontal); _canvas.Children.Add(_vertical); _canvas.Children.Add(_dot); _canvas.Children.Add(_label);
        Content = new Grid { Children = { _frame, _canvas } };
    }
    public void ShowTarget(Int32Rect client, int x, int y, string title, string detail)
    {
        Place(client.X, client.Y, client.Width, client.Height);
        _frame.Visibility = _horizontal.Visibility = _vertical.Visibility = Visibility.Visible;
        var local = ToLocal(x, y);
        _horizontal.X1 = 0; _horizontal.X2 = LocalSize.Width; _horizontal.Y1 = _horizontal.Y2 = local.Y;
        _vertical.Y1 = 0; _vertical.Y2 = LocalSize.Height; _vertical.X1 = _vertical.X2 = local.X;
        PlaceLabel(local, title, detail);
    }
    public void ShowHint(int x, int y, string title, string detail)
    {
        Place(x + 16, y + 18, (int)(340 * Scale), (int)(60 * Scale));
        _frame.Visibility = _horizontal.Visibility = _vertical.Visibility = _dot.Visibility = Visibility.Collapsed;
        _title.Text = title; _detail.Text = detail;
        Canvas.SetLeft(_label, 0); Canvas.SetTop(_label, 0);
    }
    private void PlaceLabel(Point local, string title, string detail)
    {
        _dot.Visibility = Visibility.Visible;
        Canvas.SetLeft(_dot, local.X - 5); Canvas.SetTop(_dot, local.Y - 5);
        _title.Text = title; _detail.Text = detail;
        _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double left = local.X + 14, top = local.Y + 16;
        if (left + _label.DesiredSize.Width > LocalSize.Width) left = Math.Max(0, local.X - 14 - _label.DesiredSize.Width);
        if (top + _label.DesiredSize.Height > LocalSize.Height) top = Math.Max(0, local.Y - 16 - _label.DesiredSize.Height);
        Canvas.SetLeft(_label, left); Canvas.SetTop(_label, top);
    }
    private static Brush Frozen(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
}
