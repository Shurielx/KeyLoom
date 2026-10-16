using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Keyloom.Engine;
using Keyloom.Models;
using Keyloom.Views;

namespace Keyloom.Services;

/// <summary>A point chosen inside another window, in that window's client coordinates.</summary>
public sealed record PickedPoint(IntPtr Window, WindowTarget Target, int X, int Y);

/// <summary>
/// Lets the user click once in any window to choose a target. The click is consumed so the target app never sees it;
/// right-click or Esc cancels. Hooks exist only while picking.
/// </summary>
public sealed class PointPicker : IDisposable
{
    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public WindowSystem.POINT point; public uint data, flags, time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardData { public uint key, scan, flags, time; public UIntPtr extra; }
    private readonly HookProc _mouseCallback, _keyboardCallback;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly PickerOverlayWindow _overlay = new();
    private TaskCompletionSource<PickedPoint?>? _result;
    private PickedPoint? _picked;
    private IntPtr _mouseHook, _keyboardHook;
    private uint _swallowUp;
    public PointPicker()
    {
        _mouseCallback = MouseHook; _keyboardCallback = KeyboardHook;
        _timer.Tick += (_, _) => Refresh();
    }
    public Task<PickedPoint?> PickAsync()
    {
        if (_result != null) throw new InvalidOperationException(L.T("Already picking."));
        _result = new(); _picked = null; _swallowUp = 0;
        var module = GetModuleHandle(null);
        _mouseHook = SetWindowsHookEx(14, _mouseCallback, module, 0);
        _keyboardHook = SetWindowsHookEx(13, _keyboardCallback, module, 0);
        if (_mouseHook == IntPtr.Zero || _keyboardHook == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            Finish(null);
            throw new Win32Exception(error, L.T("Windows could not start point picking."));
        }
        _overlay.Show(); Refresh(); _timer.Start();
        return _result.Task;
    }
    private void Refresh()
    {
        var (x, y) = WindowSystem.Instance.Cursor();
        var root = WindowSystem.RootAt(x, y);
        if (root == IntPtr.Zero || WindowSystem.IsOwn(root)) { _overlay.ShowHint(x, y, L.T("Choose a window outside Keyloom"), L.T("Esc or right-click cancels")); return; }
        var (cx, cy) = WindowSystem.ScreenToClient(root, x, y);
        var (width, height) = WindowSystem.Instance.ClientSize(root);
        var origin = WindowSystem.Instance.ClientToScreen(root, 0, 0);
        bool inside = cx >= 0 && cy >= 0 && cx < width && cy < height;
        _overlay.ShowTarget(new(origin.X, origin.Y, width, height), x, y, Title(root),
            inside ? L.T($"{cx}, {cy}   ·   Click to choose") : L.T("Choose a point inside the window content"));
    }
    private static string Title(IntPtr window)
    {
        var target = WindowSystem.Describe(window);
        return target.Title.Length > 0 ? target.Title : target.ProcessName;
    }
    private IntPtr MouseHook(int code, IntPtr message, IntPtr data)
    {
        if (code < 0 || _result == null) return CallNextHookEx(_mouseHook, code, message, data);
        var m = Marshal.PtrToStructure<MouseData>(data);
        if ((m.flags & 1) != 0) return CallNextHookEx(_mouseHook, code, message, data);
        uint msg = (uint)message.ToInt32();
        if (msg == _swallowUp)
        {
            _swallowUp = 0;
            if (_picked != null || msg == 0x205) Dispatch(() => Finish(_picked));
            return new IntPtr(1);
        }
        if (msg == 0x204) { _swallowUp = 0x205; _picked = null; return new IntPtr(1); }
        if (msg != 0x201) return CallNextHookEx(_mouseHook, code, message, data);
        var root = WindowSystem.RootAt(m.point.X, m.point.Y);
        // Every left click is consumed while picking; only one inside another window's content is accepted.
        _swallowUp = 0x202; _picked = null;
        if (root == IntPtr.Zero || WindowSystem.IsOwn(root)) return new IntPtr(1);
        var (cx, cy) = WindowSystem.ScreenToClient(root, m.point.X, m.point.Y);
        var (width, height) = WindowSystem.Instance.ClientSize(root);
        if (cx >= 0 && cy >= 0 && cx < width && cy < height) _picked = new(root, WindowSystem.Describe(root), cx, cy);
        return new IntPtr(1);
    }
    private IntPtr KeyboardHook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && _result != null && Marshal.PtrToStructure<KeyboardData>(data).key == 0x1B)
        {
            if (message.ToInt32() == 0x100) Dispatch(() => Finish(null));
            return new IntPtr(1);
        }
        return CallNextHookEx(_keyboardHook, code, message, data);
    }
    private void Dispatch(Action action) => _overlay.Dispatcher.BeginInvoke(action);
    public void Cancel() => Finish(null);
    private void Finish(PickedPoint? point)
    {
        _timer.Stop();
        if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        if (_keyboardHook != IntPtr.Zero) { UnhookWindowsHookEx(_keyboardHook); _keyboardHook = IntPtr.Zero; }
        _overlay.Hide();
        var result = _result; _result = null; _swallowUp = 0;
        result?.TrySetResult(point);
    }
    public void Dispose()
    {
        Finish(null); _overlay.Close();
        GC.KeepAlive(_mouseCallback); GC.KeepAlive(_keyboardCallback);
    }
}
