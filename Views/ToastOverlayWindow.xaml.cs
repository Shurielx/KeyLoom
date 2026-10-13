using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
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
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); Hide(); };
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
        var color = isActive ? Color.FromRgb(55, 124, 101) : Color.FromRgb(157, 82, 64);
        ToastCard.BorderBrush = new SolidColorBrush(color);
        ToastIcon.Fill = new SolidColorBrush(color);
        Show(); UpdateLayout();
        var area = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = transform.Transform(new Point(area.Left, area.Top));
        var bottomRight = transform.Transform(new Point(area.Right, area.Bottom));
        Left = topLeft.X + (bottomRight.X - topLeft.X - ActualWidth) / 2;
        Top = topLeft.Y + 20;
        _hideTimer.Start();
    }
}
