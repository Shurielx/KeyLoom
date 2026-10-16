using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Keyloom.Engine;
using Keyloom.Models;
using Keyloom.Services;
using Keyloom.Views;

internal static partial class Program
{
    private static async Task BuilderEngineTests()
    {
        // A press waits for the pointer to settle after a jump, so games see the move first.
        var driver = new TimedDriver();
        await using var executor = new MacroExecutor(driver);
        var p = Profile(MacroBehaviorType.Sequence);
        p.RepeatCount = 1;
        p.Actions.Add(new() { ActionType = MacroActionType.MousePosition, X = 10, Y = 10 });
        p.Actions.Add(new() { ActionType = MacroActionType.MouseClick });
        p.Actions.Add(new() { ActionType = MacroActionType.MouseClick });
        p.Actions.Add(new() { ActionType = MacroActionType.MousePosition, X = 30, Y = 10 });
        p.Actions.Add(new() { ActionType = MacroActionType.Delay, DelayMs = 40 });
        p.Actions.Add(new() { ActionType = MacroActionType.MouseClick });
        await executor.ToggleAsync(p); await Until(() => executor.ActiveProfile == null);
        var e = driver.Events.ToArray();
        double At(string name, int nth = 0) => e.Where(x => x.Name == name).ElementAt(nth).Ms;
        Check(At("MLeft:down") - At("position:10,10") >= MacroProfile.DefaultPointerSettleMs - 2, "a click right after a move waits for the pointer to settle");
        Check(At("MLeft:down", 1) - At("MLeft:up") < MacroProfile.DefaultPointerSettleMs - 5, "a second click without a move does not wait again");
        Check(At("MLeft:down", 2) - At("position:30,10") is >= 38 and < 40 + MacroProfile.DefaultPointerSettleMs - 5, "a Wait step after the move already counts as settling");
        driver.Events.Clear();
        p.PointerSettleMs = 0;
        await executor.ToggleAsync(p); await Until(() => executor.ActiveProfile == null);
        e = driver.Events.ToArray();
        Check(At("MLeft:down") - At("position:10,10") < 10, "with the wait after moving set to 0, a click follows the move right away");
        p.PointerSettleMs = 1001;
        bool tooLong = false;
        try { ProfileValidator.Validate(p); } catch (ArgumentException) { tooLong = true; }
        Check(tooLong && new MacroProfile().PointerSettleMs == MacroProfile.DefaultPointerSettleMs && System.Text.Json.JsonSerializer.Deserialize<MacroProfile>("{}")!.PointerSettleMs == 20,
            "the wait after moving is limited to a second, and older saved macros get the default");
        driver.Events.Clear();
        // Repeat input at one point only settles once.
        var start = Stopwatch.StartNew();
        var plain = Profile(MacroBehaviorType.ClickSpam);
        plain.TargetType = TargetInputType.Mouse; plain.RepeatCount = 5; plain.SpamIntervalMs = 1;
        await executor.ToggleAsync(plain); await Until(() => executor.ActiveProfile == null);
        Check(driver.Events.Count(x => x.Name == "MLeft:down") == 5 && start.ElapsedMilliseconds < 5 * MacroProfile.DefaultPointerSettleMs, "clicking without moving keeps its full speed");

        // Moves without a wait between them all stay on the route, each tied to its step.
        var steps = new List<MacroAction>
        {
            new() { ActionType = MacroActionType.MousePosition, X = 0, Y = 0 },
            new() { ActionType = MacroActionType.MousePosition, X = 100, Y = 0 },
            new() { ActionType = MacroActionType.MouseClick },
            new() { ActionType = MacroActionType.MousePosition, X = 100, Y = 50 }
        };
        var timeline = MacroTimeline.Build(steps);
        Check(timeline.Points.Count == 3 && timeline.Points.Select(x => x.Step).SequenceEqual([0, 1, 3]) && timeline.Bounds == (0, 0, 100, 50), "moves at the same moment all stay on the route");
        Check(timeline.PositionAt(0) == (100, 50), "the planned position at a moment is still the last move made then");

        // A cursor can glide to its spot instead of jumping: playback moves it along the line, and every timing counts it.
        var glide = new List<MacroAction>
        {
            new() { ActionType = MacroActionType.MousePosition, X = 0, Y = 0 },
            new() { ActionType = MacroActionType.MousePosition, X = 100, Y = 40, GlideMs = 100 },
            new() { ActionType = MacroActionType.MouseClick }
        };
        var glided = MacroTimeline.Build(glide);
        Check(glided.Duration == 100 && glided.PositionAt(50) == (50, 20) && glided.PositionAt(100) == (100, 40) && glided.Events[0].Time == 100,
            "the route glides between the spots and clicks when it arrives");
        Check(StepGroups.StartTimes(glide)[2] == 100 && glide[1].DisplayText == "Glide cursor to 100, 40 in 100 ms", "start times count the glide");
        var gliding = Profile(MacroBehaviorType.Sequence);
        gliding.RepeatCount = 1; gliding.PointerSettleMs = 0;
        gliding.Actions = new(glide.Select(a => a.Clone()));
        driver.Events.Clear();
        await executor.ToggleAsync(gliding); await Until(() => executor.ActiveProfile == null);
        var moves = driver.Events.Where(x => x.Name.StartsWith("position:")).ToArray();
        Check(moves.Length >= 5 && moves[^1].Name == "position:100,40" && moves[^1].Ms - moves[0].Ms >= 95 && moves.Skip(1).Take(moves.Length - 2).All(m => m.Name != "position:0,0"),
            "running a glide moves the cursor in steps along the line and ends on the spot");
        bool slow = false;
        try { ProfileValidator.ValidateAction(new() { ActionType = MacroActionType.MousePosition, GlideMs = 60001 }); } catch (ArgumentException) { slow = true; }
        Check(slow, "a glide is limited to a minute");

        // The builder reads a sequence as points, each with the steps that follow it.
        var built = new List<MacroAction>
        {
            new() { ActionType = MacroActionType.KeyPress, KeyName = "Q" },
            new() { ActionType = MacroActionType.MousePosition, X = 1, Y = 1 },
            new() { ActionType = MacroActionType.MouseClick, MouseButton = "Right" },
            new() { ActionType = MacroActionType.KeyPress, KeyName = "E" },
            new() { ActionType = MacroActionType.Delay, DelayMs = 200 },
            new() { ActionType = MacroActionType.GroupStart, Name = "Rocks", Repeat = 3 },
            new() { ActionType = MacroActionType.MousePosition, X = 2, Y = 2 },
            new() { ActionType = MacroActionType.GroupEnd }
        };
        var points = RouteBuilder.Points(built);
        Check(points.Count == 2 && points[0] == new RoutePoint(1, 5, -1) && points[1] == new RoutePoint(6, 7, 5) && RouteBuilder.Opening(built) == (0, 1),
            "points own the steps up to the next point or group edge, and steps before the first point stand apart");
        Check(RouteBuilder.WaitMs(built, points[0]) == 200, "a point's waits add up");
        var original = built[4];
        RouteBuilder.Change(built, 4, a => a.DelayMs = 450);
        Check(built[4].DelayMs == 450 && original.DelayMs == 200, "changing a step swaps in a copy, so undo copies stay intact");
        bool rejected = false;
        try { RouteBuilder.Change(built, 4, a => a.DelayMs = 0); } catch (ArgumentException) { rejected = true; }
        Check(rejected && built[4].DelayMs == 450, "an invalid value is rejected and leaves the step as it was");
        RouteBuilder.AddTo(built, RouteBuilder.Points(built)[1], RouteBuilder.Hold("Left", 120));
        Check(built.Skip(7).Take(4).Select(a => a.ActionType).SequenceEqual([MacroActionType.MouseDown, MacroActionType.Delay, MacroActionType.MouseUp, MacroActionType.GroupEnd]) && built[8].DelayMs == 120,
            "a hold is a press, a wait and a release, added inside the point's group");
        RouteBuilder.AddTo(built, null, RouteBuilder.HoldKey("W"));
        Check(built.Take(4).Select(a => a.KeyName).SequenceEqual(["Q", "W", "Space", "W"]) && built[1].ActionType == MacroActionType.KeyDown && built[3].ActionType == MacroActionType.KeyUp,
            "steps for before the first point go before it");
        var opening = RouteBuilder.Units(built, 0, RouteBuilder.Opening(built).End);
        Check(opening.Count == 2 && opening[1] is { Start: 1, IsHold: true }, "a press, a wait and its release read as one hold");
        Check(RouteBuilder.TrailingWait(built, RouteBuilder.Points(built)[0]) == 7, "the wait a point ends with is the wait before the next point");
        RouteBuilder.AddTo(built, RouteBuilder.Points(built)[0], [new() { ActionType = MacroActionType.MouseClick }]);
        Check(built[7].ActionType == MacroActionType.MouseClick && built[8].ActionType == MacroActionType.Delay, "steps added to a point go before that wait, so it stays the wait before the next point");
        built.RemoveAt(7);
        var units = RouteBuilder.Units(built, 5, 8);
        Check(RouteBuilder.ShiftUnit(built, units, 1, 1) && built[7].KeyName == "E" && built[6].ActionType == MacroActionType.Delay, "rows swap with their neighbour");
        Check(!RouteBuilder.ShiftUnit(built, RouteBuilder.Units(built, 5, 8), 2, 1), "the last row can't move further down");
        RouteBuilder.ShiftUnit(built, RouteBuilder.Units(built, 0, 4), 1, -1);
        Check(built.Take(4).Select(a => a.ActionType).SequenceEqual([MacroActionType.KeyDown, MacroActionType.Delay, MacroActionType.KeyUp, MacroActionType.KeyPress]), "a hold moves as one row");
        RouteBuilder.ShiftUnit(built, RouteBuilder.Units(built, 0, 4), 0, 1);
        RouteBuilder.ShiftUnit(built, RouteBuilder.Units(built, 5, 8), 1, 1);
        int added = RouteBuilder.AddPoint(built, RouteBuilder.Points(built)[0], new() { ActionType = MacroActionType.MousePosition, X = 9, Y = 9 }, [new() { ActionType = MacroActionType.MouseClick, MouseButton = "Middle" }]);
        Check(added == 8 && built[9].MouseButton == "Middle" && built[10].ActionType == MacroActionType.GroupStart, "a new point goes after the steps of the point before it");
        var three = RouteBuilder.Points(built);
        RouteBuilder.Remove(built, three[1]);
        Check(built.Count == 14 && RouteBuilder.Points(built).Count == 2, "removing a point takes its steps with it");

        // Grouping and ungrouping points.
        var route = new List<MacroAction>();
        for (int i = 0; i < 3; i++) RouteBuilder.AddPoint(route, null, new() { ActionType = MacroActionType.MousePosition, X = i, Y = i }, [new() { ActionType = MacroActionType.MouseClick }]);
        var all = RouteBuilder.Points(route);
        int groupAt = RouteBuilder.Group(route, [all[0], all[1]], "Farm");
        Check(groupAt == 0 && route[0] is { ActionType: MacroActionType.GroupStart, Name: "Farm" } && route[5].ActionType == MacroActionType.GroupEnd && RouteBuilder.Points(route)[1].Group == 0,
            "points side by side group into one group");
        var mixed = RouteBuilder.Points(route);
        rejected = false;
        try { RouteBuilder.Group(route, [mixed[1], mixed[2]], "Bad"); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "points from different groups can't be grouped together");
        RouteBuilder.Ungroup(route, 0);
        Check(route.Count == 6 && route.All(a => !a.IsGroupMarker), "ungrouping removes only the group edges");
        Check(MacroBuilderWindow.KeyName(Key.W, ModifierKeys.None) == "W" && MacroBuilderWindow.KeyName(Key.D5, ModifierKeys.None) == "5"
            && MacroBuilderWindow.KeyName(Key.E, ModifierKeys.Control | ModifierKeys.Shift) == "Ctrl+Shift+E" && KeyResolver.Resolve(MacroBuilderWindow.KeyName(Key.F8, ModifierKeys.None)).Length == 1,
            "pressed keys are stored under names the player understands");

        // Window positions go back and forth between the screen and the window.
        var windows = new BuilderWindows();
        var target = new MacroProfile { Window = new() { ProcessName = "game", ClientWidth = 400, ClientHeight = 300 }, ScaleWithWindow = true };
        var space = new RouteSpace(windows, (IntPtr)7, target);
        var step = new MacroAction { ActionType = MacroActionType.MousePosition, Space = CoordinateSpace.Window, X = 200, Y = 150 };
        Check(space.ToScreen(step) == (1400, 400), "window points resolve through the scaled window");
        Check(space.FromScreen(CoordinateSpace.Window, 1400, 400) == (200, 150) && space.SpaceAt(1400, 400) == CoordinateSpace.Window && space.SpaceAt(10, 10) == CoordinateSpace.Screen, "screen points map back into the window, and outside it stay on the screen");
    }
    private static void BuilderUiTests()
    {
        // The backdrop closes a sheet on release only, and keeps that click from reaching what is underneath.
        var sheets = new SheetLayer();
        var host = new Window { Width = 600, Height = 400, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false, Content = new Grid { Children = { sheets } } };
        host.Show(); Pump(50);
        var sheet = sheets.ShowAsync(new Border { Width = 200, Height = 100 }); Pump(250);
        var scrim = (Border)((Grid)sheets.Children[0]).Children[0];
        var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
        scrim.RaiseEvent(down);
        Check(sheets.IsOpen && down.Handled, "pressing the backdrop keeps the sheet open until release");
        var up = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent };
        scrim.RaiseEvent(up);
        Check(!sheets.IsOpen && sheet.IsCompleted && up.Handled, "releasing it closes the sheet and swallows the click");
        var stray = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent };
        _ = sheets.ShowAsync(new Border { Width = 200, Height = 100 }); Pump(50);
        ((Border)((Grid)sheets.Children[^1]).Children[0]).RaiseEvent(stray);
        Check(sheets.IsOpen, "a release whose press began elsewhere, like on the button that opened it, does not close the sheet");
        sheets.CloseTop(false); Pump(200);
        host.Close();

        // Picking a step rings its spot on the route.
        var p = Profile(MacroBehaviorType.Sequence);
        p.Actions.Add(new() { ActionType = MacroActionType.MousePosition, X = 10, Y = 10 });
        p.Actions.Add(new() { ActionType = MacroActionType.MouseClick });
        p.Actions.Add(new() { ActionType = MacroActionType.MousePosition, X = 300, Y = 200 });
        p.Actions.Add(new() { ActionType = MacroActionType.MouseClick });
        var editor = new MacroEditorView(p);
        var steps = (ListBox)editor.FindName("ActionsListBox");
        var thumbnail = (PathPreview)editor.FindName("Thumbnail");
        steps.SelectedIndex = 2;
        Check(thumbnail.Highlight.SequenceEqual([2]), "picking a move rings its point on the route");
        steps.SelectedIndex = 1;
        Check(thumbnail.Highlight.SequenceEqual([1]), "picking a click rings where it clicks");
        Render(editor, 800, 760, "editor-route-focus.png");

        // The builder hands its steps back to the editor as one undoable change.
        var builderOn = AppSettings.Current.MacroBuilder;
        Check(((FrameworkElement)editor.FindName("BuilderButton")).Visibility == (builderOn ? Visibility.Visible : Visibility.Collapsed), "Build on screen follows its own experimental setting");
        var builder = new MacroBuilderWindow("Test", Items(steps), new RouteSpace(new BuilderWindows(), IntPtr.Zero, p), new Int32Rect(-20000, -20000, 1280, 800)) { ShowActivated = false };
        BuilderResult? result = null;
        builder.Finished += s => result = s;
        builder.Show(); Pump(150);
        MacroAction[] Click = [new() { ActionType = MacroActionType.MouseClick, MouseButton = "Left" }];
        List<MacroAction> Built() => (List<MacroAction>)Field(builder, "_steps");
        List<int> Picked() => (List<int>)Field(builder, "_picked");
        Invoke(builder, "AddPointAt", new Point(500, 400), Click);
        Check(RouteBuilder.Points(Built()).Count == 3 && Picked().SequenceEqual([2]), "Click here adds a point after the last one and picks it");
        Pump(100); RenderShown((FrameworkElement)builder.Content, "builder.png");
        Invoke(builder, "EditWait", 2);
        var last = RouteBuilder.Points(Built())[2];
        Check(Built()[last.End - 1] is { ActionType: MacroActionType.Delay, DelayMs: RouteBuilder.DefaultWaitMs }, "the wait on a point's line adds a wait when it has none");
        Invoke(builder, "SetGlide", 2, 250);
        Check(Built()[RouteBuilder.Points(Built())[2].Index].GlideMs == 250, "a point can glide there instead of jumping");
        Pump(100); RenderShown((FrameworkElement)builder.Content, "builder-point.png");
        ((RadioButton)Field(builder, "_allTab")).IsChecked = true; Pump(100);
        RenderShown((FrameworkElement)builder.Content, "builder-all.png");
        ((RadioButton)Field(builder, "_pointTab")).IsChecked = true;
        Invoke(builder, "Undo"); Invoke(builder, "Undo"); Invoke(builder, "Undo");
        Check(RouteBuilder.Points(Built()).Count == 2, "undo in the builder takes changes back one by one");
        Invoke(builder, "Pick", new List<int> { 0, 1 }, false);
        Invoke(builder, "GroupPicked");
        Check(Built()[0] is { ActionType: MacroActionType.GroupStart } && RouteBuilder.Points(Built()).All(x => x.Group == 0) && Picked().SequenceEqual([0]),
            "picked points can be grouped, and the first one then shows the group's settings");
        Pump(100); RenderShown((FrameworkElement)builder.Content, "builder-group.png");
        Invoke(builder, "Undo");
        Invoke(builder, "Pick", new List<int>(), false);
        Invoke(builder, "AddPointAt", new Point(640, 100), Click);
        double scale = VisualTreeHelper.GetDpi(builder).DpiScaleX;
        int expectX = (int)Math.Round(-20000 + 640 * scale), expectY = (int)Math.Round(-20000 + 100 * scale);
        Invoke(builder, "Done");
        Pump(50);
        Check(result != null && RouteBuilder.Points(result.Steps).Count == 3 && result.ClickDelayMs == MacroProfile.DefaultPointerSettleMs, "Done hands the new steps back, with the click delay");
        Invoke(editor, "ApplyBuilt", result!);
        Check(Items(steps).Length == 6 && Items(steps)[4] is { ActionType: MacroActionType.MousePosition, Space: CoordinateSpace.Screen } added && added.X == expectX && added.Y == expectY,
            "the editor takes the built steps, with the screen position of the new point");
        Invoke(editor, "Undo");
        Check(Items(steps).Length == 4, "one Ctrl+Z brings back the steps from before the builder");

        // A point like a farming loop: a short wait, a click, then the wait before the next point, inside a group.
        var farm = new List<MacroAction>
        {
            new() { ActionType = MacroActionType.GroupStart, Name = "Rocks", Repeat = 3 },
            new() { ActionType = MacroActionType.MousePosition, X = 200, Y = 200 },
            new() { ActionType = MacroActionType.Delay, DelayMs = 10 },
            new() { ActionType = MacroActionType.MouseClick },
            new() { ActionType = MacroActionType.Delay, DelayMs = 150 },
            new() { ActionType = MacroActionType.MousePosition, X = 400, Y = 260, GlideMs = 200 },
            new() { ActionType = MacroActionType.MouseDown }, new() { ActionType = MacroActionType.Delay, DelayMs = 300 }, new() { ActionType = MacroActionType.MouseUp },
            new() { ActionType = MacroActionType.KeyPress, KeyName = "E" },
            new() { ActionType = MacroActionType.GroupEnd }
        };
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            ThemeManager.Apply(theme); Pump();
            var look = new MacroBuilderWindow("Macro 2", farm, new RouteSpace(new BuilderWindows(), IntPtr.Zero, p), new Int32Rect(-20000, -20000, 1280, 800)) { ShowActivated = false };
            BuilderResult? looked = null;
            look.Finished += s => looked = s;
            look.Show(); Pump(150);
            Invoke(look, "Pick", new List<int> { 0 }, false); Pump(150);
            var body = (ContentControl)Field(look, "_panelBody");
            var times = Descendants<TextBlock>((DependencyObject)body.Content).Select(t => t.Text).Where(t => t.StartsWith('+')).ToList();
            Check(times.SequenceEqual(["+0 ms", "+20 ms"]), "the point timeline shows the click at the click delay, with the wait before it counting toward it");
            RenderShown((FrameworkElement)look.Content, $"builder-farm-{theme.ToString().ToLowerInvariant()}.png");
            Invoke(look, "Pick", new List<int> { 1 }, false); Pump(150);
            RenderShown((FrameworkElement)look.Content, $"builder-hold-{theme.ToString().ToLowerInvariant()}.png");
            ((RadioButton)Field(look, "_allTab")).IsChecked = true; Pump(150);
            RenderShown((FrameworkElement)look.Content, $"builder-all-{theme.ToString().ToLowerInvariant()}.png");
            var delay = Descendants<TextBox>((DependencyObject)((ContentControl)Field(look, "_panelBody")).Content).First();
            delay.Text = "35";
            Invoke(look, "Done"); Pump(50);
            Check(looked is { ClickDelayMs: 35 } && looked.Steps.Count == farm.Count, "the click delay can be changed in the builder and comes back with the steps");
        }
        ThemeManager.Apply(AppTheme.Light); Pump();
        static MacroAction[] Items(ListBox list) => list.Items.Cast<MacroAction>().ToArray();
    }
    /// <summary>Renders a shown window's content as laid out, over a stand-in for a game, without laying it out again.</summary>
    private static void RenderShown(FrameworkElement root, string name)
    {
        int width = (int)root.ActualWidth, height = (int)root.ActualHeight;
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var game = new DrawingVisual();
        using (var drawing = game.RenderOpen()) drawing.DrawRectangle(new LinearGradientBrush(Color.FromRgb(70, 120, 60), Color.FromRgb(120, 160, 200), 70), null, new Rect(0, 0, width, height));
        image.Render(game); image.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        string folder = System.IO.Path.Combine(AppContext.BaseDirectory, "screenshots"); System.IO.Directory.CreateDirectory(folder);
        using var stream = System.IO.File.Create(System.IO.Path.Combine(folder, name)); encoder.Save(stream);
    }
    private sealed class TimedDriver : IInputDriver
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        public ConcurrentQueue<(string Name, double Ms)> Events { get; } = new();
        private void Add(string name) => Events.Enqueue((name, _clock.Elapsed.TotalMilliseconds));
        public void Key(ushort key, bool down) => Add($"K{key}:{(down ? "down" : "up")}");
        public void Mouse(string button, bool down) => Add($"M{button}:{(down ? "down" : "up")}");
        public void Move(int x, int y) => Add($"move:{x},{y}");
        public void Position(int x, int y) => Add($"position:{x},{y}");
        public void Scroll(int amount) => Add($"scroll:{amount}");
        public void Character(char value, bool down) => Add($"char:{(int)value}:{down}");
    }
    /// <summary>A target window whose client area starts at 1000, 100 and is twice the size it was picked at.</summary>
    private sealed class BuilderWindows : IWindowSystem
    {
        public IntPtr Find(WindowTarget target) => (IntPtr)7;
        public bool IsAlive(IntPtr window) => true;
        public (int X, int Y) ClientToScreen(IntPtr window, int x, int y) => (1000 + x, 100 + y);
        public (int Width, int Height) ClientSize(IntPtr window) => (800, 600);
        public (int X, int Y) Cursor() => (0, 0);
    }
}
