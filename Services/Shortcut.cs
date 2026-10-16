using System.Windows.Input;

namespace Keyloom.Services;

public static class Shortcut
{
    public const uint Control = 2, Shift = 4, Alt = 1, Win = 8;
    public const uint StopKey = 0x7B;
    public const uint AppModifiers = Control | Shift;
    public static bool IsReserved(uint modifiers, uint key) => modifiers == AppModifiers && key == StopKey;
    public static string Format(uint modifiers, uint key)
    {
        if (key == 0) return "No shortcut";
        var parts = new List<string>();
        if ((modifiers & Control) != 0) parts.Add("Ctrl");
        if ((modifiers & Alt) != 0) parts.Add("Alt");
        if ((modifiers & Shift) != 0) parts.Add("Shift");
        if ((modifiers & Win) != 0) parts.Add("Win");
        string name = KeyInterop.KeyFromVirtualKey((int)key).ToString();
        if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])) name = name[1..];
        parts.Add(name);
        return string.Join(" + ", parts);
    }
    public static bool IsModifier(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;
    /// <summary>Why a key can't be a macro shortcut, or null when it can: F1–F24 work alone, other keys need Ctrl, Alt or Win.</summary>
    public static string? Problem(uint modifiers, uint key)
    {
        if (IsReserved(modifiers, key)) return "This shortcut is reserved for Keyloom.";
        bool functionKey = key is >= 0x70 and <= 0x87;
        if (!functionKey && (modifiers & (Control | Alt | Win)) == 0) return "Use Ctrl, Alt, or Win with a key, or choose an F1–F24 key.";
        return null;
    }
    public static uint CurrentModifiers()
    {
        var m = Keyboard.Modifiers;
        return (m.HasFlag(ModifierKeys.Control) ? Control : 0) | (m.HasFlag(ModifierKeys.Shift) ? Shift : 0)
             | (m.HasFlag(ModifierKeys.Alt) ? Alt : 0) | (m.HasFlag(ModifierKeys.Windows) ? Win : 0);
    }
}
