using System.Windows;
using System.Windows.Controls;
using Keyloom.Engine;
using Keyloom.Models;
using Keyloom.Services;
using Keyloom.Views;

internal static partial class Program
{
    private static MacroAction Press(string key) => new() { ActionType = MacroActionType.KeyPress, KeyName = key };
    private static MacroAction If(string target, bool gone = false) => new() { ActionType = MacroActionType.GroupStart, Block = BlockKind.If, Target = target, Gone = gone };
    private static MacroAction Else() => new() { ActionType = MacroActionType.GroupStart, Block = BlockKind.Else };

    private static async Task ConditionEngineTests()
    {
        var steps = new List<MacroAction> { Press("A"), If("X"), Press("B"), End(), Else(), Press("C"), End(), Press("D") };
        string Played(Func<MacroAction, bool>? decide) => string.Join("", StepGroups.Expand(steps, decide).Select(a => a.KeyName));
        Check(Played(_ => true) == "ABD" && Played(_ => false) == "ACD", "an If plays its steps when it holds, and its Else when it does not");
        Check(Played(null) == "ABCD", "without answers, as in the preview, an If and its Else both play");
        var order = new List<string>();
        foreach (var step in StepGroups.Expand(steps, block => { order.Add("ask"); return true; })) order.Add(step.KeyName);
        Check(string.Join(",", order) == "A,ask,B,D", "an If is asked only once the steps before it have played");
        var looped = new List<MacroAction> { new() { ActionType = MacroActionType.GroupStart, Repeat = 2, GapMs = 5 }, If("X"), Press("B"), End(), Else(), Press("C"), End(), End() };
        int asked = 0;
        var played = StepGroups.Expand(looped, _ => asked++ % 2 == 0).Select(a => a.ActionType == MacroActionType.Delay ? "w" : a.KeyName);
        Check(string.Join("", played) == "BwC", "inside a repeated group the If is asked on each pass, and the gap goes between what plays");
        Check(Fails(() => StepGroups.Validate([Else(), Press("C"), End()]), "right after an If") && Fails(() => StepGroups.Validate([Press("A"), If("X"), End(), Press("B"), Else(), End()]), "right after an If"),
            "an Else has to come right after an If");
        var repeated = If("X"); repeated.Repeat = 3;
        Check(Fails(() => StepGroups.Validate([repeated, Press("A"), End()]), "plays its steps once"), "an If does not repeat");

        var screen = new FakeScreen(320, 200);
        var driver = new FakeDriver();
        await using var executor = new MacroExecutor(driver, screen: screen);
        var p = Profile(MacroBehaviorType.Sequence); p.RepeatCount = 1;
        p.Targets.Add(new ScreenTarget { Name = "Chest", Kind = ScreenTargetKind.Color, Color = "#FFCC00", Tolerance = 2 });
        p.Actions = new([If("Chest"), Press("B"), End(), Else(), Press("C"), End()]);
        await executor.ToggleAsync(p); await Until(() => executor.ActiveProfile == null);
        var noChest = driver.Events.ToArray(); driver.Events.Clear();
        screen.Fill(50, 50, 10, 10, 0xFFFFCC00);
        await executor.ToggleAsync(p); await Until(() => executor.ActiveProfile == null);
        var chest = driver.Events.ToArray(); driver.Events.Clear();
        ushort b = KeyResolver.Resolve("B")[0], c = KeyResolver.Resolve("C")[0];
        Check(noChest.SequenceEqual([$"K{c}:down", $"K{c}:up"]) && chest.SequenceEqual([$"K{b}:down", $"K{b}:up"]), "a running macro takes the If or the Else by what is on the screen");
        p.Actions[0].Gone = true;
        await executor.ToggleAsync(p); await Until(() => executor.ActiveProfile == null);
        Check(driver.Events.First() == $"K{c}:down", "an If can ask for the target not to be on the screen");

        p.Actions = new([Press("A"), If("Chest"), Press("B"), End(), Else(), If("Chest", gone: true), Press("C"), End(), End()]);
        p.Actions[1].TimeoutMs = 800; p.Actions[1].Collapsed = true;
        string json = MacroFile.Write([p]);
        var back = MacroFile.Read(json).Single();
        Check(json.Contains("\"do\": \"if\"") && json.Contains("\"else\": [") && json.Contains("\"when\": \"gone\"")
            && back.Actions.Select(a => a.DisplayText).SequenceEqual(p.Actions.Select(a => a.DisplayText)) && back.Actions[1] is { TimeoutMs: 800, Collapsed: true },
            "an If with its Else and an If inside it read back the same");
        Check(json.IndexOf("\"steps\"", json.IndexOf("\"do\": \"if\"")) < json.IndexOf("\"else\""), "an If's own steps are written before its Else");
        Check(Fails(() => MacroFile.Read(json.Replace("\"when\": \"gone\"", "\"when\": \"maybe\"")), "Step 2 else 1: unknown \"when\""), "a mistake inside an Else says where it is");
        Check(Fails(() => MacroFile.Read("{ \"keyloom\": 1, \"macros\": [ { \"name\": \"M\", \"type\": \"sequence\", \"steps\": [ { \"do\": \"press\", \"key\": \"A\", \"else\": [] } ] } ] }"), "only an if can have \"else\""),
            "only an if has an else");
    }

    private static void ConditionUiTests()
    {
        var p = Profile(MacroBehaviorType.Sequence);
        p.Targets.Add(new ScreenTarget { Name = "Chest", Kind = ScreenTargetKind.Color, Color = "#FFCC00" });
        p.Actions.Add(Press("A")); p.Actions.Add(Press("B"));
        var editor = new MacroEditorView(p);
        var host = new Window { Content = editor, Width = 1100, Height = 760, Left = -20000, Top = -20000, ShowActivated = false };
        host.Show(); Pump(200);
        var list = (ListBox)editor.FindName("ActionsListBox");
        list.SelectedIndex = 1; Pump(50);
        Invoke(editor, "MakeBlock", new List<int> { 1 }, BlockKind.If); Pump(100);
        var steps = list.Items.Cast<MacroAction>().ToList();
        var options = (StackPanel)editor.FindName("TargetOptions");
        Check(steps[1] is { Block: BlockKind.If, Target: "Chest" } && ((ContentControl)editor.FindName("IfTargetHost")).Content == options && options.IsVisible
            && ((TextBlock)editor.FindName("GroupTitleText")).Text == "If" && !((TextBox)editor.FindName("GroupNameTextBox")).IsVisible,
            "If puts the picked step in a block that asks for a target");
        ((ComboBox)editor.FindName("UntilBox")).SelectedIndex = 1; Pump(50);
        steps = list.Items.Cast<MacroAction>().ToList();
        Check(steps[1].Gone && ((TextBlock)editor.FindName("GroupSummaryText")).Text.Contains("only when Chest is not on the screen"), "the If's condition is edited in place and spelled out");
        Invoke(editor, "AddElse_Click", editor, new RoutedEventArgs()); Pump(100);
        steps = list.Items.Cast<MacroAction>().ToList();
        Check(steps.Count == 6 && steps[4].Block == BlockKind.Else && ((TextBlock)editor.FindName("GroupTitleText")).Text == "Else" && !((Button)editor.FindName("AddElseButton")).IsVisible,
            "Add Else puts an Else right after the If and picks it");
        Render(editor, 1100, 760, "condition-else.png");
        list.SelectedIndex = 1; Pump(50);
        Check(!((Button)editor.FindName("AddElseButton")).IsVisible, "an If that has its Else offers no second one");
        Render(editor, 1100, 760, "condition-if.png");
        var read = (MacroProfile)editor.GetType().GetMethod("ReadForm", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(editor, [p.Snapshot()])!;
        Check(read.Targets.Single().Name == "Chest", "a target used only by an If is kept on save");
        list.SelectedIndex = 0; Pump(50);
        Check(((ContentControl)editor.FindName("StepTargetHost")).Content == options && !options.IsVisible, "a plain step gets the target fields back, hidden");
        Invoke(editor, "Ungroup", 1); Pump(50);
        steps = list.Items.Cast<MacroAction>().ToList();
        Check(steps.Count == 4 && steps[2] is { Block: BlockKind.Group, Name: "Else" }, "removing an If keeps its Else's steps as a plain group");
        host.Close();
    }
}
