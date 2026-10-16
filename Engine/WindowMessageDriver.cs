using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Keyloom.Engine;

/// <summary>
/// Background input: posts mouse and keyboard messages to one window instead of the system input stream,
/// so the real pointer and focus stay with the user. Apps that read Raw Input or check the physical
/// key state (many games) can ignore these messages; the editor's Test button shows whether a target reacts.
/// </summary>
public sealed class WindowMessageDriver : IInputDriver
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr ChildWindowFromPointEx(IntPtr parent, WindowSystem.POINT point, uint flags);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr window);
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    {
        public int size, flags;
        public IntPtr active, focus, capture, menuOwner, moveSize, caret;
        public WindowSystem.RECT caretRect;
    }
    private const uint MouseMove = 0x200, MouseWheel = 0x20A, KeyDown = 0x100, KeyUp = 0x101, SysKeyDown = 0x104, SysKeyUp = 0x105, Char = 0x102;
    private readonly IntPtr _window;
    private readonly HashSet<ushort> _keys = new();
    private uint _buttons;
    public int X { get; private set; }
    public int Y { get; private set; }

    public WindowMessageDriver(IntPtr window, IWindowSystem? windows = null)
    {
        _window = window;
        var system = windows ?? WindowSystem.Instance;
        var (width, height) = system.ClientSize(window);
        (X, Y) = system.ClientToScreen(window, width / 2, height / 2);
    }
    public void Position(int x, int y) { X = x; Y = y; PostMouse(MouseMove, IntPtr.Zero); }
    public void Move(int x, int y) => Position(X + x, Y + y);
    public void Mouse(string button, bool down)
    {
        (uint message, uint flag, uint extra) = button switch
        {
            "Left" => (0x201u, 0x1u, 0u), "Right" => (0x204u, 0x2u, 0u), "Middle" => (0x207u, 0x10u, 0u),
            "X1" => (0x20Bu, 0x20u, 1u), "X2" => (0x20Bu, 0x40u, 2u),
            _ => throw new ArgumentException(L.T("Unknown mouse button."))
        };
        if (down) _buttons |= flag; else _buttons &= ~flag;
        // Up messages follow their down message: LBUTTONUP = LBUTTONDOWN + 1, XBUTTONUP = XBUTTONDOWN + 1.
        PostMouse(down ? message : message + 1, (IntPtr)(extra << 16));
    }
    public void Scroll(int notches)
    {
        var (target, _, _) = ChildAt();
        Post(target, MouseWheel, (IntPtr)(((long)(short)(notches * 120) << 16) | (ushort)State()), Pack(X, Y));
    }
    public void Key(ushort key, bool down)
    {
        if (down) _keys.Add(key); else _keys.Remove(key);
        uint scan = MapVirtualKey(key, 4);
        long data = 1 | ((scan & 0xFF) << 16);
        if ((scan & 0xFF00) == 0xE000 || key is 0xA3 or 0xA5 or 0x5B or 0x5C) data |= 1 << 24;
        if (!down) data |= 0xC0000000L;
        bool alt = key is 0x12 or 0xA4 or 0xA5 || _keys.Contains(0xA4) || _keys.Contains(0xA5);
        if (alt) data |= 1 << 29;
        Post(Focus(), alt ? (down ? SysKeyDown : SysKeyUp) : (down ? KeyDown : KeyUp), (IntPtr)Generic(key), (IntPtr)data);
    }
    public void Character(char value, bool down)
    {
        if (down) Post(Focus(), Char, (IntPtr)value, (IntPtr)1);
    }
    private void PostMouse(uint message, IntPtr extra)
    {
        var (target, x, y) = ChildAt();
        Post(target, message, (IntPtr)((long)extra | State()), Pack(x, y));
    }
    /// <summary>MK_ flags for wParam: held buttons plus Ctrl and Shift.</summary>
    private uint State()
    {
        uint state = _buttons;
        if (_keys.Contains(0xA0) || _keys.Contains(0xA1) || _keys.Contains(0x10)) state |= 0x4;
        if (_keys.Contains(0xA2) || _keys.Contains(0xA3) || _keys.Contains(0x11)) state |= 0x8;
        return state;
    }
    /// <summary>The deepest visible, enabled child under the virtual pointer, with the point in its client coordinates.</summary>
    private (IntPtr Window, int X, int Y) ChildAt()
    {
        IntPtr current = _window;
        for (int depth = 0; depth < 32; depth++)
        {
            var (cx, cy) = WindowSystem.ScreenToClient(current, X, Y);
            var child = ChildWindowFromPointEx(current, new() { X = cx, Y = cy }, 0x1 | 0x2 | 0x4);
            if (child == IntPtr.Zero || child == current) return (current, cx, cy);
            current = child;
        }
        var (fx, fy) = WindowSystem.ScreenToClient(current, X, Y);
        return (current, fx, fy);
    }
    private IntPtr Focus()
    {
        uint thread = GetWindowThreadProcessId(_window, out _);
        var info = new GuiThreadInfo { size = Marshal.SizeOf<GuiThreadInfo>() };
        if (GetGUIThreadInfo(thread, ref info) && info.focus != IntPtr.Zero && (info.focus == _window || IsChild(_window, info.focus))) return info.focus;
        return _window;
    }
    private static ushort Generic(ushort key) => key switch { 0xA0 or 0xA1 => 0x10, 0xA2 or 0xA3 => 0x11, 0xA4 or 0xA5 => 0x12, _ => key };
    private static IntPtr Pack(int x, int y) => (IntPtr)(((long)(ushort)(short)y << 16) | (ushort)(short)x);
    private static void Post(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (!PostMessage(window, message, wParam, lParam))
            throw new Win32Exception(Marshal.GetLastWin32Error(), L.T("The target window rejected background input. It may be closed or running as administrator."));
    }
}
