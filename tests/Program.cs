using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Keyloom.Engine;
using Keyloom.Models;
using Keyloom.Services;
using Keyloom.Views;

internal static partial class Program
{
    private static int _checks;
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            EngineTests().GetAwaiter().GetResult();
            RecordingTests().GetAwaiter().GetResult();
            StorageTests();
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Keyloom;component/Assets/Icons.xaml", UriKind.Relative) });
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Keyloom;component/Assets/Theme.xaml", UriKind.Relative) });
            UiTests();
            HotkeyRegistrationTests();
            RecordingUiTests();
            RecorderHookTests();
            if (args.Contains("--input")) NativeInputTests();
            Console.WriteLine($"PASS: {_checks} checks");
            app.Shutdown(); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Check(bool result, string message)
    {
        if (!result) throw new Exception(message);
        _checks++; Console.WriteLine("PASS " + message);
    }
    private static async Task Until(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) { if (clock.ElapsedMilliseconds > 3000) throw new TimeoutException(); await Task.Delay(5); }
    }
    private static MacroProfile Profile(MacroBehaviorType mode = MacroBehaviorType.ClickSpam) => new()
    {
        Name = "Test", Behavior = mode, TargetType = TargetInputType.Keyboard,
        TargetKey = "E", StartDelayMs = 0, SpamIntervalMs = 10, RepeatCount = 3
    };
    private static async Task EngineTests()
    {
        var driver = new FakeDriver();
        await using var executor = new MacroExecutor(driver);
        var errors = new ConcurrentQueue<string>();
        var progress = new ConcurrentQueue<long>();
        executor.ExecutionFailed += errors.Enqueue;
        executor.ProgressChanged += (_, count) => progress.Enqueue(count);
        var repeat = Profile();
        await executor.ToggleAsync(repeat); await Until(() => executor.ActiveProfile == null);
        Check(driver.Events.Count(e => e == "K69:down") == 3 && driver.Events.Count(e => e == "K69:up") == 3, "repeat count and balanced key events");
        Check(progress.Last() == 3, "final execution count");
        driver.Events.Clear();
        var hold = Profile(MacroBehaviorType.PressAndHold);
        await executor.ToggleAsync(hold); await Until(() => driver.Events.Contains("K69:down"));
        await executor.StopAsync();
        Check(driver.Events.SequenceEqual(new[] { "K69:down", "K69:up" }), "hold and stop release only the owned key");
        driver.Events.Clear();
        var sequence = Profile(MacroBehaviorType.Sequence);
        sequence.RepeatCount = 1; sequence.TargetKey = "Space";
        sequence.Actions.Add(new() { ActionType = MacroActionType.KeyDown, KeyName = "Ctrl" });
        sequence.Actions.Add(new() { ActionType = MacroActionType.KeyPress, KeyName = "Ctrl+C" });
        sequence.Actions.Add(new() { ActionType = MacroActionType.KeyUp, KeyName = "Ctrl" });
        sequence.Actions.Add(new() { ActionType = MacroActionType.MouseClick, MouseButton = "X1" });
        sequence.Actions.Add(new() { ActionType = MacroActionType.MouseMove, X = 20, Y = -10 });
        sequence.Actions.Add(new() { ActionType = MacroActionType.MouseScroll, ScrollAmount = -2 });
        sequence.Actions.Add(new() { ActionType = MacroActionType.Text, Text = "\u0105\uD83D\uDE42" });
        await executor.ToggleAsync(sequence); await Until(() => executor.ActiveProfile == null);
        Check(driver.Events.Take(4).SequenceEqual(new[] { "K162:down", "K67:down", "K67:up", "K162:up" }), "chord does not release an already held modifier");
        Check(driver.Events.Contains("MX1:up") && driver.Events.Contains("move:20,-10") && driver.Events.Contains("scroll:-2"), "mouse click, move and wheel steps");
        Check(driver.Events.Count(e => e.StartsWith("char:")) == 6, "Unicode text including surrogate pairs");
        driver.Events.Clear();
        var snapshot = Profile(MacroBehaviorType.Sequence); snapshot.StartDelayMs = 50; snapshot.RepeatCount = 2;
        snapshot.Actions.Add(new() { ActionType = MacroActionType.KeyPress, KeyName = "E" });
        await executor.ToggleAsync(snapshot); snapshot.Actions.Clear(); snapshot.TargetKey = "Q";
        await Until(() => executor.ActiveProfile == null);
        Check(driver.Events.Count(e => e == "K69:down") == 2, "editing source does not mutate running snapshot");
        var empty = Profile(MacroBehaviorType.Sequence);
        bool rejected = false;
        try { await executor.ToggleAsync(empty); } catch (ArgumentException) { rejected = true; }
        Check(rejected && executor.ActiveProfile == null, "empty sequence rejected before execution");
        var invalid = Profile(); invalid.SpamIntervalMs = 0; rejected = false;
        try { await executor.ToggleAsync(invalid); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "zero interval rejected");
        invalid = Profile(); invalid.TargetKey = "NotAKey"; rejected = false;
        try { await executor.ToggleAsync(invalid); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "invalid keyboard input rejected");
        driver.Events.Clear();
        var waiting = Profile(MacroBehaviorType.Sequence); waiting.RepeatCount = 0;
        waiting.Actions.Add(new() { ActionType = MacroActionType.KeyDown, KeyName = "Shift" });
        waiting.Actions.Add(new() { ActionType = MacroActionType.Delay, DelayMs = 10000 });
        await executor.ToggleAsync(waiting); await Until(() => driver.Events.Contains("K160:down"));
        var clock = Stopwatch.StartNew(); await executor.StopAsync();
        Check(clock.ElapsedMilliseconds < 300 && driver.Events.Contains("K160:up"), "long delays cancel promptly and release held input");
        driver.Events.Clear();
        await executor.ToggleAsync(hold); await Until(() => driver.Events.Contains("K69:down"));
        var next = Profile(); next.TargetKey = "Q"; next.RepeatCount = 1;
        await executor.ToggleAsync(next); await Until(() => executor.ActiveProfile == null);
        Check(Array.IndexOf(driver.Events.ToArray(), "K69:up") < Array.IndexOf(driver.Events.ToArray(), "K81:down"), "switching macros waits for previous cleanup");
        driver.Events.Clear();
        var startDelay = Profile(); startDelay.StartDelayMs = 10000;
        await executor.ToggleAsync(startDelay); await executor.StopAsync();
        Check(driver.Events.IsEmpty, "cancellation during start delay sends no input");
        driver.FailNext = true; await executor.ToggleAsync(Profile()); await Until(() => executor.ActiveProfile == null);
        Check(errors.Count == 1, "input failure is reported and clears running state");
        Check(KeyResolver.Resolve("Ctrl+Shift+F24").SequenceEqual(new ushort[] { 162, 160, 135 }), "extended function keys and combinations resolve");
        Check(KeyResolver.Resolve("RightCtrl+Left").SequenceEqual(new ushort[] { 163, 37 }), "right modifier and navigation keys resolve");
        driver.Events.Clear(); progress.Clear();
        var fast = Profile(); fast.SpamIntervalMs = 1; fast.RepeatCount = 0;
        await executor.ToggleAsync(fast); await Task.Delay(220); await executor.StopAsync();
        Check(progress.Count <= 5 && driver.Events.Count > 20, "fast repeat is paced and progress is throttled");
    }
    private static void StorageTests()
    {
        string folder = Path.Combine(Path.GetTempPath(), "keyloom-tests-" + Guid.NewGuid().ToString("N"));
        var storage = new ProfileStorageService(folder);
        var p = Profile(MacroBehaviorType.Sequence);
        p.PrimaryVk = 0x77; p.PrimaryModifiers = 6;
        p.Actions.Add(new() { ActionType = MacroActionType.Text, Text = "Hello" });
        storage.SaveProfiles(new[] { p });
        var loaded = storage.LoadProfiles();
        Check(loaded.Profiles[0].Actions[0].Text == "Hello" && loaded.Profiles[0].HotkeyLabel == "Ctrl + Shift + F8", "profile round-trip preserves actions and shortcuts");
        p.Name = "Changed"; storage.SaveProfiles(new[] { p });
        File.WriteAllText(storage.FilePath, "{broken");
        loaded = storage.LoadProfiles();
        Check(loaded.Profiles[0].Name == "Test" && loaded.Warning.Length > 0, "corrupt storage recovers its previous atomic-save backup");
        storage.SaveProfiles(Array.Empty<MacroProfile>());
        Check(storage.LoadProfiles().Profiles.Count == 0, "empty profile list is preserved");
        string legacy = Path.Combine(folder, "legacy"); Directory.CreateDirectory(legacy);
        var old = Profile(); old.PrimaryVk = 0x7C; old.PrimaryModifiers = 4; old.FallbackVk = 0x31; old.FallbackModifiers = 6;
        File.WriteAllText(Path.Combine(legacy, "macro_profiles.json"), System.Text.Json.JsonSerializer.Serialize(new[] { old }));
        var migrated = new ProfileStorageService(Path.Combine(folder, "new"), legacy).LoadProfiles();
        Check(migrated.Profiles[0].PrimaryVk == 0x31 && migrated.Profiles[0].FallbackVk == 0, "legacy Keychron profiles migrate to their universal fallback");
        Directory.Delete(folder, true);
    }
    private static void UiTests()
    {
        var main = new MainWindow();
        main.Show();
        Pump(); main.Hide();
        Check(Field(main, "_hotkeys") is HotkeyManager, "main window starts and initializes global shortcuts");
        Check(main.Icon != null && ((SolidColorBrush)main.Foreground).Color == Color.FromRgb(25, 54, 75), "derived windows receive the shared theme and app icon");
        var profiles = (System.Collections.ObjectModel.ObservableCollection<MacroProfile>)Field(main, "_profiles");
        profiles.Clear(); profiles.Add(ProfileStorageService.CreateDefault()); profiles.Add(new() { Name = "Copy and paste", PrimaryVk = 0x78, PrimaryModifiers = 6, Behavior = MacroBehaviorType.Sequence, RepeatCount = 1, Actions = new() { new() { ActionType = MacroActionType.KeyPress, KeyName = "Ctrl+C" }, new() { ActionType = MacroActionType.Delay, DelayMs = 100 }, new() { ActionType = MacroActionType.KeyPress, KeyName = "Ctrl+V" } } });
        var content = (FrameworkElement)main.Content;
        Render(content, 718, 438, "main.png");
        var buttons = Descendants<Button>(content).Where(b => b.Tag == profiles[0]).ToArray();
        Check(buttons.Length == 3 && buttons[0].Content is System.Windows.Shapes.Path && buttons[1].Content is System.Windows.Shapes.Path, "profile controls render vector edit and delete icons");
        profiles[0].IsActive = true; Pump();
        Check(buttons[0].Content is System.Windows.Shapes.Path && buttons[1].Content is System.Windows.Shapes.Path && (string)buttons[2].Content == "Stop", "running state changes only the Start button");
        Render(content, 718, 438, "main-active.png");
        profiles[0].IsActive = false; Pump();
        Check((string)buttons[2].Content == "Start", "stopped state restores Start");
        Render(content, 618, 398, "main-small.png");
        foreach (var mode in Enum.GetValues<MacroBehaviorType>())
        {
            var p = Profile(mode);
            if (mode == MacroBehaviorType.Sequence) p.Actions.Add(new() { ActionType = MacroActionType.KeyPress, KeyName = "Ctrl+C" });
            var editor = new MacroEditorDialog(p);
            Render((FrameworkElement)editor.Content, (int)editor.Width - 2, (int)editor.Height - 2, "editor-" + mode + ".png");
            Check(((FrameworkElement)editor.FindName("SequencePanel")).Visibility == (mode == MacroBehaviorType.Sequence ? Visibility.Visible : Visibility.Collapsed), mode + " editor visibility");
            editor.Close();
        }
        var validationEditor = new MacroEditorDialog(Profile(MacroBehaviorType.Sequence));
        Invoke(validationEditor, "Save_Click", validationEditor, new RoutedEventArgs());
        Check(((TextBlock)validationEditor.FindName("ErrorText")).Text.Contains("sequence"), "editor blocks saving an empty sequence");
        validationEditor.Close();
        var edit = new MacroEditorDialog(Profile(MacroBehaviorType.Sequence));
        ((ComboBox)edit.FindName("StepTypeBox")).SelectedValue = MacroActionType.KeyPress;
        ((TextBox)edit.FindName("StepValueTextBox")).Text = "Ctrl+C";
        Invoke(edit, "AddStep_Click", edit, new RoutedEventArgs());
        ((TextBox)edit.FindName("StepValueTextBox")).Text = "Ctrl+V";
        Invoke(edit, "UpdateStep_Click", edit, new RoutedEventArgs());
        Check(((ListBox)edit.FindName("ActionsListBox")).Items.Count == 1 && ((MacroAction)((ListBox)edit.FindName("ActionsListBox")).Items[0]).KeyName == "Ctrl+V", "sequence steps can be added and edited");
        edit.Close();
        ((TrayService)Field(main, "_tray")).Dispose();
        ((HotkeyManager)Field(main, "_hotkeys")).Dispose();
        ((Window)Field(main, "_toast")).Close();
        ((MacroExecutor)Field(main, "_executor")).DisposeAsync().AsTask().GetAwaiter().GetResult();
        typeof(MainWindow).GetField("_exiting", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(main, true);
        main.Close();
    }
    private static object Field(object instance, string name) => instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
    private static void Invoke(object instance, string name, params object[] args) => instance.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, args);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T t) yield return t;
            foreach (var next in Descendants<T>(child)) yield return next;
        }
    }
    private static void Render(FrameworkElement root, int width, int height, string name)
    {
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout(); Pump();
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(16, 17, 23)), null, new Rect(0, 0, width, height));
        image.Render(background); image.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        string folder = Path.Combine(AppContext.BaseDirectory, "screenshots"); Directory.CreateDirectory(folder);
        using var stream = File.Create(Path.Combine(folder, name)); encoder.Save(stream);
    }
    private static void Pump(int milliseconds = 20)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start();
        Dispatcher.PushFrame(frame);
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    private static void HotkeyRegistrationTests()
    {
        var window = new Window();
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        using var manager = new HotkeyManager(hwnd);
        var first = Profile(); first.PrimaryVk = 0x86; first.PrimaryModifiers = 7;
        var duplicate = first.Snapshot(); duplicate.Id = Guid.NewGuid().ToString("N");
        manager.RegisterProfiles(new[] { first, duplicate });
        Check(first.HotkeyStatus == "" && duplicate.HotkeyStatus.Contains("in use"), "actual Windows hotkey registration reports conflicts");
        var reserved = Profile(); reserved.PrimaryVk = Shortcut.StopKey; reserved.PrimaryModifiers = Shortcut.AppModifiers;
        manager.RegisterProfiles(new[] { reserved });
        Check(reserved.HotkeyStatus.Contains("Reserved"), "reserved stop shortcut cannot be assigned to a macro");
        var inputType = typeof(InputSimulator).GetNestedType("INPUT", BindingFlags.NonPublic)!;
        Check(Marshal.SizeOf(inputType) == (IntPtr.Size == 8 ? 40 : 28), "native SendInput structure matches Windows ABI");
        manager.Dispose(); window.Close();
    }
    private static void NativeInputTests()
    {
        var target = new Window { Title = "Keyloom input test", Width = 400, Height = 200, Topmost = true };
        var text = new TextBox { AcceptsReturn = true, Margin = new Thickness(20) };
        target.Content = text; target.Show(); target.Activate(); text.Focus(); Pump(100);
        var hwnd = new WindowInteropHelper(target).Handle;
        SetForegroundWindow(hwnd); target.Activate(); Keyboard.Focus(text); Pump(100);
        if (GetForegroundWindow() != hwnd) throw new Exception("Native input test could not focus its own target. No input sent.");
        int downs = 0, ups = 0, wheel = 0;
        text.PreviewKeyDown += (_, e) => { if (e.Key == Key.F9) downs++; };
        text.PreviewKeyUp += (_, e) => { if (e.Key == Key.F9) ups++; };
        text.PreviewMouseWheel += (_, e) => wheel += e.Delta;
        using var hotkeys = new HotkeyManager(hwnd);
        var shortcut = Profile(); shortcut.PrimaryVk = 0x79; shortcut.PrimaryModifiers = 6;
        int triggered = 0, stops = 0;
        hotkeys.ProfileTriggered += _ => triggered++;
        hotkeys.StopTriggered += () => stops++;
        string warning = hotkeys.RegisterProfiles(new[] { shortcut });
        Check(warning.Length == 0 && shortcut.HotkeyStatus.Length == 0, "real Windows hotkey registration");
        var simulator = new InputSimulator();
        var executor = new MacroExecutor(simulator);
        string? error = null; executor.ExecutionFailed += s => error = s;
        var p = Profile(MacroBehaviorType.Sequence); p.RepeatCount = 1;
        p.Actions.Add(new() { ActionType = MacroActionType.KeyPress, KeyName = "F9" });
        p.Actions.Add(new() { ActionType = MacroActionType.Delay, DelayMs = 20 });
        p.Actions.Add(new() { ActionType = MacroActionType.Text, Text = "Keyloom ą" });
        executor.ToggleAsync(p).GetAwaiter().GetResult();
        while (executor.ActiveProfile != null) Pump();
        Pump(100);
        Check(error == null && downs == 1 && ups == 1 && text.Text == "Keyloom ą", "actual SendInput delivery of a sequence and Unicode text");
        if (GetForegroundWindow() != hwnd) throw new Exception("Target lost focus; hotkey test aborted.");
        foreach (var key in KeyResolver.Resolve("Ctrl+Shift+F10")) simulator.Key(key, true);
        foreach (var key in KeyResolver.Resolve("Ctrl+Shift+F10").Reverse()) simulator.Key(key, false);
        Pump(60); Check(triggered == 1, "actual global shortcut delivery");
        foreach (var key in KeyResolver.Resolve("Ctrl+Shift+F12")) simulator.Key(key, true);
        foreach (var key in KeyResolver.Resolve("Ctrl+Shift+F12").Reverse()) simulator.Key(key, false);
        Pump(60); Check(stops == 1, "actual emergency-stop shortcut delivery");
        executor.DisposeAsync().AsTask().GetAwaiter().GetResult();
        target.Close();
    }
    private sealed class FakeDriver : IInputDriver
    {
        public ConcurrentQueue<string> Events { get; } = new();
        public bool FailNext;
        public void Key(ushort key, bool down)
        {
            if (FailNext) { FailNext = false; throw new InvalidOperationException("Input rejected"); }
            Events.Enqueue($"K{key}:{(down ? "down" : "up")}");
        }
        public void Mouse(string button, bool down) => Events.Enqueue($"M{button}:{(down ? "down" : "up")}");
        public void Move(int x, int y) => Events.Enqueue($"move:{x},{y}");
        public void Position(int x, int y) => Events.Enqueue($"position:{x},{y}");
        public void Scroll(int amount) => Events.Enqueue($"scroll:{amount}");
        public void Character(char value, bool down) => Events.Enqueue($"char:{(int)value}:{down}");
    }
}
