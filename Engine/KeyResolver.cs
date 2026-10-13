using System.Runtime.InteropServices;
using System.Windows.Input;

namespace Keyloom.Engine;

public static class KeyResolver
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern short VkKeyScan(char ch);
    private static readonly Dictionary<string, ushort> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = 0xA2, ["Control"] = 0xA2, ["Shift"] = 0xA0, ["Alt"] = 0xA4,
        ["Win"] = 0x5B, ["Escape"] = 0x1B, ["Esc"] = 0x1B, ["Enter"] = 0x0D,
        ["Backspace"] = 0x08, ["Space"] = 0x20
    };
    public static ushort[] Resolve(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Enter a key or combination, such as Space or Ctrl+C.");
        var keys = new List<ushort>();
        foreach (string part in text.Split('+', StringSplitOptions.TrimEntries))
        {
            if (Aliases.TryGetValue(part, out ushort alias)) { keys.Add(alias); continue; }
            if (part.Length == 1 && char.IsAsciiLetterOrDigit(part[0])) { keys.Add(char.ToUpperInvariant(part[0])); continue; }
            if (Enum.TryParse<Key>(part, true, out var key) && Enum.IsDefined(key) && key != Key.None)
            {
                int vk = KeyInterop.VirtualKeyFromKey(key);
                if (vk > 0) { keys.Add((ushort)vk); continue; }
            }
            if (part.Length == 1 || part.Equals("Plus", StringComparison.OrdinalIgnoreCase))
            {
                short mapped = VkKeyScan(part.Length == 1 ? part[0] : '+');
                if (mapped != -1)
                {
                    if ((mapped & 0x100) != 0) keys.Add(0xA0);
                    if ((mapped & 0x200) != 0) keys.Add(0xA2);
                    if ((mapped & 0x400) != 0) keys.Add(0xA4);
                    keys.Add((ushort)(mapped & 0xFF)); continue;
                }
            }
            throw new ArgumentException($"Unknown key '{part}'. Use a key name such as F8, Space, LeftCtrl, or Ctrl+C.");
        }
        return keys.Distinct().ToArray();
    }
}
