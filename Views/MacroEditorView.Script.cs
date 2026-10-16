using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using Keyloom.Engine;
using Keyloom.Models;
using Keyloom.Services;

namespace Keyloom.Views;

/// <summary>Experimental: script macros, written in JavaScript with Keyloom's own functions.</summary>
public partial class MacroEditorView
{
    /// <summary>What a new script starts with, so the box is never a blank wall.</summary>
    public const string StarterScript = """
        // This runs from the top on every run. Help lists every function.
        press("E")
        wait(500)

        """;
    private const int MaxOutputLines = 200;
    private readonly DispatcherTimer _checkTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly List<string> _output = [];
    private ScriptProblem? _scriptProblem;
    private CompletionWindow? _completion;
    private string ScriptText => ScriptBox.Text.Replace("\r\n", "\n");

    private void InitializeScript()
    {
        ScriptRadio.Visibility = AppSettings.Current.Scripts || Profile.Behavior == MacroBehaviorType.Script ? Visibility.Visible : Visibility.Collapsed;
        AddAiIfItem.Visibility = AppSettings.Current.Ai ? Visibility.Visible : Visibility.Collapsed;
        ScriptBox.Text = Profile.Script;
        ScriptBox.Options.ConvertTabsToSpaces = true;
        ScriptBox.Options.IndentationSize = 2;
        ScriptBox.Options.EnableHyperlinks = false;
        ScriptBox.Options.EnableEmailHyperlinks = false;
        ScriptBox.Options.HighlightCurrentLine = true;
        ScriptBox.TextArea.SelectionCornerRadius = 2;
        ScriptBox.TextArea.TextView.Margin = new Thickness(8, 0, 0, 0);
        ScriptBox.TextChanged += (_, _) => { _checkTimer.Stop(); _checkTimer.Start(); };
        _checkTimer.Tick += (_, _) => { _checkTimer.Stop(); CheckScript(); };
        ScriptBox.TextArea.TextEntered += ScriptTyped;
        ScriptBox.PreviewKeyDown += ScriptKeyDown;
        ExamplesMenu.Items.Clear();
        foreach (var (name, code) in MacroScript.Examples)
        {
            var item = new MenuItem { Header = name };
            item.Click += async (_, _) => await UseExampleAsync(code);
            ExamplesMenu.Items.Add(item);
        }
        ApplyScriptColors();
        ThemeManager.Changed += ApplyScriptColors;
        Unloaded += (_, _) => ThemeManager.Changed -= ApplyScriptColors;
        CheckScript();
        RefreshScriptAssets();
    }
    private void ShowScriptMode(bool script)
    {
        ScriptPanel.Visibility = ScriptSide.Visibility = script ? Visibility.Visible : Visibility.Collapsed;
        if (script && ScriptBox.Text.Trim().Length == 0) { ScriptBox.Text = StarterScript; ScriptBox.CaretOffset = ScriptBox.Text.Length; }
        if (script) RefreshScriptAssets();
    }

    /// <summary>Reads the script as it is typed and says the first mistake, or that it reads fine.</summary>
    private void CheckScript()
    {
        _scriptProblem = MacroScript.Check(ScriptText);
        int lines = ScriptBox.Document.LineCount;
        ScriptStatus.Text = _scriptProblem is { } p ? $"{p}  ·  click to go there" : $"Reads fine · {lines:N0} line{(lines == 1 ? "" : "s")}";
        ScriptStatus.Cursor = _scriptProblem is { Line: > 0 } ? Cursors.Hand : null;
        ScriptStatus.SetResourceReference(TextBlock.ForegroundProperty, _scriptProblem != null ? "DangerTextBrush" : "MutedBrush");
    }
    private void ScriptStatus_Click(object sender, MouseButtonEventArgs e)
    {
        if (_scriptProblem is not { Line: > 0 } p) return;
        GoToLine(p.Line, p.Column);
    }
    private void GoToLine(int line, int column = 0)
    {
        line = Math.Clamp(line, 1, ScriptBox.Document.LineCount);
        var at = ScriptBox.Document.GetLineByNumber(line);
        ScriptBox.CaretOffset = at.Offset + Math.Clamp(column, 0, at.Length);
        ScriptBox.ScrollToLine(line);
        ScriptBox.TextArea.Focus();
    }

    private async Task UseExampleAsync(string code)
    {
        string mine = ScriptText.Trim();
        if (mine.Length > 0 && mine != StarterScript.Trim() && SheetLayer.For(this) is { } sheets
            && !await sheets.ConfirmAsync("Replace your script?", "The example takes the place of what is in the script now. Ctrl+Z in the script brings yours back.", "Replace"))
            return;
        ScriptBox.Document.Text = code.Replace("\r\n", "\n") + "\n";
        ScriptBox.TextArea.Focus();
        CheckScript();
    }
    private void Examples_Click(object sender, RoutedEventArgs e)
    {
        ExamplesMenu.PlacementTarget = ExamplesButton;
        ExamplesMenu.IsOpen = true;
    }
    private async void ScriptHelp_Click(object sender, RoutedEventArgs e)
    {
        if (SheetLayer.For(this) is not { IsOpen: false } sheets) return;
        var help = new ScriptHelpView(_targets.Select(t => t.Name), _grids.Select(g => g.Name));
        await sheets.ShowAsync(help, 720);
        if (help.Chosen is { } code) Insert(code);
    }
    /// <summary>Puts text at the caret, on its own line when it is more than one line.</summary>
    private void Insert(string text)
    {
        var caret = ScriptBox.TextArea.Caret;
        var line = ScriptBox.Document.GetLineByNumber(caret.Line);
        if (text.Contains('\n'))
        {
            string indent = Regex.Match(ScriptBox.Document.GetText(line), @"^\s*").Value;
            bool blank = ScriptBox.Document.GetText(line).Trim().Length == 0;
            text = string.Join("\n", text.Split('\n').Select((l, i) => i == 0 ? l : indent + l));
            ScriptBox.Document.Insert(blank ? line.Offset + indent.Length : line.EndOffset, blank ? text : "\n" + indent + text);
        }
        else ScriptBox.Document.Insert(ScriptBox.CaretOffset, text);
        ScriptBox.TextArea.Focus();
    }

    private void ScriptKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F1) { e.Handled = true; ScriptHelp_Click(sender, e); }
        else if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; Suggest(force: true); }
    }
    private void ScriptTyped(object sender, TextCompositionEventArgs e)
    {
        if (e.Text.Length == 1 && (char.IsLetter(e.Text[0]) || e.Text[0] == '"')) Suggest(force: false);
    }
    /// <summary>
    /// The functions that start with the word being typed, or the targets and grids when typing inside quotes.
    /// Typing on picks from the list; Enter or Tab puts the choice in.
    /// </summary>
    private void Suggest(bool force)
    {
        if (_completion != null) return;
        var document = ScriptBox.Document;
        int caret = ScriptBox.CaretOffset, start = caret;
        while (start > 0 && (char.IsLetterOrDigit(document.GetCharAt(start - 1)) || document.GetCharAt(start - 1) == '_')) start--;
        string word = document.GetText(start, caret - start);
        var line = document.GetLineByOffset(caret);
        string before = document.GetText(line.Offset, caret - line.Offset);
        bool quoted = before.Count(c => c == '"') % 2 == 1;
        List<ICompletionData> items;
        if (quoted)
        {
            int quote = before.LastIndexOf('"');
            start = line.Offset + quote + 1;
            word = document.GetText(start, caret - start);
            items = [.. _targets.Select(t => (ICompletionData)new Suggestion(t.Name, t.Name, $"Target · {(t.Kind == ScreenTargetKind.Color ? t.Color : "picture")}")),
                     .. _grids.Select(g => (ICompletionData)new Suggestion(g.Name, g.Name, $"Grid · {g.Rows} × {g.Columns}"))];
        }
        else
        {
            if (!force && word.Length < 2) return;
            if (!force && Regex.IsMatch(before, @"(//|\.)\s*\w*$")) return;
            items = [.. MacroScript.Functions.Select(f => (ICompletionData)new Suggestion(f.Name, f.Call.Contains('(') ? f.Name + "(" : f.Name, $"{f.Call}\n{f.About}"))];
        }
        var matching = items.Where(i => i.Text.StartsWith(word, StringComparison.OrdinalIgnoreCase) || word.Length == 0).ToList();
        if (matching.Count == 0 || matching.Count == 1 && matching[0].Text == word) return;
        _completion = new CompletionWindow(ScriptBox.TextArea) { StartOffset = start, CloseWhenCaretAtBeginning = true, CloseAutomatically = true, Width = 300 };
        _completion.SetResourceReference(Control.BackgroundProperty, "SheetBrush");
        _completion.SetResourceReference(Control.ForegroundProperty, "InkBrush");
        _completion.CompletionList.ListBox.SetResourceReference(Control.ForegroundProperty, "InkBrush");
        _completion.CompletionList.ListBox.SetResourceReference(Control.BackgroundProperty, "SheetBrush");
        foreach (var item in items) _completion.CompletionList.CompletionData.Add(item);
        _completion.Closed += (_, _) => _completion = null;
        _completion.Show();
        if (word.Length > 0) _completion.CompletionList.SelectItem(word);
    }
    private sealed class Suggestion(string text, string insert, string description) : ICompletionData
    {
        public ImageSource? Image => null;
        public string Text => text;
        public object Content => text;
        public object Description => description;
        public double Priority => 0;
        public void Complete(TextArea area, ISegment segment, EventArgs e) => area.Document.Replace(segment, insert);
    }

    /// <summary>JavaScript colors that suit the theme, with Keyloom's functions standing out from the rest.</summary>
    private void ApplyScriptColors()
    {
        bool dark = ThemeManager.IsDark;
        string Hex(string light, string night) => dark ? night : light;
        string functions = string.Join("", MacroScript.Functions.Select(f => $"<Word>{f.Name}</Word>"));
        string xshd = $"""
            <SyntaxDefinition name="KeyloomScript" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
              <Color name="Comment" foreground="{Hex("#6B7785", "#8A96A3")}" fontStyle="italic"/>
              <Color name="String" foreground="{Hex("#A1501B", "#E6A472")}"/>
              <Color name="Number" foreground="{Hex("#0F7B6C", "#5FD3BC")}"/>
              <Color name="Keyword" foreground="{Hex("#7B3FC4", "#C49BFF")}" fontWeight="bold"/>
              <Color name="Keyloom" foreground="{Hex("#1460B8", "#6CB4FF")}" fontWeight="bold"/>
              <RuleSet>
                <Span color="Comment" begin="//"/>
                <Span color="Comment" multiline="true" begin="/\*" end="\*/"/>
                <Span color="String"><Begin>"</Begin><End>"</End><RuleSet><Span begin="\\" end="."/></RuleSet></Span>
                <Span color="String"><Begin>'</Begin><End>'</End><RuleSet><Span begin="\\" end="."/></RuleSet></Span>
                <Span color="String" multiline="true"><Begin>`</Begin><End>`</End><RuleSet><Span begin="\\" end="."/></RuleSet></Span>
                <Keywords color="Keyword">
                  <Word>const</Word><Word>let</Word><Word>var</Word><Word>if</Word><Word>else</Word><Word>for</Word><Word>of</Word><Word>in</Word>
                  <Word>while</Word><Word>do</Word><Word>break</Word><Word>continue</Word><Word>return</Word><Word>function</Word><Word>true</Word>
                  <Word>false</Word><Word>null</Word><Word>undefined</Word><Word>try</Word><Word>catch</Word><Word>finally</Word><Word>throw</Word>
                  <Word>new</Word><Word>switch</Word><Word>case</Word><Word>default</Word><Word>typeof</Word>
                </Keywords>
                <Keywords color="Keyloom">{functions}</Keywords>
                <Rule color="Number">\b\d+(\.\d+)?\b</Rule>
              </RuleSet>
            </SyntaxDefinition>
            """;
        using var reader = XmlReader.Create(new StringReader(xshd));
        ScriptBox.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        ScriptBox.TextArea.TextView.CurrentLineBackground = new SolidColorBrush(dark ? Color.FromArgb(18, 255, 255, 255) : Color.FromArgb(10, 0, 40, 120));
        ScriptBox.TextArea.TextView.CurrentLineBorder = new Pen(Brushes.Transparent, 0);
        ScriptBox.TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(90, 0x47, 0xA3, 0xF3));
        ScriptBox.TextArea.SelectionForeground = null;
        ScriptBox.TextArea.SelectionBorder = null;
        ScriptBox.TextArea.Caret.CaretBrush = (Brush)FindResource("InkBrush");
    }

    /// <summary>The targets and grids a script can use, each with its name to rename and a way to remove it.</summary>
    private void RefreshScriptAssets()
    {
        if (ScriptAssetList == null || Mode != 3) return;
        ScriptAssetList.Children.Clear();
        foreach (var t in _targets) ScriptAssetList.Children.Add(AssetRow(t.Name, t.Kind == ScreenTargetKind.Color ? $"Color {t.Color}" : "Picture",
            t.Kind == ScreenTargetKind.Color ? "IconTarget" : "IconImage", name => RenameAsset(t.Name, name, n => t.Name = n), () => { _targets.Remove(t); RefreshTargetBox(null); }, null));
        foreach (var g in _grids) ScriptAssetList.Children.Add(AssetRow(g.Name, "Grid", "IconGrid", name => RenameAsset(g.Name, name, n => g.Name = n), () => { _grids.Remove(g); RefreshGridBox(null); }, g));
        if (_targets.Count + _grids.Count == 0)
            ScriptAssetList.Children.Add(new TextBlock { Text = "None yet.", Style = (Style)FindResource("Muted"), FontSize = 12 });
    }
    private FrameworkElement AssetRow(string name, string kind, string icon, Func<string, bool> rename, Action remove, ScreenGrid? grid)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var use = new Button { Style = (Style)FindResource("IconButton"), Width = 28, Height = 28, MinHeight = 0, ToolTip = $"Put \"{name}\" in the script", Margin = new Thickness(0, 0, 6, 0) };
        use.Content = new System.Windows.Shapes.Path { Data = (Geometry)FindResource(icon), Style = (Style)FindResource("AccentIcon"), Width = 12, Height = 12 };
        AutomationName(use, $"Put {name} in the script");
        var box = new TextBox { Text = name, MaxLength = 60, ToolTip = kind };
        AutomationName(box, $"{kind} name");
        void Commit()
        {
            string next = box.Text.Trim();
            if (next != name && !rename(next)) box.Text = name;
        }
        box.LostKeyboardFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Commit(); } };
        use.Click += (_, _) => Insert($"\"{box.Text.Trim()}\"");
        Grid.SetColumn(use, 0); Grid.SetColumn(box, 1);
        row.Children.Add(use); row.Children.Add(box);
        if (grid != null)
        {
            var size = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };
            var rows = new TextBox { Text = grid.Rows.ToString(), Width = 40, ToolTip = "Rows" };
            var columns = new TextBox { Text = grid.Columns.ToString(), Width = 40, ToolTip = "Columns" };
            AutomationName(rows, "Rows"); AutomationName(columns, "Columns");
            void Resize()
            {
                try
                {
                    var copy = grid.Clone();
                    copy.Rows = Number(rows.Text, "Rows"); copy.Columns = Number(columns.Text, "Columns");
                    ProfileValidator.ValidateGrid(copy);
                    grid.Rows = copy.Rows; grid.Columns = copy.Columns;
                    Say("");
                }
                catch (ArgumentException ex) { Say(ex.Message, true); }
            }
            rows.TextChanged += (_, _) => Resize();
            columns.TextChanged += (_, _) => Resize();
            size.Children.Add(rows);
            size.Children.Add(new TextBlock { Text = "×", Style = (Style)FindResource("Unit"), Margin = new Thickness(4, 0, 4, 0) });
            size.Children.Add(columns);
            Grid.SetColumn(size, 2);
            row.Children.Add(size);
        }
        var delete = new Button { Style = (Style)FindResource("IconButton"), Width = 28, Height = 28, MinHeight = 0, Margin = new Thickness(4, 0, 0, 0), ToolTip = $"Remove {name}" };
        delete.Content = new System.Windows.Shapes.Path { Data = (Geometry)FindResource("IconTrash"), Fill = (Brush)FindResource("DangerIconBrush"), Width = 11, Height = 11, Stretch = Stretch.Uniform };
        AutomationName(delete, $"Remove {name}");
        delete.Click += (_, _) => { remove(); RefreshScriptAssets(); };
        Grid.SetColumn(delete, 3);
        row.Children.Add(delete);
        return row;
    }
    private static void AutomationName(DependencyObject element, string name) => System.Windows.Automation.AutomationProperties.SetName(element, name);
    /// <summary>A new name for a target or grid, also changed where the script names it in quotes.</summary>
    private bool RenameAsset(string old, string name, Action<string> set)
    {
        if (name.Length == 0 || _targets.Any(t => t.Name != old && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
            || _grids.Any(g => g.Name != old && string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            Say(name.Length == 0 ? "A name cannot be empty." : $"Something is already called '{name}'.", true);
            return false;
        }
        set(name);
        string script = ScriptBox.Text;
        string renamed = Regex.Replace(script, "([\"'])" + Regex.Escape(old) + "\\1", m => m.Groups[1].Value + name + m.Groups[1].Value);
        if (renamed != script) ScriptBox.Document.Text = renamed;
        Say("");
        RefreshScriptAssets();
        return true;
    }

    /// <summary>A line the script wrote with log(), or why it stopped, shown under Output.</summary>
    public void ShowScriptOutput(string line, bool problem = false)
    {
        if (ScriptOutput == null) return;
        if (_output.Count == 0) ScriptOutput.Text = "";
        _output.Add(problem ? "Stopped: " + line : line);
        if (_output.Count > MaxOutputLines) _output.RemoveRange(0, _output.Count - MaxOutputLines);
        ScriptOutput.Text = string.Join("\n", _output);
        if (problem && Regex.Match(line, @"Line (\d+)") is { Success: true } at) GoToLine(int.Parse(at.Groups[1].Value));
    }
    private void ClearOutput_Click(object sender, RoutedEventArgs e)
    {
        _output.Clear();
        ScriptOutput.Text = "";
    }
}
