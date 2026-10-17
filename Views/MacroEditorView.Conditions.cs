using System.Windows;
using System.Windows.Controls;
using Keyloom.Engine;
using Keyloom.Models;
using Keyloom.Services;

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
        UntilLabel.Text = L.T("Plays its steps when it");
        TimeoutLabel.Text = L.T("Look for up to");
        TimeoutUnit.Text = L.T("ms · 0 looks once");
        StopIfMissingBox.Visibility = Visibility.Collapsed;
        SetUntilWords(true);
    }
    /// <summary>The same choice reads "shows up or is gone" for Wait until and "is or is not on the screen" for an If.</summary>
    private void SetUntilWords(bool forIf)
    {
        ((ComboBoxItem)UntilBox.Items[0]).Content = forIf ? L.T("Is on the screen") : L.T("Shows up");
        ((ComboBoxItem)UntilBox.Items[1]).Content = forIf ? L.T("Is not on the screen") : L.T("Is gone");
    }

    /// <summary>Fills the block editor for a group, an If or an Else.</summary>
    private void ShowBlock(MacroAction head)
    {
        var kind = head.Block;
        bool group = kind == BlockKind.Group;
        GroupTitleText.Text = kind switch { BlockKind.If => L.T("If"), BlockKind.Else => L.T("Else"), _ => L.T("Group") };
        GroupIconPath.Data = (System.Windows.Media.Geometry)FindResource(kind == BlockKind.If ? "IconTarget" : "IconGroup");
        UngroupText.Text = group ? L.T("Ungroup") : $"Remove {GroupTitleText.Text}";
        UngroupButton.ToolTip = group ? L.T("Keep the steps and remove the group (Ctrl+Shift+G)") : L.T($"Keep the steps and remove the {GroupTitleText.Text}; they then always play");
        GroupNameLabel.Visibility = GroupNameTextBox.Visibility = GroupTimingPanel.Visibility = group ? Visibility.Visible : Visibility.Collapsed;
        int end = StepGroups.EndOf(_steps, _groupStart);
        bool hasElse = end >= 0 && end + 1 < _steps.Count && _steps[end + 1] is { ActionType: MacroActionType.GroupStart, Block: BlockKind.Else };
        AddElseButton.Visibility = kind == BlockKind.If && !hasElse ? Visibility.Visible : Visibility.Collapsed;
        PlaceTargetOptions(kind == BlockKind.If);
        IfTargetHost.Visibility = kind == BlockKind.If && !head.AsksAi ? Visibility.Visible : Visibility.Collapsed;
        IfAskPanel.Visibility = kind == BlockKind.If && head.AsksAi ? Visibility.Visible : Visibility.Collapsed;
        GroupIconPath.Data = (System.Windows.Media.Geometry)FindResource(kind == BlockKind.If ? head.AsksAi ? "IconAi" : "IconTarget" : "IconGroup");
        if (kind != BlockKind.If) return;
        bool loading = _loadingStep;
        _loadingStep = true;
        if (head.AsksAi)
        {
            if (IfQuestionTextBox.Text != head.Question) IfQuestionTextBox.Text = head.Question;
            IfAnswerBox.SelectedIndex = head.Gone ? 1 : 0;
            AskResultText.Text = "";
            _loadingStep = loading;
            return;
        }
        ShowIfOptions();
        TargetBox.SelectedItem = _targets.FirstOrDefault(t => string.Equals(t.Name, head.Target, StringComparison.OrdinalIgnoreCase))?.Name;
        UntilBox.SelectedIndex = head.Gone ? 1 : 0;
        TimeoutTextBox.Text = TimeText.Format(head.TimeoutMs);
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
            BlockKind.If when head.AsksAi => head.Gone ? L.T($"Plays its {steps} only when the AI answers no. It asks each time the macro gets here.") : L.T($"Plays its {steps} only when the AI answers yes. It asks each time the macro gets here."),
            BlockKind.If => head.TimeoutMs > 0 ? L.T($"Plays its {steps} only when {head.Condition}, looking for up to {head.TimeoutMs:N0} ms.") : L.T($"Plays its {steps} only when {head.Condition}, checked once."),
            BlockKind.Else => L.T($"Plays its {steps} when the If above does not."),
            _ => ""
        };
    }

    /// <summary>Writes the target fields into the picked If.</summary>
    private void CommitIf()
    {
        try
        {
            var copy = _steps[_groupStart].Clone();
            if (copy.AsksAi)
            {
                copy.Question = IfQuestionTextBox.Text.Trim().Length > 0 ? IfQuestionTextBox.Text.Trim() : throw new ArgumentException(L.T("Write a yes-or-no question for the AI."));
                copy.Gone = IfAnswerBox.SelectedIndex == 1;
            }
            else
            {
                copy.Target = TargetBox.SelectedItem as string ?? throw new ArgumentException(L.T("Pick what the If looks for, or take a new picture or color from the screen."));
                copy.Gone = UntilBox.SelectedIndex == 1;
                copy.TimeoutMs = Time(TimeoutTextBox.Text, L.T("Time to look"));
            }
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
    /// <summary>Puts the picked steps in an If that asks the AI, and opens its question.</summary>
    private void AddAiIf_Click(object sender, RoutedEventArgs e) => Attempt(() =>
    {
        MakeBlock(PickedBlock(), BlockKind.If);
        if (!IfPicked) return;
        var head = _steps[_groupStart].Clone();
        head.Target = null; head.Question = L.T("Is a shop window open?");
        _committing = true;
        try { _steps[_groupStart] = head; ActionsListBox.SelectedItem = head; }
        finally { _committing = false; }
        ShowSelection();
        Dispatcher.BeginInvoke(() => { IfQuestionTextBox.Focus(); IfQuestionTextBox.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Loaded);
    });
    private void IfQuestion_Changed(object sender, EventArgs e)
    {
        if (!_loadingStep && IsInitialized && IfPicked) CommitIf();
    }
    /// <summary>Asks the If's question now, so the answer can be checked before the macro runs.</summary>
    private async void AskNow_Click(object sender, RoutedEventArgs e)
    {
        string question = IfQuestionTextBox.Text.Trim();
        if (question.Length == 0) return;
        if (AppSettings.Current.AiSetup is not { } setup) { AskResultText.Text = AiEyes.OffMessage; return; }
        AskResultText.Text = L.T($"Asking {setup.Model}...");
        try
        {
            var answer = await OutOfTheWay(async () =>
            {
                IntPtr window = _window != null ? WindowSystem.Instance.Find(_window) : IntPtr.Zero;
                var picture = new AiEyes(() => null, ScreenCapture.Instance, WindowSystem.Instance, window).Everything;
                var shot = ScreenCapture.Instance.Capture(picture.X, picture.Y, picture.Width, picture.Height);
                return await Task.Run(() => new AiVision(setup).Ask(question + "\nAnswer only yes or no.", shot, CancellationToken.None));
            });
            if (answer == null) return;
            bool? yes = AiVision.YesNo(answer);
            bool plays = yes == true != (IfAnswerBox.SelectedIndex == 1);
            AskResultText.Text = plays ? L.T($"The AI answered \"{answer}\", so the steps would play now.") : L.T($"The AI answered \"{answer}\", so the steps would not play now.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { AskResultText.Text = ex.Message; }
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
        Say(L.T("Else added. Drag steps into it, or add steps after picking it."));
    });
}
