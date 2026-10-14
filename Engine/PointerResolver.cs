using Keyloom.Models;

namespace Keyloom.Engine;

/// <summary>Turns stored positions into screen coordinates at the moment each step runs, so a moved window is followed.</summary>
public sealed class PointerResolver
{
    private readonly IWindowSystem _windows;
    private readonly IntPtr _window;
    private readonly WindowTarget? _target;
    private readonly bool _scale;
    private readonly (int X, int Y) _start;
    public PointerResolver(IWindowSystem windows, IntPtr window, MacroProfile profile)
    {
        _windows = windows; _window = window; _target = profile.Window; _scale = profile.ScaleWithWindow;
        _start = profile.Actions.Any(a => a.ActionType == MacroActionType.MousePosition && a.Space == CoordinateSpace.Start) ? windows.Cursor() : (0, 0);
    }
    public void EnsureAlive()
    {
        if (_window != IntPtr.Zero && !_windows.IsAlive(_window)) throw new InvalidOperationException("The target window was closed. The macro stopped.");
    }
    public (int X, int Y) Resolve(CoordinateSpace space, int x, int y)
    {
        switch (space)
        {
            case CoordinateSpace.Start: return (_start.X + x, _start.Y + y);
            case CoordinateSpace.Window when _window != IntPtr.Zero:
                if (_scale && _target is { ClientWidth: > 0, ClientHeight: > 0 } t)
                {
                    var (width, height) = _windows.ClientSize(_window);
                    if (width > 0 && height > 0)
                    {
                        x = (int)Math.Round(x * (double)width / t.ClientWidth);
                        y = (int)Math.Round(y * (double)height / t.ClientHeight);
                    }
                }
                return _windows.ClientToScreen(_window, x, y);
            default: return (x, y);
        }
    }
}
