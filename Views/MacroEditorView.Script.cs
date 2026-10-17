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
        ToScriptButton.Visibility = AppSettings.Current.Scripts ? Visibility.Visible : Visibility.Collapsed;
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
        ScriptStatus.Text = _scriptProblem is { } p ? L.T($"{p}  ·  click to go there") : L.T($"Reads fine · {L.N(lines, "{0} line", "{0} lines")}");
        ScriptStatus.Cursor = _scriptProblem is { Line: > 0 } ? Cursors.Hand : null;
        ScriptStatus.SetResourceReference(TextBlock.ForegroundProperty, _scriptProblem != null ? "DangerTextBrush" : "MutedBrush");
        RefreshScriptSettings();
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
            && !await sheets.ConfirmAsync(L.T("Replace your script?"), L.T("The example takes the place of what is in the script now. Ctrl+Z in the script brings yours back."), L.T("Replace")))
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
        // Undoing past the start of a script made from steps brings the steps back.
        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control && _turnedFrom != null && !ScriptBox.Document.UndoStack.CanUndo) { e.Handled = true; BackToSteps(); }
        else if (e.Key == Key.F1) { e.Handled = true; ScriptHelp_Click(sender, e); }
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
            items = [.. _targets.Select(t => (ICompletionData)new Suggestion(t.Name, t.Name, t.Kind == ScreenTargetKind.Color ? L.T($"Target · {t.Color}") : L.T("Target · picture"))),
                     .. _grids.Select(g => (ICompletionData)new Suggestion(g.Name, g.Name, L.T($"Grid · {g.Rows} × {g.Columns}")))];
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
        foreach (var t in _targets) ScriptAssetList.Children.Add(AssetRow(t.Name, t.Kind == ScreenTargetKind.Color ? L.T($"Color {t.Color}") : L.T("Picture"),
            t.Kind == ScreenTargetKind.Color ? "IconTarget" : "IconImage", name => RenameAsset(t.Name, name, n => { RetargetGrids(t.Name, n); t.Name = n; }), () => { _targets.Remove(t); RetargetGrids(t.Name, null); RefreshTargetBox(null); }, null));
        foreach (var g in _grids) ScriptAssetList.Children.Add(AssetRow(g.Name, L.T("Grid"), "IconGrid", name => RenameAsset(g.Name, name, n => g.Name = n), () => { _grids.Remove(g); RefreshGridBox(null); }, g));
        if (_targets.Count + _grids.Count == 0)
            ScriptAssetList.Children.Add(new TextBlock { Text = L.T("None yet."), Style = (Style)FindResource("Muted"), FontSize = 12 });
        // Settings that pick a grid or a target list them.
        _settingsShape = "";
        RefreshScriptSettings();
    }
    private FrameworkElement AssetRow(string name, string kind, string icon, Func<string, bool> rename, Action remove, ScreenGrid? grid)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var use = new Button { Style = (Style)FindResource("IconButton"), Width = 28, Height = 28, MinHeight = 0, ToolTip = L.T($"Put \"{name}\" in the script"), Margin = new Thickness(0, 0, 6, 0) };
        use.Content = new System.Windows.Shapes.Path { Data = (Geometry)FindResource(icon), Style = (Style)FindResource("AccentIcon"), Width = 12, Height = 12 };
        AutomationName(use, L.T($"Put {name} in the script"));
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
            var rows = new TextBox { Text = grid.Rows.ToString(), Width = 40, ToolTip = L.T("Rows") };
            var columns = new TextBox { Text = grid.Columns.ToString(), Width = 40, ToolTip = L.T("Columns") };
            AutomationName(rows, L.T("Rows")); AutomationName(columns, L.T("Columns"));
            void Resize()
            {
                try
                {
                    var copy = grid.Clone();
                    copy.Rows = Number(rows.Text, L.T("Rows")); copy.Columns = Number(columns.Text, L.T("Columns"));
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
            // What the grid moves with, on a line of its own under the name.
            row.RowDefinitions.Add(new RowDefinition());
            row.RowDefinitions.Add(new RowDefinition());
            var follow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            follow.Children.Add(new TextBlock { Text = L.T("Moves with"), Style = (Style)FindResource("Unit"), Margin = new Thickness(0, 0, 8, 0) });
            var followBox = new ComboBox { MinWidth = 140, ToolTip = L.T("When the game moves its board, the grid moves as far as this picture did. Keep the picture where it is now while you pick it.") };
            AutomationName(followBox, L.T($"{name} moves with"));
            FillFollowBox(followBox, grid);
            followBox.SelectionChanged += (_, _) => _ = FollowAsync(grid, followBox.SelectedIndex > 0 ? followBox.SelectedItem as string : null);
            follow.Children.Add(followBox);
            Grid.SetRow(follow, 1); Grid.SetColumn(follow, 1); Grid.SetColumnSpan(follow, 3);
            row.Children.Add(follow);
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
            Say(name.Length == 0 ? L.T("A name cannot be empty.") : L.T($"Something is already called '{name}'."), true);
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
        _output.Add(problem ? L.T($"Stopped: {line}") : line);
        if (_output.Count > MaxOutputLines) _output.RemoveRange(0, _output.Count - MaxOutputLines);
        ScriptOutput.Text = string.Join("\n", _output);
        if (problem && Regex.Match(line, @"Line (\d+)") is { Success: true } at) GoToLine(int.Parse(at.Groups[1].Value));
    }
    private void ClearOutput_Click(object sender, RoutedEventArgs e)
    {
        _output.Clear();
        ScriptOutput.Text = "";
    }

    // ---- Turning steps into a script ----

    /// <summary>What the script box held before the steps were turned into a script, while Ctrl+Z can still go back.</summary>
    private string? _turnedFrom;
    private async void ToScript_Click(object sender, RoutedEventArgs e) => await TurnIntoScriptAsync();
    /// <summary>Writes the steps as a script that plays the same, after asking. The steps stay until the macro is saved.</summary>
    public async Task TurnIntoScriptAsync(bool ask = true)
    {
        if (!AppSettings.Current.Scripts || Mode != 2) return;
        try { StepGroups.Validate(_steps); }
        catch (ArgumentException ex) { Say(ex.Message, true); return; }
        if (ask && SheetLayer.For(this) is { } sheets && !await sheets.ConfirmAsync(L.T("Turn the steps into a script?"),
                L.T("The macro becomes a script that plays the same, with its targets, grids and run settings. Saving replaces the steps; Ctrl+Z or Cancel brings them back. A script cannot be turned back into steps."),
                L.T("Turn into script")))
            return;
        _turnedFrom = ScriptBox.Text;
        ScriptBox.Document.Text = SequenceScript.From(_steps);
        ScriptBox.Document.UndoStack.ClearAll();
        SetMode(3);
        CheckScript();
        Say(L.T("The steps are now a script. Ctrl+Z goes back to them."));
    }
    private void BackToSteps()
    {
        if (_turnedFrom == null) return;
        ScriptBox.Document.Text = _turnedFrom;
        ScriptBox.Document.UndoStack.ClearAll();
        _turnedFrom = null;
        SetMode(2);
        Say(L.T("Back to the steps."));
    }

    // ---- Settings the script shows beside it ----

    private string _settingsShape = "";
    private readonly Dictionary<string, Control> _settingFields = [];
    private bool _showingSettings;
    /// <summary>
    /// The script's settings as fields. Typing in the script updates the fields; the same settings keep their fields,
    /// so the one being typed in is not rebuilt under the caret.
    /// </summary>
    private void RefreshScriptSettings()
    {
        if (ScriptSettingList == null) return;
        var settings = ScriptSettings.Read(ScriptBox.Text);
        string shape = string.Join("\n", settings.Select(s => s.Shape));
        _showingSettings = true;
        try
        {
            if (shape != _settingsShape)
            {
                _settingsShape = shape;
                _settingFields.Clear();
                ScriptSettingList.Children.Clear();
                foreach (var s in settings) ScriptSettingList.Children.Add(SettingRow(s));
            }
            else foreach (var s in settings) ShowSettingValue(_settingFields[s.Name], s.Value);
        }
        finally { _showingSettings = false; }
        ScriptSettingsHint.Visibility = settings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshScriptControls();
    }
    private static void ShowSettingValue(Control field, string value)
    {
        switch (field)
        {
            case TextBox { Tag: TextBox y } x:
                var xy = value.Split(',', StringSplitOptions.TrimEntries);
                if (!x.IsKeyboardFocusWithin && x.Text != xy[0]) x.Text = xy[0];
                if (!y.IsKeyboardFocusWithin && y.Text != xy[^1]) y.Text = xy[^1];
                break;
            case CheckBox box: box.IsChecked = value == "true"; break;
            case ComboBox list: list.SelectedItem = value; break;
            case TextBox text when !text.IsKeyboardFocusWithin && text.Text != value: text.Text = value; break;
        }
    }
    private FrameworkElement SettingRow(ScriptSetting s)
    {
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        Control field;
        switch (s.Kind)
        {
            case ScriptSettingKind.Switch:
                var box = new CheckBox { Content = s.Label, IsChecked = s.Value == "true" };
                box.Checked += (_, _) => WriteSetting(s.Name, "true");
                box.Unchecked += (_, _) => WriteSetting(s.Name, "false");
                field = box;
                break;
            case ScriptSettingKind.Choice:
                var list = new ComboBox { ItemsSource = s.Choices, SelectedItem = s.Value, MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Left };
                list.SelectionChanged += (_, _) => { if (list.SelectedItem is string chosen) WriteSetting(s.Name, chosen); };
                field = list;
                break;
            case ScriptSettingKind.Point:
            {
                var xy = s.Value.Split(',', StringSplitOptions.TrimEntries);
                var y = new TextBox { Text = xy[1], Width = 70, ToolTip = L.T($"{s.Name} y") };
                var x = new TextBox { Text = xy[0], Width = 70, Tag = y, Margin = new Thickness(0, 0, 6, 0) };
                AutomationName(y, L.T($"{s.Label} y"));
                x.TextChanged += (_, _) => WriteSetting(s.Name, $"{x.Text}, {y.Text}");
                y.TextChanged += (_, _) => WriteSetting(s.Name, $"{x.Text}, {y.Text}");
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(x); line.Children.Add(y);
                line.Children.Add(SpotButton(L.T($"Click the spot for {s.Label} on the screen. Esc cancels."), L.T($"Pick {s.Label} on the screen"), async () =>
                {
                    if (await OutOfTheWay(() => ScreenSnipWindow.TakeAsync(true, hint: L.T($"Click the spot for {s.Label}"))) is { } snip)
                        WriteSetting(s.Name, $"{snip.Bounds.X}, {snip.Bounds.Y}");
                }));
                return Labeled(s, x, line);
            }
            case ScriptSettingKind.Color:
            {
                var swatch = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 6, 0) };
                swatch.SetResourceReference(Border.BorderBrushProperty, "FieldBorderBrush");
                var hex = new TextBox { Text = s.Value, Width = 90, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
                void Paint()
                {
                    try { swatch.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex.Text.Trim())); }
                    catch (FormatException) { swatch.Background = Brushes.Transparent; }
                }
                hex.TextChanged += (_, _) => { Paint(); WriteSetting(s.Name, hex.Text); };
                Paint();
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(swatch); line.Children.Add(hex);
                line.Children.Add(SpotButton(L.T($"Click a color on the screen for {s.Label}. Esc cancels."), L.T($"Pick {s.Label} on the screen"), async () =>
                {
                    if (await OutOfTheWay(() => ScreenSnipWindow.TakeAsync(true, hint: L.T($"Click the color for {s.Label}"))) is { } snip)
                        WriteSetting(s.Name, TargetPicture.ColorText(snip.Color));
                }));
                return Labeled(s, hex, line);
            }
            case ScriptSettingKind.Grid:
            case ScriptSettingKind.Target:
                var names = (s.Kind == ScriptSettingKind.Grid ? _grids.Select(g => g.Name) : _targets.Select(t => t.Name)).ToList();
                if (!names.Contains(s.Value)) names.Insert(0, s.Value);
                var pick = new ComboBox { ItemsSource = names, SelectedItem = s.Value, MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Left };
                pick.SelectionChanged += (_, _) => { if (pick.SelectedItem is string chosen) WriteSetting(s.Name, chosen); };
                field = pick;
                break;
            default:
                var text = new TextBox { Text = s.Value };
                if (s.Kind == ScriptSettingKind.Number) { text.Width = 100; text.HorizontalAlignment = HorizontalAlignment.Left; }
                text.TextChanged += (_, _) => WriteSetting(s.Name, text.Text);
                field = text;
                break;
        }
        if (s.Kind != ScriptSettingKind.Switch) row.Children.Add(new TextBlock { Text = s.Label, Style = (Style)FindResource("StackLabel") });
        field.ToolTip = L.T($"{s.Name} on line {s.Line} of the script");
        AutomationName(field, s.Label);
        row.Children.Add(field);
        _settingFields[s.Name] = field;
        return row;
    }
    /// <summary>A setting with more than one control on its line: the label above, then the line.</summary>
    private StackPanel Labeled(ScriptSetting s, Control field, FrameworkElement line)
    {
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        row.Children.Add(new TextBlock { Text = s.Label, Style = (Style)FindResource("StackLabel") });
        field.ToolTip = L.T($"{s.Name} on line {s.Line} of the script");
        AutomationName(field, s.Label);
        row.Children.Add(line);
        _settingFields[s.Name] = field;
        return row;
    }
    private Button SpotButton(string tip, string name, Func<Task> pick)
    {
        var button = new Button { Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0), ToolTip = tip };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new System.Windows.Shapes.Path { Data = (Geometry)FindResource("IconTarget"), Style = (Style)FindResource("Icon"), Width = 12, Height = 12, Margin = new Thickness(0, 0, 6, 0) });
        content.Children.Add(new TextBlock { Text = L.T("Pick") });
        button.Content = content;
        AutomationName(button, name);
        button.Click += async (_, _) => await pick();
        return button;
    }
    /// <summary>Writes a field's value into the script, where the setting is, so Ctrl+Z in the script undoes it.</summary>
    private void WriteSetting(string name, string value)
    {
        if (_showingSettings) return;
        if (ScriptSettings.Read(ScriptBox.Text).FirstOrDefault(s => s.Name == name) is not { } setting) return;
        try
        {
            string literal = ScriptSettings.Literal(setting, value);
            if (ScriptBox.Document.GetText(setting.Offset, setting.Length) != literal) ScriptBox.Document.Replace(setting.Offset, setting.Length, literal);
            Say("");
        }
        catch (ArgumentException ex) { Say(ex.Message, true); }
    }
}
