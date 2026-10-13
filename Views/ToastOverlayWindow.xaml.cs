using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Keyloom.Views;

public partial class ToastOverlayWindow : Window
{
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int style);
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(1400) };
    public ToastOverlayWindow()
    {
        InitializeComponent();
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); Animate(false); };
        Closed += (_, _) => _hideTimer.Stop();
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x20 | 0x80 | 0x08000000);
    }
    public void ShowStatus(string macroName, bool isActive)
    {
        _hideTimer.Stop();
        ToastTitle.Text = isActive ? "Macro running" : "Macro stopped";
        ToastSubtext.Text = macroName;
        var color = isActive ? Color.FromRgb(46, 158, 107) : Color.FromRgb(196, 43, 28);
        ToastIcon.Data = (Geometry)FindResource(isActive ? "IconPlay" : "IconStop");
        ToastIcon.Fill = new SolidColorBrush(color);
        ToastBadge.Background = new SolidColorBrush(Color.FromArgb(0x21, color.R, color.G, color.B));
        Show(); UpdateLayout();
        var area = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = transform.Transform(new Point(area.Left, area.Top));
        var bottomRight = transform.Transform(new Point(area.Right, area.Bottom));
        Left = topLeft.X + (bottomRight.X - topLeft.X - ActualWidth) / 2;
        Top = topLeft.Y + 12;
        Animate(true);
        _hideTimer.Start();
    }
    private void Animate(bool show)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(show ? 320 : 220);
        var fade = new DoubleAnimation(show ? 1 : 0, duration) { EasingFunction = ease };
        if (show) fade.From = 0;
        else fade.Completed += (_, _) => { if (ToastCard.Opacity == 0) Hide(); };
        ToastCard.BeginAnimation(OpacityProperty, fade);
        ToastShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(show ? -14 : 0, show ? 0 : -8, duration) { EasingFunction = ease });
        var scale = new DoubleAnimation(show ? .94 : 1, show ? 1 : .97, duration) { EasingFunction = ease };
        ToastScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        ToastScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
    }
}
