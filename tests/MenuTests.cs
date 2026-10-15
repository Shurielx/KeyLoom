using System.Windows;
using System.Windows.Controls;
using Keyloom.Models;
using Keyloom.Views;

internal static partial class Program
{
    private static void MenuUiTests()
    {
        var p = Profile(MacroBehaviorType.Sequence);
        p.Actions = new() { KeyStep("A"), KeyStep("B"), KeyStep("C"), new() { ActionType = MacroActionType.Delay, DelayMs = 50 }, KeyStep("D") };
        var editor = new MacroEditorView(p);
        var steps = (ListBox)editor.FindName("ActionsListBox");
        MacroAction[] Items() => steps.Items.Cast<MacroAction>().ToArray();
        string[] Headers() => ((IEnumerable<Control>)Call(editor, "BuildStepMenu")!).OfType<MenuItem>().Select(m => (string)m.Header).ToArray();

        steps.SelectedItem = Items()[1];
        var single = Headers();
        Check(single.Contains("Insert wait before") && single.Contains("Group") && single.Contains("Duplicate several times") && !single.Contains("Insert waits between picked steps")
            && !single.Contains("Ungroup"), "step menu offers wait, group and duplicate actions for one step");

        Invoke(editor, "InsertWait", 1);
        Check(Items()[1] is { ActionType: MacroActionType.Delay, DelayMs: 100 } && steps.SelectedItem == Items()[1]
            && ((FrameworkElement)editor.FindName("StepEditor")).Visibility == Visibility.Visible, "insert wait adds a picked 100 ms wait at the gap");
        Invoke(editor, "Undo");

        // A, B and C picked: waits go after A and B; C is followed by a wait already, and is last anyway.
        steps.SelectedItems.Clear(); foreach (int i in new[] { 0, 1, 2 }) steps.SelectedItems.Add(steps.Items[i]);
        Check(Headers().Contains("Insert waits between picked steps"), "step menu offers waits between several picked steps");
        Invoke(editor, "InsertWaitsBetween");
        var kinds = Items().Select(a => a.ActionType == MacroActionType.Delay ? "w" : a.KeyName).ToArray();
        Check(kinds.SequenceEqual(new[] { "A", "w", "B", "w", "C", "w", "D" }) && steps.SelectedItems.Count == 2
            && ((FrameworkElement)editor.FindName("BulkEditor")).Visibility == Visibility.Visible, "waits between picked steps are added once and picked for a bulk time");
        Invoke(editor, "Undo");

        steps.SelectedItems.Clear(); steps.SelectedItems.Add(steps.Items[0]); steps.SelectedItems.Add(steps.Items[1]);
        Invoke(editor, "DuplicateTimes", new List<int> { 0, 1 }, 3);
        Check(Items().Select(a => a.KeyName).Take(8).SequenceEqual(new[] { "A", "B", "A", "B", "A", "B", "A", "B" }) && Items().Length == 11, "duplicate several times adds the copies in one go");
        Invoke(editor, "Undo");
        Check(Items().Length == 5, "several copies undo as one change");

        // A group header gets fold and ungroup.
        steps.SelectedItems.Clear(); steps.SelectedItems.Add(steps.Items[1]); steps.SelectedItems.Add(steps.Items[2]);
        Invoke(editor, "GroupSteps", (List<int>)Call(editor, "PickedBlock")!);
        var group = Headers();
        Check(group.Contains("Fold group") && group.Contains("Ungroup") && group.Contains("Put in a new group"), "group menu offers fold and ungroup");
        steps.SelectedItem = Items()[1];
        Invoke(editor, "InsertWait", 5);
        Check(Items()[5].ActionType == MacroActionType.Delay && Items()[4].ActionType == MacroActionType.GroupEnd, "insert wait after a group lands after its end");
        steps.SelectedItems.Clear();
        Check(Headers().Length == 0 || Headers().All(h => h == "Paste"), "with nothing picked the menu only offers paste");
    }
}
