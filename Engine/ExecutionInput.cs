namespace Keyloom.Engine;

internal sealed class ExecutionInput(IInputDriver driver)
{
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
    public void Move(int x, int y) => driver.Move(x, y);
    public void Position(int x, int y) => driver.Position(x, y);
    public void Scroll(int amount) => driver.Scroll(amount);
    public void ReleaseOwned()
    {
        var failures = new List<Exception>();
        foreach (char ch in _characters.ToArray()) Try(() => { driver.Character(ch, false); _characters.Remove(ch); });
        foreach (ushort key in _keys.Reverse().ToArray()) Try(() => KeyUp(key));
        foreach (string button in _buttons.ToArray()) Try(() => MouseUp(button));
        if (failures.Count > 0) throw new AggregateException("Some macro inputs could not be released.", failures);
        void Try(Action action) { try { action(); } catch (Exception ex) { failures.Add(ex); } }
    }
}
