using Keyloom;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

using Keyloom.Models;
using Keyloom.Services;
using Keyloom.Views;

internal static partial class Program
{
    /// <summary>
    /// Every text people read, as the catalog keys it: XAML texts as written, and the C# texts in L.T and L.N,
    /// with values written as {0}, {1:N0} and so on, the way <see cref="Phrase"/> makes them at run time.
    /// </summary>
    private static SortedSet<string> UiStrings()
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        string root = RepoRoot();
        foreach (var file in new[] { "Views", "Engine", "Services", "Models" }.SelectMany(d => Directory.GetFiles(Path.Combine(root, d), "*.cs")).Append(Path.Combine(root, "App.xaml.cs")))
        {
            string src = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(src, @"\bL\.(T|N)\("))
            {
                int at = m.Index + m.Length;
                while (char.IsWhiteSpace(src[at])) at++;
                if (m.Groups[1].Value == "N")
                {
                    // L.N(count, "one", "many"): skip the count, take the two forms.
                    at = SkipArgument(src, at);
                    var (one, afterOne) = ReadLiteral(src, at + 1);
                    var (many, _) = ReadLiteral(src, SkipSpaceComma(src, afterOne));
                    if (one != null && many != null) keys.Add(one + "|" + many);
                    continue;
                }
                var (key, _) = ReadLiteral(src, at);
                if (key != null) keys.Add(key);
            }
        }
        foreach (var file in Directory.GetFiles(Path.Combine(root, "Views"), "*.xaml").Append(Path.Combine(root, "App.xaml")))
        {
            string xaml = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(xaml, @"\s(?:Text|Content|ToolTip|Header|Title|AutomationProperties\.Name)=""([^""{][^""]*)"""))
            {
                string text = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
                if (Regex.IsMatch(text, "[A-Za-z]{2,}")) keys.Add(text);
            }
        }
        return keys;
    }
    private static int SkipSpaceComma(string s, int at)
    {
        while (char.IsWhiteSpace(s[at]) || s[at] == ',') at++;
        return at;
    }
    /// <summary>Moves past one argument and the comma after it.</summary>
    private static int SkipArgument(string s, int at)
    {
        int depth = 0;
        for (; at < s.Length; at++)
        {
            char c = s[at];
            if (c == '"') { at = EndOfString(s, at + 1, false, false) - 1; continue; }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == ',' && depth == 0) break;
        }
        while (char.IsWhiteSpace(s[at + 1])) at++;
        return at;
    }
    /// <summary>The catalog key of the string literal at <paramref name="at"/>, or null when there is none.</summary>
    private static (string? Key, int End) ReadLiteral(string s, int at)
    {
        while (char.IsWhiteSpace(s[at])) at++;
        bool interpolated = s[at] == '$';
        if (interpolated) at++;
        bool verbatim = s[at] == '@';
        if (verbatim) at++;
        if (s[at] != '"') return (null, at);
        int end = EndOfString(s, at + 1, interpolated, verbatim);
        string body = s[(at + 1)..(end - 1)];
        if (verbatim) return (body.Replace("\"\"", "\""), end);
        if (!interpolated) return (Unescape(body), end);
        var key = new StringBuilder();
        int hole = 0;
        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];
            if ((c == '{' || c == '}') && i + 1 < body.Length && body[i + 1] == c) { key.Append(c).Append(c); i++; continue; }
            if (c == '{')
            {
                int close = EndOfHole(body, i + 1);
                string inside = body[(i + 1)..(close - 1)];
                key.Append('{').Append(hole++).Append(FormatPart(inside)).Append('}');
                i = close - 1; continue;
            }
            key.Append(c);
        }
        return (Unescape(key.ToString()), end);
    }
    private static string FormatPart(string hole)
    {
        int depth = 0;
        for (int i = 0; i < hole.Length; i++)
        {
            char c = hole[i];
            if (c == '"') { i = EndOfString(hole, i + 1, false, false) - 1; continue; }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (depth == 0 && c == ':') return ":" + hole[(i + 1)..];
            else if (depth == 0 && c == ',') return "," + hole[(i + 1)..].Trim();
        }
        return "";
    }
    private static int EndOfString(string s, int at, bool interpolated, bool verbatim)
    {
        for (; at < s.Length; at++)
        {
            char c = s[at];
            if (verbatim && c == '"') { if (at + 1 < s.Length && s[at + 1] == '"') { at++; continue; } return at + 1; }
            if (!verbatim && c == '\\') { at++; continue; }
            if (!verbatim && c == '"') return at + 1;
            if (interpolated && c == '{')
            {
                if (at + 1 < s.Length && s[at + 1] == '{') { at++; continue; }
                at = EndOfHole(s, at + 1) - 1;
            }
        }
        return s.Length;
    }
    private static int EndOfHole(string s, int at)
    {
        int depth = 0;
        for (; at < s.Length; at++)
        {
            char c = s[at];
            if (c == '"' || (c == '$' && s[at + 1] == '"'))
            {
                bool inner = c == '$';
                at = EndOfString(s, at + (inner ? 2 : 1), inner, false) - 1; continue;
            }
            if (c == '\'') { at = s.IndexOf('\'', at + (s[at + 1] == '\\' ? 3 : 2)); continue; }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']') depth--;
            else if (c == '}') { if (depth == 0) return at + 1; depth--; }
        }
        return s.Length;
    }
    private static string Unescape(string s) => Regex.Replace(s, @"\\(u[0-9a-fA-F]{4}|.)", m => m.Groups[1].Value switch
    {
        "n" => "\n", "t" => "\t", "r" => "\r", "\"" => "\"", "\\" => "\\", "0" => "\0",
        var u when u.StartsWith('u') => ((char)Convert.ToInt32(u[1..], 16)).ToString(),
        var other => other
    });

    /// <summary>Writes a translation template: every text with an empty translation, or the one a language file already has.</summary>
    private static void DumpStrings(string path, string? code = null)
    {
        var known = code == null ? new Dictionary<string, string>() : L.Load(code);
        var all = UiStrings().ToDictionary(k => k, k => known.GetValueOrDefault(k, ""));
        File.WriteAllText(path, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Console.WriteLine($"{all.Count} texts, {all.Count(x => x.Value.Length == 0)} to translate");
    }

    /// <summary>A few made-up translations, enough to see a language work without shipping one.</summary>
    private static readonly Dictionary<string, string> SampleCatalog = new()
    {
        ["Save macro"] = "Zapisz makro",
        ["Plays steps you record or build"] = "Odtwarza nagrane lub ułożone kroki",
        ["Added {0}. Rename it so the steps read well, like \"Play button\"."] = "Dodano {0}. Zmień nazwę, żeby kroki dobrze się czytały.",
        ["{0} step|{0} steps"] = "{0} krok|{0} kroki|{0} kroków",
        ["Get started"] = "Na początek",
        ["Click"] = "Kliknij",
        ["Keyloom is already running. Open it from the tray."] = ""
    };

    private static void LanguageEngineTests()
    {
        try
        {
            L.Use("pl", SampleCatalog);
            Check(L.T("Save macro") == "Zapisz makro" && L.T("Something not translated") == "Something not translated", "a translation replaces English, and what is not translated stays English");
            string name = "Farma";
            Check(L.T($"Added {name}. Rename it so the steps read well, like \"Play button\".") == "Dodano Farma. Zmień nazwę, żeby kroki dobrze się czytały.", "values go into the translated sentence");
            Check(L.N(1, "{0} step", "{0} steps") == "1 krok" && L.N(3, "{0} step", "{0} steps") == "3 kroki" && L.N(5, "{0} step", "{0} steps") == "5 kroków"
                && L.N(22, "{0} step", "{0} steps") == "22 kroki" && L.N(12, "{0} step", "{0} steps") == "12 kroków", "counts use the language's own forms, like 1 krok, 3 kroki, 5 kroków");
            L.Use("en");
            Check(L.N(1, "{0} step", "{0} steps") == "1 step" && L.N(2, "{0} step", "{0} steps") == "2 steps" && L.T("Save macro") == "Save macro", "English stays as written");
            Check(L.Available.SequenceEqual(["en"]) && L.CodeFor("") == "en" && L.CodeFor("xx") == "en", "Keyloom ships in English, and a language it lacks falls back to English");

            var keys = UiStrings();
            Check(keys.Count > 1000 && keys.Contains("Save macro") && keys.Contains("Added {0}. Rename it so the steps read well, like \"Play button\".") && keys.Contains("{0} step|{0} steps")
                && keys.Contains("Plays its {0} only when {1}, looking for up to {2:N0} ms."), $"the template finds every text to translate, from XAML and code ({keys.Count})");
            var bad = keys.Where(k => Regex.IsMatch(k, @"\{\d") && k.Split('|').Any(part => { try { string.Format(part, new object[10]); return false; } catch (FormatException) { return true; } })).ToList();
            Check(bad.Count == 0, $"every text reads as a format with its values in braces ({string.Join(" | ", bad.Take(3))})");
        }
        finally { L.Use("en"); }
    }

    private static void LanguageUiTests()
    {
        try
        {
            L.Hook();
            L.Use("pl", SampleCatalog);
            var p = Profile(MacroBehaviorType.Sequence);
            p.Actions.Add(Press("E"));
            var editor = new MacroEditorView(p);
            var host = new Window { Content = editor, Width = 1100, Height = 760, Left = -20000, Top = -20000, ShowActivated = false };
            host.Show(); Pump(250);
            Check(((TextBlock)editor.FindName("ModeText")).Text == "Odtwarza nagrane lub ułożone kroki" && Descendants<TextBlock>(editor).Any(t => t.Text == "Zapisz makro")
                && Descendants<TextBlock>(editor).All(t => t.Text != "Save macro"), "the editor shows a language's texts, from its XAML and from its code");
            host.Close();
            var help = new HelpView();
            var helpHost = new Window { Content = help, Width = 900, Height = 900, Left = -20000, Top = -20000, ShowActivated = false };
            helpHost.Show(); Pump(150);
            Check(Descendants<TextBlock>(help).Any(t => t.Text == "Na początek") && Descendants<TextBlock>(help).SelectMany(t => t.Inlines.OfType<System.Windows.Documents.Run>()).Any(r => r.Text == "Kliknij"),
                "help texts and the words inside a sentence are translated too");
            helpHost.Close();
            L.Use("en");
            var settings = new SettingsView(Path.Combine(Path.GetTempPath(), "keyloom-lang-" + Guid.NewGuid().ToString("N") + ".json"));
            Check(((StackPanel)settings.FindName("LanguagePanel")).Visibility != Visibility.Visible, "with only English, Settings does not ask for a language");
        }
        finally { L.Use("en"); }
    }
}
