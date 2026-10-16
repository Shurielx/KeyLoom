using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Keyloom.Models;

namespace Keyloom.Views;

/// <summary>Dragging steps to a new place. Moves the whole pick when a picked row is dragged.</summary>
public partial class MacroEditorView
{
    private const double DragEdge = 28, DimmedOpacity = .4;
    private MacroAction? _pressed;
    private Point _pressPoint;
    private bool _keepPick, _dragging;
    private List<int> _dragged = [];
    private int _dropAt = -1;
    private DispatcherTimer? _dragScroll;

    private static T? Ancestor<T>(object source) where T : DependencyObject
    {
        var node = source as DependencyObject;
        while (node != null && node is not T && node is not ListBox)
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        return node as T;
    }

    private void Steps_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = null; _keepPick = false;
        // Checkboxes and row tools handle their own clicks.
        if (Ancestor<ButtonBase>(e.OriginalSource) != null || Ancestor<ListBoxItem>(e.OriginalSource) is not { DataContext: MacroAction step } row) return;
        _pressed = step; _pressPoint = e.GetPosition(ActionsListBox);
        // Pressing a row of a multi-step pick keeps the pick so it can be dragged; releasing without dragging picks just that row.
        if (Keyboard.Modifiers == ModifierKeys.None && ActionsListBox.SelectedItems.Count > 1 && row.IsSelected)
        {
            _keepPick = true; e.Handled = true;
            row.Focus();
        }
    }
    private void Steps_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pressed == null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _pressed = null; return; }
        var at = e.GetPosition(ActionsListBox);
        if (!_dragging)
        {
            if (Math.Abs(at.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance * 2 && Math.Abs(at.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance * 2) return;
            BeginDrag();
        }
        // Stops the list from extending the pick while the pointer moves.
        e.Handled = true;
        UpdateDrop(at);
    }
    private void Steps_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging) { e.Handled = true; EndDrag(true); }
        else if (_keepPick && _pressed != null) Select([_steps.IndexOf(_pressed)]);
        _pressed = null; _keepPick = false;
    }
    private void Steps_LostCapture(object sender, MouseEventArgs e)
    {
        if (_dragging && !ActionsListBox.IsMouseCaptured) EndDrag(false);
    }

    private void BeginDrag()
    {
        int pressed = _steps.IndexOf(_pressed!);
        if (pressed < 0) { _pressed = null; return; }
        if (!ActionsListBox.SelectedItems.Contains(_pressed!)) Select([pressed]);
        _dragged = PickedBlock();
        // Taking capture from the list's own selection capture raises LostMouseCapture, so capture before the drag counts as started.
        ActionsListBox.CaptureMouse();
        _dragging = true; _dropAt = -1;
        ActionsListBox.Cursor = Cursors.SizeAll;
        DimDragged(DimmedOpacity);
        DragBadgeText.Text = L.N(_dragged.Count, "Move {0} step", "Move {0} steps");
        DragBadge.Visibility = Visibility.Visible;
        _dragScroll = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) => AutoScroll(), Dispatcher);
        _dragScroll.Start();
    }
    private void EndDrag(bool drop)
    {
        if (!_dragging) return;
        _dragging = false;
        _dragScroll?.Stop(); _dragScroll = null;
        ActionsListBox.ClearValue(CursorProperty);
        DropLine.Visibility = DragBadge.Visibility = Visibility.Collapsed;
        DimDragged(1);
        if (ActionsListBox.IsMouseCaptured) ActionsListBox.ReleaseMouseCapture();
        if (drop && _dropAt >= 0) Attempt(() => DropSteps(_dragged, _dropAt));
        _dragged = []; _dropAt = -1; _pressed = null;
    }
    private void DimDragged(double opacity)
    {
        foreach (int i in _dragged)
            if (ActionsListBox.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem row)
                row.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, TimeSpan.FromMilliseconds(140)));
    }

    /// <summary>Finds the gap under the pointer and moves the drop line there. Gaps that would not move anything hide the line.</summary>
    private void UpdateDrop(Point at)
    {
        Canvas.SetLeft(DragBadge, Math.Min(at.X + 14, Math.Max(0, DropLayer.ActualWidth - DragBadge.ActualWidth - 4)));
        Canvas.SetTop(DragBadge, Math.Min(at.Y + 12, Math.Max(0, DropLayer.ActualHeight - DragBadge.ActualHeight - 4)));
        int gap = GapAt(at.Y);
        bool noMove = gap < 0 || IsContiguous(_dragged) && gap >= _dragged[0] && gap <= _dragged[^1] + 1;
        _dropAt = noMove ? -1 : gap;
        if (noMove) { DropLine.Visibility = Visibility.Collapsed; return; }
        double? y = GapTop(gap);
        if (y == null) { DropLine.Visibility = Visibility.Collapsed; return; }
        DropLine.Width = Math.Max(0, DropLayer.ActualWidth - 20);
        double top = y.Value - DropLine.Height / 2;
        if (DropLine.Visibility != Visibility.Visible)
        {
            DropLine.BeginAnimation(Canvas.TopProperty, null);
            Canvas.SetTop(DropLine, top);
            DropLine.Visibility = Visibility.Visible;
        }
        else DropLine.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(top, TimeSpan.FromMilliseconds(90)) { EasingFunction = new QuadraticEase() });
    }
    /// <summary>The index a drop at this height would insert at: before a row when over its top half, after it otherwise.</summary>
    private int GapAt(double y)
    {
        ListBoxItem? last = null; int lastIndex = -1;
        for (int i = 0; i < _steps.Count; i++)
        {
            if (ActionsListBox.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem { IsVisible: true } row) continue;
            double top = row.TranslatePoint(new Point(), ActionsListBox).Y;
            if (top + row.ActualHeight < 0) continue;
            if (y < top + row.ActualHeight / 2) return i;
            last = row; lastIndex = i;
            if (top > ActionsListBox.ActualHeight) break;
        }
        // Below a folded group's header means after the whole group.
        return last == null ? -1 : InsertionAfter(lastIndex);
    }
    /// <summary>Height of a gap: the top of the next visible row, or the bottom of the last visible row before it.</summary>
    private double? GapTop(int gap)
    {
        for (int i = gap; i < _steps.Count; i++)
            if (ActionsListBox.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem { IsVisible: true } next)
                return next.TranslatePoint(new Point(), DropLayer).Y;
        for (int i = gap - 1; i >= 0; i--)
            if (ActionsListBox.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem { IsVisible: true } previous)
                return previous.TranslatePoint(new Point(0, previous.ActualHeight), DropLayer).Y;
        return null;
    }
    private static bool IsContiguous(List<int> indices) => indices.Count > 0 && indices[^1] - indices[0] == indices.Count - 1;
    /// <summary>Scrolls while the pointer rests near the top or bottom edge, faster the closer it gets.</summary>
    private void AutoScroll()
    {
        if (!_dragging) return;
        var at = Mouse.GetPosition(ActionsListBox);
        double speed = at.Y < DragEdge ? -(DragEdge - at.Y) : at.Y > ActionsListBox.ActualHeight - DragEdge ? at.Y - (ActionsListBox.ActualHeight - DragEdge) : 0;
        if (speed == 0) return;
        var scroll = FindChild<ScrollViewer>(ActionsListBox);
        if (scroll == null) return;
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + Math.Clamp(speed, -DragEdge, DragEdge) / 2);
        Dispatcher.BeginInvoke(() => { if (_dragging) { DimDragged(DimmedOpacity); UpdateDrop(Mouse.GetPosition(ActionsListBox)); } }, DispatcherPriority.Loaded);
    }
    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is T deeper) return deeper;
        }
        return null;
    }

    /// <summary>Moves the steps, in their order, into the gap before <paramref name="gap"/> (a position in the list before the move).</summary>
    private void DropSteps(List<int> picked, int gap)
    {
        if (picked.Count == 0 || gap < 0 || gap > _steps.Count) return;
        var block = picked.Select(i => _steps[i]).ToList();
        int target = gap - picked.Count(i => i < gap);
        if (IsContiguous(picked) && target == picked[0]) return;
        Remember();
        _selecting = true;
        try
        {
            foreach (int i in Enumerable.Reverse(picked)) _steps.RemoveAt(i);
            for (int k = 0; k < block.Count; k++) _steps.Insert(target + k, block[k]);
        }
        finally { _selecting = false; }
        var moved = Enumerable.Range(target, block.Count).ToList();
        var roots = Roots(moved);
        Select(roots); AnimateSteps(moved, -6); FocusStep(roots[^1]);
    }
}
