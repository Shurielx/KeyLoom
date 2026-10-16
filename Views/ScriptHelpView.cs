using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Keyloom.Engine;

namespace Keyloom.Views;

/// <summary>
/// Help for scripts, shown as a sheet: how a script runs, then every function with an example to put in the script.
/// <see cref="Chosen"/> holds the example picked with Insert.
/// </summary>
public sealed class ScriptHelpView : UserControl
{
    private readonly StackPanel _list = new();
    private readonly TextBox _search = new() { Margin = new Thickness(0, 4, 0, 10) };
    private readonly IReadOnlyList<string> _targets, _grids;
    public string? Chosen { get; private set; }

    public ScriptHelpView(IEnumerable<string> targets, IEnumerable<string> grids)
    {
        _targets = targets.ToList(); _grids = grids.ToList();
        var root = new DockPanel();
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel();
        title.Children.Add(new TextBlock { Text = "Script help", Style = Find<Style>("SectionTitle"), FontSize = 16 });
        title.Children.Add(new TextBlock
        {
            Text = "A script is JavaScript. Each run plays it from the top; Runs and Pause between runs work as for sequences. " +
                   "Keyloom adds the functions below. Text after // is a note for you and does nothing.",
            Style = Find<Style>("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0)
        });
        head.Children.Add(title);
        var close = new Button { Style = Find<Style>("IconButton"), Width = 30, Height = 30, MinHeight = 0, VerticalAlignment = VerticalAlignment.Top, ToolTip = "Close (Esc)" };
        close.Content = new System.Windows.Shapes.Path { Data = Find<Geometry>("IconClose"), Style = Find<Style>("Icon"), Width = 10, Height = 10 };
        System.Windows.Automation.AutomationProperties.SetName(close, "Close help");
        close.Click += (_, _) => SheetLayer.Close(this, false);
        Grid.SetColumn(close, 1);
        head.Children.Add(close);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        var find = new TextBlock { Text = "Find a function", Style = Find<Style>("FieldLabel"), Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(find, Dock.Top);
        root.Children.Add(find);
        System.Windows.Automation.AutomationProperties.SetName(_search, "Search functions");
        _search.ToolTip = "Type to find a function, like click or wait";
        _search.TextChanged += (_, _) => Fill();
        DockPanel.SetDock(_search, Dock.Top);
        root.Children.Add(_search);
        // One scroll for the whole list; the sheet itself stays put.
        root.Children.Add(new ScrollViewer
        {
            Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = Math.Max(240, SystemParameters.WorkArea.Height * 0.55), Padding = new Thickness(0, 0, 8, 0)
        });
        Content = root;
        Fill();
        Loaded += (_, _) => _search.Focus();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; SheetLayer.Close(this, false); } };
    }

    private void Fill()
    {
        _list.Children.Clear();
        string query = _search.Text.Trim();
        bool Matches(ScriptFunction f) => query.Length == 0 || f.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || f.About.Contains(query, StringComparison.OrdinalIgnoreCase) || f.Group.Contains(query, StringComparison.OrdinalIgnoreCase);
        if (query.Length == 0 && _targets.Count + _grids.Count > 0)
        {
            Group("In this macro");
            var names = new WrapPanel();
            foreach (string t in _targets) names.Children.Add(Chip($"\"{t}\"", "Target"));
            foreach (string g in _grids) names.Children.Add(Chip($"\"{g}\"", "Grid"));
            _list.Children.Add(names);
        }
        foreach (var group in MacroScript.Functions.Where(Matches).GroupBy(f => f.Group))
        {
            Group(group.Key);
            foreach (var f in group) _list.Children.Add(Entry(f));
        }
        if (_list.Children.Count == 0)
            _list.Children.Add(new TextBlock { Text = $"No function matches \"{query}\".", Style = Find<Style>("Muted"), Margin = new Thickness(0, 8, 0, 0) });
    }
    private void Group(string name) =>
        _list.Children.Add(new TextBlock { Text = name, Style = Find<Style>("SectionTitle"), FontSize = 12.5, Margin = new Thickness(0, _list.Children.Count == 0 ? 2 : 16, 0, 6) });
    private Button Chip(string text, string kind)
    {
        var chip = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 6), ToolTip = $"{kind} · puts {text} in the script", FontFamily = Mono };
        chip.Click += (_, _) => Choose(text);
        return chip;
    }
    private FrameworkElement Entry(ScriptFunction f)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(new TextBlock { Text = f.Call, FontFamily = Mono, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = Find<Brush>("AccentBrush") });
        panel.Children.Add(new TextBlock { Text = f.About, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 6) });
        var example = new Grid();
        example.ColumnDefinitions.Add(new ColumnDefinition());
        example.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var code = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(10, 6, 10, 6) };
        code.SetResourceReference(Border.BackgroundProperty, "SurfaceAltBrush");
        code.SetResourceReference(Border.BorderBrushProperty, "FieldBorderBrush");
        code.Child = new TextBlock { Text = f.Example, FontFamily = Mono, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        example.Children.Add(code);
        var insert = new Button { Content = "Insert", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top, ToolTip = "Put this example in the script at the cursor" };
        System.Windows.Automation.AutomationProperties.SetName(insert, $"Insert {f.Name} example");
        insert.Click += (_, _) => Choose(f.Example);
        Grid.SetColumn(insert, 1);
        example.Children.Add(insert);
        panel.Children.Add(example);
        return panel;
    }
    private void Choose(string text)
    {
        Chosen = text;
        SheetLayer.Close(this, true);
    }
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    private T Find<T>(string key) => (T)FindResource(key);
}
