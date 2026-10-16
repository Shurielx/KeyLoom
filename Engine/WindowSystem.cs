using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Keyloom.Models;

namespace Keyloom.Engine;

/// <summary>Window queries the executor needs; replaceable in tests.</summary>
public interface IWindowSystem
{
    IntPtr Find(WindowTarget target);
    bool IsAlive(IntPtr window);
    (int X, int Y) ClientToScreen(IntPtr window, int x, int y);
    (int Width, int Height) ClientSize(IntPtr window);
    (int X, int Y) Cursor();
}

public sealed class WindowSystem : IWindowSystem
{
    public static readonly WindowSystem Instance = new();
    private delegate bool EnumProc(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref POINT point);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr window, ref POINT point);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    /// <summary>
    /// Brings a minimized window back to its place. Windows parks minimized windows near -32000, -32000, so anything drawn
    /// on them would land far off the screen. Returns false when the window stays minimized.
    /// </summary>
    public static bool Unminimize(IntPtr window)
    {
        if (window == IntPtr.Zero || !IsIconic(window)) return true;
        ShowWindow(window, 9);
        return !IsIconic(window);
    }
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int max);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public IntPtr Find(WindowTarget target)
    {
        IntPtr exact = IntPtr.Zero, sameClass = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || !ProcessName(window).Equals(target.ProcessName, StringComparison.OrdinalIgnoreCase)) return true;
            if (target.ClassName.Length > 0 && ClassName(window) != target.ClassName) return true;
            if (Title(window) == target.Title) { exact = window; return false; }
            if (sameClass == IntPtr.Zero) sameClass = window;
            return true;
        }, IntPtr.Zero);
        return exact != IntPtr.Zero ? exact : sameClass;
    }
    public bool IsAlive(IntPtr window) => window != IntPtr.Zero && IsWindow(window);
    public (int X, int Y) ClientToScreen(IntPtr window, int x, int y)
    {
        var point = new POINT { X = x, Y = y };
        ClientToScreen(window, ref point); return (point.X, point.Y);
    }
    public static (int X, int Y) ScreenToClient(IntPtr window, int x, int y)
    {
        var point = new POINT { X = x, Y = y };
        ScreenToClient(window, ref point); return (point.X, point.Y);
    }
    public (int Width, int Height) ClientSize(IntPtr window) =>
        GetClientRect(window, out var r) ? (r.Right - r.Left, r.Bottom - r.Top) : (0, 0);
    public (int X, int Y) Cursor() => GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);
    /// <summary>The top-level window under a screen point.</summary>
    public static IntPtr RootAt(int x, int y)
    {
        var child = WindowFromPoint(new POINT { X = x, Y = y });
        return child == IntPtr.Zero ? IntPtr.Zero : GetAncestor(child, 2);
    }
    public static RECT Bounds(IntPtr window) => GetWindowRect(window, out var r) ? r : default;
    public static bool IsOwn(IntPtr window)
    {
        GetWindowThreadProcessId(window, out uint process); return process == Environment.ProcessId;
    }
    public static WindowTarget Describe(IntPtr window)
    {
        var (width, height) = Instance.ClientSize(window);
        return new() { ProcessName = ProcessName(window), ClassName = ClassName(window), Title = Title(window), ClientWidth = width, ClientHeight = height };
    }
    private static string Title(IntPtr window)
    {
        var text = new StringBuilder(512); GetWindowText(window, text, text.Capacity); return text.ToString();
    }
    private static string ClassName(IntPtr window)
    {
        var text = new StringBuilder(256); GetClassName(window, text, text.Capacity); return text.ToString();
    }
    private static string ProcessName(IntPtr window)
    {
        GetWindowThreadProcessId(window, out uint id);
        try { using var process = Process.GetProcessById((int)id); return process.ProcessName; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return ""; }
    }
}
