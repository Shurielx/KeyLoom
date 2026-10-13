using Keyloom.Engine;
using Keyloom.Models;

namespace Keyloom.Recording;

public sealed class RecordingBuffer(RecordingOptions options, int maxSteps = ProfileValidator.MaxActions)
{
    private readonly List<MacroAction> _steps = new();
    private readonly Dictionary<ushort, string> _keys = new();
    private readonly HashSet<string> _buttons = new();
    private (int X, int Y, long Time)? _pendingMove;
    private (int X, int Y)? _lastPosition;
    private long _lastTime, _lastMoveTime = -1;
    private bool _completed;
    public int Count => _steps.Count;
    public bool LimitReached { get; private set; }
    public bool HoldsKey(ushort key) => _keys.ContainsKey(key);

    public void Key(ushort key, string name, bool down, long time)
    {
        if (!options.Keyboard || _completed || LimitReached) return;
        if (down == _keys.ContainsKey(key)) return;
        FlushMovement();
        if (!Append(new() { ActionType = down ? MacroActionType.KeyDown : MacroActionType.KeyUp, KeyName = name }, time)) return;
        if (down) _keys[key] = name; else _keys.Remove(key);
    }
    public void Movement(int x, int y, long time)
    {
        if (!options.MouseMovement || _completed || LimitReached) return;
        if (_lastPosition == (x, y) && _pendingMove == null) return;
        if (_lastMoveTime < 0 || time - _lastMoveTime >= options.MovementIntervalMs)
        {
            _pendingMove = null; Position(x, y, time);
        }
        else _pendingMove = (x, y, time);
    }
    public void Button(string button, bool down, int x, int y, long time)
    {
        if (!options.MouseButtons || _completed || LimitReached || down == _buttons.Contains(button)) return;
        FlushMovement();
        if (options.MouseMovement) Position(x, y, time);
        if (!Append(new() { ActionType = down ? MacroActionType.MouseDown : MacroActionType.MouseUp, MouseButton = button }, time)) return;
        if (down) _buttons.Add(button); else _buttons.Remove(button);
    }
    public void Wheel(int notches, int x, int y, long time)
    {
        if (!options.MouseButtons || _completed || LimitReached || notches == 0) return;
        FlushMovement();
        if (options.MouseMovement) Position(x, y, time);
        Append(new() { ActionType = MacroActionType.MouseScroll, ScrollAmount = Math.Clamp(notches, -100, 100) }, time);
    }
    public void Pause(long time)
    {
        if (_completed) return;
        FlushMovement(); ReleaseHeld(time);
    }
    public IReadOnlyList<MacroAction> Complete(long time)
    {
        if (!_completed) { FlushMovement(); ReleaseHeld(time); _completed = true; }
        return _steps.Select(a => a.Clone()).ToArray();
    }
    private void Position(int x, int y, long time)
    {
        if (_lastPosition == (x, y)) return;
        if (Append(new() { ActionType = MacroActionType.MousePosition, X = x, Y = y }, time))
        { _lastPosition = (x, y); _lastMoveTime = time; }
    }
    private void FlushMovement()
    {
        if (_pendingMove is not { } p) return;
        _pendingMove = null; Position(p.X, p.Y, p.Time);
    }
    private bool Append(MacroAction action, long time, bool finishing = false)
    {
        if (!finishing && (LimitReached || _steps.Count + 2 + _keys.Count + _buttons.Count + 2 > maxSteps))
        { LimitReached = true; return false; }
        AddDelay(time);
        _steps.Add(action); return true;
    }
    private void AddDelay(long time)
    {
        time = Math.Max(time, _lastTime);
        long elapsed = time - _lastTime;
        if (options.Timing && elapsed > 0)
            _steps.Add(new() { ActionType = MacroActionType.Delay, DelayMs = (int)Math.Min(3600000, elapsed) });
        _lastTime = time;
    }
    private void ReleaseHeld(long time)
    {
        if (_steps.Count == 0) return;
        AddDelay(time);
        foreach (var key in _keys.Reverse())
            Append(new() { ActionType = MacroActionType.KeyUp, KeyName = key.Value }, time, true);
        foreach (string button in _buttons)
            Append(new() { ActionType = MacroActionType.MouseUp, MouseButton = button }, time, true);
        _keys.Clear(); _buttons.Clear();
    }
}
