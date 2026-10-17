using System.Globalization;
using System.Text.RegularExpressions;

namespace Keyloom.Services;

/// <summary>Time fields read "250", "250ms", "2s", "1.5s", "10m", "1h" or "1h 30m"; a bare number is milliseconds.</summary>
public static partial class TimeText
{
    [GeneratedRegex(@"\G(\d+(?:[.,]\d+)?)(ms|s|m|h)")]
    private static partial Regex Part();

    public static bool TryParse(string? text, out int ms)
    {
        ms = 0;
        string s = (text ?? "").Replace(" ", "").ToLowerInvariant();
        if (s.Length == 0) return false;
        if (int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out ms)) return true;
        double total = 0;
        int at = 0;
        while (at < s.Length)
        {
            var m = Part().Match(s, at);
            if (!m.Success) return false;
            double value = double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            total += value * m.Groups[2].Value switch { "h" => 3600000, "m" => 60000, "s" => 1000, _ => 1 };
            at += m.Length;
        }
        if (total > int.MaxValue) return false;
        ms = (int)Math.Round(total);
        return true;
    }

    /// <summary>Whole seconds, minutes and hours read short ("2m", "1h 30m"); anything else stays in ms.</summary>
    public static string Format(int ms)
    {
        if (ms < 1000 || ms % 1000 != 0) return ms.ToString(CultureInfo.InvariantCulture);
        int h = ms / 3600000, m = ms / 60000 % 60, sec = ms / 1000 % 60;
        var parts = new List<string>();
        if (h > 0) parts.Add($"{h}h");
        if (m > 0) parts.Add($"{m}m");
        if (sec > 0) parts.Add($"{sec}s");
        return string.Join(" ", parts);
    }

    /// <summary>A time to read in a sentence: "30s", "1m 30s" or "250 ms".</summary>
    public static string Display(int ms) => ms < 1000 || ms % 1000 != 0 ? $"{ms:N0} ms" : Format(ms);
}
