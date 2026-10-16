using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Keyloom.Services;

namespace Keyloom.Views;

/// <summary>
/// The on-screen card for a macro: while it runs it stays up with the elapsed time and how to stop it; when it stops it says so
/// briefly and fades. With the indicator turned off in settings, starting shows the card only for a moment.
/// </summary>
public partial class ToastOverlayWindow : Window
{
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int style);
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(1600) };
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Stopwatch _elapsed = new();
    private Func<string>? _progress;
    private bool _shown;
    public ToastOverlayWindow()
    {
        InitializeComponent();
        L.Apply(this);
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); Animate(false); };
        _clockTimer.Tick += (_, _) => UpdateClock();
        Closed += (_, _) => { _hideTimer.Stop(); _clockTimer.Stop(); };
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        // Click-through, tool window, never activated: a macro can click right where the card is.
        SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x20 | 0x80 | 0x08000000);
    }
    /// <summary>Shows that a macro started. <paramref name="progress"/> gives the count shown beside the clock, like "34 clicks".</summary>
    public void ShowRunning(string macroName, Func<string>? progress = null)
    {
        bool stay = AppSettings.Current.ShowRunIndicator;
        _progress = progress;
        _elapsed.Restart();
        SetLook(running: true, L.T("Running"), macroName);
        UpdateClock();
        Present();
        _hideTimer.Stop();
        if (stay) _clockTimer.Start(); else _hideTimer.Start();
    }
    /// <summary>Shows that a macro stopped, with how long it ran, then fades.</summary>
    public void ShowStopped(string macroName, string summary)
    {
        _clockTimer.Stop(); _elapsed.Stop();
        SetLook(running: false, L.T("Stopped"), macroName);
        ToastSubtext.Text = summary;
        Present();
        _hideTimer.Stop(); _hideTimer.Start();
    }
    private void SetLook(bool running, string title, string name)
    {
        ToastTitle.Text = title;
        ToastName.Text = "  " + name;
        var color = running ? Color.FromRgb(46, 158, 107) : Color.FromRgb(120, 132, 146);
        ToastIcon.Data = (Geometry)FindResource(running ? "IconPlay" : "IconStop");
        ToastIcon.Fill = new SolidColorBrush(color);
        ToastBadge.Background = new SolidColorBrush(Color.FromArgb(0x24, color.R, color.G, color.B));
    }
    private void UpdateClock()
    {
        string count = _progress?.Invoke() ?? "";
        ToastSubtext.Text = UsageStore.Clock(_elapsed.Elapsed) + (count.Length > 0 ? " · " + count : "") + "  ·  " + L.T("Ctrl + Shift + F12 stops");
    }
    private void Present()
    {
        bool wasShown = _shown && IsVisible;
        Show(); UpdateLayout();
        var area = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = transform.Transform(new Point(area.Left, area.Top));
        var bottomRight = transform.Transform(new Point(area.Right, area.Bottom));
        var place = AppSettings.Current.IndicatorPlace;
        Left = place == IndicatorPlace.Top ? topLeft.X + (bottomRight.X - topLeft.X - ActualWidth) / 2 : bottomRight.X - ActualWidth - 4;
        Top = place == IndicatorPlace.BottomRight ? bottomRight.Y - ActualHeight - 4 : topLeft.Y + 12;
        // Already up: change the text in place instead of animating in again.
        if (!wasShown) Animate(true);
        else ToastCard.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
        _shown = true;
    }
    private void Animate(bool show)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(show ? 280 : 220);
        var fade = new DoubleAnimation(show ? 1 : 0, duration) { EasingFunction = ease };
        if (show) fade.From = 0;
        else fade.Completed += (_, _) => { if (ToastCard.Opacity == 0) { Hide(); _shown = false; } };
        ToastCard.BeginAnimation(OpacityProperty, fade);
        bool bottom = AppSettings.Current.IndicatorPlace == IndicatorPlace.BottomRight;
        double offset = bottom ? 10 : -10;
        ToastShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(show ? offset : 0, show ? 0 : offset * .6, duration) { EasingFunction = ease });
        var scale = new DoubleAnimation(show ? .96 : 1, show ? 1 : .98, duration) { EasingFunction = ease };
        ToastScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        ToastScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
    }
}
