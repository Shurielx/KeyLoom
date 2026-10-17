using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Keyloom.Engine;
using Keyloom.Models;
using Keyloom.Services;

namespace Keyloom.Views;

/// <summary>Groups in the step list: making and removing them, folding, and the group editor.</summary>
public partial class MacroEditorView
{
    private int _groupStart = -1;

    /// <summary>The picked steps with every picked group taken whole, from its header to its end.</summary>
    private List<int> PickedBlock() => WholeGroups(SelectedIndices());
    private List<int> WholeGroups(IEnumerable<int> indices)
    {
        var block = new SortedSet<int>();
        foreach (int i in indices)
        {
            int start = _steps[i].ActionType switch
            {
                MacroActionType.GroupStart => i,
                MacroActionType.GroupEnd => StepGroups.StartOf(_steps, i),
                _ => -1
            };
            if (start < 0) { block.Add(i); continue; }
            int end = StepGroups.EndOf(_steps, start);
            for (int k = start; k <= (end < 0 ? _steps.Count - 1 : end); k++) block.Add(k);
        }
        return block.ToList();
    }
    /// <summary>The outermost steps of a sorted block: steps inside a group that is also in the block are left out, so picking a moved group picks its header.</summary>
    private List<int> Roots(IEnumerable<int> block)
    {
        var roots = new List<int>();
        int coveredUntil = -1;
        foreach (int i in block)
        {
            if (i <= coveredUntil) continue;
            roots.Add(i);
            if (_steps[i].ActionType == MacroActionType.GroupStart)
                coveredUntil = StepGroups.EndOf(_steps, i) is var end and >= 0 ? end : _steps.Count - 1;
        }
        return roots;
    }
    /// <summary>Where a step added after <paramref name="last"/> goes: after a folded group rather than hidden inside it.</summary>
    private int InsertionAfter(int last)
    {
        if (last < 0) return _steps.Count;
        if (_steps[last] is { ActionType: MacroActionType.GroupStart, Collapsed: true })
            return StepGroups.EndOf(_steps, last) is var end and >= 0 ? end + 1 : _steps.Count;
        return last + 1;
    }

    /// <summary>Header of the outermost folded group hiding the step at <paramref name="index"/>.</summary>
    private int FoldedGroupAround(int index)
    {
        var open = new Stack<int>();
        for (int i = 0; i < index; i++)
        {
            if (_steps[i].ActionType == MacroActionType.GroupStart) open.Push(i);
            else if (_steps[i].ActionType == MacroActionType.GroupEnd && open.Count > 0) open.Pop();
        }
        return open.Reverse().FirstOrDefault(i => _steps[i].Collapsed, index);
    }

    /// <summary>Puts the steps in a new group where the first of them was. Steps that were apart are gathered in order.</summary>
    private void GroupSteps(List<int> block) => MakeBlock(block, BlockKind.Group);
    /// <summary>Puts the steps in a new group that plays them 10 times, ready to set how often or for how long.</summary>
    private void RepeatSteps(List<int> block) => WrapSteps(block, BlockKind.Group, repeat: true);
    /// <summary>Puts the steps in a new group or If where the first of them was.</summary>
    private void MakeBlock(List<int> block, BlockKind kind) => WrapSteps(block, kind, repeat: false);
    private void WrapSteps(List<int> block, BlockKind kind, bool repeat)
    {
        EnsureRoom(2);
        Remember();
        var start = kind == BlockKind.If
            ? new MacroAction { ActionType = MacroActionType.GroupStart, Block = BlockKind.If, Target = _targets.FirstOrDefault()?.Name }
            : repeat ? new MacroAction { ActionType = MacroActionType.GroupStart, Name = NextGroupName(L.T("Repeat")), Repeat = 10 }
            : new MacroAction { ActionType = MacroActionType.GroupStart, Name = NextGroupName() };
        int at = block.Count > 0 ? block[0] : InsertionAfter(SelectedIndices().LastOrDefault(-1));
        var moving = block.Select(i => _steps[i]).ToList();
        _selecting = true;
        try
        {
            foreach (int i in Enumerable.Reverse(block)) _steps.RemoveAt(i);
            _steps.Insert(at, start);
            for (int k = 0; k < moving.Count; k++) _steps.Insert(at + 1 + k, moving[k]);
            _steps.Insert(at + 1 + moving.Count, new MacroAction { ActionType = MacroActionType.GroupEnd });
        }
        finally { _selecting = false; }
        Select([at]); AnimateSteps(Enumerable.Range(at, moving.Count + 2), -6); FocusStep(at);
        var field = repeat ? GroupRunsTextBox : GroupNameTextBox;
        if (kind == BlockKind.Group) Dispatcher.BeginInvoke(() => { field.Focus(); field.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Loaded);
    }
    /// <summary>"Group 1", "Group 2"; with a <paramref name="word"/> like Repeat: "Repeat", then "Repeat 2".</summary>
    private string NextGroupName(string? word = null)
    {
        var names = _steps.Where(a => a.ActionType == MacroActionType.GroupStart).Select(a => a.GroupName).ToHashSet();
        if (word != null && !names.Contains(word)) return word;
        for (int n = word == null ? 1 : 2; ; n++) if (!names.Contains($"{word ?? "Group"} {n}")) return $"{word ?? "Group"} {n}";
    }
    /// <summary>Removes a group's header and end and keeps its steps where they are.</summary>
    private void Ungroup(int start)
    {
        if (start < 0 || _steps[start].ActionType != MacroActionType.GroupStart) return;
        int end = StepGroups.EndOf(_steps, start);
        Remember();
        _selecting = true;
        try
        {
            // An Else left without its If keeps its steps as a plain group.
            if (_steps[start].Block == BlockKind.If && end >= 0 && end + 1 < _steps.Count && _steps[end + 1] is { ActionType: MacroActionType.GroupStart, Block: BlockKind.Else } orphan)
            {
                var kept = orphan.Clone(); kept.Block = BlockKind.Group; kept.Name = L.T("Else");
                _steps[end + 1] = kept;
            }
            if (end >= 0) _steps.RemoveAt(end);
            _steps.RemoveAt(start);
        }
        finally { _selecting = false; }
        int count = (end < 0 ? _steps.Count : end - 1) - start;
        Select(count > 0 ? Enumerable.Range(start, count) : []);
        if (count > 0) FocusStep(start);
    }
    /// <summary>The group a picked header or end belongs to, or -1.</summary>
    private int PickedGroup() => ActionsListBox.SelectedItems.Count == 1 && ActionsListBox.SelectedItem is MacroAction a && _steps.IndexOf(a) is var i and >= 0
        ? a.ActionType switch { MacroActionType.GroupStart => i, MacroActionType.GroupEnd => StepGroups.StartOf(_steps, i), _ => -1 } : -1;

    private void ToggleFold(MacroAction header)
    {
        int i = _steps.IndexOf(header);
        if (i < 0 || header.ActionType != MacroActionType.GroupStart) return;
        var copy = header.Clone(); copy.Collapsed = !header.Collapsed;
        bool picked = ActionsListBox.SelectedItems.Contains(header);
        var keep = SelectedIndices();
        _selecting = true;
        try { _steps[i] = copy; }
        finally { _selecting = false; }
        // Steps folded away stop being picked; the header stays picked.
        Select(copy.Collapsed ? keep.Where(k => k == i || !StepGroups.IsHidden(_steps, k)) : keep);
        if (picked) FocusStep(i);
    }
    private void Fold_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: MacroAction header }) ToggleFold(header);
    }
    private void Steps_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Ancestor<ListBoxItem>(e.OriginalSource) is { DataContext: MacroAction { ActionType: MacroActionType.GroupStart } header })
        {
            e.Handled = true; ToggleFold(header);
        }
    }
    private void AddGroup_Click(object sender, RoutedEventArgs e) => Attempt(() => GroupSteps(PickedBlock()));
    private void AddRepeat_Click(object sender, RoutedEventArgs e) => Attempt(() => RepeatSteps(PickedBlock()));
    private void BulkGroup_Click(object sender, RoutedEventArgs e) => Attempt(() => GroupSteps(PickedBlock()));
    private void Ungroup_Click(object sender, RoutedEventArgs e) => Ungroup(_groupStart);

    /// <summary>Shows the group editor for the group whose header is at <paramref name="start"/>.</summary>
    private void ShowGroup(int start)
    {
        _groupStart = start;
        var g = _steps[start];
        _loadingStep = true;
        GroupNameTextBox.Text = g.Name ?? "";
        (g.ForMs > 0 ? GroupForRadio : GroupTimesRadio).IsChecked = true;
        GroupRunsTextBox.Text = g.ForMs > 0 ? TimeText.Format(g.ForMs) : g.Runs.ToString();
        GroupRunsUnit.Text = g.ForMs > 0 ? "" : L.T("times");
        GroupGapTextBox.Text = TimeText.Format(g.GapMs);
        _loadingStep = false;
        ShowBlock(g);
        ShowGroupHint("");
    }
    private void ShowGroupHint(string error)
    {
        _stepError = error;
        GroupHintText.SetResourceReference(TextBlock.ForegroundProperty, error.Length > 0 ? "DangerTextBrush" : "MutedBrush");
        GroupHintText.Visibility = Visibility.Visible;
        if (error.Length > 0) { GroupHintText.Text = error; return; }
        if (_groupStart < 0 || _groupStart >= _steps.Count) return;
        int end = StepGroups.EndOf(_steps, _groupStart);
        var group = _steps.Skip(_groupStart).Take(end < 0 ? _steps.Count - _groupStart : end - _groupStart + 1).ToList();
        int inside = group.Count(a => !a.IsGroupMarker);
        var played = StepGroups.Expand(group).ToList();
        double seconds = MacroTimeline.Build(played).Duration / 1000;
        GroupHintText.Text = inside == 0 ? (group[0].Block == BlockKind.Group ? L.T("Empty group. Drag steps into it, or pick steps and press Ctrl+G.")
                : L.T($"Empty {group[0].GroupName}. Drag steps into it, or add a step while it is picked."))
            : L.T($"{L.N(inside, "{0} step", "{0} steps")} inside · plays {L.N(played.Count, "{0} step", "{0} steps")} in {seconds:0.0} s");
        ShowGroupInfo(group, inside, played.Count);
    }
    /// <summary>Spells out what the group does, when it starts, how long a pass takes and which steps it holds.</summary>
    private void ShowGroupInfo(List<MacroAction> group, int inside, int playedCount)
    {
        var head = group[0];
        GroupInfo.Visibility = inside == 0 ? Visibility.Collapsed : Visibility.Visible;
        // The details say it all; the one-line hint stays for empty groups and mistakes.
        GroupHintText.Visibility = inside == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (inside == 0) return;
        var once = group.Select(a => a.Clone()).ToList();
        once[0].Repeat = 0; once[0].ForMs = 0;
        double pass = MacroTimeline.Build(StepGroups.Expand(once).ToList()).Duration, total = MacroTimeline.Build(StepGroups.Expand(group).ToList()).Duration;
        long passes = StepGroups.PassesOf(group, 0);
        GroupSummaryText.Text = (head.ForMs > 0 ? L.T($"Plays its {L.N(inside, "{0} step", "{0} steps")} again and again for {TimeText.Display(head.ForMs)}, about {L.N(passes, "{0} time", "{0} times")}") : head.Runs == 1 ? L.T($"Plays its {L.N(inside, "{0} step", "{0} steps")} once") : L.T($"Plays its {L.N(inside, "{0} step", "{0} steps")} {L.N(head.Runs, "{0} time", "{0} times")}"))
            + (head.GapMs == 0 ? "." : head.Repeats ? L.T($", waiting {head.GapMs:N0} ms between steps and between passes.") : L.T($", waiting {head.GapMs:N0} ms between steps."))
            + (playedCount > inside ? " " + L.T($"That is {L.N(playedCount, "{0} step", "{0} steps")} in a run.") : "");
        if (head.Block != BlockKind.Group) GroupSummaryText.Text = BlockSummary(head, inside);
        GroupStartText.Text = (string)new StepLayoutConverter().Convert([head, _steps, StepsRevision], typeof(string), "Time", System.Globalization.CultureInfo.CurrentCulture);
        GroupPassText.Text = StepLayoutConverter.FormatTime(pass);
        GroupTotalText.Text = StepLayoutConverter.FormatTime(total);
        // The group's own steps, nested groups as one line each.
        var lines = new List<string>();
        for (int i = 1, depth = 0; i < group.Count - 1; i++)
        {
            var a = group[i];
            if (a.ActionType == MacroActionType.GroupEnd) { depth--; continue; }
            if (depth == 0) lines.Add((a.ActionType == MacroActionType.GroupStart ? "" : "· ") + a.DisplayText);
            if (a.ActionType == MacroActionType.GroupStart) depth++;
        }
        const int Shown = 8;
        if (lines.Count > Shown) lines = [.. lines.Take(Shown - 1), L.T($"and {lines.Count - Shown + 1:N0} more")];
        GroupStepsList.ItemsSource = lines;
    }
    private void GroupField_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loadingStep || !IsInitialized || _groupStart < 0 || _groupStart >= _steps.Count) return;
        try
        {
            var copy = _steps[_groupStart].Clone();
            copy.Name = GroupNameTextBox.Text.Trim() is { Length: > 0 } name ? name : null;
            if (GroupForRadio.IsChecked == true)
            {
                int ms = Time(GroupRunsTextBox.Text, L.T("Repeat for"));
                if (ms is < 1 or > StepGroups.MaxForMs) throw new ArgumentException(L.T("A group can repeat for 1 ms up to 1h."));
                copy.ForMs = ms; copy.Repeat = 0;
            }
            else
            {
                int runs = Number(GroupRunsTextBox.Text, L.T("Times"));
                if (runs is < 1 or > StepGroups.MaxRepeat) throw new ArgumentException(L.T($"A group can play 1 to {StepGroups.MaxRepeat:N0} times."));
                copy.Repeat = runs == 1 ? 0 : runs; copy.ForMs = 0;
            }
            copy.GapMs = Time(GroupGapTextBox.Text, L.T("Wait between steps"));
            var check = _steps.ToList(); check[_groupStart] = copy;
            StepGroups.Validate(check);
            if (!_editRemembered) { Remember(); _editRemembered = true; }
            bool headerPicked = ActionsListBox.SelectedItem == _steps[_groupStart];
            _committing = true;
            _steps[_groupStart] = copy;
            if (headerPicked) ActionsListBox.SelectedItem = copy;
            GroupIconPath.Data = (System.Windows.Media.Geometry)FindResource(copy.Repeats ? "IconRepeat" : "IconGroup");
            GroupTitleText.Text = copy.Repeats ? L.T("Repeat") : L.T("Group");
            ShowGroupHint("");
        }
        catch (ArgumentException ex) { ShowGroupHint(ex.Message); }
        finally { _committing = false; }
    }
    /// <summary>Switches the group between a number of times and a time. A time needs the steps to take time, so a group of
    /// quick steps gets a short wait between them, shown in its field.</summary>
    private void GroupRepeatMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingStep || !IsInitialized || _groupStart < 0 || _groupStart >= _steps.Count) return;
        var g = _steps[_groupStart];
        bool timed = GroupForRadio.IsChecked == true;
        GroupRunsUnit.Text = timed ? "" : L.T("times");
        if (timed && g.GapMs == 0 && StepGroups.PassMs(_steps, _groupStart) <= 0)
        {
            _loadingStep = true; GroupGapTextBox.Text = "50"; _loadingStep = false;
        }
        GroupRunsTextBox.Text = timed ? "10s" : "10";
        GroupRunsTextBox.Focus(); GroupRunsTextBox.SelectAll();
    }
}
