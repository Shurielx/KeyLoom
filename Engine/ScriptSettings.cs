using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Keyloom.Engine;

/// <summary>Point is { x, y } with a Pick on the screen; Color, Grid and Target are text with a picker, asked for after a comma.</summary>
public enum ScriptSettingKind { Number, Text, Switch, Choice, Point, Color, Grid, Target }

/// <summary>
/// One constant a script shows as a field beside it, marked with a comment so the script stays plain JavaScript:
/// <c>const SPEED = 80 // setting: How fast</c>. <see cref="Offset"/> and <see cref="Length"/> are where its value is
/// written in the script, quotes included.
/// </summary>
public sealed record ScriptSetting(string Name, string Label, ScriptSettingKind Kind, string Value, IReadOnlyList<string> Choices, int Line, int Offset, int Length, char Quote)
{
    /// <summary>What decides the fields: the same shape keeps the fields and only their values change.
    /// Grids and targets also keep their value in it, so a name the list does not have yet rebuilds it.</summary>
    public string Shape => $"{Name}\u001f{Label}\u001f{Kind}\u001f{string.Join("\u001e", Choices)}" + (Kind is ScriptSettingKind.Grid or ScriptSettingKind.Target ? "\u001f" + Value : "");
}

/// <summary>Finds a script's settings, and writes a new value back into the script where the setting is.</summary>
public static partial class ScriptSettings
{
    /// <summary>Every line like <c>const NAME = value // setting: Label</c>, in order. A value is a number, text in quotes, true or false,
    /// or a point <c>{ x: 10, y: 20 }</c>. Choices follow the label after a comma, split by |: <c>// setting: Mode, fast | slow</c>.
    /// Text can ask for a picker the same way: <c>// setting: Glow, color</c>, <c>, grid</c> or <c>, target</c>.</summary>
    public static IReadOnlyList<ScriptSetting> Read(string code)
    {
        var found = new List<ScriptSetting>();
        int start = 0, number = 0;
        while (start <= code.Length)
        {
            int end = code.IndexOf('\n', start);
            if (end < 0) end = code.Length;
            number++;
            string line = code[start..end].TrimEnd('\r');
            if (Marked().Match(line) is { Success: true } m && !found.Any(s => s.Name == m.Groups["name"].Value))
                found.Add(Setting(m, number, start));
            start = end + 1;
        }
        return found;
    }
    private static ScriptSetting Setting(System.Text.RegularExpressions.Match m,int line, int lineStart)
    {
        var literal = m.Groups["value"];
        string text = literal.Value, label = m.Groups["label"].Value.Trim();
        List<string> choices = [];
        ScriptSettingKind? picker = null;
        int comma = label.IndexOf(',');
        if (comma >= 0 && label[(comma + 1)..].Contains('|'))
        {
            choices = label[(comma + 1)..].Split('|').Select(c => c.Trim()).Where(c => c.Length > 0).Distinct().ToList();
            label = label[..comma].Trim();
        }
        else if (label.LastIndexOf(',') is var last and >= 0 && label[(last + 1)..].Trim().ToLowerInvariant() is "color" or "grid" or "target" && text[0] is '"' or '\'')
        {
            picker = Enum.Parse<ScriptSettingKind>(label[(last + 1)..].Trim(), ignoreCase: true);
            label = label[..last].Trim();
        }
        if (label.Length == 0) label = m.Groups["name"].Value;
        char quote = text[0] is '"' or '\'' ? text[0] : '\0';
        if (Point().Match(text) is { Success: true } point)
            return new(m.Groups["name"].Value, label, ScriptSettingKind.Point, $"{point.Groups["x"].Value}, {point.Groups["y"].Value}", [], line, lineStart + literal.Index, literal.Length, quote);
        string value = quote != '\0' ? Unquote(text) : text;
        var kind = picker ?? (text is "true" or "false" ? ScriptSettingKind.Switch : choices.Count > 0 ? ScriptSettingKind.Choice
            : quote != '\0' ? ScriptSettingKind.Text : ScriptSettingKind.Number);
        if (kind != ScriptSettingKind.Choice) choices = [];
        else if (!choices.Contains(value)) choices.Insert(0, value);
        return new(m.Groups["name"].Value, label, kind, value, choices, line, lineStart + literal.Index, literal.Length, quote);
    }

    /// <summary>The value as it is written in the script: a number, text in the setting's own quotes, or true or false.</summary>
    /// <exception cref="ArgumentException">The value does not suit the setting, like letters for a number.</exception>
    public static string Literal(ScriptSetting setting, string value)
    {
        switch (setting.Kind)
        {
            case ScriptSettingKind.Switch:
                return value is "true" or "false" ? value : throw new ArgumentException(L.T($"{setting.Label} is on or off."));
            case ScriptSettingKind.Point:
                var parts = value.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length != 2 || !parts.All(p => int.TryParse(p, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)))
                    throw new ArgumentException(L.T($"{setting.Label} is a point: two whole numbers, x and y."));
                return $"{{ x: {int.Parse(parts[0], CultureInfo.InvariantCulture)}, y: {int.Parse(parts[1], CultureInfo.InvariantCulture)} }}";
            case ScriptSettingKind.Color:
                value = value.Trim();
                if (!ColorText().IsMatch(value)) throw new ArgumentException(L.T($"{setting.Label} is a color like #3FBF3F."));
                return Quote(value.ToUpperInvariant(), setting.Quote);
            case ScriptSettingKind.Number:
            case ScriptSettingKind.Choice when setting.Quote == '\0':
                value = value.Trim();
                if (!NumberText().IsMatch(value) || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    throw new ArgumentException(L.T($"{setting.Label} must be a number, such as 80 or 0.5."));
                return value;
            default:
                return Quote(value, setting.Quote);
        }
    }
    /// <summary>The script with the setting's value changed and everything else as it was.</summary>
    public static string Write(string code, ScriptSetting setting, string value) =>
        code[..setting.Offset] + Literal(setting, value) + code[(setting.Offset + setting.Length)..];

    private static string Quote(string text, char quote)
    {
        var s = new StringBuilder().Append(quote);
        foreach (char c in text)
            s.Append(c switch
            {
                '\\' => "\\\\", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t",
                _ when c == quote => "\\" + quote,
                _ when c < ' ' => $"\\u{(int)c:x4}",
                _ => c.ToString()
            });
        return s.Append(quote).ToString();
    }
    private static string Unquote(string literal)
    {
        var s = new StringBuilder();
        for (int i = 1; i < literal.Length - 1; i++)
        {
            char c = literal[i];
            if (c != '\\' || i + 1 >= literal.Length - 1) { s.Append(c); continue; }
            char next = literal[++i];
            if (next == 'u' && i + 4 < literal.Length - 1 && int.TryParse(literal.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
            {
                s.Append((char)code); i += 4; continue;
            }
            s.Append(next switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '0' => '\0', _ => next });
        }
        return s.ToString();
    }

    [GeneratedRegex("""^\s*(?:const|let|var)\s+(?<name>[A-Za-z_$][\w$]*)\s*=\s*(?<value>-?\d+(?:\.\d+)?|\{\s*x\s*:\s*-?\d+\s*,\s*y\s*:\s*-?\d+\s*\}|"(?:[^"\\]|\\.)*"|'(?:[^'\\]|\\.)*'|true|false)\s*;?\s*//\s*(?i:setting)\b\s*:?(?<label>.*)$""")]
    private static partial Regex Marked();
    [GeneratedRegex(@"^-?\d+(\.\d+)?$")] private static partial Regex NumberText();
    [GeneratedRegex(@"^\{\s*x\s*:\s*(?<x>-?\d+)\s*,\s*y\s*:\s*(?<y>-?\d+)\s*\}$")] private static partial Regex Point();
    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")] private static partial Regex ColorText();
}
