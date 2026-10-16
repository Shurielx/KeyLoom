using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Keyloom.Engine;
using Keyloom.Models;
using Keyloom.Services;
using Keyloom.Views;

/// <summary>Step start times, usage statistics, Move to options and changing macros straight from the list.</summary>
internal static partial class Program
{
    private static void PolishEngineTests()
    {
        var wait = (int ms) => new MacroAction { ActionType = MacroActionType.Delay, DelayMs = ms };
        var steps = new[] { KeyStep("A"), wait(100), Start("G", repeat: 3, gap: 50), KeyStep("B"), KeyStep("C"), End(), KeyStep("D") };
        var times = StepGroups.StartTimes(steps);
        // A at 0, wait 100, group: B at 100, gap 50, C at 150, then two more runs of B and C with gaps: 6 played, 5 gaps of 50 ms.
        Check(times[0] == 0 && times[1] == 0 && times[2] == 100 && times[3] == 100 && times[4] == 150 && times[6] == 100 + 5 * 50,
            "start times count waits and group gaps, and name when each step first plays");
        Check(StepLayoutConverter.FormatTime(250) == "0.25 s" && StepLayoutConverter.FormatTime(65_200) == "1:05.2", "start times read as seconds, then minutes");

        Check(!Shortcut.IsReserved(Shortcut.AppModifiers, 0x49) && Shortcut.IsReserved(Shortcut.AppModifiers, Shortcut.StopKey), "Ctrl + Shift + I is free; only the stop shortcut is reserved");
        Check(Shortcut.Problem(0, 0x41) != null && Shortcut.Problem(0, 0x77) == null && Shortcut.Problem(Shortcut.Control, 0x41) == null, "shortcuts need a modifier unless they are F-keys");

        string file = Path.Combine(Path.GetTempPath(), "keyloom-usage-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var usage = UsageStore.Load(file);
            usage.Started("a"); usage.Stopped("a", TimeSpan.FromSeconds(90), 12);
            usage.Started("a"); usage.Stopped("a", TimeSpan.FromSeconds(30), 3);
            usage.Started("b"); usage.Stopped("b", TimeSpan.FromSeconds(5), 1);
            var again = UsageStore.Load(file);
            Check(again.For("a") is { Starts: 2, Cycles: 15, RunMs: 120_000 } && again.TotalStarts == 3 && again.TotalRunMs == 125_000, "usage adds up starts, repeats and run time and survives a restart");
            again.Keep(["a"]);
            Check(again.For("b").Starts == 0 && again.For("a").Starts == 2, "usage of deleted macros is dropped");
            Check(UsageStore.Duration(45_000) == "45 s" && UsageStore.Duration(12 * 60_000) == "12 min" && UsageStore.Duration(185 * 60_000) == "3 h 5 min", "durations read the way people say them");
            Check(UsageStore.Clock(TimeSpan.FromSeconds(7)) == "0:07" && UsageStore.Clock(TimeSpan.FromSeconds(3723)) == "1:02:03", "the running clock counts minutes and hours");
            var now = new DateTime(2026, 10, 16, 15, 0, 0);
            Check(UsageStore.When(new DateTime(2026, 10, 16, 14, 2, 0), now) == "today 14:02" && UsageStore.When(now.AddDays(-1), now) == "yesterday" && UsageStore.When(null, now) == "never", "last use reads as today, yesterday or a date");
            File.WriteAllText(file, "{ not json");
            Check(UsageStore.Load(file).TotalStarts == 0, "an unreadable usage file starts empty");
        }
        finally { File.Delete(file); }
    }

    private static void PolishUiTests()
    {
        // Move to: pick again, and add or drop the click after it.
        var p = Profile(MacroBehaviorType.Sequence);
        p.Actions = new()
        {
            new() { ActionType = MacroActionType.MousePosition, X = 10, Y = 20 },
            new() { ActionType = MacroActionType.MouseClick },
            new() { ActionType = MacroActionType.Delay, DelayMs = 200 },
            KeyStep("E")
        };
        var editor = new MacroEditorView(p);
        var steps = (ListBox)editor.FindName("ActionsListBox");
        MacroAction[] Items() => steps.Items.Cast<MacroAction>().ToArray();
        bool Visible(string name) => ((FrameworkElement)editor.FindName(name)).Visibility == Visibility.Visible;
        var thenClick = (CheckBox)editor.FindName("ThenClickBox");
        steps.SelectedIndex = 0;
        Check(Visible("MoveOptions") && thenClick.IsChecked == true, "a picked Move to offers picking again and shows its click");
        thenClick.IsChecked = false;
        Check(Items().Length == 3 && Items()[1].ActionType == MacroActionType.Delay && steps.SelectedIndex == 0, "unticking Click after moving removes the click and keeps the move picked");
        thenClick.IsChecked = true;
        Check(Items().Length == 4 && Items()[1] is { ActionType: MacroActionType.MouseClick, MouseButton: "Left" }, "ticking it adds a left click right after the move");
        Invoke(editor, "Undo");
        Check(Items().Length == 3, "adding the click can be undone");
        steps.SelectedIndex = 2;
        Check(!Visible("MoveOptions"), "other steps hide the Move to options");

        // The route only shows when the pointer travels; the strip always shows the run.
        Pump(50);
        Check(!Visible("PreviewCard") && Visible("StripPanel"), "one position shows no route card, only the run strip");
        var strip = (RunStrip)editor.FindName("Strip");
        Check(strip.Duration == 200 && strip.Picked == 2, "the strip covers one run and marks the picked step");
        Invoke(editor, "Strip_StepPicked", 2);
        Check(steps.SelectedIndex == 2, "clicking a mark on the strip picks its step");
        Render(editor, 800, 760, "editor-strip.png");

        // Changing a macro from the list
        var main = new MainWindow();
        main.Show(); Pump(); main.Hide();
        var profiles = (System.Collections.ObjectModel.ObservableCollection<MacroProfile>)Field(main, "_profiles");
        profiles.Clear(); profiles.Add(new() { Name = "Farm", PrimaryVk = 0x78, PrimaryModifiers = Shortcut.Control });
        var farm = profiles[0];
        var storage = (ProfileStorageService)Field(main, "_storage");
        // Keep the test away from the real macro file.
        string folder = Path.Combine(Path.GetTempPath(), "keyloom-list-" + Guid.NewGuid().ToString("N"));
        const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var realUsage = Field(main, "_usage");
        typeof(MainWindow).GetField("_storage", Private)!.SetValue(main, new ProfileStorageService(folder));
        typeof(MainWindow).GetField("_usage", Private)!.SetValue(main, UsageStore.Load(Path.Combine(folder, "usage.json")));
        try
        {
            var content = (FrameworkElement)main.Content;
            content.Measure(new Size(838, 598)); content.Arrange(new Rect(0, 0, 838, 598)); content.UpdateLayout(); Pump(100);
            var nameBox = Descendants<TextBox>(content).First(t => t.Name == "NameBox");
            Invoke(main, "RowName_Click", Descendants<Border>(content).First(b => b.Name == "NameChip"), new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            Pump(50);
            Check(farm.IsRenaming && nameBox.Visibility == Visibility.Visible, "clicking a name in the list turns it into a name box");
            nameBox.Text = "Farm loop";
            Invoke(main, "NameBox_KeyDown", nameBox, new KeyEventArgs(Keyboard.PrimaryDevice, new FakeSource(), 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
            Check(!farm.IsRenaming && farm.Name == "Farm loop" && new ProfileStorageService(folder).LoadProfiles().Profiles[0].Name == "Farm loop", "Enter renames the macro and saves it");
            Invoke(main, "RowName_Click", Descendants<Border>(content).First(b => b.Name == "NameChip"), new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            nameBox.Text = "Nope";
            Invoke(main, "NameBox_KeyDown", nameBox, new KeyEventArgs(Keyboard.PrimaryDevice, new FakeSource(), 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
            Check(farm.Name == "Farm loop" && !farm.IsRenaming, "Esc leaves the name as it was");

            var shortcut = Descendants<Button>(content).First(b => b.Name == "ShortcutButton");
            Invoke(main, "RowShortcut_Click", shortcut, new RoutedEventArgs());
            Check(farm.IsCapturing, "clicking the shortcut waits for new keys");
            Render(content, 838, 300, "main-capture.png");
            Invoke(main, "RowShortcut_KeyDown", shortcut, new KeyEventArgs(Keyboard.PrimaryDevice, new FakeSource(), 0, Key.F7) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Check(!farm.IsCapturing && farm.PrimaryVk == 0x76 && farm.PrimaryModifiers == 0 && farm.HotkeyLabel == "F7", "pressing a key sets the new shortcut");
            Invoke(main, "RowShortcut_Click", shortcut, new RoutedEventArgs());
            Invoke(main, "RowShortcut_KeyDown", shortcut, new KeyEventArgs(Keyboard.PrimaryDevice, new FakeSource(), 0, Key.Back) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Check(farm.PrimaryVk == 0 && farm.HotkeyLabel == "No shortcut", "Backspace removes the shortcut");

            var picker = (IconPicker)main.FindName("RowIconPicker");
            Invoke(main, "RowIcon_Click", Descendants<Button>(content).First(b => b.Name == "IconButton"), new RoutedEventArgs());
            ((TextBox)picker.FindName("GlyphTextBox")).Text = "F";
            Check(farm is { IconKind: MacroIconKind.Glyph, IconGlyph: "F" } && new ProfileStorageService(folder).LoadProfiles().Profiles[0].IconGlyph == "F", "the icon can be changed from the list");
            ((Popup)main.FindName("RowIconPopup")).IsOpen = false;

            // Usage shows up in the row and in settings.
            var usage = (UsageStore)Field(main, "_usage");
            Check(farm.UsageText == "Not used yet", "an unused macro says so");
            Invoke(main, "OnRunChanged", farm, true);
            farm.ExecutionCount = 41;
            Invoke(main, "ShowRun", farm);
            Check(farm.IsActive && farm.RunText.StartsWith("Running · 0:0") && farm.RunText.EndsWith("41 clicks"), "a running macro shows its time and count");
            Render(content, 838, 300, "main-running.png");
            Invoke(main, "OnRunChanged", farm, false);
            Check(!farm.IsActive && farm.UsageText.StartsWith("Used 1×") && usage.For(farm.Id).Cycles == 41, "stopping adds the run to the statistics");
            var settings = new SettingsView(Path.Combine(folder, "settings.json"), profiles, usage);
            Check(((TextBlock)settings.FindName("StartsText")).Text == "1" && ((TextBlock)settings.FindName("TopText")).Text == "Farm loop", "settings show totals and the most used macro");
            Render(settings, 760, 1100, "settings-usage.png");
            usage.Reset();
            ((Window)Field(main, "_toast")).Hide();
        }
        finally
        {
            typeof(MainWindow).GetField("_storage", Private)!.SetValue(main, storage);
            typeof(MainWindow).GetField("_usage", Private)!.SetValue(main, realUsage);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
    /// <summary>Deleting every step leaves nothing to focus; Ctrl+Z must still bring them back.</summary>
    private static void DeleteAllUndoTests()
    {
        var p = Profile(MacroBehaviorType.Sequence);
        p.Actions = new() { KeyStep("A"), KeyStep("B"), KeyStep("C") };
        var editor = new MacroEditorView(p);
        var host = new Window { Content = editor, Width = 760, Height = 900, ShowActivated = false };
        host.Show(); Pump(100);
        try
        {
            var steps = (ListBox)editor.FindName("ActionsListBox");
            steps.SelectAll();
            Invoke(editor, "Delete", (List<int>)Call(editor, "PickedBlock")!); Pump(50);
            Check(steps.Items.Count == 0 && FocusManager.GetFocusedElement(host) == steps, "deleting every step keeps focus on the step list");
            Check(editor.UndoShortcut(ModifierKeys.Control, Key.Z) && steps.Items.Count == 3, "Ctrl+Z from outside the steps brings every deleted step back");
            Check(editor.UndoShortcut(ModifierKeys.Control, Key.Y) && steps.Items.Count == 0, "Ctrl+Y deletes them again");
            Check(!editor.UndoShortcut(ModifierKeys.Control, Key.C), "other shortcuts are left to the steps");
        }
        finally { host.Close(); }
    }
    private sealed class FakeSource : PresentationSource
    {
        protected override System.Windows.Media.CompositionTarget GetCompositionTargetCore() => null!;
        public override System.Windows.Media.Visual RootVisual { get => null!; set { } }
        public override bool IsDisposed => false;
    }
}
