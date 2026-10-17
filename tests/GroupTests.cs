using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Keyloom.Engine;
using Keyloom.Models;
using Keyloom.Services;
using Keyloom.Views;

internal static partial class Program
{
    private static MacroAction Start(string name, int repeat = 0, int gap = 0) => new() { ActionType = MacroActionType.GroupStart, Name = name, Repeat = repeat, GapMs = gap };
    private static MacroAction End() => new() { ActionType = MacroActionType.GroupEnd };
    private static MacroAction KeyStep(string key) => new() { ActionType = MacroActionType.KeyPress, KeyName = key };

    private static async Task GroupEngineTests()
    {
        // A group plays its steps N times with a wait between every step, also between runs.
        var steps = new List<MacroAction> { KeyStep("Q"), Start("Loop", 3, 100), KeyStep("A"), KeyStep("B"), End(), KeyStep("W") };
        string Show(MacroAction a) => a.ActionType == MacroActionType.Delay ? a.DelayMs.ToString() : a.KeyName;
        var played = StepGroups.Expand(steps).Select(Show).ToArray();
        Check(played.SequenceEqual(new[] { "Q", "A", "100", "B", "100", "A", "100", "B", "100", "A", "100", "B", "W" }), "groups repeat their steps with a wait between each");
        Check(StepGroups.PlayedCount(steps) == played.Length, "played step count matches the expanded steps");
        var nested = new List<MacroAction> { Start("Outer", 2), KeyStep("A"), Start("Inner", 2), KeyStep("B"), End(), End() };
        Check(StepGroups.Expand(nested).Select(Show).SequenceEqual(new[] { "A", "B", "B", "A", "B", "B" }) && StepGroups.PlayedCount(nested) == 6, "nested groups repeat inside each other");
        Check(Throws(() => StepGroups.Validate([Start("Open"), KeyStep("A")])) && Throws(() => StepGroups.Validate([KeyStep("A"), End()])), "unclosed groups and stray ends are rejected");
        Check(Throws(() => StepGroups.Validate([Start("Big", StepGroups.MaxRepeat + 1), KeyStep("A"), End()])), "group run counts are limited");
        var huge = new List<MacroAction> { Start("A", 10000) };
        huge.AddRange(Enumerable.Range(0, 200).Select(_ => KeyStep("A"))); huge.Add(End());
        Check(Throws(() => StepGroups.Validate(huge)), "groups cannot expand past the played-step limit");
        // A group can repeat for a time: in the preview passes take what their steps take, playing reads the clock.
        MacroAction For(int ms, int gap = 0) { var g = Start("Spam", 0, gap); g.ForMs = ms; return g; }
        var wait = new MacroAction { ActionType = MacroActionType.Delay, DelayMs = 100 };
        var timed = new List<MacroAction> { For(1000), KeyStep("A"), wait, End(), KeyStep("W") };
        Check(StepGroups.Expand(timed).Count(a => a.KeyName == "A") == 10 && StepGroups.PassesOf(timed, 0) == 10 && StepGroups.PlayedCount(timed) == 21
            && StepGroups.StartTimes(timed)[4] == 1000, "a group repeats for its time in the preview and the timing");
        double now = 0;
        int real = StepGroups.Expand([For(1000), KeyStep("A"), End()], null, () => now).Count(a => { now += 250; return true; });
        Check(real == 4, "playing, a timed group starts passes until its time is up on the clock");
        Check(Throws(() => StepGroups.Validate([For(1000), KeyStep("A"), End()])) && !Throws(() => StepGroups.Validate([For(1000, 30), KeyStep("A"), KeyStep("B"), End()]))
            && Throws(() => StepGroups.Validate([For(StepGroups.MaxForMs + 1), wait, End()])), "a timed group needs steps that take time and at most 1h");
        Check(For(30000).RepeatText == "for 30s" && Start("X", 5).RepeatText == "×5" && Start("X").RepeatText == "", "groups say how often they repeat");
        Check(SequenceScript.From(timed).Contains("Date.now() + 1000") && MacroScript.Check(SequenceScript.From(timed)) == null, "a timed group turns into a loop that checks the time");
        var file = Profile(MacroBehaviorType.Sequence); file.Actions = new(timed);
        Check(MacroFile.Read(MacroFile.Write([file]))[0].Actions[0] is { ForMs: 1000, Repeat: 0 } && MacroFile.Write([file]).Contains("\"forMs\": 1000"), "a timed group keeps its time in .keyloom files");

        Check(StepGroups.DepthAt(nested, 3) == 2 && StepGroups.DepthAt(nested, 0) == 0 && StepGroups.EndOf(nested, 0) == 5 && StepGroups.StartOf(nested, 4) == 2, "group structure is found in the flat list");

        // The executor plays groups as expanded.
        var driver = new FakeDriver();
        await using var executor = new MacroExecutor(driver);
        var errors = new ConcurrentQueue<string>(); executor.ExecutionFailed += errors.Enqueue;
        var p = Profile(MacroBehaviorType.Sequence); p.RepeatCount = 1;
        p.Actions = new() { KeyStep("Q"), Start("Loop", 3, 5), KeyStep("A"), KeyStep("B"), End(), KeyStep("W") };
        await executor.ToggleAsync(p); await Until(() => executor.ActiveProfile == null);
        var downs = driver.Events.Where(e => e.EndsWith(":down")).ToArray();
        Check(errors.IsEmpty && downs.SequenceEqual(new[] { "K81:down", "K65:down", "K66:down", "K65:down", "K66:down", "K65:down", "K66:down", "K87:down" }), "sequences play groups with their repeats");
        Check(p.BehaviorSummary.StartsWith("4 steps"), "macro list counts steps, not group markers");

        // Group settings survive saving; plain steps do not gain group fields.
        string folder = Path.Combine(Path.GetTempPath(), "keyloom-groups-" + Guid.NewGuid().ToString("N"));
        var storage = new ProfileStorageService(folder);
        storage.SaveProfiles(new[] { p });
        string json = File.ReadAllText(storage.FilePath);
        var loaded = storage.LoadProfiles().Profiles[0].Actions;
        Check(loaded[1] is { ActionType: MacroActionType.GroupStart, Name: "Loop", Repeat: 3, GapMs: 5 } && loaded[4].ActionType == MacroActionType.GroupEnd
            && json.Split("\"repeat\"").Length == 2,"groups round-trip through storage without bloating other steps");
        Directory.Delete(folder, true);
    }

    private static void GroupUiTests()
    {
        var p = Profile(MacroBehaviorType.Sequence);
        p.Actions = new() { KeyStep("A"), KeyStep("B"), KeyStep("C"), new() { ActionType = MacroActionType.Delay, DelayMs = 50 }, KeyStep("D") };
        var editor = new MacroEditorView(p);
        var steps = (ListBox)editor.FindName("ActionsListBox");
        MacroAction[] Items() => steps.Items.Cast<MacroAction>().ToArray();
        string Layout(MacroAction a, string part) => editor.Dispatcher.Invoke(() =>
            new StepLayoutConverter().Convert([a, steps.ItemsSource, 0], typeof(object), part, System.Globalization.CultureInfo.InvariantCulture).ToString()!);
        bool Visible(string name) => ((FrameworkElement)editor.FindName(name)).Visibility == Visibility.Visible;

        // Ctrl+G groups the picked steps and opens the group editor.
        steps.SelectedItems.Add(steps.Items[1]); steps.SelectedItems.Add(steps.Items[2]);
        Invoke(editor, "GroupSteps", (List<int>)Call(editor, "PickedBlock")!);
        var items = Items();
        Check(items.Length == 7 && items[1].ActionType == MacroActionType.GroupStart && items[4].ActionType == MacroActionType.GroupEnd && items[2].KeyName == "B"
            && steps.SelectedItem == items[1] && Visible("GroupEditor") && !Visible("StepEditor"), "picked steps become a group and the group editor opens");
        Check(Layout(items[1], "Number") == "" && Layout(items[2], "Number") == "2" && Layout(items[5], "Number") == "4" && Layout(items[2], "Indent").StartsWith("22")
            && Layout(items[1], "Children") == "2 steps", "groups indent their steps and are not numbered");

        // Editing the group applies as you type.
        ((TextBox)editor.FindName("GroupNameTextBox")).Text = "Combo";
        ((TextBox)editor.FindName("GroupRunsTextBox")).Text = "3";
        ((TextBox)editor.FindName("GroupGapTextBox")).Text = "100";
        var header = Items()[1];
        Check(header is { Name: "Combo", Repeat: 3, GapMs: 100 } && ((TextBlock)editor.FindName("GroupHintText")).Text.Contains("plays 11 steps"), "group editor sets name, runs and wait");
        ((TextBox)editor.FindName("GroupRunsTextBox")).Text = "0";
        Check(Items()[1].Repeat == 3 && ((TextBlock)editor.FindName("GroupHintText")).Text.Contains("1 to"), "invalid group settings are reported and not applied");
        ((TextBox)editor.FindName("GroupRunsTextBox")).Text = "3";

        // Folding hides the steps; new steps added after a folded header land after the group.
        Invoke(editor, "ToggleFold", Items()[1]);
        Check(Items()[1].Collapsed && Layout(Items()[2], "Visibility") == "Collapsed" && Layout(Items()[4], "Visibility") == "Collapsed" && Layout(Items()[5], "Visibility") == "Visible", "folding a group hides its steps");
        steps.SelectedItems.Clear(); steps.SelectedItem = Items()[1];
        editor.AddStep(MacroActionType.Delay);
        Check(Items()[5].ActionType == MacroActionType.Delay && Items()[5].DelayMs == 100, "steps added on a folded group go after it");
        Invoke(editor, "Undo");
        steps.SelectedItems.Clear(); steps.SelectedItem = Items()[0];
        Invoke(editor, "MoveSteps", new List<int> { 0 }, 1);
        Check(Items()[4].KeyName == "A" && Items()[0].ActionType == MacroActionType.GroupStart, "moving a step down passes a folded group in one move");
        Invoke(editor, "MoveSteps", new List<int> { 4 }, -1);
        Check(Items()[0].KeyName == "A", "moving a step up passes a folded group in one move");

        // Copy, delete and undo work on the whole group.
        steps.SelectedItems.Clear(); steps.SelectedItem = Items()[1];
        Invoke(editor, "Copy", (List<int>)Call(editor, "PickedBlock")!);
        steps.SelectedItems.Clear(); steps.SelectedItem = Items()[^1];
        Invoke(editor, "Paste");
        Check(Items().Length == 11 && Items()[7].ActionType == MacroActionType.GroupStart && Items()[10].ActionType == MacroActionType.GroupEnd && steps.SelectedItem == Items()[7]
            && !ReferenceEquals(Items()[7], Items()[1]), "a copied group pastes whole and picks its header");
        Invoke(editor, "Delete", (List<int>)Call(editor, "PickedBlock")!);
        Check(Items().Length == 7, "deleting a group header deletes the group");
        steps.SelectedItems.Clear(); steps.SelectedItem = Items()[1];
        Invoke(editor, "Ungroup", 1);
        Check(Items().Length == 5 && !Items().Any(a => a.IsGroupMarker) && steps.SelectedItems.Count == 2, "ungroup keeps the steps and picks them");
        Invoke(editor, "Undo");
        Check(Items().Length == 7 && Items()[1].Name == "Combo", "undo brings the group back");
        Save(editor);
        Check(p.Actions.Count == 7 && p.Actions[1] is { ActionType: MacroActionType.GroupStart, Repeat: 3 }, "saving keeps groups");

        // Add step, Repeat: the picked step plays 10 times, or for a time; quick steps get a short wait so the time can pass.
        steps.SelectedItems.Clear(); steps.SelectedItem = Items()[0];
        Invoke(editor, "RepeatSteps", (List<int>)Call(editor, "PickedBlock")!);
        var repeat = Items()[0];
        Check(repeat is { ActionType: MacroActionType.GroupStart, Name: "Repeat", Repeat: 10, ForMs: 0 } && Items()[1].KeyName == "A" && Items()[2].ActionType == MacroActionType.GroupEnd
            && StepView.From(repeat).Detail == "×10", "Repeat puts the picked step in a group that plays 10 times");
        ((RadioButton)editor.FindName("GroupForRadio")).IsChecked = true;
        ((TextBox)editor.FindName("GroupRunsTextBox")).Text = "30s";
        repeat = Items()[0];
        Check(repeat is { ForMs: 30000, Repeat: 0, GapMs: 50 } && StepView.From(repeat).Detail == "for 30s · 50 ms between steps"
            && ((TextBlock)editor.FindName("GroupSummaryText")).Text.StartsWith("Plays its 1 step again and again for 30s, about 601 times"), "a repeat can play for a time, with its pace shown");
        ((RadioButton)editor.FindName("GroupTimesRadio")).IsChecked = true;
        Check(Items()[0] is { ForMs: 0, Repeat: 10 }, "switching back repeats a number of times");
    }
    private static object? Call(object instance, string name, params object[] args) =>
        instance.GetType().GetMethod(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(instance, args);
}
