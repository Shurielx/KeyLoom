using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Keyloom.Engine;
using Keyloom.Models;

namespace Keyloom.Views;

/// <summary>The step list: numbering, sizing, picking several steps, editing them together, the step clipboard and undo.</summary>
public partial class MacroEditorView
{
    public static readonly DependencyProperty StepsRevisionProperty = DependencyProperty.Register(nameof(StepsRevision), typeof(int), typeof(MacroEditorView));
    /// <summary>Changes with every edit to the step list so row numbers are recalculated.</summary>
    public int StepsRevision { get => (int)GetValue(StepsRevisionProperty); private set => SetValue(StepsRevisionProperty, value); }
    public static readonly DependencyProperty IsMultiSelectProperty = DependencyProperty.Register(nameof(IsMultiSelect), typeof(bool), typeof(MacroEditorView));
    /// <summary>True while several steps are picked; rows then show their checkboxes.</summary>
    public bool IsMultiSelect { get => (bool)GetValue(IsMultiSelectProperty); private set => SetValue(IsMultiSelectProperty, value); }

    /// <summary>Copied steps, shared by every editor so steps can move between macros.</summary>
    private static MacroAction[] _clipboard = [];
    private sealed record UndoState(MacroAction[] Steps, int[] Picked);
    private const int UndoLimit = 30;
    private readonly List<UndoState> _undo = [], _redo = [];
    private bool _selecting, _editRemembered;

    /// <summary>Room the steps card needs besides the list: its header, the step editor and margins.</summary>
    private const double StepListReserve = 200, StepListMinimum = 216;
    private void InitializeStepList()
    {
        FormScroll.ScrollChanged += (_, e) =>
        {
            // The list has its own scroll viewer whose events bubble up here; only the form's own viewport counts.
            if (e.OriginalSource == FormScroll && e.ViewportHeightChange != 0) ActionsListBox.MaxHeight = Math.Max(StepListMinimum, e.ViewportHeight - StepListReserve);
        };
        _loadingStep = true; BulkMouseBox.SelectedIndex = -1; _loadingStep = false;
    }

    /// <summary>Slides a row in from the direction it came from. Runs after layout so the container exists.</summary>
    private void AnimateStep(MacroAction step, double from = -8) => Dispatcher.BeginInvoke(() =>
    {
        if (ActionsListBox.ItemContainerGenerator.ContainerFromItem(step) is not ListBoxItem row) return;
        var shift = new TranslateTransform(0, from);
        row.RenderTransform = shift;
        var ease = (IEasingFunction)FindResource("EaseOutStrong");
        row.BeginAnimation(OpacityProperty, new DoubleAnimation(from == 0 ? 1 : 0, 1, TimeSpan.FromMilliseconds(220)));
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
    }, DispatcherPriority.Loaded);
    private void AnimateSteps(IEnumerable<int> indices, double from = -8)
    {
        foreach (int i in indices.Take(40)) AnimateStep(_steps[i], from);
    }

    // ---- Picking steps ----

    private List<int> SelectedIndices() => ActionsListBox.SelectedItems.Cast<MacroAction>().Select(_steps.IndexOf).Where(i => i >= 0).Order().ToList();
    /// <summary>Picks exactly these steps, raising one selection update instead of one per step.</summary>
    private void Select(IEnumerable<int> indices)
    {
        var picked = indices.Where(i => i >= 0 && i < _steps.Count).ToList();
        _selecting = true;
        try
        {
            ActionsListBox.SelectedItems.Clear();
            foreach (int i in picked) ActionsListBox.SelectedItems.Add(_steps[i]);
        }
        finally { _selecting = false; }
        _editRemembered = false;
        ShowSelection();
        if (picked.Count > 0) ActionsListBox.ScrollIntoView(_steps[picked[^1]]);
    }
    /// <summary>Row tools act on every picked step when their row is picked, otherwise on their own row.</summary>
    private List<int> Targets(object sender)
    {
        if (sender is not FrameworkElement { Tag: MacroAction a }) return [];
        if (ActionsListBox.SelectedItems.Contains(a)) return PickedBlock();
        int i = _steps.IndexOf(a);
        if (i < 0) return [];
        Select([i]); return WholeGroups([i]);
    }
    /// <summary>Puts keyboard focus on a row so shortcuts keep working after a button click.
    /// With no row left, the list itself takes it, so Ctrl+Z still reaches the steps.</summary>
    private void FocusStep(int index) => Dispatcher.BeginInvoke(() =>
    {
        if (index >= 0 && ActionsListBox.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem row) row.Focus();
        else ActionsListBox.Focus();
    }, DispatcherPriority.Loaded);

    private void ShowBulk()
    {
        // Picked groups count with everything inside them.
        var picked = PickedBlock().Select(i => _steps[i]).ToList();
        var waits = picked.Where(a => a.ActionType == MacroActionType.Delay).ToList();
        var buttons = picked.Where(IsMouseButtonStep).ToList();
        int steps = picked.Count(a => !a.IsGroupMarker), groups = picked.Count(a => a.ActionType == MacroActionType.GroupStart);
        BulkTitle.Text = $"{steps:N0} {(steps == 1 ? "step" : "steps")} picked" + (groups == 0 ? "" : $" in {groups:N0} {(groups == 1 ? "group" : "groups")}");
        BulkInfo.Text = waits.Count == 0 ? "" : $"{waits.Count:N0} {(waits.Count == 1 ? "wait" : "waits")} · {waits.Sum(a => (long)a.DelayMs):N0} ms";
        BulkWaitPanel.Visibility = waits.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BulkButtonPanel.Visibility = buttons.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!BulkWaitTextBox.IsKeyboardFocused) BulkWaitTextBox.Text = waits.Select(a => a.DelayMs).Distinct().Count() == 1 ? waits[0].DelayMs.ToString() : "";
        _loadingStep = true;
        BulkMouseBox.SelectedIndex = buttons.Select(a => a.MouseButton).Distinct().Count() == 1 ? Array.IndexOf(ProfileButtons, buttons[0].MouseButton) : -1;
        _loadingStep = false;
    }
    private static bool IsMouseButtonStep(MacroAction a) => a.ActionType is MacroActionType.MouseClick or MacroActionType.MouseDown or MacroActionType.MouseUp;

    // ---- Editing picked steps ----

    private void EnsureRoom(int adding)
    {
        if (_steps.Count + adding > ProfileValidator.MaxActions) throw new ArgumentException($"A sequence can contain up to {ProfileValidator.MaxActions:N0} steps.");
    }
    /// <summary>Changes copies of the picked steps and swaps them in only if every one is still valid.</summary>
    private void ChangeSelection(Func<MacroAction, bool> change)
    {
        var picked = SelectedIndices();
        var changed = new List<(int Index, MacroAction Step)>();
        // A picked group changes everything inside it; the pick itself stays as it was.
        foreach (int i in WholeGroups(picked))
        {
            var copy = _steps[i].Clone();
            if (!change(copy)) continue;
            ProfileValidator.ValidateAction(copy);
            changed.Add((i, copy));
        }
        if (changed.Count == 0) return;
        Remember();
        _selecting = true;
        try { foreach (var (i, step) in changed) _steps[i] = step; }
        finally { _selecting = false; }
        Select(picked);
    }
    /// <summary>Reads 100 (set), +20 or -20 (add) and x1.5 (scale) into a change for each wait.</summary>
    private static Func<int, int> ReadWaitChange(string text)
    {
        text = text.Replace(" ", "");
        if (text.Length == 0) throw new ArgumentException("Enter a wait time, like 100, +20 or x1.5.");
        if (text[0] is 'x' or 'X' or '*' or '×')
        {
            string factorText = text[1..].Replace(',', '.');
            if (!double.TryParse(factorText, NumberStyles.Float, CultureInfo.InvariantCulture, out double factor) || factor <= 0)
                throw new ArgumentException("Scale waits by a positive number, like x1.5.");
            return ms => (int)Math.Clamp(Math.Round(ms * factor), 1, int.MaxValue);
        }
        int value = Number(text, "Wait time");
        return text[0] is '+' or '-' ? ms => ms + value : _ => value;
    }
    private void ApplyBulkWait()
    {
        var change = ReadWaitChange(BulkWaitTextBox.Text);
        int count = 0;
        ChangeSelection(a =>
        {
            if (a.ActionType != MacroActionType.Delay) return false;
            int next = change(a.DelayMs);
            if (next == a.DelayMs) return false;
            a.DelayMs = next; count++; return true;
        });
        Say(count == 0 ? "Waits already match." : $"Updated {count:N0} {(count == 1 ? "wait" : "waits")}.");
        BulkWaitTextBox.SelectAll();
    }
    private void BulkWait_Click(object sender, RoutedEventArgs e) => Attempt(ApplyBulkWait);
    private void BulkWait_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true; Attempt(ApplyBulkWait);
    }
    private void BulkMouse_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingStep || !IsInitialized || BulkMouseBox.SelectedIndex < 0) return;
        string button = ProfileButtons[BulkMouseBox.SelectedIndex];
        Attempt(() => ChangeSelection(a =>
        {
            if (!IsMouseButtonStep(a) || a.MouseButton == button) return false;
            a.MouseButton = button; return true;
        }));
    }
    private void BulkDuplicate_Click(object sender, RoutedEventArgs e) => Attempt(() => Duplicate(PickedBlock()));
    private void BulkDelete_Click(object sender, RoutedEventArgs e) => Delete(PickedBlock());
    private void BulkClear_Click(object sender, RoutedEventArgs e) => ClearPick();
    private void ClearPick()
    {
        var picked = SelectedIndices();
        if (picked.Count == 0) return;
        Select([picked[0]]); FocusStep(picked[0]);
    }

    private void RowDuplicate_Click(object sender, RoutedEventArgs e) => Attempt(() => Duplicate(Targets(sender)));
    private void RowRemove_Click(object sender, RoutedEventArgs e) => Delete(Targets(sender));

    /// <summary>Inserts copies of the steps as one block after the last of them and picks the copies.</summary>
    private void Duplicate(List<int> picked)
    {
        if (picked.Count == 0) return;
        Insert(picked[^1] + 1, picked.Select(i => _steps[i].Clone()).ToList());
    }
    private void Insert(int at, IReadOnlyList<MacroAction> steps)
    {
        EnsureRoom(steps.Count);
        Remember();
        for (int k = 0; k < steps.Count; k++) _steps.Insert(at + k, steps[k]);
        var inserted = Enumerable.Range(at, steps.Count).ToList();
        var roots = Roots(inserted);
        Select(roots); AnimateSteps(inserted.Where(i => !StepGroups.IsHidden(_steps, i))); FocusStep(roots[^1]);
    }
    private void Delete(List<int> picked)
    {
        if (picked.Count == 0) return;
        int steps = picked.Count(i => !_steps[i].IsGroupMarker);
        bool groups = picked.Any(i => _steps[i].ActionType == MacroActionType.GroupStart);
        Remember();
        _selecting = true;
        try { foreach (int i in Enumerable.Reverse(picked)) _steps.RemoveAt(i); }
        finally { _selecting = false; }
        int next = Math.Min(picked[0], _steps.Count - 1);
        Select(next >= 0 ? [next] : []); FocusStep(next);
        if (picked.Count > 1) Say($"Deleted {steps:N0} {(steps == 1 ? "step" : "steps")}{(groups ? " and their group" : "")}. Ctrl+Z brings them back.");
    }
    /// <summary>Moves every picked step one place, keeping gaps between them. Stops at either end.</summary>
    private void MoveSteps(List<int> picked, int offset)
    {
        if (picked.Count == 0 || picked[0] + offset < 0 || picked[^1] + offset >= _steps.Count) return;
        // A folded group is passed in one move instead of taking the steps inside it, out of sight.
        int neighbour = offset < 0 ? picked[0] - 1 : picked[^1] + 1;
        if (offset < 0 && StepGroups.IsHidden(_steps, neighbour)) { DropSteps(picked, FoldedGroupAround(neighbour)); return; }
        if (offset > 0 && _steps[neighbour] is { ActionType: MacroActionType.GroupStart, Collapsed: true }) { DropSteps(picked, InsertionAfter(neighbour)); return; }
        Remember();
        _selecting = true;
        try { foreach (int i in offset < 0 ? picked : Enumerable.Reverse(picked)) _steps.Move(i, i + offset); }
        finally { _selecting = false; }
        var moved = picked.Select(i => i + offset).ToList();
        var roots = Roots(moved);
        Select(roots); AnimateSteps(moved, offset * -10); FocusStep(offset < 0 ? roots[0] : roots[^1]);
    }
    private void Copy(List<int> picked)
    {
        if (picked.Count == 0) return;
        _clipboard = picked.Select(i => _steps[i].Clone()).ToArray();
        Say($"Copied {picked.Count:N0} {(picked.Count == 1 ? "step" : "steps")}.");
    }
    private void Paste()
    {
        if (_clipboard.Length == 0) return;
        var picked = SelectedIndices();
        Insert(InsertionAfter(picked.Count > 0 ? picked[^1] : -1), _clipboard.Select(a => a.Clone()).ToList());
        Say($"Pasted {_clipboard.Length:N0} {(_clipboard.Length == 1 ? "step" : "steps")}.");
    }

    // ---- Undo ----

    private UndoState Snapshot() => new(_steps.Select(a => a.Clone()).ToArray(), SelectedIndices().ToArray());
    /// <summary>Saves the steps before a change so Ctrl+Z can bring them back.</summary>
    private void Remember()
    {
        _undo.Add(Snapshot());
        if (_undo.Count > UndoLimit) _undo.RemoveAt(0);
        _redo.Clear();
    }
    private void Restore(List<UndoState> from, List<UndoState> to)
    {
        if (from.Count == 0) return;
        to.Add(Snapshot());
        var state = from[^1]; from.RemoveAt(from.Count - 1);
        _selecting = true;
        try { BindSteps(new ObservableCollection<MacroAction>(state.Steps.Select(a => a.Clone()))); }
        finally { _selecting = false; }
        Select(state.Picked); Say("");
        if (state.Picked.Length > 0) FocusStep(state.Picked[^1]);
    }
    private void Undo() => Restore(_undo, _redo);
    private void Redo() => Restore(_redo, _undo);
    /// <summary>Ctrl+Z and Ctrl+Y from anywhere in the editor, for when focus is outside the steps,
    /// like after the last step was deleted. Text fields keep their own undo.</summary>
    public bool UndoShortcut(ModifierKeys mods, Key key)
    {
        if (Keyboard.FocusedElement is TextBoxBase or ComboBox || _dragging) return false;
        Action? action = (mods, key) switch
        {
            (ModifierKeys.Control, Key.Z) => Undo,
            (ModifierKeys.Control, Key.Y) or (ModifierKeys.Control | ModifierKeys.Shift, Key.Z) => Redo,
            _ => null
        };
        if (action == null) return false;
        Attempt(action);
        return true;
    }

    private void Steps_KeyDown(object sender, KeyEventArgs e)
    {
        // Text fields keep their own shortcuts.
        if (Keyboard.FocusedElement is TextBoxBase or ComboBox) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_dragging) { if (key == Key.Escape) EndDrag(false); e.Handled = true; return; }
        var mods = Keyboard.Modifiers;
        Action? action = (mods, key) switch
        {
            (ModifierKeys.None, Key.Delete) => () => Delete(PickedBlock()),
            (ModifierKeys.None, Key.Escape) when IsMultiSelect => ClearPick,
            (ModifierKeys.Control, Key.C) => () => Copy(PickedBlock()),
            (ModifierKeys.Control, Key.X) => () => { var picked = PickedBlock(); Copy(picked); Delete(picked); },
            (ModifierKeys.Control, Key.V) => Paste,
            (ModifierKeys.Control, Key.D) => () => Duplicate(PickedBlock()),
            (ModifierKeys.Control, Key.G) => () => GroupSteps(PickedBlock()),
            (ModifierKeys.Control | ModifierKeys.Shift, Key.G) => () => Ungroup(PickedGroup()),
            (ModifierKeys.Control, Key.Z) => Undo,
            (ModifierKeys.Control, Key.Y) or (ModifierKeys.Control | ModifierKeys.Shift, Key.Z) => Redo,
            (ModifierKeys.Alt, Key.Up) => () => MoveSteps(PickedBlock(), -1),
            (ModifierKeys.Alt, Key.Down) => () => MoveSteps(PickedBlock(), 1),
            _ => null
        };
        if (action == null) return;
        e.Handled = true;
        Attempt(action);
    }
}
