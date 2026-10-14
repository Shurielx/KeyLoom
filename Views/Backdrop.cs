using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Keyloom.Services;

namespace Keyloom.Views;

/// <summary>Gives windows rounded corners and, on Windows 11 22H2+, a blurred acrylic backdrop behind a translucent shell. Follows the light or dark theme.</summary>
internal static class Backdrop
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    private const int DarkMode = 20, CornerPreference = 33, SystemBackdropType = 38, RoundCorners = 2, Acrylic = 3;
    public static bool IsAcrylicSupported => Environment.OSVersion.Version.Build >= 22621;
    public static void Attach(Window window) => window.SourceInitialized += (_, _) =>
    {
        bool glass = Apply(window);
        void Refresh()
        {
            SetDark(window);
            if (!glass) return;
            window.Resources.Remove("ShellBrush");
            window.Resources["ShellBrush"] = window.FindResource("GlassShellBrush");
        }
        ThemeManager.Changed += Refresh;
        window.Closed += (_, _) => ThemeManager.Changed -= Refresh;
    };
    private static bool Apply(Window window)
    {
        if (PresentationSource.FromVisual(window) is not HwndSource source) return false;
        IntPtr hwnd = source.Handle;
        int corners = RoundCorners;
        DwmSetWindowAttribute(hwnd, CornerPreference, ref corners, sizeof(int));
        SetDark(window);
        if (!IsAcrylicSupported) return false;
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        if (DwmExtendFrameIntoClientArea(hwnd, ref margins) != 0) return false;
        int backdrop = Acrylic;
        if (DwmSetWindowAttribute(hwnd, SystemBackdropType, ref backdrop, sizeof(int)) != 0) return false;
        source.CompositionTarget.BackgroundColor = Colors.Transparent;
        window.Background = Brushes.Transparent;
        window.Resources["ShellBrush"] = window.FindResource("GlassShellBrush");
        return true;
    }
    /// <summary>Dark mode tints the acrylic material and the window border to match the dark palette.</summary>
    private static void SetDark(Window window)
    {
        if (PresentationSource.FromVisual(window) is not HwndSource source) return;
        int dark = ThemeManager.IsDark ? 1 : 0;
        DwmSetWindowAttribute(source.Handle, DarkMode, ref dark, sizeof(int));
    }
}
