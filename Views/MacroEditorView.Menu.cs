using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Keyloom.Engine;
using Keyloom.Models;

namespace Keyloom.Views;

/// <summary>Quicker editing: the right-click menu on steps and the + that inserts a wait between two rows.</summary>
public partial class MacroEditorView
{
    private const int DefaultWaitMs = 100;
    /// <summary>How close to the line between two rows the pointer must be to offer the + button.</summary>
    private const double GapReach = 6;
    private int _gapAt = -1;

    // ---- Right-click menu ----

    private void Steps_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Right-clicking a row outside the pick picks just that row, like Explorer.
        if (Ancestor<ListBoxItem>(e.OriginalSource) is { DataContext: MacroAction step, IsSelected: false }) Select([_steps.IndexOf(step)]);
        StepMenu.Items.Clear();
        foreach (var item in BuildStepMenu()) StepMenu.Items.Add(item);
        if (StepMenu.Items.Count == 0) e.Handled = true;
    }
    private IEnumerable<Control> BuildStepMenu()
    {
        var block = PickedBlock();
        if (block.Count == 0)
        {
            if (_clipboard.Length > 0) yield return Item(L.T("Paste"), null, "Ctrl+V", Paste);
            yield break;
        }
        var picked = SelectedIndices();
        int group = PickedGroup();
        yield return Item(L.T("Insert wait before"), "IconClock", null, () => InsertWait(block[0]));
        yield return Item(L.T("Insert wait after"), "IconClock", null, () => InsertWait(block[^1] + 1));
        if (picked.Count(i => !_steps[i].IsGroupMarker) > 1)
            yield return Item(L.T("Insert waits between picked steps"), "IconClock", null, InsertWaitsBetween);
        yield return new Separator();
        if (group >= 0)
        {
            bool folded = _steps[group].Collapsed;
            yield return Item(folded ? L.T("Unfold group") : L.T("Fold group"), "IconChevron", null, () => ToggleFold(_steps[group]));
            yield return Item(L.T("Ungroup"), "IconGroup", "Ctrl+Shift+G", () => Ungroup(group));
        }
        yield return Item(group >= 0 ? L.T("Put in a new group") : L.T("Group"), "IconGroup", "Ctrl+G", () => GroupSteps(PickedBlock()));
        yield return new Separator();
        yield return Item(L.T("Duplicate"), "IconCopy", "Ctrl+D", () => Duplicate(PickedBlock()));
        var copies = Item(L.T("Duplicate several times"), "IconCopy", null, null);
        foreach (int n in new[] { 2, 3, 5, 10 }) copies.Items.Add(Item(L.N(n, "{0} copy", "{0} copies"), null, null, () => DuplicateTimes(PickedBlock(), n)));
        yield return copies;
        yield return Item(L.T("Copy"), null, "Ctrl+C", () => Copy(PickedBlock()));
        yield return Item(L.T("Cut"), null, "Ctrl+X", () => { var cut = PickedBlock(); Copy(cut); Delete(cut); });
        var paste = Item(L.T("Paste"), null, "Ctrl+V", Paste);
        paste.IsEnabled = _clipboard.Length > 0;
        yield return paste;
        yield return new Separator();
        var delete = Item(L.T("Delete"), "IconTrash", "Delete", () => Delete(PickedBlock()));
        if (delete.Icon is Path icon) icon.SetResourceReference(Shape.FillProperty, "DangerIconBrush");
        yield return delete;
    }
    private MenuItem Item(string header, string? icon, string? gesture, Action? action)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
        if (icon != null) item.Icon = new Path { Data = (Geometry)FindResource(icon), Style = (Style)FindResource("MenuIcon") };
        if (action != null) item.Click += (_, _) => Attempt(action);
        return item;
    }

    /// <summary>Inserts a wait at <paramref name="at"/> and opens it with its time selected, ready to type.</summary>
    private void InsertWait(int at)
    {
        Insert(Math.Clamp(at, 0, _steps.Count), [new MacroAction { ActionType = MacroActionType.Delay, DelayMs = DefaultWaitMs }]);
        Dispatcher.BeginInvoke(() => { StepValueTextBox.Focus(); StepValueTextBox.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Loaded);
    }
    /// <summary>Adds a wait after each picked step but the last, unless a wait is already there, and picks the new waits so their time can be set at once.</summary>
    private void InsertWaitsBetween()
    {
        var picked = SelectedIndices().Where(i => !_steps[i].IsGroupMarker).ToList();
        var after = picked.Take(picked.Count - 1)
            .Where(i => _steps[i].ActionType != MacroActionType.Delay && (i + 1 >= _steps.Count || _steps[i + 1].ActionType != MacroActionType.Delay)).ToList();
        if (after.Count == 0) { Say(L.T("The picked steps already have waits between them.")); return; }
        EnsureRoom(after.Count);
        Remember();
        var waits = new List<MacroAction>();
        _selecting = true;
        try
        {
            foreach (int i in Enumerable.Reverse(after))
            {
                var wait = new MacroAction { ActionType = MacroActionType.Delay, DelayMs = DefaultWaitMs };
                _steps.Insert(i + 1, wait); waits.Add(wait);
            }
        }
        finally { _selecting = false; }
        var added = waits.Select(_steps.IndexOf).Order().ToList();
        Select(added); AnimateSteps(added);
        Say(L.T($"Added {L.N(added.Count, "{0} wait", "{0} waits")}. Set their time below."));
        Dispatcher.BeginInvoke(() => { BulkWaitTextBox.Focus(); BulkWaitTextBox.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Loaded);
    }
    /// <summary>Adds <paramref name="copies"/> copies of the steps right after them, as one change.</summary>
    private void DuplicateTimes(List<int> block, int copies)
    {
        if (block.Count == 0) return;
        Insert(block[^1] + 1, Enumerable.Range(0, copies).SelectMany(_ => block.Select(i => _steps[i].Clone())).ToList());
        Say(L.T($"Added {L.N(copies, "{0} copy", "{0} copies")}."));
    }

    // ---- + between rows ----

    private void InitializeGapButton()
    {
        ActionsListBox.MouseMove += (_, e) => UpdateGap(e.GetPosition(ActionsListBox));
        ActionsListBox.MouseLeave += (_, _) => HideGapLater();
        GapButton.MouseLeave += (_, _) => HideGapLater();
        ActionsListBox.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) => { if (e.VerticalChange != 0) HideGap(); }));
    }
    /// <summary>Shows the + on the line between two visible rows when the pointer is close to it.</summary>
    private void UpdateGap(Point at)
    {
        if (_dragging || _pressed != null && Mouse.LeftButton == MouseButtonState.Pressed) { HideGap(); return; }
        int gap = -1; double y = 0, best = GapReach;
        int last = -1; double lastBottom = 0;
        for (int i = 0; i < _steps.Count; i++)
        {
            if (ActionsListBox.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem { IsVisible: true } row) continue;
            double top = row.TranslatePoint(new Point(), ActionsListBox).Y;
            if (top > ActionsListBox.ActualHeight) break;
            if (Math.Abs(at.Y - top) < best) { best = Math.Abs(at.Y - top); gap = i; y = top; }
            last = i; lastBottom = top + row.ActualHeight;
        }
        if (last >= 0 && Math.Abs(at.Y - lastBottom) < best) { gap = InsertionAfter(last); y = lastBottom; }
        if (gap < 0) { HideGap(); return; }
        _gapAt = gap;
        Canvas.SetTop(GapButton, Math.Round(y - GapButton.Height / 2));
        GapLine.Width = Math.Max(0, GapLayer.ActualWidth - Canvas.GetLeft(GapLine) - 20);
        Canvas.SetTop(GapLine, Math.Round(y));
        if (GapButton.Visibility != Visibility.Visible)
        {
            GapButton.Visibility = GapLine.Visibility = Visibility.Visible;
            GapButton.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        }
    }
    private void HideGap() { GapButton.Visibility = GapLine.Visibility = Visibility.Collapsed; _gapAt = -1; }
    private void HideGapLater() => Dispatcher.BeginInvoke(() =>
    {
        if (!ActionsListBox.IsMouseOver && !GapButton.IsMouseOver) HideGap();
    }, System.Windows.Threading.DispatcherPriority.Input);
    private void GapButton_Click(object sender, RoutedEventArgs e)
    {
        int at = _gapAt;
        HideGap();
        if (at >= 0) Attempt(() => InsertWait(at));
    }
}
