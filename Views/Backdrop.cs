using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Keyloom.Views;

/// <summary>Gives windows rounded corners and, on Windows 11 22H2+, a blurred acrylic backdrop behind a translucent shell.</summary>
internal static class Backdrop
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    private const int CornerPreference = 33, SystemBackdropType = 38, RoundCorners = 2, Acrylic = 3;
    public static bool IsAcrylicSupported => Environment.OSVersion.Version.Build >= 22621;
    public static void Attach(Window window) => window.SourceInitialized += (_, _) => Apply(window);
    private static void Apply(Window window)
    {
        if (PresentationSource.FromVisual(window) is not HwndSource source) return;
        IntPtr hwnd = source.Handle;
        int corners = RoundCorners;
        DwmSetWindowAttribute(hwnd, CornerPreference, ref corners, sizeof(int));
        if (!IsAcrylicSupported) return;
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        if (DwmExtendFrameIntoClientArea(hwnd, ref margins) != 0) return;
        int backdrop = Acrylic;
        if (DwmSetWindowAttribute(hwnd, SystemBackdropType, ref backdrop, sizeof(int)) != 0) return;
        source.CompositionTarget.BackgroundColor = Colors.Transparent;
        window.Background = Brushes.Transparent;
        window.Resources["ShellBrush"] = window.FindResource("GlassShellBrush");
    }
}
