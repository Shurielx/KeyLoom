using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Keyloom.Engine;
using Keyloom.Models;

namespace Keyloom.Views;

/// <summary>Grids: marking one on the screen, reading its numbers, and the Click a cell and Drag a matching pair steps.</summary>
public partial class MacroEditorView
{
    /// <summary>A grid can be much bigger than a target picture; this keeps it to a 4K screen.</summary>
    private const int MaxGridSide = 4096;
    private List<ScreenGrid> _grids = [];
    /// <summary>Reads grids for Read it now; tests put in their own reader.</summary>
    public IGridReader GridReader { get; init; } = WindowsOcrReader.Instance;

    private void InitializeGrids()
    {
        _grids = Profile.Grids.Select(g => g.Clone()).ToList();
        RefreshGridBox(null);
    }
    private ScreenGrid? CurrentGrid => GridBox.SelectedItem is string name ? _grids.FirstOrDefault(g => g.Name == name) : null;
    private void RefreshGridBox(string? select)
    {
        bool loading = _loadingStep;
        _loadingStep = true;
        GridBox.ItemsSource = _grids.Select(g => g.Name).ToList();
        GridBox.SelectedItem = select;
        _loadingStep = loading;
        ShowGridCard();
        RefreshScriptAssets();
    }
    private void ShowGridOptions(bool grid)
    {
        GridOptions.Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
        if (!grid) return;
        CellPickPanel.Visibility = StepType == MacroActionType.ClickCell ? Visibility.Visible : Visibility.Collapsed;
        DragTimePanel.Visibility = StepType == MacroActionType.DragPair ? Visibility.Visible : Visibility.Collapsed;
        CellValueTextBox.Visibility = CellPickBox.SelectedIndex == (int)CellPick.Equals ? Visibility.Visible : Visibility.Collapsed;
    }
    private void PrepareGridStep()
    {
        GridBox.SelectedItem = _grids.FirstOrDefault()?.Name;
        CellPickBox.SelectedIndex = 0; CellValueTextBox.Text = "0";
        DragTimeTextBox.Text = "150";
        GridStopBox.IsChecked = false;
        ShowGridCard();
    }
    private void ShowGridStep(MacroAction a)
    {
        if (a.ActionType is not (MacroActionType.ClickCell or MacroActionType.DragPair)) return;
        GridBox.SelectedItem = _grids.FirstOrDefault(g => string.Equals(g.Name, a.Grid, StringComparison.OrdinalIgnoreCase))?.Name;
        CellPickBox.SelectedIndex = (int)a.Pick;
        CellValueTextBox.Text = a.CellValue.ToString();
        DragTimeTextBox.Text = a.GlideMs.ToString();
        GridStopBox.IsChecked = a.StopIfMissing;
        ShowGridOptions(true);
        ShowGridCard();
    }
    private void ReadGridStep(MacroAction a)
    {
        a.Grid = GridBox.SelectedItem as string ?? throw new ArgumentException(L.T("Pick a grid, or mark a new one on the screen."));
        a.StopIfMissing = GridStopBox.IsChecked == true;
        if (a.ActionType == MacroActionType.ClickCell)
        {
            a.Pick = (CellPick)Math.Max(0, CellPickBox.SelectedIndex);
            a.CellValue = a.Pick == CellPick.Equals ? (long.TryParse(CellValueTextBox.Text.Trim(), out long v) ? v : throw new ArgumentException(L.T("The number to click must be a whole number."))) : 0;
        }
        else a.GlideMs = Number(DragTimeTextBox.Text, L.T("Drag time"));
    }
    /// <summary>Keeps the grids that steps read; one marked and then left unused goes away on save.</summary>
    private void ApplyGrids(MacroProfile p)
    {
        if (Mode == 3) { p.Grids = _grids.Select(g => g.Clone()).ToList(); return; }
        p.Grids = UsedGrids().Select(g => g.Clone()).ToList();
    }
    private IEnumerable<ScreenGrid> UsedGrids()
    {
        if (Mode == 3) return _grids;
        var used = _steps.Where(a => a.ActionType is MacroActionType.ClickCell or MacroActionType.DragPair).Select(a => a.Grid ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _grids.Where(g => used.Contains(g.Name));
    }

    private void ShowGridCard()
    {
        var g = CurrentGrid;
        GridCard.Visibility = g == null ? Visibility.Collapsed : Visibility.Visible;
        if (g == null) return;
        bool loading = _loadingStep;
        _loadingStep = true;
        GridNameTextBox.Text = g.Name;
        GridRowsTextBox.Text = g.Rows.ToString();
        GridColumnsTextBox.Text = g.Columns.ToString();
        GridInfoText.Text = g.Area.Space == CoordinateSpace.Window ? L.T($"{g.Area.Width} × {g.Area.Height} px in the target window, at {g.Area.X}, {g.Area.Y}") : L.T($"{g.Area.Width} × {g.Area.Height} px on the screen, at {g.Area.X}, {g.Area.Y}");
        FillFollowBox(GridFollowBox, g);
        _loadingStep = loading;
    }
    private void GridBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingStep || !IsInitialized) return;
        GridReadText.Text = GridReadNote.Text = "";
        ShowGridCard();
        CommitLooking();
    }
    /// <summary>"Stays put", then every target the grid could move with.</summary>
    private void FillFollowBox(ComboBox box, ScreenGrid g)
    {
        box.ItemsSource = new[] { L.T("Stays put") }.Concat(_targets.Select(t => t.Name)).ToList();
        box.SelectedIndex = Math.Max(0, _targets.FindIndex(t => string.Equals(t.Name, g.Follow, StringComparison.OrdinalIgnoreCase)) + 1);
    }
    private void GridFollow_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingStep || !IsInitialized || CurrentGrid is not { } g) return;
        _ = FollowAsync(g, GridFollowBox.SelectedIndex > 0 ? GridFollowBox.SelectedItem as string : null);
    }
    /// <summary>
    /// Makes the grid move with a target: Keyloom finds the target on the screen now and keeps where it was, so while the
    /// macro runs the grid moves as far as the target did. The target has to be where it was when the grid was marked.
    /// </summary>
    private async Task FollowAsync(ScreenGrid g, string? name)
    {
        if (name == null)
        {
            g.Follow = null;
            Say(L.T($"{g.Name} stays where it was marked."));
            return;
        }
        if (_targets.FirstOrDefault(t => t.Name == name) is not { } target) return;
        try
        {
            var spot = await OutOfTheWay(() =>
            {
                IntPtr window = _window != null ? WindowSystem.Instance.Find(_window) : IntPtr.Zero;
                var found = new TargetLocator(ScreenCapture.Instance, WindowSystem.Instance, window, [target]).Locate(target.Name);
                return Task.FromResult<(int X, int Y)?>(found is { } m ? GridLocator.InSpace(WindowSystem.Instance, window, g.Area.Space, m.Center) : null);
            });
            if (spot is { } at)
            {
                g.Follow = target.Name; g.FollowX = at.X; g.FollowY = at.Y;
                Say(L.T($"{g.Name} now moves with {target.Name}."));
            }
            else Say(L.T($"'{target.Name}' is not on the screen now. Show it where it was when you marked {g.Name}, then pick it again."), true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { Say(ex.Message, true); }
        ShowGridCard();
        RefreshScriptAssets();
    }
    /// <summary>A target was renamed or removed: grids that moved with it follow along, or stay put.</summary>
    private void RetargetGrids(string old, string? name)
    {
        foreach (var g in _grids.Where(g => string.Equals(g.Follow, old, StringComparison.OrdinalIgnoreCase))) g.Follow = name;
    }
    private void CellPick_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || CellValueTextBox == null) return;
        CellValueTextBox.Visibility = CellPickBox.SelectedIndex == (int)CellPick.Equals ? Visibility.Visible : Visibility.Collapsed;
        StepField_Changed(sender, e);
    }
    private void GridSize_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loadingStep || !IsInitialized || CurrentGrid is not { } g) return;
        try
        {
            var copy = g.Clone();
            copy.Rows = Number(GridRowsTextBox.Text, L.T("Rows"));
            copy.Columns = Number(GridColumnsTextBox.Text, L.T("Columns"));
            ProfileValidator.ValidateGrid(copy);
            g.Rows = copy.Rows; g.Columns = copy.Columns;
            ShowStepHint("");
            GridReadText.Text = GridReadNote.Text = "";
        }
        catch (ArgumentException ex) { ShowStepHint(ex.Message); }
    }
    private void GridName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; RenameGrid(); }
    }
    private void GridName_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => RenameGrid();
    /// <summary>Renames the grid and every step that reads it.</summary>
    private void RenameGrid()
    {
        if (CurrentGrid is not { } g) return;
        string name = GridNameTextBox.Text.Trim(), old = g.Name;
        if (name == old) return;
        if (name.Length == 0 || _grids.Any(x => x != g && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            Say(name.Length == 0 ? L.T("A grid needs a name.") : L.T($"Another grid is already called '{name}'."), true);
            GridNameTextBox.Text = old; return;
        }
        var picked = SelectedIndices();
        g.Name = name;
        _selecting = true;
        try
        {
            for (int i = 0; i < _steps.Count; i++)
                if (string.Equals(_steps[i].Grid, old, StringComparison.OrdinalIgnoreCase)) { var c = _steps[i].Clone(); c.Grid = name; _steps[i] = c; }
        }
        finally { _selecting = false; }
        RefreshGridBox(name);
        Select(picked);
    }

    private void AddGridStep_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || !Enum.TryParse(tag, out MacroActionType kind)) return;
        Attempt(() => InsertStep(new() { ActionType = kind, Grid = _grids.FirstOrDefault()?.Name, GlideMs = kind == MacroActionType.DragPair ? 150 : 0 }));
        if (_grids.Count == 0) Dispatcher.BeginInvoke(() => MarkGridAsync());
    }
    private void MarkGrid_Click(object sender, RoutedEventArgs e) => _ = MarkGridAsync();
    /// <summary>Drag around a grid on the screen; it becomes the picked step's grid. Inside the target window it follows the window.</summary>
    private async Task MarkGridAsync()
    {
        var snip = await OutOfTheWay(() => ScreenSnipWindow.TakeAsync(false, MaxGridSide, L.T("Drag around the whole grid, edge to edge")));
        if (snip == null) return;
        var area = new ScreenArea(snip.Bounds.X, snip.Bounds.Y, snip.Bounds.Width, snip.Bounds.Height);
        if (_window != null && WindowSystem.Instance.Find(_window) is var handle && handle != IntPtr.Zero)
        {
            var origin = WindowSystem.Instance.ClientToScreen(handle, 0, 0);
            var (width, height) = WindowSystem.Instance.ClientSize(handle);
            if (area.X >= origin.X && area.Y >= origin.Y && area.X + area.Width <= origin.X + width && area.Y + area.Height <= origin.Y + height)
                area = new(area.X - origin.X, area.Y - origin.Y, area.Width, area.Height, CoordinateSpace.Window);
        }
        int n = 1;
        while (_grids.Any(g => string.Equals(g.Name, $"Grid {n}", StringComparison.OrdinalIgnoreCase))) n++;
        var grid = new ScreenGrid { Name = $"Grid {n}", Area = area, Rows = CurrentGrid?.Rows ?? 4, Columns = CurrentGrid?.Columns ?? 4 };
        _grids.Add(grid);
        if (StepEditor.IsVisible && StepType is MacroActionType.ClickCell or MacroActionType.DragPair)
        {
            RefreshGridBox(grid.Name);
            CommitLooking();
        }
        else RefreshGridBox(CurrentGrid?.Name);
        Say(L.T($"Added {grid.Name}. Set its rows and columns, then Read it now to check the numbers."));
        GridRowsTextBox.Focus(); GridRowsTextBox.SelectAll();
    }
    private async void ReadGrid_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentGrid is not { } g) return;
        var clock = new Stopwatch();
        try
        {
            var reading = await OutOfTheWay(() =>
            {
                IntPtr window = _window != null ? WindowSystem.Instance.Find(_window) : IntPtr.Zero;
                clock.Start();
                var targets = new TargetLocator(ScreenCapture.Instance, WindowSystem.Instance, window, _targets);
                var read = new GridLocator(ScreenCapture.Instance, WindowSystem.Instance, window, [g], GridReader, targets).Read(g.Name);
                clock.Stop();
                return Task.FromResult<GridReading?>(read);
            });
            if (reading == null) return;
            ShowReading(reading, clock.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            GridReadText.Text = "";
            GridReadNote.Text = ex.Message;
        }
    }
    private void ShowReading(GridReading reading, long ms)
    {
        GridReadText.Text = reading.ToString();
        int numbers = reading.Values.Count(v => v != null);
        string pair = reading.Pair() is var (from, to) ? L.T($" A matching pair: {reading.Values[from]} at row {from / reading.Columns + 1}, column {from % reading.Columns + 1}.") : L.T(" No matching pair.");
        GridReadNote.Text = (numbers == 0 ? L.T("No numbers found. Check the grid's edges and that the numbers are on the screen.") : L.T($"Read {numbers} of {reading.Values.Length} cells in {ms} ms."))
            + (numbers > 0 ? pair : "");
    }
}
