using System.Diagnostics;

namespace Keyloom.Engine;

/// <param name="pointerSettleMs">How long a button press waits after the pointer moved; see <see cref="Models.MacroProfile.PointerSettleMs"/>.</param>
/// <param name="cursor">Where the real pointer is, for a glide that starts before any position is known.</param>
internal sealed class ExecutionInput(Func<(int X, int Y)> cursor, IInputDriver driver, int pointerSettleMs = 0)
{
    /// <summary>A gliding cursor takes a step about this often; close to one frame on a 120 Hz screen.</summary>
    private const int GlideStepMs = 8;
    private readonly Stopwatch _sinceMove = new();
    private (int X, int Y)? _position;
    private readonly HashSet<ushort> _keys = new();
    private readonly HashSet<string> _buttons = new();
    private readonly HashSet<char> _characters = new();
    public void KeyDown(ushort key)
    {
        if (_keys.Contains(key)) return;
        driver.Key(key, true); _keys.Add(key);
    }
    public void KeyUp(ushort key)
    {
        if (!_keys.Contains(key)) return;
        driver.Key(key, false); _keys.Remove(key);
    }
    public void MouseDown(string button)
    {
        if (_buttons.Contains(button)) return;
        SettlePointer();
        driver.Mouse(button, true); _buttons.Add(button);
    }
    public void MouseUp(string button)
    {
        if (!_buttons.Contains(button)) return;
        driver.Mouse(button, false); _buttons.Remove(button);
    }
    public void Press(ushort[] keys)
    {
        var newlyHeld = keys.Where(k => !_keys.Contains(k)).ToArray();
        foreach (ushort key in newlyHeld) KeyDown(key);
        foreach (ushort key in newlyHeld.Reverse()) KeyUp(key);
    }
    public void Click(string button)
    {
        if (_buttons.Contains(button)) throw new InvalidOperationException("Release the held mouse button before clicking it.");
        MouseDown(button); MouseUp(button);
    }
    public void Text(string value, CancellationToken token)
    {
        foreach (char ch in value)
        {
            token.ThrowIfCancellationRequested();
            driver.Character(ch, true); _characters.Add(ch);
            driver.Character(ch, false); _characters.Remove(ch);
        }
    }
    public void Move(int x, int y)
    {
        driver.Move(x, y);
        if (x != 0 || y != 0) { _position = null; _sinceMove.Restart(); }
    }
    public void Position(int x, int y)
    {
        driver.Position(x, y);
        // Clicking the same point again, as repeat input does, needs no wait.
        if (_position != (x, y)) _sinceMove.Restart();
        _position = (x, y);
    }
    /// <summary>Moves the cursor in a straight line to a spot over a time, so the app sees it travel instead of jump.</summary>
    public void Glide(int x, int y, int milliseconds, CancellationToken token)
    {
        var (fromX, fromY) = _position ?? cursor();
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds)
        {
            double t = clock.Elapsed.TotalMilliseconds / milliseconds;
            var at = ((int)Math.Round(fromX + (x - fromX) * t), (int)Math.Round(fromY + (y - fromY) * t));
            if (at != _position) Position(at.Item1, at.Item2);
            if (token.WaitHandle.WaitOne(GlideStepMs)) token.ThrowIfCancellationRequested();
        }
        Position(x, y);
    }
    /// <summary>Waits out the rest of the settle time after the last move; a Wait step in between already did.</summary>
    private void SettlePointer()
    {
        if (!_sinceMove.IsRunning) return;
        long left = pointerSettleMs - _sinceMove.ElapsedMilliseconds;
        _sinceMove.Reset();
        if (left > 0) Thread.Sleep((int)left);
    }
    public void Scroll(int amount) => driver.Scroll(amount);
    /// <summary>Called when the macro waits; a quick-switch driver gives focus back unless something is still held.</summary>
    public void Settle()
    {
        if (_keys.Count == 0 && _buttons.Count == 0 && _characters.Count == 0) (driver as IBurstDriver)?.EndBurst();
    }
    public void ReleaseOwned()
    {
        var failures = new List<Exception>();
        foreach (char ch in _characters.ToArray()) Try(() => { driver.Character(ch, false); _characters.Remove(ch); });
        foreach (ushort key in _keys.Reverse().ToArray()) Try(() => KeyUp(key));
        foreach (string button in _buttons.ToArray()) Try(() => MouseUp(button));
        Try(() => (driver as IBurstDriver)?.EndBurst());
        if (failures.Count > 0) throw new AggregateException("Some macro inputs could not be released.", failures);
        void Try(Action action) { try { action(); } catch (Exception ex) { failures.Add(ex); } }
    }
}
