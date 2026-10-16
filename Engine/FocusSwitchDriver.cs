using System.Runtime.InteropServices;

namespace Keyloom.Engine;

/// <summary>Drivers that hold something between inputs can let it go when the macro pauses.</summary>
public interface IBurstDriver
{
    void EndBurst();
}

/// <summary>Foreground and pointer access for <see cref="FocusSwitchDriver"/>; replaceable in tests.</summary>
public interface IFocusSystem
{
    IntPtr Foreground();
    bool Activate(IntPtr window);
    bool IsMinimized(IntPtr window);
    (int X, int Y) Cursor();
    void SetCursor(int x, int y);
    void Pause(int milliseconds);
}

/// <summary>
/// Quick switch: for each burst of input, brings the target window to the front, sends real system input,
/// then gives focus and the pointer back. Works with games that ignore window messages (Roblox, for example),
/// at the cost of a short focus change every time the macro acts.
/// </summary>
public sealed class FocusSwitchDriver : IInputDriver, IBurstDriver
{
    /// <summary>Time for the game to notice focus before input, and to read the input before focus leaves.</summary>
    public const int SettleMs = 35;
    private readonly IInputDriver _inner;
    private readonly IntPtr _window;
    private readonly IFocusSystem _focus;
    private bool _engaged;
    private IntPtr _previous;
    private (int X, int Y) _cursor;
    /// <summary>Puts your cursor back where it was when focus is handed back. Off leaves it where the macro moved it.</summary>
    public bool RestoreCursor { get; set; } = true;
    public int X { get; private set; }
    public int Y { get; private set; }

    public FocusSwitchDriver(IInputDriver inner, IntPtr window, IWindowSystem? windows = null, IFocusSystem? focus = null)
    {
        _inner = inner; _window = window; _focus = focus ?? FocusSystem.Instance;
        var system = windows ?? WindowSystem.Instance;
        var (width, height) = system.ClientSize(window);
        (X, Y) = system.ClientToScreen(window, width / 2, height / 2);
    }
    public void Position(int x, int y)
    {
        Engage();
        X = x; Y = y;
        _inner.Position(x, y);
        // Many games only register a click after the pointer has really moved over them.
        _inner.Move(1, 0); _inner.Move(-1, 0);
    }
    public void Move(int x, int y) { Engage(); X += x; Y += y; _inner.Move(x, y); }
    public void Mouse(string button, bool down) { Engage(); _inner.Mouse(button, down); }
    public void Scroll(int notches) { Engage(); _inner.Scroll(notches); }
    public void Key(ushort key, bool down) { Engage(); _inner.Key(key, down); }
    public void Character(char value, bool down) { Engage(); _inner.Character(value, down); }
    private void Engage()
    {
        if (_engaged) return;
        _previous = _focus.Foreground();
        _engaged = true;
        if (_previous == _window) { _previous = IntPtr.Zero; return; }
        if (_focus.IsMinimized(_window)) { _engaged = false; throw new InvalidOperationException(L.T("The target window is minimized. Leave it open, it can stay behind your other windows.")); }
        _cursor = _focus.Cursor();
        if (!_focus.Activate(_window)) { _engaged = false; throw new InvalidOperationException(L.T("Windows did not let Keyloom bring the target window to the front.")); }
        _focus.Pause(SettleMs);
        // Put the pointer over the target first, so input never lands in the window you are using.
        _inner.Position(X, Y); _inner.Move(1, 0); _inner.Move(-1, 0);
    }
    /// <summary>Gives focus and the pointer back to whatever you were using.</summary>
    public void EndBurst()
    {
        if (!_engaged) return;
        _engaged = false;
        if (_previous == IntPtr.Zero) return;
        _focus.Pause(SettleMs);
        if (RestoreCursor) _focus.SetCursor(_cursor.X, _cursor.Y);
        _focus.Activate(_previous);
    }
}

public sealed class FocusSystem : IFocusSystem
{
    public static readonly FocusSystem Instance = new();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint to, bool on);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out WindowSystem.POINT point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    public IntPtr Foreground() => GetForegroundWindow();
    public bool IsMinimized(IntPtr window) => IsIconic(window);
    public bool Activate(IntPtr window)
    {
        if (!IsWindow(window)) return false;
        if (GetForegroundWindow() == window) return true;
        // Windows only lets the foreground thread change focus; sharing its input state lifts that limit.
        uint current = GetCurrentThreadId(), foreground = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        bool attached = foreground != 0 && foreground != current && AttachThreadInput(current, foreground, true);
        try { BringWindowToTop(window); SetForegroundWindow(window); }
        finally { if (attached) AttachThreadInput(current, foreground, false); }
        return GetForegroundWindow() == window;
    }
    public (int X, int Y) Cursor() => GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);
    public void SetCursor(int x, int y) => SetCursorPos(x, y);
    public void Pause(int milliseconds) => Thread.Sleep(milliseconds);
}
