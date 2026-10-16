using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Keyloom.Engine;
using Keyloom.Models;

namespace Keyloom.Views;

/// <summary>
/// Targets in the builder: each picture or color the macro looks for is drawn where it is on the screen right now,
/// can be picked to see and change it, and new ones are taken straight from the screen under the builder.
/// </summary>
public sealed partial class MacroBuilderWindow
{
    /// <summary>What is not being edited fades to this, so the picked point or target stands out.</summary>
    private const double FadedOpacity = .4;
    private static readonly Brush TargetStroke = Frozen(new SolidColorBrush(Color.FromRgb(46, 189, 133)));
    private static readonly Brush TargetFill = Frozen(new SolidColorBrush(Color.FromArgb(0x22, 46, 189, 133)));
    private static readonly Brush EditingFill = Frozen(new SolidColorBrush(Color.FromRgb(255, 255, 255)));
    private enum SnipMode { None, Picture, Color }
    private readonly List<ScreenTarget> _targets;
    private readonly IScreen _screen;
    /// <summary>Where each target was found the last time the builder looked, in screen pixels; null when it was not there.</summary>
    private readonly Dictionary<string, Match?> _found = new(StringComparer.OrdinalIgnoreCase);
    private string? _pickedTarget;
    private SnipMode _snip;
    private Point? _snipFrom;
    private readonly Rectangle _snipBox = new() { Stroke = Brushes.White, StrokeThickness = 2, StrokeDashArray = [4, 3], Fill = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255))), IsHitTestVisible = false };
    /// <summary>What happens with a target taken from the screen; by default a Click on it is added.</summary>
    private Action<string>? _snipThen;

    private bool Faded(int k) => (_picked.Count > 0 || _pickedTarget != null) && !_picked.Contains(k);
    private ScreenTarget? TargetNamed(string? name) => _targets.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A clear "Editing" tag over the picked point, so the panel and the screen point at the same thing.</summary>
    private void AddEditingLabel(Point at)
    {
        var tag = new Border
        {
            Child = new TextBlock { Text = L.T($"Editing point {Current + 1}"), FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = MoveFill },
            Background = EditingFill, CornerRadius = new(9), Padding = new(8, 2, 8, 3), IsHitTestVisible = false
        };
        tag.Loaded += (_, _) => { Canvas.SetLeft(tag, at.X - tag.ActualWidth / 2); Canvas.SetTop(tag, at.Y - 26 - tag.ActualHeight); };
        Canvas.SetLeft(tag, at.X); Canvas.SetTop(tag, at.Y - 44);
        _canvas.Children.Add(tag);
    }

    // ---- Finding targets ----

    /// <summary>Looks for every target with the builder out of the way, then draws where they are.</summary>
    private async Task LocateAsync()
    {
        await Hidden(Locate);
        DrawPoints(); BuildPanel();
    }
    private void Locate()
    {
        var locator = new TargetLocator(_screen, _space.Windows, _space.Window, _targets);
        _found.Clear();
        foreach (var t in _targets)
        {
            try { _found[t.Name] = locator.Locate(t.Name); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { _found[t.Name] = null; }
        }
    }
    /// <summary>Makes the builder see-through while the screen is read, so it does not find itself.</summary>
    private async Task Hidden(Action read)
    {
        double opacity = Opacity;
        Opacity = 0;
        try
        {
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            await Task.Delay(150);
            read();
        }
        finally { Opacity = opacity; }
    }
    private List<(int Index, string Verb)> Uses(string name)
    {
        var uses = new List<(int, string)>();
        for (int i = 0; i < _steps.Count; i++)
        {
            var a = _steps[i];
            if (!string.Equals(a.Target, name, StringComparison.OrdinalIgnoreCase)) continue;
            string? verb = a.ActionType switch
            {
                MacroActionType.ClickTarget => "click",
                MacroActionType.WaitUntil => "wait",
                MacroActionType.GroupStart when a.Block == BlockKind.If => "if",
                _ => null
            };
            if (verb != null) uses.Add((i, verb));
        }
        return uses;
    }
    private void DrawTargets()
    {
        foreach (var t in _targets)
        {
            if (!_found.TryGetValue(t.Name, out var found) || found is not { } m) continue;
            var topLeft = ToLocal(m.X, m.Y);
            double scale = Scale, width = Math.Max(10, m.Width / scale), height = Math.Max(10, m.Height / scale);
            bool picked = string.Equals(_pickedTarget, t.Name, StringComparison.OrdinalIgnoreCase);
            bool faded = (_picked.Count > 0 || _pickedTarget != null) && !picked;
            var box = new Rectangle
            {
                Width = width + 8, Height = height + 8, RadiusX = 5, RadiusY = 5, Fill = TargetFill, Cursor = Cursors.Hand,
                Stroke = picked ? Brushes.White : TargetStroke, StrokeThickness = picked ? 3 : 2, StrokeDashArray = picked ? null : [4, 2],
                Opacity = faded ? FadedOpacity : 1, ToolTip = L.T($"{t.Name}: found here. Click to see and change it.")
            };
            string name = t.Name;
            box.MouseLeftButtonDown += (_, e) => { e.Handled = true; PickTarget(name); };
            Canvas.SetLeft(box, topLeft.X - 4); Canvas.SetTop(box, topLeft.Y - 4);
            _canvas.Children.Add(box);
            var verbs = Uses(t.Name).Select(u => u.Verb).Distinct().ToList();
            var label = new Border
            {
                Child = new TextBlock { Text = t.Name + (verbs.Count > 0 ? " · " + string.Join(", ", verbs) : L.T(" · not used yet")), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White },
                Background = picked ? MoveFill : TargetStroke, CornerRadius = new(6), Padding = new(6, 1, 6, 2), Opacity = faded ? FadedOpacity : 1, Cursor = Cursors.Hand
            };
            label.MouseLeftButtonDown += (_, e) => { e.Handled = true; PickTarget(name); };
            Canvas.SetLeft(label, topLeft.X - 4); Canvas.SetTop(label, topLeft.Y - 26);
            _canvas.Children.Add(label);
        }
    }
    private void PickTarget(string name)
    {
        CommitEdits();
        _picked.Clear();
        _pickedTarget = name;
        if (ShowingAll) { _rebuilding = true; _pointTab.IsChecked = true; _rebuilding = false; }
        Refresh();
    }

    // ---- The panel ----

    private void TargetsSummary(StackPanel body)
    {
        body.Children.Add(Label(L.T("Targets")));
        if (_targets.Count == 0)
        {
            body.Children.Add(Muted(L.T("None yet. Right click and choose New picture to find, to click a button wherever it is, or wait for it."), new(0, 0, 0, 12)));
            return;
        }
        foreach (var t in _targets)
        {
            var row = RowGrid(-1);
            bool here = _found.TryGetValue(t.Name, out var m) && m != null;
            AddCell(row, 1, Glyph(t.Kind == ScreenTargetKind.Color ? "IconTarget" : "IconImage", 12));
            var text = new TextBlock { Text = t.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Cursor = Cursors.Hand, ToolTip = L.T("See and change it") };
            Grid.SetColumnSpan(text, 2);
            AddCell(row, 2, text);
            AddCell(row, 4, Muted(here ? L.T("on screen") : L.T("not on screen"), new(6, 0, 0, 0)));
            string name = t.Name;
            row.MouseLeftButtonUp += (_, _) => PickTarget(name);
            body.Children.Add(row);
        }
        var again = new Button { Content = L.T("Look again"), Padding = new(10, 3, 10, 3), Margin = new(0, 2, 0, 12), HorizontalAlignment = HorizontalAlignment.Left, ToolTip = L.T("Find the targets on the screen as it is now") };
        again.Click += (_, _) => _ = LocateAsync();
        body.Children.Add(again);
    }
    private void TargetPanel(StackPanel body, ScreenTarget t)
    {
        var head = new DockPanel { Margin = new(0, 0, 0, 10) };
        var uses = Uses(t.Name);
        var delete = IconButton("IconTrash", uses.Count == 0 ? L.T("Delete this target") : L.T("Steps still look for it; remove them first"), () => Attempt(() => DeleteTarget(t)), uses.Count == 0);
        DockPanel.SetDock(delete, Dock.Right); head.Children.Add(delete);
        bool here = _found.TryGetValue(t.Name, out var found) && found != null;
        string where = found is { } m ? L.T($"on the screen at {m.Center.X}, {m.Center.Y}") : L.T("not on the screen right now");
        head.Children.Add(new StackPanel { Children = { Heading(L.T($"Target · {t.Name}")), Muted((t.Kind == ScreenTargetKind.Color ? L.T($"Color {t.Color}, ") : "Picture, ") + where, new()) } });
        body.Children.Add(head);
        var preview = new Border { Width = 120, Height = 72, CornerRadius = new(6), BorderThickness = new(1), HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 0, 0, 12), ClipToBounds = true };
        preview.SetResourceReference(Border.BorderBrushProperty, "HairlineBrush");
        preview.SetResourceReference(Border.BackgroundProperty, "SurfaceAltBrush");
        if (t.Kind == ScreenTargetKind.Color)
        {
            uint c = TargetPicture.ParseColor(t.Color);
            preview.Child = new Border { Margin = new(8), CornerRadius = new(4), Background = new SolidColorBrush(Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c)) };
        }
        else
        {
            // A brush, not an Image element: an Image in this window would rebuild the panel on every DPI notice it raises.
            try
            {
                var brush = new ImageBrush(ScreenSnipWindow.ToBitmap(TargetPicture.Decode(t.Picture))) { Stretch = Stretch.Uniform };
                RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
                preview.Child = new Border { Margin = new(4), Background = brush };
            }
            catch (ArgumentException ex) { preview.Child = Muted(ex.Message, new(6)); }
        }
        body.Children.Add(preview);
        body.Children.Add(Field(L.T("Name"), Box(t.Name, 180, L.T("Steps that look for it follow the new name"), text => RenameTarget(t, text))));
        body.Children.Add(Field(L.T("Tolerance"), Box(t.Tolerance.ToString(), 52, L.T("0 needs it exact; 10 is normal; higher lets the screen look more different"), text =>
        {
            int value = Whole(text, L.T("Tolerance"));
            if (value > 100) throw new ArgumentException(L.T("Tolerance goes from 0 to 100."));
            t.Tolerance = value;
        }), L.T("0 to 100")));

        body.Children.Add(Label(L.T("Steps that use it")));
        if (uses.Count == 0) body.Children.Add(Muted(L.T("None yet."), new(0, 0, 0, 4)));
        foreach (var (index, _) in uses)
        {
            int number = _steps.Take(index).Count(a => !a.IsGroupMarker) + 1;
            string text = _steps[index].ActionType == MacroActionType.GroupStart ? _steps[index].DisplayText : L.T($"Step {number}: {_steps[index].DisplayText}");
            body.Children.Add(Muted(text, new(0, 0, 0, 2)));
        }
        string name = t.Name;
        var click = new Button { Content = L.T("Click it"), Padding = new(10, 4, 10, 4), Margin = new(0, 10, 6, 0), ToolTip = L.T("Adds a Click on this target at the end") };
        click.Click += (_, _) => Attempt(() => AddTargetStep(new() { ActionType = MacroActionType.ClickTarget, Target = name, MouseButton = _lastButton, TimeoutMs = 1000 }));
        var wait = new Button { Content = L.T("Wait for it"), Padding = new(10, 4, 10, 4), Margin = new(0, 10, 6, 0), ToolTip = L.T("Adds a Wait until it shows, at the end") };
        wait.Click += (_, _) => Attempt(() => AddTargetStep(new() { ActionType = MacroActionType.WaitUntil, Target = name }));
        var again = new Button { Content = L.T("Look again"), Padding = new(10, 4, 10, 4), Margin = new(0, 10, 0, 0), ToolTip = L.T("Find it on the screen as it is now") };
        again.Click += (_, _) => _ = LocateAsync();
        body.Children.Add(new WrapPanel { Children = { click, wait, again } });
        if (!here) body.Children.Add(Muted(L.T("Not found now. Open what it belongs to, then Look again, or raise the tolerance."), new(0, 8, 0, 0)));
    }
    private void RenameTarget(ScreenTarget t, string text)
    {
        string name = text.Trim(), old = t.Name;
        if (name == old) return;
        if (name.Length is 0 or > 60) throw new ArgumentException(L.T("Give the target a name of 1 to 60 characters."));
        if (_targets.Any(x => x != t && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException(L.T($"Another target is already called '{name}'."));
        foreach (var (index, _) in Uses(old)) RouteBuilder.Change(_steps, index, a => a.Target = name);
        t.Name = name;
        if (_found.Remove(old, out var found)) _found[name] = found;
        if (string.Equals(_pickedTarget, old, StringComparison.OrdinalIgnoreCase)) _pickedTarget = name;
    }
    private void DeleteTarget(ScreenTarget t)
    {
        if (Uses(t.Name).Count > 0) throw new ArgumentException(L.T("Steps still look for this target. Remove them first."));
        _targets.Remove(t); _found.Remove(t.Name);
        _pickedTarget = null;
        Refresh();
    }
    /// <summary>Adds a step that looks for a target: at the picked point, or at the end.</summary>
    private void AddTargetStep(MacroAction step)
    {
        int at = Current >= 0 ? AtPoint(Current)() : _steps.Count;
        Change(() => RouteBuilder.Insert(_steps, at, [step]));
        Say(L.T($"Added: {step.DisplayText}."));
    }
    /// <summary>A menu item per target, plus taking a new one from the screen.</summary>
    private MenuItem TargetItems(string header, string icon, Action<string> add)
    {
        var menu = new MenuItem { Header = header, Icon = MenuIcon(icon) };
        foreach (var t in _targets)
        {
            string name = t.Name;
            menu.Items.Add(Item(name, t.Kind == ScreenTargetKind.Color ? "IconTarget" : "IconImage", () => add(name)));
        }
        if (_targets.Count > 0) menu.Items.Add(new Separator());
        menu.Items.Add(Item(L.T("New picture..."), "IconImage", () => StartSnip(false, add)));
        menu.Items.Add(Item(L.T("New color..."), "IconTarget", () => StartSnip(true, add)));
        return menu;
    }
    /// <summary>The target a Click on or Wait until step looks for, picked from the macro's targets.</summary>
    private FrameworkElement TargetEditor(int i, MacroAction a)
    {
        var names = _targets.Select(t => t.Name).ToArray();
        var combo = new ComboBox { Width = 150, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = names, SelectedItem = names.FirstOrDefault(n => string.Equals(n, a.Target, StringComparison.OrdinalIgnoreCase)), ToolTip = a.DisplayText };
        combo.SelectionChanged += (_, _) =>
        {
            if (_rebuilding || combo.SelectedItem is not string name) return;
            Attempt(() => Change(() => RouteBuilder.Change(_steps, i, s => s.Target = name)));
        };
        return combo;
    }
    /// <summary>An If or an Else around the picked point: what it checks, changed in place.</summary>
    private void BlockSection(StackPanel body, MacroAction g)
    {
        int start = _steps.IndexOf(g);
        var box = new Border { Margin = new(0, 14, 0, 0), Padding = new(10, 8, 10, 8), CornerRadius = new(8), BorderThickness = new(1) };
        box.SetResourceReference(Border.BackgroundProperty, "SurfaceAltBrush");
        box.SetResourceReference(Border.BorderBrushProperty, "HairlineBrush");
        var stack = new StackPanel();
        if (g.Block == BlockKind.Else)
        {
            stack.Children.Add(new TextBlock { Text = L.T("Inside an Else"), FontWeight = FontWeights.SemiBold });
            stack.Children.Add(Muted(L.T("This point plays only when the If before it does not."), new(0, 2, 0, 0)));
        }
        else
        {
            stack.Children.Add(new TextBlock { Text = L.T("Inside an If"), FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 6) });
            var names = _targets.Select(t => t.Name).ToArray();
            var target = new ComboBox { Width = 150, ItemsSource = names, SelectedItem = names.FirstOrDefault(n => string.Equals(n, g.Target, StringComparison.OrdinalIgnoreCase)) };
            target.SelectionChanged += (_, _) => { if (!_rebuilding && target.SelectedItem is string name) Attempt(() => Change(() => RouteBuilder.Change(_steps, start, s => s.Target = name))); };
            var condition = new ComboBox { Width = 160, ItemsSource = new[] { L.T("is on the screen"), L.T("is not on the screen") }, SelectedIndex = g.Gone ? 1 : 0, Margin = new(0, 6, 0, 0) };
            condition.SelectionChanged += (_, _) => { if (!_rebuilding) Attempt(() => Change(() => RouteBuilder.Change(_steps, start, s => s.Gone = condition.SelectedIndex == 1))); };
            stack.Children.Add(new WrapPanel { Children = { new TextBlock { Text = L.T("Plays when"), VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 8, 0) }, target } });
            stack.Children.Add(condition);
        }
        box.Child = stack;
        body.Children.Add(box);
    }

    // ---- Taking a new target from the screen ----

    /// <param name="then">What to do with the new target; without it, a Click on it is added.</param>
    private void StartSnip(bool color, Action<string>? then = null)
    {
        CommitEdits();
        _snip = color ? SnipMode.Color : SnipMode.Picture;
        _snipThen = then;
        Cursor = Cursors.Cross;
        Say(color ? L.T("Click the color to find. Esc cancels.") : L.T("Drag around what to find, like a button or an icon. Esc cancels."));
    }
    private void EndSnip()
    {
        _snip = SnipMode.None; _snipFrom = null;
        Cursor = null;
        _overlay.Children.Remove(_snipBox);
        if (IsMouseCaptured) ReleaseMouseCapture();
    }
    private void Snip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_snip == SnipMode.None || _panel.IsMouseOver) return;
        e.Handled = true;
        var at = e.GetPosition(_canvas);
        if (_snip == SnipMode.Color)
        {
            var (x, y) = ToScreen(at);
            _ = FinishSnip(new Int32Rect(x, y, 1, 1));
            return;
        }
        _snipFrom = at;
        if (!_overlay.Children.Contains(_snipBox)) _overlay.Children.Add(_snipBox);
        Canvas.SetLeft(_snipBox, at.X); Canvas.SetTop(_snipBox, at.Y); _snipBox.Width = _snipBox.Height = 0;
        CaptureMouse();
    }
    private void Snip_MouseMove(object sender, MouseEventArgs e)
    {
        if (_snipFrom is not { } from) return;
        var at = e.GetPosition(_canvas);
        Canvas.SetLeft(_snipBox, Math.Min(from.X, at.X)); Canvas.SetTop(_snipBox, Math.Min(from.Y, at.Y));
        _snipBox.Width = Math.Abs(at.X - from.X); _snipBox.Height = Math.Abs(at.Y - from.Y);
        e.Handled = true;
    }
    private void Snip_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_snipFrom is not { } from) return;
        e.Handled = true;
        var at = e.GetPosition(_canvas);
        var (x1, y1) = ToScreen(new Point(Math.Min(from.X, at.X), Math.Min(from.Y, at.Y)));
        var (x2, y2) = ToScreen(new Point(Math.Max(from.X, at.X), Math.Max(from.Y, at.Y)));
        _snipFrom = null;
        _overlay.Children.Remove(_snipBox);
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (x2 - x1 < 4 || y2 - y1 < 4) { Say(L.T("Drag a bigger box, at least 4 by 4 pixels. Esc cancels."), true); return; }
        _ = FinishSnip(new Int32Rect(x1, y1, Math.Min(x2 - x1, ScreenTarget.MaxPictureSide), Math.Min(y2 - y1, ScreenTarget.MaxPictureSide)));
    }
    /// <summary>Reads the rectangle from the screen under the builder and makes it a target.</summary>
    private async Task FinishSnip(Int32Rect rect)
    {
        bool color = _snip == SnipMode.Color;
        var then = _snipThen;
        EndSnip();
        PixelImage? shot = null;
        await Hidden(() => shot = _screen.Capture(rect.X, rect.Y, rect.Width, rect.Height));
        if (shot == null || shot.Width == 0) { Say(L.T("The screen could not be read there."), true); return; }
        string stem = color ? L.T("Color") : L.T("Picture");
        int n = 1;
        while (TargetNamed($"{stem} {n}") != null) n++;
        var target = new ScreenTarget
        {
            Name = $"{stem} {n}", Kind = color ? ScreenTargetKind.Color : ScreenTargetKind.Picture, Tolerance = color ? 4 : ScreenTarget.DefaultTolerance,
            Color = color ? TargetPicture.ColorText(shot.Pixels[0]) : "#000000", Picture = color ? "" : TargetPicture.Encode(shot)
        };
        _targets.Add(target);
        _found[target.Name] = new Match(rect.X, rect.Y, rect.Width, rect.Height);
        Attempt(() =>
        {
            if (then != null) then(target.Name);
            else AddTargetStep(new() { ActionType = MacroActionType.ClickTarget, Target = target.Name, MouseButton = _lastButton, TimeoutMs = 1000 });
        });
        PickTarget(target.Name);
        Say(L.T($"Added {target.Name}. Give it a name that says what it is."));
        FocusTargetName();
    }
    private void FocusTargetName() => Dispatcher.BeginInvoke(() =>
    {
        if (Descendants(_panelBody).OfType<TextBox>().FirstOrDefault() is { } box) { box.Focus(); box.SelectAll(); }
    }, System.Windows.Threading.DispatcherPriority.Loaded);
}
