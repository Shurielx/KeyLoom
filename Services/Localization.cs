using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Keyloom;

/// <summary>
/// Translations. The English text is the key, so code and XAML read as they always did, and anything not yet translated
/// shows in English. Code wraps what people read in <see cref="T(string)"/>, or <see cref="T(ref Phrase)"/> for text with
/// values in it; counted words go through <see cref="N"/>. XAML needs nothing: its texts are translated as they load.
/// A language is a file Assets/Lang/&lt;code&gt;.json built into Keyloom, mapping each English text to its translation;
/// docs/TRANSLATING.md says how to make one.
/// </summary>
public static class L
{
    private static Dictionary<string, string> _catalog = new(StringComparer.Ordinal);
    /// <summary>The language in use, as a two-letter code like "en".</summary>
    public static string Code { get; private set; } = "en";
    public static bool IsEnglish => _catalog.Count == 0;
    /// <summary>English, plus every language file built into Keyloom.</summary>
    public static IReadOnlyList<string> Available { get; } =
        ["en", .. typeof(L).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("Keyloom.Lang.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
            .Select(n => n["Keyloom.Lang.".Length..^".json".Length]).Where(c => c != "en").Order()];

    /// <summary>The language for a setting: a code, or empty to follow Windows when Keyloom has that language.</summary>
    public static string CodeFor(string? setting)
    {
        string code = string.IsNullOrWhiteSpace(setting) ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName : setting;
        return Available.Contains(code) ? code : "en";
    }
    /// <summary>The language's own name, like "English" or "polski".</summary>
    public static string NameOf(string code)
    {
        try { var name = CultureInfo.GetCultureInfo(code).NativeName; return char.ToUpper(name[0]) + name[1..]; }
        catch (CultureNotFoundException) { return code; }
    }
    /// <summary>Switches the language for everything shown from now on.</summary>
    public static void Use(string code) => Use(code, code == "en" ? null : Load(code));
    /// <summary>Uses the given translations; tests use it to try a language Keyloom does not ship.</summary>
    public static void Use(string code, IDictionary<string, string>? catalog)
    {
        Code = code;
        _catalog = catalog == null ? new(StringComparer.Ordinal) : new(catalog, StringComparer.Ordinal);
    }
    /// <summary>The translations for a language, from the file built into Keyloom.</summary>
    public static Dictionary<string, string> Load(string code)
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream($"Keyloom.Lang.{code}.json");
        if (stream == null) return new(StringComparer.Ordinal);
        var read = JsonSerializer.Deserialize<Dictionary<string, string>>(stream, new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return read == null ? new(StringComparer.Ordinal) : new(read.Where(x => x.Value.Length > 0).ToDictionary(), StringComparer.Ordinal);
    }

    public static string T(string text) => text.Length > 0 && _catalog.TryGetValue(text, out var translated) ? translated : text;
    /// <summary>Text with values in it, like <c>L.T($"Added {name}.")</c>: the key is "Added {0}." and the values go back in after.</summary>
    public static string T([InterpolatedStringHandlerArgument] ref Phrase phrase)
    {
        var (format, values) = phrase.Finish();
        string chosen = _catalog.TryGetValue(format, out var translated) ? translated : format;
        try { return string.Format(CultureInfo.CurrentCulture, chosen, values); }
        catch (FormatException) { return string.Format(CultureInfo.CurrentCulture, format, values); }
    }
    /// <summary>
    /// A count with the word that goes with it: <c>L.N(3, "{0} step", "{0} steps")</c>. The translation lists its
    /// language's forms split by |: two for most languages, three for Polish, Czech, Russian or Ukrainian (1 krok, 2 kroki, 5 kroków).
    /// </summary>
    public static string N(long count, string one, string many)
    {
        string number = count.ToString("N0", CultureInfo.CurrentCulture);
        if (_catalog.TryGetValue(one + "|" + many, out var forms))
        {
            var parts = forms.Split('|');
            int form = Code switch { "pl" => PolishForm(count), "ru" or "uk" => EastSlavicForm(count), "cs" or "sk" => count == 1 ? 0 : count is >= 2 and <= 4 ? 1 : 2, _ => count == 1 ? 0 : 1 };
            return string.Format(CultureInfo.CurrentCulture, parts[Math.Min(form, parts.Length - 1)], number);
        }
        return string.Format(CultureInfo.CurrentCulture, count == 1 ? one : many, number);
    }
    private static int PolishForm(long n)
    {
        n = Math.Abs(n);
        if (n == 1) return 0;
        long tens = n % 100, ones = n % 10;
        return ones is >= 2 and <= 4 && tens is < 12 or > 14 ? 1 : 2;
    }
    private static int EastSlavicForm(long n)
    {
        n = Math.Abs(n);
        long tens = n % 100, ones = n % 10;
        return ones == 1 && tens != 11 ? 0 : ones is >= 2 and <= 4 && tens is < 12 or > 14 ? 1 : 2;
    }

    /// <summary>Translates XAML texts as elements load: text blocks, buttons, menu items, tool tips and names for screen readers.</summary>
    private static bool _hooked;
    public static void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => Translate((DependencyObject)s)), true);
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler((s, _) => Translate((DependencyObject)s)), true);
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((s, _) => TranslateTree((DependencyObject)s)), true);
    }
    /// <summary>One element's own texts. Only texts written in XAML are touched, never bound or typed ones.</summary>
    public static void Translate(DependencyObject element)
    {
        if (IsEnglish) return;
        switch (element)
        {
            case TextBox or PasswordBox: break;
            case TextBlock block when Literal(block, TextBlock.TextProperty):
                block.Text = T(block.Text); break;
            case TextBlock block when block.Inlines.Count > 0 && !BindingOps(block, TextBlock.TextProperty):
                foreach (var run in block.Inlines.OfType<Run>().ToList()) if (T(run.Text) is var t && t != run.Text) run.Text = t;
                break;
            case HeaderedItemsControl headered when headered.Header is string header && Literal(headered, HeaderedItemsControl.HeaderProperty):
                headered.Header = T(header); break;
            case ContentControl content when content.Content is string text && Literal(content, ContentControl.ContentProperty):
                content.Content = T(text); break;
        }
        if (element is FrameworkElement fe)
        {
            if (fe.ToolTip is string tip && Literal(fe, FrameworkElement.ToolTipProperty)) fe.ToolTip = T(tip);
            if (fe is Window window && Literal(window, Window.TitleProperty)) window.Title = T(window.Title);
        }
        if (element.ReadLocalValue(AutomationProperties.NameProperty) is string name) AutomationProperties.SetName(element, T(name));
    }
    private static void TranslateTree(DependencyObject root)
    {
        Translate(root);
        if (root is FrameworkElement { ContextMenu: { } menu }) TranslateTree(menu);
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) TranslateTree(child);
        if (root is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D)
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++) TranslateTree(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
    }
    /// <summary>
    /// Translates a view's XAML texts: everything it declares now, and again once it is shown, for what its templates
    /// made. Each view calls it right after InitializeComponent.
    /// </summary>
    public static void Apply(FrameworkElement view)
    {
        if (IsEnglish) return;
        TranslateTree(view);
        view.Loaded += (_, _) => TranslateTree(view);
    }
    private static bool Literal(DependencyObject element, DependencyProperty property) => element.ReadLocalValue(property) is string { Length: > 0 };
    private static bool BindingOps(DependencyObject element, DependencyProperty property) => System.Windows.Data.BindingOperations.IsDataBound(element, property);
}

/// <summary>Collects an interpolated string as a format, like "Line {0}: {1}", and the values that go in it.</summary>
[InterpolatedStringHandler]
public ref struct Phrase
{
    private readonly StringBuilder _format;
    private readonly object?[] _values;
    private int _count;
    public Phrase(int literalLength, int formattedCount)
    {
        _format = new StringBuilder(literalLength + formattedCount * 4);
        _values = new object?[formattedCount];
        _count = 0;
    }
    public void AppendLiteral(string text) => _format.Append(text.Replace("{", "{{").Replace("}", "}}"));
    public void AppendFormatted<T>(T value) => Hole(value, 0, null);
    public void AppendFormatted<T>(T value, string? format) => Hole(value, 0, format);
    public void AppendFormatted<T>(T value, int alignment) => Hole(value, alignment, null);
    public void AppendFormatted<T>(T value, int alignment, string? format) => Hole(value, alignment, format);
    private void Hole(object? value, int alignment, string? format)
    {
        _format.Append('{').Append(_count);
        if (alignment != 0) _format.Append(',').Append(alignment);
        if (format != null) _format.Append(':').Append(format);
        _format.Append('}');
        _values[_count++] = value;
    }
    public (string Format, object?[] Values) Finish() => (_format.ToString(), _values);
}
