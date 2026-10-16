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
    }
    private static object? Call(object instance, string name, params object[] args) =>
        instance.GetType().GetMethod(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(instance, args);
}
