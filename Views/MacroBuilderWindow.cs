using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Keyloom.Engine;
using Keyloom.Models;

namespace Keyloom.Views;

/// <summary>What the builder hands back on Done: the steps and the macro's click delay after moving.</summary>
public sealed record BuilderResult(List<MacroAction> Steps, int ClickDelayMs);

/// <summary>
/// Experimental: builds a sequence right on the screen. Every cursor position step is a numbered point where it will
/// happen, carrying the steps that follow it (clicks, holds, waits, keys). Left click picks and drags; right click adds.
/// A side panel, which can be moved and hidden, shows the picked point on a small timeline, or every step of the macro.
/// Works on a copy; <see cref="Finished"/> hands back the new steps, or null when cancelled.
/// </summary>
public sealed class MacroBuilderWindow : OverlayWindow
{
    public const int MaxPoints = 400, DefaultGlideMs = 200;
    private const double PanelWidth = 400;
    private static readonly (string Value, string Label)[] Buttons = [("Left", "Left"), ("Right", "Right"), ("Middle", "Middle"), ("X1", "Back"), ("X2", "Forward")];
    private static readonly Brush ClickFill = Frozen(new SolidColorBrush(Color.FromRgb(210, 65, 47)));
    private static readonly Brush HoldFill = Frozen(new SolidColorBrush(Color.FromRgb(214, 128, 24)));
    private static readonly Brush MoveFill = Frozen(new SolidColorBrush(Color.FromRgb(15, 108, 189)));
    private static readonly Brush LineBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xE0, 90, 176, 247)));
    private static readonly Brush HaloBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x90, 10, 14, 20)));
    private static readonly Brush PillFill = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 24, 32, 44)));
    private readonly List<MacroAction> _steps;
    private readonly RouteSpace _space;
    private readonly Int32Rect _monitor;
    private readonly string _name;
    private int _settleMs;
    private readonly List<MacroAction[]> _undo = [];
    private readonly Canvas _canvas = new() { ClipToBounds = true };
    private readonly Canvas _overlay = new();
    private readonly Border _panel;
    private readonly ContentControl _panelBody = new() { Focusable = false };
    private readonly Border _showPanel;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly RadioButton _pointTab = new() { Content = "Point", GroupName = "BuilderTab", IsChecked = true };
    private readonly RadioButton _allTab = new() { Content = "All steps", GroupName = "BuilderTab" };
    /// <summary>Text fields that have not applied their value yet; they run before anything else changes.</summary>
    private readonly List<Action> _editors = [];
    private List<RoutePoint> _points = [];
    /// <summary>Picked points by number; the last one is the one the panel shows.</summary>
    private readonly List<int> _picked = [];
    private int _dragging = -1;
    private bool _dragMoved, _backdropPressed, _done, _rebuilding, _panelPlaced, _statusSticky;
    private Point _dragFrom, _panelGrab;
    private string _lastButton = "Left";
    /// <summary>The step whose field gets focus after the next rebuild, like a wait that was just added.</summary>
    private int _focusStep = -1;
    public event Action<BuilderResult?>? Finished;
    protected override bool ClickThrough => false;
    private int Current => _picked.Count > 0 ? _picked[^1] : -1;
    private bool ShowingAll => _allTab.IsChecked == true;

    /// <param name="monitor">The monitor to cover, in screen pixels.</param>
    /// <param name="settleMs">The macro's click delay after moving, shown on the point timeline and changeable here.</param>
    public MacroBuilderWindow(string name, IEnumerable<MacroAction> steps, RouteSpace space, Int32Rect monitor, int settleMs = MacroProfile.DefaultPointerSettleMs)
    {
        _steps = steps.Select(a => a.Clone()).ToList();
        if (RouteBuilder.Points(_steps).Count > MaxPoints)
            throw new ArgumentException($"The builder shows up to {MaxPoints:N0} points. Recorded macros with more are easier to change in the list.");
        _space = space; _monitor = monitor; _name = name; _settleMs = settleMs;
        Title = "Keyloom macro builder";
        IsHitTestVisible = true; Focusable = true; ShowActivated = true;
        var backdrop = new Border { Background = Frozen(new SolidColorBrush(Color.FromArgb(0x38, 5, 10, 18))) };
        backdrop.MouseLeftButtonDown += (_, e) => { _backdropPressed = true; e.Handled = true; };
        backdrop.MouseLeftButtonUp += (_, e) =>
        {
            if (!_backdropPressed) return;
            _backdropPressed = false; e.Handled = true;
            CommitEdits(); Pick([]);
        };
        backdrop.MouseRightButtonUp += (_, e) => { e.Handled = true; CommitEdits(); OpenMenu(SpaceMenu(e.GetPosition(_canvas))); };
        _canvas.MouseMove += Canvas_MouseMove;
        _canvas.MouseLeftButtonUp += (_, e) => { if (_dragging >= 0) { e.Handled = true; _canvas.ReleaseMouseCapture(); } };
        _canvas.LostMouseCapture += (_, _) => { if (_dragging >= 0) { _dragging = -1; Refresh(); } };
        foreach (var tab in new[] { _pointTab, _allTab })
        {
            tab.SetResourceReference(StyleProperty, "Segment");
            tab.Checked += (_, _) => { if (!_rebuilding) { CommitEdits(); BuildPanel(); } };
        }
        _panel = Panel();
        _showPanel = ShowPanelPill();
        _overlay.Children.Add(_panel);
        _overlay.Children.Add(_showPanel);
        Content = new Grid { Children = { backdrop, _canvas, _overlay } };
        SourceInitialized += (_, _) => Place(_monitor.X, _monitor.Y, _monitor.Width, _monitor.Height);
        Loaded += (_, _) => { if (ShowActivated) { Activate(); Keyboard.Focus(this); } Refresh(); };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(() => { Place(_monitor.X, _monitor.Y, _monitor.Width, _monitor.Height); _panelPlaced = false; Refresh(); });
        PreviewKeyDown += Builder_KeyDown;
        Closed += (_, _) => { if (!_done) { _done = true; Finished?.Invoke(null); } };
    }

    // ---- Points on the screen ----

    private Point Local(MacroAction step)
    {
        var (x, y) = _space.ToScreen(step);
        return ToLocal(x, y);
    }
    /// <summary>Redraws the points and rebuilds the panel after any change.</summary>
    private void Refresh()
    {
        DrawPoints();
        BuildPanel();
    }
    private void DrawPoints()
    {
        _canvas.Children.Clear();
        _points = RouteBuilder.Points(_steps);
        _picked.RemoveAll(k => k >= _points.Count);
        var spots = _points.Select(p => Local(_steps[p.Index])).ToList();
        // A solid line glides there; a dashed one jumps.
        for (int k = 1; k < spots.Count; k++)
        {
            bool glides = _steps[_points[k].Index].GlideMs > 0;
            var dashes = glides ? null : new DoubleCollection { 3, 2.5 };
            _canvas.Children.Add(new Line { X1 = spots[k - 1].X, Y1 = spots[k - 1].Y, X2 = spots[k].X, Y2 = spots[k].Y, Stroke = HaloBrush, StrokeThickness = 5, IsHitTestVisible = false });
            _canvas.Children.Add(new Line { X1 = spots[k - 1].X, Y1 = spots[k - 1].Y, X2 = spots[k].X, Y2 = spots[k].Y, Stroke = LineBrush, StrokeThickness = 2, StrokeDashArray = dashes, IsHitTestVisible = false });
        }
        // How long the run stays at a point sits on the line to the next one; the last point's beside it.
        for (int k = 0; k < _points.Count; k++)
        {
            int ms = RouteBuilder.WaitMs(_steps, _points[k]);
            int glide = k + 1 < _points.Count ? _steps[_points[k + 1].Index].GlideMs : 0;
            bool last = k + 1 == spots.Count;
            if (ms == 0 && glide == 0 && k != Current) continue;
            var at = !last ? new Point((spots[k].X + spots[k + 1].X) / 2, (spots[k].Y + spots[k + 1].Y) / 2) : spots[k] + new Vector(0, 38);
            AddTimePill(k, ms, glide, at, last);
        }
        for (int k = 0; k < _points.Count; k++) AddPoint(k, spots[k]);
    }
    private void AddTimePill(int k, int waitMs, int glideMs, Point at, bool last)
    {
        string text = waitMs == 0 && glideMs == 0 ? "+ wait" : string.Join(" · ", new[]
        {
            waitMs > 0 ? $"{waitMs:N0} ms" : "", glideMs > 0 ? $"glide {glideMs:N0} ms" : ""
        }.Where(s => s.Length > 0));
        string next = last ? "the next run" : $"point {k + 2}";
        var pill = new Border
        {
            Child = new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White },
            Padding = new(7, 2, 7, 3), CornerRadius = new(9), Cursor = Cursors.Hand, Background = PillFill,
            BorderBrush = LineBrush, BorderThickness = new(1), Opacity = text == "+ wait" ? .75 : 1,
            ToolTip = text == "+ wait" ? $"Add a wait at point {k + 1} before {next}"
                : $"From point {k + 1} to {next}: stays {waitMs:N0} ms at point {k + 1}{(glideMs > 0 ? $", then glides {glideMs:N0} ms" : "")}. Click to change."
        };
        pill.MouseLeftButtonDown += (_, e) => e.Handled = true;
        pill.MouseLeftButtonUp += (_, e) => { e.Handled = true; CommitEdits(); Attempt(() => EditWait(k)); };
        pill.Loaded += (_, _) => { Canvas.SetLeft(pill, at.X - pill.ActualWidth / 2); Canvas.SetTop(pill, at.Y - pill.ActualHeight / 2); };
        Canvas.SetLeft(pill, at.X); Canvas.SetTop(pill, at.Y);
        _canvas.Children.Add(pill);
    }
    private void AddPoint(int k, Point at)
    {
        var p = _points[k];
        var own = p.Steps.Select(i => _steps[i]).ToList();
        bool hold = own.Any(a => a.ActionType == MacroActionType.MouseDown), click = own.Any(a => a.ActionType == MacroActionType.MouseClick);
        var dot = new Grid { Width = 30, Height = 30, Cursor = Cursors.SizeAll, ToolTip = Summary(k) };
        if (_picked.Contains(k))
            dot.Children.Add(new Ellipse { Width = 42, Height = 42, Margin = new(-6), Stroke = Brushes.White, StrokeThickness = k == Current ? 3 : 2, Fill = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255))) });
        // Points inside a group get a dashed ring, and the repeat count when the group plays more than once.
        var group = p.Group >= 0 ? _steps[p.Group] : null;
        if (group != null) dot.Children.Add(new Ellipse { Width = 36, Height = 36, Margin = new(-3), Stroke = Brushes.White, StrokeThickness = 1.5, StrokeDashArray = [2, 2] });
        dot.Children.Add(new Ellipse { Fill = hold ? HoldFill : click ? ClickFill : MoveFill, Stroke = Brushes.White, StrokeThickness = 2 });
        dot.Children.Add(new TextBlock { Text = (k + 1).ToString(), Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        if (group is { Runs: > 1 }) dot.Children.Add(Badge($"×{group.Runs}", MoveFill, new(0, -10, -16, 0), VerticalAlignment.Top));
        // Keys pressed at the point, like W or E, show under it.
        var keys = own.Where(a => a.ActionType is MacroActionType.KeyPress or MacroActionType.KeyDown).Select(a => a.KeyName).Distinct().ToList();
        if (own.Any(a => a.ActionType == MacroActionType.Text)) keys.Add("text");
        if (keys.Count > 0) dot.Children.Add(Badge(keys.Count > 3 ? string.Join(" ", keys.Take(3)) + " …" : string.Join(" ", keys), PillFill, new(-40, 0, -40, -22), VerticalAlignment.Bottom, HorizontalAlignment.Center));
        dot.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            CommitEdits();
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                Pick(_picked.Contains(k) ? _picked.Where(x => x != k).ToList() : _picked.Append(k).ToList());
                return;
            }
            if (k != Current || _picked.Count > 1) Pick([k]);
            _dragging = k; _dragMoved = false; _dragFrom = e.GetPosition(_canvas);
            _canvas.CaptureMouse();
        };
        dot.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true; CommitEdits();
            if (!_picked.Contains(k)) Pick([k]);
            OpenMenu(PointMenu(k));
        };
        Canvas.SetLeft(dot, at.X - 15); Canvas.SetTop(dot, at.Y - 15);
        _canvas.Children.Add(dot);
    }
    private static Border Badge(string text, Brush fill, Thickness margin, VerticalAlignment vertical, HorizontalAlignment horizontal = HorizontalAlignment.Right) => new()
    {
        Child = new TextBlock { Text = text, FontSize = 10, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center },
        Background = fill, CornerRadius = new(7), Padding = new(5, 0, 5, 1), Margin = margin,
        HorizontalAlignment = horizontal, VerticalAlignment = vertical, IsHitTestVisible = false
    };
    private string Summary(int k)
    {
        var p = _points[k];
        var own = p.Steps.Select(i => _steps[i].DisplayText).ToList();
        return $"Point {k + 1}: cursor to {Where(_steps[p.Index])}" + (own.Count == 0 ? "\nNothing else happens here" : "\n" + string.Join("\n", own.Take(8)) + (own.Count > 8 ? "\n…" : ""));
    }
    private static string Where(MacroAction move) => move.Space switch
    {
        CoordinateSpace.Window => $"{move.X}, {move.Y} in the window",
        CoordinateSpace.Start => $"{move.X:+0;-0;+0}, {move.Y:+0;-0;+0} from where the cursor starts",
        _ => $"{move.X}, {move.Y} on the screen"
    };

    // ---- Picking, dragging and menus ----

    /// <param name="fromList">Picked from the all-steps list, which then stays open.</param>
    private void Pick(IEnumerable<int> points, bool fromList = false)
    {
        var next = points.Distinct().ToList();
        if (!fromList && next.Count > 0 && ShowingAll) { _rebuilding = true; _pointTab.IsChecked = true; _rebuilding = false; }
        else if (next.SequenceEqual(_picked)) return;
        _picked.Clear(); _picked.AddRange(next);
        Refresh();
    }
    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging < 0 || e.LeftButton != MouseButtonState.Pressed) return;
        var now = e.GetPosition(_canvas);
        if (!_dragMoved && (now - _dragFrom).Length < 3) return;
        if (!_dragMoved) { Remember(); _dragMoved = true; }
        var move = _steps[_points[_dragging].Index];
        var (x, y) = ToScreen(now);
        var (sx, sy) = _space.FromScreen(move.Space, x, y);
        try { RouteBuilder.Move(_steps, _points[_dragging], sx, sy); }
        catch (ArgumentException ex) { Say(ex.Message, true); return; }
        DrawPoints();
    }
    private void OpenMenu(ContextMenu menu)
    {
        menu.PlacementTarget = this;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }
    private static MenuItem Item(string header, string? icon, Action click, string? gesture = null, bool enabled = true)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsEnabled = enabled };
        if (icon != null) item.Icon = MenuIcon(icon);
        item.Click += (_, _) => click();
        return item;
    }
    private static Path MenuIcon(string icon)
    {
        var path = new Path { Data = (Geometry)Application.Current.FindResource(icon) };
        path.SetResourceReference(StyleProperty, "MenuIcon");
        return path;
    }
    /// <summary>Right click on empty space: new points there, and steps for the picked point or the end.</summary>
    private ContextMenu SpaceMenu(Point at)
    {
        string after = Current >= 0 ? $", after point {Current + 1}" : _points.Count > 0 ? ", at the end" : "";
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = $"New point here{after}", IsEnabled = false });
        menu.Items.Add(Item("Click here", "IconMouse", () => AddPointAt(at, [new() { ActionType = MacroActionType.MouseClick, MouseButton = _lastButton }])));
        menu.Items.Add(Item("Hold here", "IconHold", () => AddPointAt(at, RouteBuilder.Hold(_lastButton))));
        menu.Items.Add(Item("Cursor here, no click", "IconTarget", () => AddPointAt(at, [])));
        menu.Items.Add(new Separator());
        if (Current >= 0) menu.Items.Add(AddMenu($"Add to point {Current + 1}", AtPoint(Current), Current));
        menu.Items.Add(AddMenu("Add at the end", () => _steps.Count, -1));
        if (_picked.Count > 0) menu.Items.Add(Item(_picked.Count > 1 ? $"Group {_picked.Count} points" : "Group this point", "IconGroup", GroupPicked, "Ctrl+G"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(_panel.Visibility == Visibility.Visible ? "Hide panel" : "Show panel", "IconEdit", TogglePanel, "H"));
        menu.Items.Add(Item("Undo", null, Undo, "Ctrl+Z", _undo.Count > 0));
        menu.Items.Add(Item("Done", "IconCheck", Done, "Esc"));
        return menu;
    }
    private ContextMenu PointMenu(int k)
    {
        var menu = new ContextMenu();
        menu.Items.Add(AddMenu($"Add to point {k + 1}", AtPoint(k), k));
        bool glides = _steps[_points[k].Index].GlideMs > 0;
        menu.Items.Add(Item(glides ? "Jump here instead of gliding" : "Glide here instead of jumping", "IconMove", () => Attempt(() => SetGlide(k, glides ? 0 : DefaultGlideMs))));
        menu.Items.Add(Item(_picked.Count > 1 ? $"Group {_picked.Count} points" : "Group this point", "IconGroup", GroupPicked, "Ctrl+G"));
        if (_points[k].Group >= 0) menu.Items.Add(Item("Ungroup", "IconGroup", () => Attempt(() => Change(() => RouteBuilder.Ungroup(_steps, _points[k].Group)))));
        menu.Items.Add(Item("Duplicate point", "IconCopy", () => Attempt(() => Duplicate(k)), "Ctrl+D"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(_picked.Count > 1 ? $"Delete {_picked.Count} points" : "Delete point", "IconTrash", DeletePicked, "Delete"));
        return menu;
    }
    /// <summary>Where steps added to a point go: before the wait it ends with, so that stays the wait before the next point.</summary>
    private Func<int> AtPoint(int k) => () => k >= 0
        ? RouteBuilder.TrailingWait(_steps, _points[k]) is >= 0 and int wait ? wait : _points[k].End
        : RouteBuilder.Opening(_steps).End;
    private MenuItem AddMenu(string header, Func<int> at, int point)
    {
        var menu = new MenuItem { Header = header, Icon = MenuIcon("IconPlus") };
        foreach (var item in AddItems(at, point)) menu.Items.Add(item);
        return menu;
    }
    /// <summary>The steps that can be added: inserted where <paramref name="at"/> says, then shown for <paramref name="point"/>.</summary>
    private List<object> AddItems(Func<int> at, int point)
    {
        void Add(params MacroAction[] steps) => Attempt(() =>
        {
            int index = 0;
            Change(() => { index = at(); RouteBuilder.Insert(_steps, index, steps); });
            // The first value to set gets focus, so a new wait or key can be typed right away.
            int first = Array.FindIndex(steps, a => a.ActionType is MacroActionType.Delay or MacroActionType.KeyPress or MacroActionType.KeyDown or MacroActionType.Text);
            _focusStep = first >= 0 ? index + first : -1;
            if (point >= 0 && !_picked.Contains(point)) _picked.Add(point);
            Refresh();
        });
        return
        [
            Item("Click", "IconMouse", () => Add(new MacroAction { ActionType = MacroActionType.MouseClick, MouseButton = _lastButton })),
            Item("Hold a button", "IconHold", () => Add(RouteBuilder.Hold(_lastButton))),
            Item("Wait", "IconClock", () => Add(new MacroAction { ActionType = MacroActionType.Delay, DelayMs = RouteBuilder.DefaultWaitMs })),
            new Separator(),
            Item("Press a key", "IconKeyboard", () => Add(new MacroAction { ActionType = MacroActionType.KeyPress, KeyName = "E" })),
            Item("Hold a key", "IconHold", () => Add(RouteBuilder.HoldKey("W"))),
            Item("Type text", "IconText", () => Add(new MacroAction { ActionType = MacroActionType.Text, Text = "Hello" })),
            Item("Scroll", "IconScroll", () => Add(new MacroAction { ActionType = MacroActionType.MouseScroll, ScrollAmount = 1 }))
        ];
    }

    // ---- Changes ----

    private void AddPointAt(Point local, IReadOnlyList<MacroAction> then) => Attempt(() =>
    {
        var (x, y) = ToScreen(local);
        var space = _space.SpaceAt(x, y);
        var (sx, sy) = _space.FromScreen(space, x, y);
        var move = new MacroAction { ActionType = MacroActionType.MousePosition, Space = space, X = sx, Y = sy };
        var after = Current >= 0 ? _points[Current] : _points.Count > 0 ? _points[^1] : null;
        int at = 0;
        Change(() => at = RouteBuilder.AddPoint(_steps, after, move, then));
        _picked.Clear(); _picked.Add(RouteBuilder.Points(_steps).FindIndex(p => p.Index == at));
        Refresh();
    });
    /// <summary>Picks a point and puts the cursor in the wait before the next point, adding one when there is none.</summary>
    private void EditWait(int k)
    {
        int wait = RouteBuilder.TrailingWait(_steps, _points[k]);
        if (wait < 0)
        {
            int end = _points[k].End;
            Change(() => RouteBuilder.Insert(_steps, end, [new MacroAction { ActionType = MacroActionType.Delay, DelayMs = RouteBuilder.DefaultWaitMs }]));
            wait = end;
        }
        _rebuilding = true; _pointTab.IsChecked = true; _rebuilding = false;
        _picked.Clear(); _picked.Add(k);
        _focusStep = wait;
        Refresh();
    }
    private void SetGlide(int k, int ms) => Change(() => RouteBuilder.Change(_steps, _points[k].Index, a => a.GlideMs = ms));
    private void Duplicate(int k)
    {
        var p = _points[k];
        var copy = Enumerable.Range(p.Index, p.End - p.Index).Select(i => _steps[i].Clone()).ToList();
        // The copy sits a little off the original, so both can be seen and dragged.
        copy[0].X += 24; copy[0].Y += 24;
        int at = p.End;
        Change(() => RouteBuilder.Insert(_steps, at, copy));
        _picked.Clear(); _picked.Add(RouteBuilder.Points(_steps).FindIndex(x => x.Index == at));
        Refresh();
    }
    private void DeletePicked()
    {
        if (_picked.Count == 0) return;
        var gone = _picked.Select(k => _points[k]).OrderByDescending(p => p.Index).ToList();
        int first = _picked.Min();
        _picked.Clear();
        Change(() => { foreach (var p in gone) RouteBuilder.Remove(_steps, p); });
        if (_points.Count > 0) Pick([Math.Min(first, _points.Count - 1)]);
    }
    private void GroupPicked() => Attempt(() =>
    {
        var points = _picked.Select(k => _points[k]).ToList();
        int number = _steps.Count(a => a.ActionType == MacroActionType.GroupStart) + 1;
        int first = _picked.Min();
        Change(() => RouteBuilder.Group(_steps, points, $"Group {number}"));
        // The first point shows the group's settings.
        Pick([first]);
        Say($"Grouped {points.Count:N0} {(points.Count == 1 ? "point" : "points")}. Set how often the group repeats below.");
    });
    /// <summary>Changes the steps as one undo step, then redraws. A failed change leaves the steps as they were.</summary>
    private void Change(Action change)
    {
        var before = _steps.Select(a => a.Clone()).ToArray();
        try { change(); }
        catch { _steps.Clear(); _steps.AddRange(before); throw; }
        _undo.Add(before);
        if (_undo.Count > 50) _undo.RemoveAt(0);
        Say("");
        Refresh();
    }
    private void Remember()
    {
        _undo.Add(_steps.Select(a => a.Clone()).ToArray());
        if (_undo.Count > 50) _undo.RemoveAt(0);
    }
    private void Undo()
    {
        if (_undo.Count == 0) return;
        _steps.Clear(); _steps.AddRange(_undo[^1]); _undo.RemoveAt(_undo.Count - 1);
        Say("");
        Refresh();
    }

    // ---- The side panel ----

    private Border Panel()
    {
        var header = new Grid { Cursor = Cursors.SizeAll, Background = Brushes.Transparent, Margin = new(0, 0, 0, 10), ToolTip = "Drag to move the panel" };
        header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var title = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        var flask = Icon("IconFlask", 12);
        flask.Margin = new(0, 0, 8, 0);
        DockPanel.SetDock(flask, Dock.Left); title.Children.Add(flask);
        title.Children.Add(new TextBlock { Text = $"Builder · {_name}", FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        header.Children.Add(title);
        var help = Icon("IconHelp", 13);
        help.Margin = new(0, 0, 4, 0);
        help.Cursor = Cursors.Help;
        help.ToolTip = "Left click picks a point and drags it; Ctrl+click picks more.\nRight click adds points and steps.\nA dashed line jumps the cursor, a solid one glides it.\nH hides this panel, Esc finishes, Ctrl+Z undoes.";
        var hide = IconButton("IconMinimize", "Hide the panel (H). Right click still adds and changes things.", TogglePanel);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Children = { help, hide } };
        Grid.SetColumn(tools, 1);
        header.Children.Add(tools);
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) != null) return;
            _panelGrab = e.GetPosition(_panel); header.CaptureMouse(); e.Handled = true;
        };
        header.MouseMove += (_, e) =>
        {
            if (!header.IsMouseCaptured) return;
            var at = e.GetPosition(_overlay) - (Vector)_panelGrab;
            PlacePanel(at.X, at.Y);
        };
        header.MouseLeftButtonUp += (_, _) => header.ReleaseMouseCapture();

        _status.Margin = new(0, 0, 0, 10);
        _status.Visibility = Visibility.Collapsed;
        var tabs = new Border { Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _pointTab, _allTab } }, HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 0, 0, 12) };
        tabs.SetResourceReference(StyleProperty, "SegmentBar");
        var undo = new Button { Content = "Undo", Padding = new(11, 4, 11, 4), Margin = new(0, 0, 6, 0), ToolTip = "Ctrl+Z" };
        undo.Click += (_, _) => Undo();
        var cancel = new Button { Content = "Cancel", Padding = new(11, 4, 11, 4), Margin = new(0, 0, 6, 0), ToolTip = "Leave without changing the macro" };
        cancel.Click += (_, _) => Finish(null);
        var done = new Button { Content = "Done", Padding = new(14, 4, 14, 4), ToolTip = "Put these steps in the editor (Esc). Save the macro there; one Ctrl+Z in the list undoes it." };
        done.SetResourceReference(StyleProperty, "PrimaryButton");
        done.Click += (_, _) => Done();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 12, 0, 0), Children = { undo, cancel, done } };
        var scroll = new ScrollViewer { Content = _panelBody, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        var layout = new DockPanel { LastChildFill = true };
        foreach (var top in new UIElement[] { header, _status, tabs }) { DockPanel.SetDock(top, Dock.Top); layout.Children.Add(top); }
        DockPanel.SetDock(actions, Dock.Bottom); layout.Children.Add(actions);
        layout.Children.Add(scroll);
        var panel = Surface(layout, new(16, 12, 16, 14));
        panel.Width = PanelWidth;
        foreach (var routed in new[] { MouseLeftButtonDownEvent, MouseLeftButtonUpEvent, MouseRightButtonUpEvent })
            panel.AddHandler(routed, new MouseButtonEventHandler((_, e) => e.Handled = true));
        return panel;
    }
    private Border ShowPanelPill()
    {
        var text = new TextBlock { Text = "Show panel  (H)", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White };
        var pill = new Border { Child = text, Padding = new(12, 6, 12, 7), CornerRadius = new(14), Background = PillFill, BorderBrush = LineBrush, BorderThickness = new(1), Cursor = Cursors.Hand, Visibility = Visibility.Collapsed };
        pill.MouseLeftButtonDown += (_, e) => e.Handled = true;
        pill.MouseLeftButtonUp += (_, e) => { e.Handled = true; TogglePanel(); };
        Canvas.SetTop(pill, 16);
        return pill;
    }
    private void TogglePanel()
    {
        CommitEdits();
        bool show = _panel.Visibility != Visibility.Visible;
        _panel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _showPanel.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (show) return;
        _showPanel.UpdateLayout();
        Canvas.SetLeft(_showPanel, LocalSize.Width - _showPanel.ActualWidth - 16);
        Keyboard.Focus(this);
    }
    /// <summary>Keeps the panel on the monitor. It starts at the right edge.</summary>
    private void PlacePanel(double x, double y)
    {
        var size = LocalSize;
        if (size.Width <= 1) return;
        if (!_panelPlaced) { x = size.Width - PanelWidth - 24; y = 72; _panelPlaced = true; }
        _panel.MaxHeight = Math.Max(200, size.Height - 32);
        double height = _panel.ActualHeight > 0 ? _panel.ActualHeight : 300;
        Canvas.SetLeft(_panel, Math.Clamp(x, 8, Math.Max(8, size.Width - PanelWidth - 8)));
        Canvas.SetTop(_panel, Math.Clamp(y, 8, Math.Max(8, size.Height - height - 8)));
    }
    private void BuildPanel()
    {
        _rebuilding = true;
        try
        {
            _editors.Clear();
            var body = new StackPanel();
            if (ShowingAll) AllStepsPanel(body);
            else if (_picked.Count > 1) PickedPanel(body);
            else if (Current >= 0) PointPanel(body, Current);
            else OverviewPanel(body);
            _panelBody.Content = body;
        }
        finally { _rebuilding = false; }
        if (!_statusSticky) ShowHint();
        if (_focusStep >= 0) FocusField(_focusStep);
        _focusStep = -1;
        if (!_panelPlaced && IsLoaded) PlacePanel(0, 0);
    }
    /// <summary>The status line only shows messages; how the builder works is in the help tooltip.</summary>
    private void ShowHint()
    {
        _status.Text = "";
        _status.Visibility = Visibility.Collapsed;
    }
    private void OverviewPanel(StackPanel body)
    {
        int steps = _steps.Count(a => !a.IsGroupMarker);
        body.Children.Add(Heading(_points.Count == 0 ? "No points yet" : $"{_points.Count:N0} {(_points.Count == 1 ? "point" : "points")} · {steps:N0} steps"));
        body.Children.Add(Muted(_points.Count == 0 ? "Right click where the cursor should go and choose Click here, Hold here or Cursor here." : "Pick a point to see what happens there, or open All steps.", new(0, 0, 0, 12)));
        body.Children.Add(ClickDelayField());
        var (start, end) = RouteBuilder.Opening(_steps);
        if (end > start || _points.Count == 0)
        {
            body.Children.Add(Label("Before the first point"));
            Timeline(body, RouteBuilder.Units(_steps, start, end), press: false);
            body.Children.Add(AddButton(AtPoint(-1), -1, "Add a step"));
        }
    }
    private void PickedPanel(StackPanel body)
    {
        body.Children.Add(Heading($"{_picked.Count:N0} points picked"));
        body.Children.Add(Muted("Points " + string.Join(", ", _picked.Order().Select(k => k + 1)), new(0, 0, 0, 12)));
        var group = new Button { Content = "Group them", Padding = new(11, 4, 11, 4), Margin = new(0, 0, 6, 0), ToolTip = "Ctrl+G. Points must sit in the same group, or in none." };
        group.Click += (_, _) => GroupPicked();
        var delete = new Button { Content = "Delete", Padding = new(11, 4, 11, 4), ToolTip = "Delete" };
        delete.SetResourceReference(StyleProperty, "DangerButton");
        delete.Click += (_, _) => DeletePicked();
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { group, delete } });
    }
    /// <summary>A point as a small timeline: how the cursor gets there, what happens once it is there, and the wait before the next point.</summary>
    private void PointPanel(StackPanel body, int k)
    {
        var p = _points[k];
        var move = _steps[p.Index];
        var head = new DockPanel { Margin = new(0, 0, 0, 10) };
        var delete = IconButton("IconTrash", "Delete this point and its steps (Delete)", DeletePicked);
        DockPanel.SetDock(delete, Dock.Right); head.Children.Add(delete);
        head.Children.Add(new StackPanel { Children = { Heading($"Point {k + 1}"), Muted($"at {Where(move)}", new()) } });
        body.Children.Add(head);

        // How the cursor gets there.
        var bar = new Border { HorizontalAlignment = HorizontalAlignment.Left };
        bar.SetResourceReference(StyleProperty, "SegmentBar");
        var jump = new RadioButton { Content = "Jump", GroupName = "Travel", IsChecked = move.GlideMs == 0, ToolTip = "The cursor appears there at once" };
        var glide = new RadioButton { Content = "Glide", GroupName = "Travel", IsChecked = move.GlideMs > 0, ToolTip = "The cursor travels there along a line, like a hand would" };
        foreach (var choice in new[] { jump, glide }) choice.SetResourceReference(StyleProperty, "Segment");
        jump.Checked += (_, _) => { if (!_rebuilding) Attempt(() => SetGlide(k, 0)); };
        glide.Checked += (_, _) => { if (!_rebuilding) Attempt(() => SetGlide(k, DefaultGlideMs)); };
        bar.Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { jump, glide } };
        var travel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 0, 0, 14) };
        travel.Children.Add(new TextBlock { Text = "Cursor", Width = 58, VerticalAlignment = VerticalAlignment.Center });
        travel.Children.Add(bar);
        if (move.GlideMs > 0)
        {
            travel.Children.Add(Muted("in", new(8, 0, 6, 0)));
            travel.Children.Add(Unit(Box(move.GlideMs.ToString(), 56, "How long the glide takes", text => RouteBuilder.Change(_steps, p.Index, a => a.GlideMs = Whole(text, "Glide"))), "ms"));
        }
        body.Children.Add(travel);

        // What happens there, timed from the moment the cursor arrives.
        var units = RouteBuilder.Units(_steps, p.Index + 1, p.End);
        int trailing = RouteBuilder.TrailingWait(_steps, p);
        var inner = trailing >= 0 ? units.Take(units.Count - 1).ToList() : units;
        body.Children.Add(Label("At this point"));
        Timeline(body, inner, press: true);
        body.Children.Add(AddButton(AtPoint(k), k, "Add a step"));

        // The wait before the next point closes the timeline.
        string next = k + 1 < _points.Count ? $"point {k + 2}" : "the next run";
        var then = new DockPanel { Margin = new(0, 14, 0, 0), LastChildFill = true };
        var clock = Icon("IconClock", 12);
        clock.Margin = new(0, 0, 8, 0);
        DockPanel.SetDock(clock, Dock.Left); then.Children.Add(clock);
        if (trailing >= 0)
        {
            int at = trailing;
            var remove = IconButton("IconClose", "Go straight on, without this wait", () => Attempt(() => Change(() => _steps.RemoveAt(at))));
            DockPanel.SetDock(remove, Dock.Right); then.Children.Add(remove);
            then.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Tag = trailing,
                Children =
                {
                    new TextBlock { Text = "Then wait", VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 8, 0) },
                    Box(_steps[trailing].DelayMs.ToString(), 60, "Milliseconds before going on", text => RouteBuilder.Change(_steps, at, a => a.DelayMs = Whole(text, "Wait"))),
                    Muted($"ms → {next}", new(6, 0, 0, 0))
                }
            });
        }
        else
        {
            var add = new Button { Content = "+ Wait", Padding = new(8, 2, 8, 2), ToolTip = $"Wait at point {k + 1} before {next}" };
            add.Click += (_, _) => Attempt(() => EditWait(k));
            DockPanel.SetDock(add, Dock.Right); then.Children.Add(add);
            then.Children.Add(Muted($"Then straight on → {next}", new()));
        }
        body.Children.Add(then);
        if (p.Group >= 0) GroupSection(body, p.Group);
    }
    /// <summary>Rows with the time each one starts at, counted from when the cursor arrives.</summary>
    private void Timeline(StackPanel body, List<StepUnit> units, bool press)
    {
        if (units.Count == 0) { body.Children.Add(Muted("Nothing yet: the cursor only goes here.", new(0, 0, 0, 2))); return; }
        int time = 0;
        bool settled = false;
        for (int u = 0; u < units.Count; u++)
        {
            var unit = units[u];
            var first = _steps[unit.Start];
            // The first click or hold waits for the click delay after moving; waits before it count toward that.
            string? why = null;
            if (press && !settled && first.ActionType is MacroActionType.MouseClick or MacroActionType.MouseDown)
            {
                settled = true;
                if (_settleMs > time)
                {
                    why = $"Starts at {_settleMs} ms because of the click delay after moving" + (time > 0 ? $"; the {time} ms of waits before it count toward that." : ".") + " Change it under All steps.";
                    time = _settleMs;
                }
            }
            int index = u;
            var row = UnitRow(unit, $"+{time:N0} ms",
                u > 0 ? () => Attempt(() => Change(() => RouteBuilder.ShiftUnit(_steps, units, index, -1))) : null,
                u < units.Count - 1 ? () => Attempt(() => Change(() => RouteBuilder.ShiftUnit(_steps, units, index, 1))) : null);
            if (why != null && row.Children[0] is TextBlock timeText)
            {
                timeText.ToolTip = why;
                timeText.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                timeText.Cursor = Cursors.Help;
            }
            body.Children.Add(row);
            time += Enumerable.Range(unit.Start, unit.Length).Sum(i => _steps[i].DurationMs);
        }
    }
    /// <summary>The macro's click delay after moving, which applies to every click and hold after a cursor move.</summary>
    private FrameworkElement ClickDelayField()
    {
        var box = Box(_settleMs.ToString(), 52, "Clicks and holds wait at least this long after the cursor arrives, so games see it there first. Waits before the click count toward it. 0 clicks right away.", text =>
        {
            int ms = Whole(text, "Click delay after moving");
            if (ms > 1000) throw new ArgumentException("Click delay after moving must be between 0 and 1000 ms.");
            _settleMs = ms;
        });
        var row = Field("Click delay", box, "ms after moving");
        row.Margin = new(0, 0, 0, 12);
        row.ColumnDefinitions[0].Width = new(80);
        return row;
    }
    /// <summary>Every step of the macro in order, with when it first plays, groups indented under their name.</summary>
    private void AllStepsPanel(StackPanel body)
    {
        body.Children.Add(ClickDelayField());
        var times = StepGroups.StartTimes(_steps, _settleMs);
        var owner = new int[_steps.Count];
        Array.Fill(owner, -1);
        for (int k = 0; k < _points.Count; k++) for (int i = _points[k].Index; i < _points[k].End; i++) owner[i] = k;
        int depth = 0;
        for (int i = 0; i < _steps.Count;)
        {
            var a = _steps[i];
            if (a.ActionType == MacroActionType.GroupEnd) { depth = Math.Max(0, depth - 1); i++; continue; }
            if (a.ActionType == MacroActionType.GroupStart)
            {
                body.Children.Add(GroupRow(i, depth, StepLayoutConverter.FormatTime(times[i])));
                depth++; i++; continue;
            }
            var unit = new StepUnit(i, RouteBuilder.IsHoldAt(_steps, i, _steps.Count) ? 3 : 1);
            var row = a.ActionType == MacroActionType.MousePosition ? CursorRow(owner[i], StepLayoutConverter.FormatTime(times[i])) : UnitRow(unit, StepLayoutConverter.FormatTime(times[i]), null, null);
            row.Margin = new(depth * 14, 0, 0, 4);
            if (owner[i] >= 0 && _picked.Contains(owner[i])) row.SetResourceReference(Grid.BackgroundProperty, "ListSelectedBrush");
            body.Children.Add(row);
            i += unit.Length;
        }
        if (_steps.Count == 0) body.Children.Add(Muted("No steps yet.", new(0, 0, 0, 2)));
        body.Children.Add(AddButton(() => _steps.Count, -1, "Add a step at the end"));
    }
    private Grid CursorRow(int k, string time)
    {
        var move = _steps[_points[k].Index];
        var row = RowGrid(_points[k].Index);
        AddCell(row, 0, Muted(time, new()));
        var dot = new Grid { Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Left, Children = { new Ellipse { Fill = MoveFill }, new TextBlock { Text = (k + 1).ToString(), FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } } };
        AddCell(row, 1, dot);
        var text = new TextBlock { Text = $"{(move.GlideMs > 0 ? "Glide" : "Cursor")} to {Where(move)}{(move.GlideMs > 0 ? $" in {move.GlideMs:N0} ms" : "")}", TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, ToolTip = "Show this point" };
        Grid.SetColumnSpan(text, 2);
        AddCell(row, 2, text);
        row.MouseLeftButtonUp += (_, _) => Pick([k], fromList: true);
        var delete = IconButton("IconClose", "Delete this point and its steps", () => Attempt(() => Change(() => RouteBuilder.Remove(_steps, _points[k]))));
        AddCell(row, 4, delete);
        ShowOnHover(row, delete);
        return row;
    }
    private Grid GroupRow(int start, int depth, string time)
    {
        var g = _steps[start];
        var row = RowGrid(start);
        row.Margin = new(depth * 14, 4, 0, 4);
        AddCell(row, 0, Muted(time, new()));
        AddCell(row, 1, Icon("IconGroup", 12));
        var text = new TextBlock { Text = $"{g.GroupName}{(g.Runs > 1 ? $" · repeats {g.Runs:N0}×" : "")}{(g.GapMs > 0 ? $" · +{g.GapMs:N0} ms between steps" : "")}", FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, ToolTip = "Show its first point and the group's settings" };
        Grid.SetColumnSpan(text, 2);
        AddCell(row, 2, text);
        row.MouseLeftButtonUp += (_, _) =>
        {
            int first = _points.FindIndex(p => p.Group == start);
            if (first >= 0) Pick([first]);
        };
        var ungroup = IconButton("IconClose", "Ungroup: keep the steps, drop the group", () => Attempt(() => Change(() => RouteBuilder.Ungroup(_steps, start))));
        AddCell(row, 4, ungroup);
        ShowOnHover(row, ungroup);
        return row;
    }
    private void GroupSection(StackPanel body, int start)
    {
        var g = _steps[start];
        var box = new Border { Margin = new(0, 14, 0, 0), Padding = new(10, 8, 6, 8), CornerRadius = new(8), BorderThickness = new(1) };
        box.SetResourceReference(Border.BackgroundProperty, "SurfaceAltBrush");
        box.SetResourceReference(Border.BorderBrushProperty, "HairlineBrush");
        var top = new DockPanel { Margin = new(0, 0, 0, 6) };
        var icon = Icon("IconGroup", 12);
        icon.Margin = new(0, 0, 8, 0);
        DockPanel.SetDock(icon, Dock.Left); top.Children.Add(icon);
        var ungroup = IconButton("IconClose", "Ungroup: keep the steps, drop the group", () => Attempt(() => Change(() => RouteBuilder.Ungroup(_steps, start))));
        DockPanel.SetDock(ungroup, Dock.Right); top.Children.Add(ungroup);
        top.Children.Add(Box(g.GroupName, 150, "Group name", text => RouteBuilder.Change(_steps, start, a => a.Name = text.Trim())));
        int count = _points.Count(p => p.Group == start);
        var settings = new WrapPanel { Margin = new(20, 0, 0, 0) };
        settings.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new(0, 0, 14, 4),
            ToolTip = $"The group's {count:N0} {(count == 1 ? "point plays" : "points play")} this many times in a row before the macro goes on",
            Children =
            {
                new TextBlock { Text = "Repeat", VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 6, 0) },
                Unit(Box(g.Runs.ToString(), 44, "Times in a row", text => RouteBuilder.Change(_steps, start, a => a.Repeat = Whole(text, "Repeat"))), "×")
            }
        });
        settings.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new(0, 0, 0, 4),
            ToolTip = "Added after every step inside the group, and between repeats. Usually 0: Wait steps are clearer.",
            Children =
            {
                new TextBlock { Text = "Extra wait", VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 6, 0) },
                Unit(Box(g.GapMs.ToString(), 44, "Milliseconds after each step in the group", text => RouteBuilder.Change(_steps, start, a => a.GapMs = Whole(text, "Extra wait"))), "ms")
            }
        });
        box.Child = new StackPanel { Children = { top, settings } };
        body.Children.Add(box);
    }
    private Button AddButton(Func<int> at, int point, string text)
    {
        var button = new Button { Padding = new(10, 4, 10, 4), Margin = new(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Also on right click" };
        button.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { Icon("IconPlus", 9), new TextBlock { Text = text, Margin = new(7, 0, 0, 0) } } };
        button.Click += (_, _) =>
        {
            CommitEdits();
            var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
            foreach (var item in AddItems(at, point)) menu.Items.Add(item);
            menu.IsOpen = true;
        };
        return button;
    }

    // ---- Step rows ----

    private static Grid RowGrid(int step)
    {
        var row = new Grid { Tag = step, Background = Brushes.Transparent, Margin = new(0, 0, 0, 4) };
        row.ColumnDefinitions.Add(new() { Width = new(56) });
        row.ColumnDefinitions.Add(new() { Width = new(22) });
        row.ColumnDefinitions.Add(new() { Width = new(64) });
        row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        return row;
    }
    /// <summary>Row tools only show while the pointer is over the row, to keep the list calm.</summary>
    private static void ShowOnHover(FrameworkElement row, UIElement tools)
    {
        tools.Visibility = Visibility.Collapsed;
        row.MouseEnter += (_, _) => tools.Visibility = Visibility.Visible;
        row.MouseLeave += (_, _) => tools.Visibility = Visibility.Collapsed;
    }
    private static void AddCell(Grid row, int column, UIElement cell)
    {
        Grid.SetColumn(cell, column);
        row.Children.Add(cell);
    }
    /// <summary>One row: when it starts, what it is, its value to change, and tools to move or remove it.</summary>
    private Grid UnitRow(StepUnit unit, string time, Action? up, Action? down)
    {
        var a = _steps[unit.Start];
        var row = RowGrid(unit.Start);
        AddCell(row, 0, Muted(time, new()));
        var (icon, label) = unit.IsHold
            ? (a.ActionType == MacroActionType.KeyDown ? ("IconHold", "Hold key") : ("IconHold", "Hold"))
            : a.ActionType switch
            {
                MacroActionType.MouseClick => ("IconMouse", "Click"),
                MacroActionType.MouseDown => ("IconHold", "Press"),
                MacroActionType.MouseUp => ("IconRelease", "Release"),
                MacroActionType.KeyPress => ("IconKeyboard", "Key"),
                MacroActionType.KeyDown => ("IconHold", "Key down"),
                MacroActionType.KeyUp => ("IconRelease", "Key up"),
                MacroActionType.Delay => ("IconClock", "Wait"),
                MacroActionType.MouseMove => ("IconMove", "Move by"),
                MacroActionType.MouseScroll => ("IconScroll", "Scroll"),
                MacroActionType.Text => ("IconText", "Type"),
                _ => ("IconInfo", "Step")
            };
        AddCell(row, 1, Icon(icon, 12));
        AddCell(row, 2, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        AddCell(row, 3, unit.IsHold ? HoldEditor(unit) : Editor(unit.Start, a));
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(6, 0, 0, 0) };
        if (up != null || down != null)
        {
            tools.Children.Add(IconButton("IconArrowUp", "Earlier", up ?? (() => { }), up != null));
            tools.Children.Add(IconButton("IconArrowDown", "Later", down ?? (() => { }), down != null));
        }
        tools.Children.Add(IconButton("IconClose", "Remove", () => Attempt(() => Change(() => _steps.RemoveRange(unit.Start, unit.Length)))));
        AddCell(row, 4, tools);
        ShowOnHover(row, tools);
        return row;
    }
    /// <summary>A hold as one row: what is held, and for how long. Both the press and the release follow.</summary>
    private FrameworkElement HoldEditor(StepUnit unit)
    {
        var a = _steps[unit.Start];
        int press = unit.Start, wait = unit.Start + 1, release = unit.Start + 2;
        FrameworkElement what = a.ActionType == MacroActionType.KeyDown
            ? KeyBox(a.KeyName, name => { RouteBuilder.Change(_steps, press, s => s.KeyName = name); RouteBuilder.Change(_steps, release, s => s.KeyName = name); })
            : ButtonBox(a.MouseButton, button => { RouteBuilder.Change(_steps, press, s => s.MouseButton = button); RouteBuilder.Change(_steps, release, s => s.MouseButton = button); }, 80);
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                what, Muted("for", new(6, 0, 6, 0)),
                Unit(Box(_steps[wait].DelayMs.ToString(), 50, "How long it stays held", text => RouteBuilder.Change(_steps, wait, s => s.DelayMs = Whole(text, "Hold time"))), "ms")
            }
        };
    }
    /// <summary>The value of a step, changed in place. Text fields apply on Enter or when they lose focus.</summary>
    private FrameworkElement Editor(int i, MacroAction a) => a.ActionType switch
    {
        MacroActionType.Delay => Unit(Box(a.DelayMs.ToString(), 70, "Milliseconds", text => RouteBuilder.Change(_steps, i, s => s.DelayMs = Whole(text, "Wait"))), "ms"),
        MacroActionType.MouseClick or MacroActionType.MouseDown or MacroActionType.MouseUp => ButtonBox(a.MouseButton, button => RouteBuilder.Change(_steps, i, s => s.MouseButton = button)),
        MacroActionType.KeyPress or MacroActionType.KeyDown or MacroActionType.KeyUp => KeyBox(a.KeyName, name => RouteBuilder.Change(_steps, i, s => s.KeyName = name)),
        MacroActionType.MouseScroll => Unit(Box(a.ScrollAmount.ToString(), 56, "Notches. Negative scrolls down.", text => RouteBuilder.Change(_steps, i, s => s.ScrollAmount = Whole(text, "Scroll", negative: true))), "notches"),
        MacroActionType.MouseMove => Box($"{a.X}, {a.Y}", 90, "x, y in pixels", text =>
        {
            var parts = text.Split(',');
            if (parts.Length != 2) throw new ArgumentException("Enter the move as x, y. Example: 20, -10.");
            RouteBuilder.Change(_steps, i, s => { s.X = Whole(parts[0], "x", true); s.Y = Whole(parts[1], "y", true); });
        }),
        MacroActionType.Text => Box(a.Text, 150, "Text to type", text => RouteBuilder.Change(_steps, i, s => s.Text = text)),
        _ => Muted(a.DisplayText, new())
    };
    private ComboBox ButtonBox(string value, Action<string> apply, double width = 100)
    {
        var combo = new ComboBox { Width = width, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = Buttons.Select(b => b.Label).ToArray(), SelectedIndex = Math.Max(0, Array.FindIndex(Buttons, b => b.Value == value)) };
        combo.SelectionChanged += (_, _) =>
        {
            if (_rebuilding || combo.SelectedIndex < 0) return;
            string button = Buttons[combo.SelectedIndex].Value;
            _lastButton = button;
            Attempt(() => Change(() => apply(button)));
        };
        return combo;
    }
    private TextBox Box(string value, double width, string tip, Action<string> apply)
    {
        var box = new TextBox { Text = value, Width = width, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = tip, Tag = value };
        void Commit()
        {
            if (_rebuilding || _done || box.Text == (string)box.Tag) return;
            box.Tag = box.Text;
            Attempt(() => Change(() => apply(box.Text)));
        }
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Commit(); } };
        box.LostKeyboardFocus += (_, _) => Commit();
        _editors.Add(Commit);
        return box;
    }
    /// <summary>Click it, then press the key or combination, like W or Ctrl+E.</summary>
    private TextBox KeyBox(string value, Action<string> apply)
    {
        var box = new TextBox { Text = value, Width = 80, IsReadOnly = true, Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Click, then press a key or combination" };
        box.GotKeyboardFocus += (_, _) => box.Text = "Press a key…";
        box.LostKeyboardFocus += (_, _) => { if (!_rebuilding) box.Text = value; };
        box.PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.Tab) return;
            e.Handled = true;
            if (key == Key.Escape) { Keyboard.Focus(this); return; }
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
            string name = KeyName(key, Keyboard.Modifiers);
            Attempt(() => Change(() => apply(name)));
        };
        return box;
    }
    /// <summary>The name a pressed key is stored under, like W, 5, F8 or Ctrl+E.</summary>
    public static string KeyName(Key key, ModifierKeys modifiers)
    {
        string name = key is >= Key.D0 and <= Key.D9 ? ((char)('0' + (key - Key.D0))).ToString() : key.ToString();
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        parts.Add(name);
        return string.Join("+", parts);
    }
    private void CommitEdits()
    {
        foreach (var commit in _editors.ToArray()) commit();
    }
    private void FocusField(int step) => Dispatcher.BeginInvoke(() =>
    {
        var row = Descendants(_panelBody).OfType<FrameworkElement>().FirstOrDefault(g => g.Tag is int i && i == step);
        var field = row == null ? null : Descendants(row).OfType<Control>().FirstOrDefault(c => c is TextBox or ComboBox);
        if (field == null) return;
        field.Focus();
        if (field is TextBox { IsReadOnly: false } text) text.SelectAll();
    }, System.Windows.Threading.DispatcherPriority.Loaded);

    // ---- Keys and finishing ----

    private void Builder_KeyDown(object sender, KeyEventArgs e)
    {
        // Fields keep their own keys; Esc in a text field only leaves it.
        if (Keyboard.FocusedElement is TextBox or ComboBox or ComboBoxItem)
        {
            if (e.Key == Key.Escape && Keyboard.FocusedElement is TextBox { IsReadOnly: false }) { CommitEdits(); Keyboard.Focus(this); e.Handled = true; }
            return;
        }
        var mods = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.Escape when _picked.Count > 0: Pick([]); break;
            case Key.Escape: Done(); break;
            case Key.Delete: DeletePicked(); break;
            case Key.H when mods == ModifierKeys.None: TogglePanel(); break;
            case Key.Z when mods == ModifierKeys.Control: Undo(); break;
            case Key.G when mods == ModifierKeys.Control: GroupPicked(); break;
            case Key.D when mods == ModifierKeys.Control && Current >= 0: Attempt(() => Duplicate(Current)); break;
            case Key.A when mods == ModifierKeys.Control: Pick(Enumerable.Range(0, _points.Count)); break;
            default: return;
        }
        e.Handled = true;
    }
    private void Done()
    {
        CommitEdits();
        Finish(new(_steps, _settleMs));
    }
    private void Finish(BuilderResult? result)
    {
        if (_done) return;
        _done = true;
        Finished?.Invoke(result);
        Close();
    }
    private void Attempt(Action action)
    {
        try { action(); }
        catch (ArgumentException ex) { Refresh(); Say(ex.Message, true); }
    }
    private void Say(string text, bool problem = false)
    {
        if (text.Length == 0) { _statusSticky = false; return; }
        _status.Text = text; _statusSticky = true;
        _status.Visibility = Visibility.Visible;
        _status.SetResourceReference(TextBlock.ForegroundProperty, problem ? "DangerTextBrush" : "SuccessTextBrush");
    }
    private static int Whole(string text, string label, bool negative = false)
    {
        if (!int.TryParse(text.Trim(), out int value) || !negative && value < 0) throw new ArgumentException($"{label} must be a whole number{(negative ? "" : " of 0 or more")}.");
        return value;
    }

    // ---- Building blocks ----

    private static Border Surface(UIElement child, Thickness padding)
    {
        var surface = new Border { Child = child, Padding = padding, CornerRadius = new(10), BorderThickness = new(1) };
        surface.SetResourceReference(Border.BackgroundProperty, "SheetBrush");
        surface.SetResourceReference(Border.BorderBrushProperty, "HairlineBrush");
        surface.SetResourceReference(TextElement.ForegroundProperty, "InkBrush");
        return surface;
    }
    private static Path Icon(string icon, double size) => new()
    {
        Data = (Geometry)Application.Current.FindResource(icon), Width = size, Height = size,
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
        Style = (Style)Application.Current.FindResource("AccentIcon")
    };
    private static Button IconButton(string icon, string tip, Action click, bool enabled = true)
    {
        var button = new Button { Content = new Path { Data = (Geometry)Application.Current.FindResource(icon), Style = (Style)Application.Current.FindResource("Icon"), Width = 9, Height = 9 }, ToolTip = tip, IsEnabled = enabled, Width = 24, Height = 24 };
        button.SetResourceReference(StyleProperty, "IconButton");
        button.Click += (_, _) => click();
        return button;
    }
    private static StackPanel Unit(FrameworkElement field, string unit) => new() { Orientation = Orientation.Horizontal, Children = { field, Muted(unit, new(6, 0, 0, 0)) } };
    private static Grid Field(string label, FrameworkElement field, string? unit = null)
    {
        var row = new Grid { Margin = new(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new() { Width = new(76) });
        row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        FrameworkElement value = unit == null ? field : Unit(field, unit);
        Grid.SetColumn(value, 1); row.Children.Add(value);
        return row;
    }
    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 2) };
    private static TextBlock Label(string text) => new() { Text = text, Margin = new(0, 0, 0, 6), FontWeight = FontWeights.SemiBold, FontSize = 12 };
    private static TextBlock Muted(string text, Thickness margin)
    {
        var label = new TextBlock { Text = text, Margin = margin, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(StyleProperty, "Muted");
        return label;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var next in Descendants(child)) yield return next;
        }
    }
    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        for (var at = d; at != null; at = VisualTreeHelper.GetParent(at)) if (at is T found) return found;
        return null;
    }
    private static T Frozen<T>(T value) where T : Freezable { value.Freeze(); return value; }
}
