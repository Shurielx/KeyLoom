using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Keyloom.Engine;
using Keyloom.Models;
using Keyloom.Recording;
using Keyloom.Services;
using Keyloom.Views;

internal static partial class Program
{
    private static readonly WindowTarget TestTarget = new() { ProcessName = "game", ClassName = "GameWindow", Title = "Game", ClientWidth = 800, ClientHeight = 600 };
    private static async Task FeatureEngineTests()
    {
        // Window-relative positions follow the window and can scale with its size.
        var windows = new FakeWindows { Origin = (1000, 200), Size = (800, 600), CursorAt = (50, 60) };
        var standard = new FakeDriver(); var background = new FakeDriver();
        await using var executor = new MacroExecutor(standard, windows, _ => background);
        var errors = new ConcurrentQueue<string>(); executor.ExecutionFailed += errors.Enqueue;
        var p = Profile(MacroBehaviorType.Sequence); p.RepeatCount = 1; p.Window = TestTarget;
        p.Actions.Add(new() { ActionType = MacroActionType.MousePosition, Space = CoordinateSpace.Window, X = 100, Y = 50 });
        p.Actions.Add(new() { ActionType = MacroActionType.MousePosition, Space = CoordinateSpace.Start, X = 5, Y = -5 });
        p.Actions.Add(new() { ActionType = MacroActionType.MousePosition, X = 7, Y = 8 });
        await executor.ToggleAsync(p); await Until(() => executor.ActiveProfile == null);
        Check(standard.Events.SequenceEqual(new[] { "position:1100,250", "position:55,55", "position:7,8" }), "window, start-point and screen positions resolve at run time");
        standard.Events.Clear();
        windows.Size = (1600, 1200); p.ScaleWithWindow = true;
        await executor.ToggleAsync(p); await Until(() => executor.ActiveProfile == null);
        Check(standard.Events.First() == "position:1200,300", "window positions scale proportionally when enabled");
        standard.Events.Clear(); windows.Size = (800, 600); p.ScaleWithWindow = false;

        // Background mode sends only through the per-window driver.
        var repeat = Profile(); repeat.TargetType = TargetInputType.Mouse; repeat.RepeatCount = 2; repeat.Window = TestTarget;
        repeat.ExecutionMode = ExecutionMode.Background; repeat.UsePoint = true; repeat.PointX = 30; repeat.PointY = 40;
        await executor.ToggleAsync(repeat); await Until(() => executor.ActiveProfile == null);
        Check(standard.Events.IsEmpty && background.Events.Count(e => e == "position:1030,240") == 2 && background.Events.Count(e => e == "MLeft:up") == 2,
            "background mode clicks the chosen point through the window driver only");
        Check(repeat.BehaviorSummary.Contains("background") && repeat.BehaviorSummary.Contains("30, 40"), "macro list summarizes point and background mode");

        bool rejected = false; windows.Present = false;
        try { await executor.ToggleAsync(repeat); } catch (ArgumentException ex) { rejected = ex.Message.Contains("not found"); }
        Check(rejected && executor.ActiveProfile == null, "missing target window is reported before running");
        windows.Present = true;
        var loop = Profile(MacroBehaviorType.Sequence); loop.RepeatCount = 0; loop.Window = TestTarget; loop.ExecutionMode = ExecutionMode.Background;
        loop.Actions.Add(new() { ActionType = MacroActionType.MouseClick });
        loop.Actions.Add(new() { ActionType = MacroActionType.Delay, DelayMs = 5 });
        background.Events.Clear();
        await executor.ToggleAsync(loop); await Until(() => background.Events.Count > 4);
        windows.Alive = false;
        await Until(() => executor.ActiveProfile == null);
        Check(errors.Any(e => e.Contains("closed")), "closing the target window stops the macro with a message");
        windows.Alive = true;

        var invalid = Profile(); invalid.ExecutionMode = ExecutionMode.Background;
        Check(Throws(() => ProfileValidator.Validate(invalid)), "background mode without a target window is rejected");
        var startInBackground = Profile(MacroBehaviorType.Sequence); startInBackground.Window = TestTarget; startInBackground.ExecutionMode = ExecutionMode.Background;
        startInBackground.Actions.Add(new() { ActionType = MacroActionType.MousePosition, Space = CoordinateSpace.Start });
        Check(Throws(() => ProfileValidator.Validate(startInBackground)), "start-relative positions are rejected in background mode");
        var windowStep = Profile(MacroBehaviorType.Sequence);
        windowStep.Actions.Add(new() { ActionType = MacroActionType.MousePosition, Space = CoordinateSpace.Window });
        Check(Throws(() => ProfileValidator.Validate(windowStep)), "window positions require a chosen window");

        // Quick switch: focus moves to the target for each burst and comes back while the macro waits.
        var focus = new FakeFocus { Current = (IntPtr)7 };
        var switched = new FakeDriver();
        await using (var quickExecutor = new MacroExecutor(new FakeDriver(), windows, _ => background, w => new FocusSwitchDriver(switched, w, windows, focus)))
        {
            var quick = Profile(MacroBehaviorType.Sequence); quick.RepeatCount = 1; quick.Window = TestTarget; quick.ExecutionMode = ExecutionMode.QuickSwitch;
            quick.Actions.Add(new() { ActionType = MacroActionType.MousePosition, Space = CoordinateSpace.Window, X = 30, Y = 40 });
            quick.Actions.Add(new() { ActionType = MacroActionType.MouseClick });
            quick.Actions.Add(new() { ActionType = MacroActionType.Delay, DelayMs = 5 });
            quick.Actions.Add(new() { ActionType = MacroActionType.KeyPress, KeyName = "E" });
            await quickExecutor.ToggleAsync(quick); await Until(() => quickExecutor.ActiveProfile == null);
            Check(focus.Log.SequenceEqual(new[] { "activate:42", "cursor:5,5", "activate:7", "activate:42", "cursor:5,5", "activate:7" }),
                "quick switch brings the target forward per burst and gives focus and pointer back");
            Check(switched.Events.Contains("position:1030,240") && switched.Events.Contains("MLeft:up") && switched.Events.Contains("K69:up"),
                "quick switch sends real input at the chosen point");
            Check(quick.BehaviorSummary.Contains("quick switch"), "macro list summarizes quick switch mode");
            focus.Log.Clear(); focus.Current = (IntPtr)7;
            quick.RestoreCursor = false;
            await quickExecutor.ToggleAsync(quick); await Until(() => quickExecutor.ActiveProfile == null);
            Check(focus.Log.SequenceEqual(new[] { "activate:42", "activate:7", "activate:42", "activate:7" }) && quick.Snapshot().RestoreCursor == false,
                "with put the cursor back off, quick switch still gives focus back but leaves the cursor where it clicked");
        }
        var quickHold = Profile(MacroBehaviorType.PressAndHold); quickHold.Window = TestTarget; quickHold.ExecutionMode = ExecutionMode.QuickSwitch;
        Check(Throws(() => ProfileValidator.Validate(quickHold)), "hold is rejected in quick switch mode");

        // Recording post-processing.
        var recorded = new List<MacroAction>();
        for (int i = 0; i <= 50; i++)
        {
            recorded.Add(new() { ActionType = MacroActionType.Delay, DelayMs = 16 });
            recorded.Add(new() { ActionType = MacroActionType.MousePosition, X = 100 + i * 4, Y = 300 });
        }
        recorded.Add(new() { ActionType = MacroActionType.MouseDown });
        recorded.Add(new() { ActionType = MacroActionType.Delay, DelayMs = 30 });
        recorded.Add(new() { ActionType = MacroActionType.MousePosition, X = 301, Y = 300 });
        recorded.Add(new() { ActionType = MacroActionType.MouseUp });
        var simple = RecordingTransform.Simplify(recorded);
        int before = recorded.Count(a => a.ActionType == MacroActionType.MousePosition), after = simple.Count(a => a.ActionType == MacroActionType.MousePosition);
        Check(after < before / 2 && simple.Sum(a => a.ActionType == MacroActionType.Delay ? a.DelayMs : 0) == recorded.Sum(a => a.ActionType == MacroActionType.Delay ? a.DelayMs : 0),
            $"path simplification keeps total timing ({before} → {after} positions)");
        int down = simple.ToList().FindIndex(a => a.ActionType == MacroActionType.MouseDown);
        Check(simple[down - 1].X == 300 && simple.Any(a => a.X == 301) && simple.Last().ActionType == MacroActionType.MouseUp, "simplification keeps the exact position at press and release");
        var gaps = new List<int>(); int gap = 0;
        foreach (var a in simple.Take(down)) { if (a.ActionType == MacroActionType.Delay) gap += a.DelayMs; else { gaps.Add(gap); gap = 0; } }
        Check(gaps.Max() <= 48, "simplified paths still sample at least every ~40 ms so playback glides");
        var curve = Enumerable.Range(0, 40).SelectMany(i => new MacroAction[] { new() { ActionType = MacroActionType.Delay, DelayMs = 1 }, new() { ActionType = MacroActionType.MousePosition, X = i * 10, Y = (int)(Math.Sin(i / 4.0) * 100) } }).ToList();
        Check(RecordingTransform.Simplify(curve).Count(a => a.ActionType == MacroActionType.MousePosition) > 10, "curves keep enough points to stay within tolerance");
        var mapped = RecordingTransform.ToSpace(new MacroAction[] { new() { ActionType = MacroActionType.MousePosition, X = 1100, Y = 250 }, new() { ActionType = MacroActionType.KeyPress } }, CoordinateSpace.Window, 1000, 200);
        Check(mapped[0].X == 100 && mapped[0].Y == 50 && mapped[0].Space == CoordinateSpace.Window && mapped[1].Space == CoordinateSpace.Screen, "recordings convert to window-relative positions");

        // Timeline for preview.
        var steps = new MacroAction[]
        {
            new() { ActionType = MacroActionType.KeyPress, KeyName = "E" },
            new() { ActionType = MacroActionType.MousePosition, X = 10, Y = 20 },
            new() { ActionType = MacroActionType.Delay, DelayMs = 100 },
            new() { ActionType = MacroActionType.MouseMove, X = 5, Y = 5 },
            new() { ActionType = MacroActionType.MouseClick },
            new() { ActionType = MacroActionType.Delay, DelayMs = 300 }
        };
        var timeline = MacroTimeline.Build(steps);
        Check(timeline.Duration == 400 && timeline.PositionAt(50) == (10, 20) && timeline.PositionAt(100) == (15, 25), "timeline tracks positions and relative moves over time");
        Check(timeline.DelayAt(250) is { Progress: 0.5 } && timeline.DelayAt(100) is { Delay.Step: 5 } && timeline.StepAt(50) == 2, "timeline reports running delays and steps");
        Check(timeline.Events[0] is { X: 10, Y: 20 } && timeline.Events[1] is { X: 15, Kind: TimelineEventKind.Click }, "inputs are placed at the pointer position at their time");
        var big = new List<MacroAction>();
        for (int i = 0; i < ProfileValidator.MaxActions / 2; i++)
        {
            big.Add(new() { ActionType = MacroActionType.MousePosition, X = i % 1920, Y = i % 1080 });
            big.Add(new() { ActionType = MacroActionType.Delay, DelayMs = 8 });
        }
        var clock = Stopwatch.StartNew();
        var large = MacroTimeline.Build(big);
        for (int i = 0; i < 1000; i++) { large.PositionAt(i * 80); large.DelayAt(i * 80); large.StepAt(i * 80); }
        Check(clock.ElapsedMilliseconds < 500, $"20,000-step timeline builds and answers 3,000 queries in {clock.ElapsedMilliseconds} ms");

        Check(AppVersion.Format("0.1-dev-3+abc123") == "0.1-dev-3" && AppVersion.Display == "0.2-dev-1", "version is shown as 0.2-dev-1 without build metadata");
        string settingsPath = Path.Combine(Path.GetTempPath(), "keyloom-settings-" + Guid.NewGuid().ToString("N") + ".json");
        new AppSettings { ShowExperimental = true }.Save(settingsPath);
        Check(AppSettings.Load(settingsPath).ShowExperimental, "experimental setting round-trips");
        File.WriteAllText(settingsPath, "{broken"); Check(!AppSettings.Load(settingsPath).ShowExperimental, "unreadable settings fall back to defaults");
        File.Delete(settingsPath);

        string folder = Path.Combine(Path.GetTempPath(), "keyloom-target-" + Guid.NewGuid().ToString("N"));
        var storage = new ProfileStorageService(folder);
        storage.SaveProfiles(new[] { p, repeat });
        var loaded = storage.LoadProfiles().Profiles;
        Check(loaded[0].Window == TestTarget && loaded[0].Actions[0].Space == CoordinateSpace.Window && loaded[1].ExecutionMode == ExecutionMode.Background && loaded[1].PointY == 40,
            "target window, point, run mode and coordinate spaces round-trip through storage");
        Directory.Delete(folder, true);
    }
    private static void Save(MacroEditorView editor)
    {
        bool saved = false;
        void OnSaved() => saved = true;
        editor.Saved += OnSaved;
        Invoke(editor, "Save_Click", editor, new RoutedEventArgs());
        editor.Saved -= OnSaved;
        Check(saved && ((TextBlock)editor.FindName("ErrorText")).Text.Length == 0, "editor saves without validation errors");
    }
    private static bool Throws(Action action)
    {
        try { action(); return false; } catch (ArgumentException) { return true; }
    }
    private static void FeatureUiTests()
    {
        AppSettings.Load(Path.Combine(Path.GetTempPath(), "keyloom-missing-" + Guid.NewGuid().ToString("N") + ".json"));
        var p = Profile(MacroBehaviorType.Sequence); p.Window = TestTarget;
        p.Actions = new()
        {
            new() { ActionType = MacroActionType.MousePosition, X = 200, Y = 150 },
            new() { ActionType = MacroActionType.Delay, DelayMs = 120 },
            new() { ActionType = MacroActionType.MousePosition, X = 420, Y = 260 },
            new() { ActionType = MacroActionType.MouseClick },
            new() { ActionType = MacroActionType.Delay, DelayMs = 200 },
            new() { ActionType = MacroActionType.MousePosition, X = 300, Y = 380 },
            new() { ActionType = MacroActionType.MouseDown },
            new() { ActionType = MacroActionType.Delay, DelayMs = 150 },
            new() { ActionType = MacroActionType.MousePosition, X = 520, Y = 420 },
            new() { ActionType = MacroActionType.MouseUp }
        };
        var shown = p.Snapshot(); shown.Name = "Collect and drag"; shown.Window = null;
        var editor = new MacroEditorView(p);
        Check(((FrameworkElement)editor.FindName("RunModePanel")).Visibility == Visibility.Collapsed, "run mode stays hidden until experimental features are enabled");
        var steps = (ListBox)editor.FindName("ActionsListBox");
        var number = new StepLayoutConverter();
        string NumberOf(object step) => (string)number.Convert([step, steps.ItemsSource, editor.StepsRevision], typeof(string), "Number", System.Globalization.CultureInfo.InvariantCulture);
        var third = steps.Items[2]; int revision = editor.StepsRevision;
        steps.SelectedIndex = 2; Invoke(editor, "MoveSteps", new List<int> { 2 }, -1);
        Check(NumberOf(third) == "2" && NumberOf(steps.Items[2]) == "3" && editor.StepsRevision != revision, "step numbers follow moves");
        Invoke(editor, "Undo");
        Check(steps.Items[2] is MacroAction { ActionType: MacroActionType.MousePosition, X: 420 } && steps.SelectedItems.Count == 1, "undo puts a moved step back");
        var original = p.Actions.Select(a => a.DisplayText).ToArray();
        string[] Shown() => steps.Items.Cast<MacroAction>().Select(a => a.DisplayText).ToArray();

        // Pick every wait, change them together, then undo and redo.
        foreach (var w in steps.Items.Cast<MacroAction>().Where(a => a.ActionType == MacroActionType.Delay).ToList()) steps.SelectedItems.Add(w);
        steps.SelectedItems.Remove(steps.Items[2]);
        Check(editor.IsMultiSelect && ((FrameworkElement)editor.FindName("BulkEditor")).Visibility == Visibility.Visible
            && ((FrameworkElement)editor.FindName("StepEditor")).Visibility == Visibility.Collapsed && ((TextBlock)editor.FindName("BulkTitle")).Text.StartsWith("3"),
            "picking several steps opens the bulk editor");
        var bulkWait = (TextBox)editor.FindName("BulkWaitTextBox");
        int[] Waits() => steps.Items.Cast<MacroAction>().Where(a => a.ActionType == MacroActionType.Delay).Select(a => a.DelayMs).ToArray();
        bulkWait.Text = "100"; Invoke(editor, "BulkWait_Click", editor, new RoutedEventArgs());
        Check(Waits().All(ms => ms == 100) && steps.SelectedItems.Count == 3, "bulk editor sets every picked wait and keeps the pick");
        bulkWait.Text = "+20"; Invoke(editor, "BulkWait_Click", editor, new RoutedEventArgs());
        bulkWait.Text = "x2"; Invoke(editor, "BulkWait_Click", editor, new RoutedEventArgs());
        Check(Waits().All(ms => ms == 240), "bulk waits add and scale");
        bulkWait.Text = "-500"; Invoke(editor, "BulkWait_Click", editor, new RoutedEventArgs());
        Check(Waits().All(ms => ms == 240) && ((TextBlock)editor.FindName("ErrorText")).Text.Contains("delay"), "invalid bulk waits are reported and nothing changes");
        Invoke(editor, "Undo"); Invoke(editor, "Undo"); Invoke(editor, "Undo");
        Check(Shown().SequenceEqual(original) && steps.SelectedItems.Count == 3, "undo restores the waits and the pick");
        Invoke(editor, "Redo");
        Check(Waits().All(ms => ms == 100), "redo applies the change again");
        Invoke(editor, "Undo");

        // Copy, paste, duplicate and delete work on the whole pick.
        steps.SelectedItems.Clear(); steps.SelectedItems.Add(steps.Items[3]); steps.SelectedItems.Add(steps.Items[6]);
        Invoke(editor, "Copy", new List<int> { 3, 6 });
        steps.SelectedItems.Clear(); steps.SelectedIndex = 9;
        Invoke(editor, "Paste");
        Check(steps.Items.Count == 12 && ((MacroAction)steps.Items[10]).ActionType == MacroActionType.MouseClick && ((MacroAction)steps.Items[11]).ActionType == MacroActionType.MouseDown
            && steps.SelectedItems.Count == 2 && !ReferenceEquals(steps.Items[10], steps.Items[3]), "pasted steps land after the pick as independent copies");
        Invoke(editor, "Duplicate", new List<int> { 10, 11 });
        Check(steps.Items.Count == 14 && steps.SelectedItems.Contains(steps.Items[12]) && steps.SelectedItems.Contains(steps.Items[13]), "duplicate copies the whole pick");
        Invoke(editor, "Delete", new List<int> { 10, 11, 12, 13 });
        Check(Shown().SequenceEqual(original), "delete removes every picked step");

        // Dropping steps into a gap moves them as one block, in order.
        string[] Order(params int[] order) => order.Select(i => original[i]).ToArray();
        Invoke(editor, "DropSteps", new List<int> { 1, 2 }, 5);
        Check(Shown().SequenceEqual(Order(0, 3, 4, 1, 2, 5, 6, 7, 8, 9)) && steps.SelectedItems.Count == 2 && steps.SelectedIndex == 3, "dragging two steps down drops them in the gap");
        Invoke(editor, "Undo");
        Invoke(editor, "DropSteps", new List<int> { 3, 7, 9 }, 0);
        Check(Shown().SequenceEqual(Order(3, 7, 9, 0, 1, 2, 4, 5, 6, 8)), "scattered steps drop together at the top, keeping their order");
        Invoke(editor, "Undo");
        Invoke(editor, "DropSteps", new List<int> { 4, 5 }, 6);
        Invoke(editor, "DropSteps", new List<int> { 8 }, 10);
        Check(Shown().SequenceEqual(Order(0, 1, 2, 3, 4, 5, 6, 7, 9, 8)), "drops next to the steps themselves change nothing; dropping at the end works");
        Invoke(editor, "Undo");
        Check(Shown().SequenceEqual(original), "undo reverts a drop");
        var wait = StepView.From(new MacroAction { ActionType = MacroActionType.Delay, DelayMs = 100 });
        var key = StepView.From(new MacroAction { ActionType = MacroActionType.KeyPress, KeyName = "Ctrl+C" });
        var place = StepView.From(new MacroAction { ActionType = MacroActionType.MousePosition, X = 420, Y = 310, Space = CoordinateSpace.Window });
        Check(wait.IsWait && wait.Detail.Contains("100") && key.IsKey && key.Detail == "Ctrl+C" && place.Detail.Contains("in window") && place.Tone == "Move",
            "step rows split into a tone, a title and a readable detail");
        Check(((PathPreview)editor.FindName("Thumbnail")).Timeline?.HasPointer == true && ((TextBlock)editor.FindName("WindowText")).Text.Contains("Game"), "editor shows the target window and a route thumbnail");
        Render(editor, 800, 760, "editor-target.png");
        editor.AddStep(MacroActionType.MousePosition);
        ((ComboBox)editor.FindName("StepSpaceBox")).SelectedIndex = 1;
        ((TextBox)editor.FindName("StepValueTextBox")).Text = "12, 34";
        Save(editor);
        Check(editor.Profile.Actions.Any(a => a.Space == CoordinateSpace.Window && a.X == 12) && editor.Profile.Window == TestTarget, "editor saves window-relative steps and the target");

        string settings = Path.Combine(Path.GetTempPath(), "keyloom-settings-" + Guid.NewGuid().ToString("N") + ".json");
        new AppSettings { ShowExperimental = true }.Save(settings);
        var repeat = Profile(); repeat.TargetType = TargetInputType.Mouse; repeat.Window = TestTarget; repeat.UsePoint = true; repeat.PointX = 30; repeat.PointY = 40;
        var repeatEditor = new MacroEditorView(repeat);
        ((RadioButton)repeatEditor.FindName("BackgroundModeRadio")).IsChecked = true;
        ((System.Windows.Controls.Primitives.ToggleButton)repeatEditor.FindName("MoreToggle")).IsChecked = true;
        Check(((FrameworkElement)repeatEditor.FindName("RunModePanel")).Visibility == Visibility.Visible && ((FrameworkElement)repeatEditor.FindName("TestButton")).Visibility == Visibility.Visible,
            "experimental setting reveals background mode and its test button");
        Render(repeatEditor, 800, 900, "editor-background.png");
        Save(repeatEditor);
        Check(repeatEditor.Profile is { ExecutionMode: ExecutionMode.Background, UsePoint: true, PointX: 30 }, "editor saves background mode and the chosen point");
        File.Delete(settings); AppSettings.Load(settings);

        var preview = new PreviewView(shown, new FakeWindows { Present = false });
        var previewHost = new Window { Content = preview, Width = 680, Height = 520, ShowActivated = false };
        previewHost.Show(); Pump(100);
        var route = (PathPreview)preview.FindName("Route");
        var slider = (Slider)preview.FindName("TimeSlider");
        slider.Value = 250; Pump();
        Check(route.Time == 250 && ((TextBlock)preview.FindName("StepText")).Text.Contains("Step 5"), "preview scrubbing moves the marker and shows the running step");
        Render(preview, 680, 520, "preview.png");
        Invoke(preview, "Play_Click", preview, new RoutedEventArgs());
        Pump(120);
        Check(route.Time > 250, "preview plays in real time");
        ((ComboBox)preview.FindName("SpeedBox")).SelectedIndex = 4;
        ((CheckBox)preview.FindName("ScreenBox")).IsChecked = true; Pump(100);
        var overlay = (Window)typeof(PreviewView).GetField("_overlay", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(preview)!;
        var style = GetWindowLong(new WindowInteropHelper(overlay).Handle, -20);
        Check(overlay.IsVisible && (style & 0x20) != 0 && (style & 0x08000000) != 0, "on-screen preview is click-through and never takes focus");
        preview.Dispose(); previewHost.Close(); Pump();
        Check(!overlay.IsVisible && !((PreviewPlayer)typeof(PreviewView).GetField("_player", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(preview)!).IsPlaying,
            "closing preview stops playback and removes the overlay");

        var options = new RecordingOptionsView();
        ((ComboBox)options.FindName("SpaceBox")).SelectedIndex = 2;
        Invoke(options, "Start_Click", options, new RoutedEventArgs());
        Check(options.Options is { Space: CoordinateSpace.Start, Simplify: true }, "record options choose coordinate space and simplification");
        string settingsFile = Path.Combine(Path.GetTempPath(), "keyloom-settings-" + Guid.NewGuid().ToString("N") + ".json");
        var settingsView = new SettingsView(settingsFile);
        Check(((TextBlock)settingsView.FindName("VersionText")).Text == "Keyloom 0.2-dev-1", "settings show the version");
        ((RadioButton)settingsView.FindName("DarkThemeRadio")).IsChecked = true;
        Check(ThemeManager.IsDark && AppSettings.Load(settingsFile).Theme == AppTheme.Dark, "choosing a theme applies and saves it right away");
        ((RadioButton)settingsView.FindName("LightThemeRadio")).IsChecked = true;
        Check(!ThemeManager.IsDark && AppSettings.Load(settingsFile).Theme == AppTheme.Light, "light theme can be chosen again");
        File.Delete(settingsFile); AppSettings.Load(settingsFile);
        IconTests();

        var highlight = new PickerOverlayWindow(); highlight.Show();
        var under = new Window { Title = "Keyloom hit-test target", Left = 100, Top = 100, Width = 300, Height = 200, ShowActivated = false, Topmost = true };
        under.Show(); Pump(50);
        var underHandle = new WindowInteropHelper(under).Handle;
        var (ux, uy) = WindowSystem.Instance.ClientToScreen(underHandle, 20, 20);
        highlight.ShowTarget(new(ux - 10, uy - 10, 200, 120), ux, uy, "Target", "20, 20"); Pump(50);
        Check(WindowSystem.RootAt(ux + 5, uy + 5) == underHandle, "picker highlight is invisible to hit testing, so the window below is chosen");
        highlight.Close(); under.Close();

        using var picker = new PointPicker();
        var pick = picker.PickAsync();
        var keyboard = typeof(PointPicker).GetMethod("KeyboardHook", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var data = Marshal.AllocHGlobal(24);
        try
        {
            for (int i = 0; i < 24; i++) Marshal.WriteByte(data, i, 0);
            Marshal.WriteInt32(data, 0, 0x1B);
            var consumed = (IntPtr)keyboard.Invoke(picker, new object[] { 0, new IntPtr(0x100), data })!;
            Pump(50);
            Check(consumed == new IntPtr(1) && pick.IsCompleted && pick.Result == null, "Esc cancels point picking and is not passed on");
        }
        finally { Marshal.FreeHGlobal(data); }
        Check((IntPtr)typeof(PointPicker).GetField("_mouseHook", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(picker)! == IntPtr.Zero, "point picking releases its hooks");
    }
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    /// <summary>Macro icons: built-in symbols, characters and stored pictures.</summary>
    private static void IconTests()
    {
        string iconFolder = Path.Combine(Path.GetTempPath(), "keyloom-icons-" + Guid.NewGuid().ToString("N"));
        string previousIcons = IconStore.Folder; IconStore.Folder = iconFolder;
        string picture = Path.Combine(Path.GetTempPath(), "keyloom-picture-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(300, 200, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            var drawing = new System.Windows.Media.DrawingVisual();
            using (var dc = drawing.RenderOpen()) dc.DrawRectangle(System.Windows.Media.Brushes.OrangeRed, null, new Rect(0, 0, 300, 200));
            bitmap.Render(drawing);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var file = File.Create(picture)) encoder.Save(file);
            var iconEditor = new MacroEditorView(Profile());
            iconEditor.UseIconImage(picture);
            Save(iconEditor);
            string stored = iconEditor.Profile.IconImage;
            Check(iconEditor.Profile.IconKind == MacroIconKind.Image && IconStore.IsValidName(stored) && IconStore.Load(stored) is System.Windows.Media.Imaging.BitmapSource { PixelWidth: 96, PixelHeight: 96 },
                "a custom picture is copied into the icon folder as a square icon");
            Render(iconEditor, 800, 300, "editor-icon-image.png");
            var chooser = (IconPicker)iconEditor.FindName("IconChooser");
            ((TextBox)chooser.FindName("GlyphTextBox")).Text = "P1";
            Save(iconEditor);
            Check(iconEditor.Profile is { IconKind: MacroIconKind.Glyph, IconGlyph: "P1", IconImage: "" }, "typing a letter or number makes it the icon");
            Render(iconEditor, 800, 300, "editor-icon-glyph.png");
            Render((FrameworkElement)((System.Windows.Controls.Primitives.Popup)iconEditor.FindName("IconPopup")).Child, 290, 300, "icon-picker.png");
            ((RadioButton)chooser.FindName("CursorIconRadio")).IsChecked = true;
            Save(iconEditor);
            Check(iconEditor.Profile is { IconKind: MacroIconKind.Cursor, IconGlyph: "" }, "built-in icons can be chosen");
            IconStore.Prune([]);
            Check(IconStore.Load(stored) == null, "unused pictures are removed");
            var glyph = Profile(); glyph.IconKind = MacroIconKind.Glyph; glyph.IconGlyph = "ABC";
            Check(Throws(() => ProfileValidator.Validate(glyph)), "icons are limited to two characters");
            glyph.IconGlyph = "K"; glyph.IconImage = "..\\evil.png";
            Check(Throws(() => ProfileValidator.Validate(glyph)), "icon pictures must be stored names, not paths");
            Check(MacroIcon.Effective(MacroIconKind.Glyph, " ", "") == MacroIconKind.Auto && MacroIcon.Effective(MacroIconKind.Image, "", "") == MacroIconKind.Auto,
                "empty icons fall back to the automatic icon");
        }
        finally
        {
            IconStore.Folder = previousIcons;
            if (File.Exists(picture)) File.Delete(picture);
            if (Directory.Exists(iconFolder)) Directory.Delete(iconFolder, true);
        }
    }
    /// <summary>Resource use of the new features, measured in this process. Run with --bench.</summary>
    private static void Benchmarks()
    {
        using var self = Process.GetCurrentProcess();
        (double Cpu, double MemoryMb) Measure(int milliseconds)
        {
            self.Refresh(); var cpu = self.TotalProcessorTime; var clock = Stopwatch.StartNew();
            Pump(milliseconds);
            self.Refresh();
            return ((self.TotalProcessorTime - cpu).TotalMilliseconds / clock.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100, self.WorkingSet64 / 1048576.0);
        }
        void Report(string name, (double Cpu, double MemoryMb) m) => Console.WriteLine($"BENCH {name}: CPU {m.Cpu:0.00}% of all cores, working set {m.MemoryMb:0} MB");
        GC.Collect(); Report("idle", Measure(3000));
        var p = Profile(MacroBehaviorType.Sequence);
        p.Actions = new();
        for (int i = 0; i < ProfileValidator.MaxActions / 2; i++)
        {
            p.Actions.Add(new() { ActionType = MacroActionType.MousePosition, X = 200 + (int)(Math.Sin(i / 300.0) * 600) + 600, Y = 200 + (int)(Math.Cos(i / 170.0) * 300) + 300 });
            p.Actions.Add(new() { ActionType = i % 50 == 0 ? MacroActionType.MouseClick : MacroActionType.Delay, DelayMs = 16 });
        }
        var clock = Stopwatch.StartNew();
        var preview = new PreviewView(p, new FakeWindows { Present = false });
        var previewHost = new Window { Content = preview, Width = 680, Height = 520, ShowActivated = false };
        previewHost.Show(); Pump(200);
        Console.WriteLine($"BENCH preview open (20,000 steps): {clock.ElapsedMilliseconds} ms");
        Report("preview open, paused", Measure(3000));
        Invoke(preview, "Play_Click", preview, new RoutedEventArgs());
        Report("preview playing", Measure(3000));
        ((CheckBox)preview.FindName("ScreenBox")).IsChecked = true;
        Report("preview playing + on-screen overlay", Measure(3000));
        preview.Dispose(); previewHost.Close(); GC.Collect();
        Report("after preview closed", Measure(3000));
        using var form = new System.Windows.Forms.Form { Text = "Keyloom bench target", ShowInTaskbar = false, ClientSize = new(300, 200) };
        int clicks = 0; form.MouseDown += (_, _) => clicks++;
        form.Show(); Pump(100);
        var executor = new MacroExecutor(null, null);
        var target = WindowSystem.Describe(form.Handle);
        var click = Profile(); click.TargetType = TargetInputType.Mouse; click.RepeatCount = 0; click.SpamIntervalMs = 1;
        click.Window = target; click.ExecutionMode = ExecutionMode.Background; click.UsePoint = true; click.PointX = 50; click.PointY = 50;
        // Pump instead of blocking: executor continuations return to this UI thread.
        void Await(Task task) { while (!task.IsCompleted) Pump(); task.GetAwaiter().GetResult(); }
        Await(executor.ToggleAsync(click));
        var m = Measure(3000);
        Await(executor.StopAsync());
        Report($"background clicking every 1 ms ({clicks / 3.0:0}/s delivered)", m);
        click.SpamIntervalMs = 50; clicks = 0;
        Await(executor.ToggleAsync(click));
        m = Measure(3000);
        Await(executor.StopAsync());
        Report($"background clicking every 50 ms ({clicks / 3.0:0}/s delivered)", m);
        form.Close();
    }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out WindowSystem.POINT point);
    /// <summary>Real PostMessage delivery to a test window. Uses no system input, so it is safe to run unattended.</summary>
    private static void BackgroundInputTests()
    {
        using var form = new System.Windows.Forms.Form { Text = "Keyloom background test", ShowInTaskbar = false, StartPosition = System.Windows.Forms.FormStartPosition.Manual, Location = new(40, 40), ClientSize = new(320, 200) };
        var panel = new System.Windows.Forms.Panel { Location = new(20, 20), Size = new(120, 80) };
        var text = new System.Windows.Forms.TextBox { Location = new(160, 20), Width = 140 };
        form.Controls.Add(panel); form.Controls.Add(text);
        var clicks = new List<string>();
        panel.MouseDown += (_, e) => clicks.Add($"down:{e.Button}:{e.X},{e.Y}");
        panel.MouseUp += (_, e) => clicks.Add($"up:{e.Button}:{e.X},{e.Y}");
        form.Show(); text.Focus(); Pump(100);
        GetCursorPos(out var cursorBefore);
        var driver = new WindowMessageDriver(form.Handle);
        var (x, y) = WindowSystem.Instance.ClientToScreen(form.Handle, 50, 60);
        driver.Position(x, y); driver.Mouse("Left", true); driver.Mouse("Left", false);
        driver.Mouse("Right", true); driver.Mouse("Right", false);
        foreach (char ch in "ok") { driver.Character(ch, true); driver.Character(ch, false); }
        Pump(150);
        GetCursorPos(out var cursorAfter);
        Check(clicks.SequenceEqual(new[] { "down:Left:30,40", "up:Left:30,40", "down:Right:30,40", "up:Right:30,40" }), "background clicks reach the child control at the chosen point");
        Check(text.Text == "ok", "background text reaches the focused control");
        Check(cursorBefore.X == cursorAfter.X && cursorBefore.Y == cursorAfter.Y, "background input leaves the real pointer where it was");
        Check(WindowSystem.Instance.Find(WindowSystem.Describe(form.Handle)) == form.Handle, "target windows are found again by process, class and title");
        form.Close();
    }
    private sealed class FakeWindows : IWindowSystem
    {
        public (int X, int Y) Origin = (0, 0), CursorAt = (0, 0);
        public (int Width, int Height) Size = (800, 600);
        public volatile bool Present = true, Alive = true;
        public IntPtr Find(WindowTarget target) => Present ? new IntPtr(42) : IntPtr.Zero;
        public bool IsAlive(IntPtr window) => Alive;
        public (int X, int Y) ClientToScreen(IntPtr window, int x, int y) => (Origin.X + x, Origin.Y + y);
        public (int Width, int Height) ClientSize(IntPtr window) => Size;
        public (int X, int Y) Cursor() => CursorAt;
    }
}
