using System.Windows;
using System.Windows.Controls;
using Keyloom.Engine;
using Keyloom.Models;

namespace Keyloom.Views;

/// <summary>If and Else: blocks in the step list that play only when a target is, or is not, on the screen.</summary>
public partial class MacroEditorView
{
    /// <summary>True while the picked block is an If, so the target fields edit its header instead of a step.</summary>
    private bool IfPicked => _groupStart >= 0 && _groupStart < _steps.Count && _steps[_groupStart].Block == BlockKind.If && GroupEditor.Visibility == Visibility.Visible;

    /// <summary>The target fields sit in the step editor, or in the block editor while an If is picked.</summary>
    private void PlaceTargetOptions(bool inIf)
    {
        var host = inIf ? IfTargetHost : StepTargetHost;
        if (host.Content == TargetOptions) return;
        StepTargetHost.Content = null; IfTargetHost.Content = null;
        host.Content = TargetOptions;
    }
    private void ShowIfOptions()
    {
        TargetOptions.Visibility = Visibility.Visible;
        UntilPanel.Visibility = Visibility.Visible;
        UntilLabel.Text = "Plays its steps when it";
        TimeoutLabel.Text = "Look for up to";
        TimeoutUnit.Text = "ms · 0 looks once";
        StopIfMissingBox.Visibility = Visibility.Collapsed;
        SetUntilWords(true);
    }
    /// <summary>The same choice reads "shows up or is gone" for Wait until and "is or is not on the screen" for an If.</summary>
    private void SetUntilWords(bool forIf)
    {
        ((ComboBoxItem)UntilBox.Items[0]).Content = forIf ? "Is on the screen" : "Shows up";
        ((ComboBoxItem)UntilBox.Items[1]).Content = forIf ? "Is not on the screen" : "Is gone";
    }

    /// <summary>Fills the block editor for a group, an If or an Else.</summary>
    private void ShowBlock(MacroAction head)
    {
        var kind = head.Block;
        bool group = kind == BlockKind.Group;
        GroupTitleText.Text = kind switch { BlockKind.If => "If", BlockKind.Else => "Else", _ => "Group" };
        GroupIconPath.Data = (System.Windows.Media.Geometry)FindResource(kind == BlockKind.If ? "IconTarget" : "IconGroup");
        UngroupText.Text = group ? "Ungroup" : $"Remove {GroupTitleText.Text}";
        UngroupButton.ToolTip = group ? "Keep the steps and remove the group (Ctrl+Shift+G)" : $"Keep the steps and remove the {GroupTitleText.Text}; they then always play";
        GroupNameLabel.Visibility = GroupNameTextBox.Visibility = GroupTimingPanel.Visibility = group ? Visibility.Visible : Visibility.Collapsed;
        int end = StepGroups.EndOf(_steps, _groupStart);
        bool hasElse = end >= 0 && end + 1 < _steps.Count && _steps[end + 1] is { ActionType: MacroActionType.GroupStart, Block: BlockKind.Else };
        AddElseButton.Visibility = kind == BlockKind.If && !hasElse ? Visibility.Visible : Visibility.Collapsed;
        PlaceTargetOptions(kind == BlockKind.If);
        IfTargetHost.Visibility = kind == BlockKind.If ? Visibility.Visible : Visibility.Collapsed;
        if (kind != BlockKind.If) return;
        bool loading = _loadingStep;
        _loadingStep = true;
        ShowIfOptions();
        TargetBox.SelectedItem = _targets.FirstOrDefault(t => string.Equals(t.Name, head.Target, StringComparison.OrdinalIgnoreCase))?.Name;
        UntilBox.SelectedIndex = head.Gone ? 1 : 0;
        TimeoutTextBox.Text = head.TimeoutMs.ToString();
        FindResultText.Text = "";
        _loadingStep = loading;
        ShowTargetCard();
    }
    /// <summary>One sentence on when the block plays, for the details under the block editor.</summary>
    private static string BlockSummary(MacroAction head, int inside)
    {
        string steps = $"{inside:N0} {(inside == 1 ? "step" : "steps")}";
        return head.Block switch
        {
            BlockKind.If => $"Plays its {steps} only when {head.Condition}" + (head.TimeoutMs > 0 ? $", looking for up to {head.TimeoutMs:N0} ms." : ", checked once."),
            BlockKind.Else => $"Plays its {steps} when the If above does not.",
            _ => ""
        };
    }

    /// <summary>Writes the target fields into the picked If.</summary>
    private void CommitIf()
    {
        try
        {
            var copy = _steps[_groupStart].Clone();
            copy.Target = TargetBox.SelectedItem as string ?? throw new ArgumentException("Pick what the If looks for, or take a new picture or color from the screen.");
            copy.Gone = UntilBox.SelectedIndex == 1;
            copy.TimeoutMs = Number(TimeoutTextBox.Text, "Time to look");
            var check = _steps.ToList(); check[_groupStart] = copy;
            StepGroups.Validate(check);
            if (!_editRemembered) { Remember(); _editRemembered = true; }
            bool headerPicked = ActionsListBox.SelectedItem == _steps[_groupStart];
            _committing = true;
            _steps[_groupStart] = copy;
            if (headerPicked) ActionsListBox.SelectedItem = copy;
            ShowGroupHint("");
        }
        catch (ArgumentException ex) { ShowGroupHint(ex.Message); }
        finally { _committing = false; }
    }

    private void AddIf_Click(object sender, RoutedEventArgs e)
    {
        Attempt(() => MakeBlock(PickedBlock(), BlockKind.If));
        if (_targets.Count == 0 && IfPicked) Dispatcher.BeginInvoke(() => CaptureAsync(false));
    }
    /// <summary>Adds an empty Else right after the picked If and picks it.</summary>
    private void AddElse_Click(object sender, RoutedEventArgs e) => Attempt(() =>
    {
        if (!IfPicked) return;
        int end = StepGroups.EndOf(_steps, _groupStart);
        if (end < 0) return;
        EnsureRoom(2);
        Remember();
        var start = new MacroAction { ActionType = MacroActionType.GroupStart, Block = BlockKind.Else };
        _selecting = true;
        try
        {
            _steps.Insert(end + 1, start);
            _steps.Insert(end + 2, new MacroAction { ActionType = MacroActionType.GroupEnd });
        }
        finally { _selecting = false; }
        Select([end + 1]); AnimateSteps([end + 1, end + 2], -6); FocusStep(end + 1);
        Say("Else added. Drag steps into it, or add steps after picking it.");
    });
}
