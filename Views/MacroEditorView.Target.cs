using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;
using Keyloom.Engine;
using Keyloom.Models;
using Keyloom.Services;

namespace Keyloom.Views;

/// <summary>Target window, chosen point, run mode and route thumbnail.</summary>
public partial class MacroEditorView
{
    private WindowTarget? _window;
    private int _pointX, _pointY;
    private PointPicker? _picker;
    private bool _thumbnailQueued;
    private void InitializeTarget()
    {
        _window = Profile.Window;
        _pointX = Profile.PointX; _pointY = Profile.PointY;
        UsePointBox.IsChecked = Profile.UsePoint;
        ScaleBox.IsChecked = Profile.ScaleWithWindow;
        RestoreCursorBox.IsChecked = Profile.RestoreCursor;
        (Profile.ExecutionMode switch { ExecutionMode.Background => BackgroundModeRadio, ExecutionMode.QuickSwitch => SwitchModeRadio, _ => StandardModeRadio }).IsChecked = true;
        Unloaded += (_, _) => { _picker?.Dispose(); _picker = null; };
    }
    /// <summary>Run modes stay hidden unless experimental features are on, or this macro already uses one.</summary>
    private bool ShowRunMode => AppSettings.Current.ShowExperimental || Profile.ExecutionMode != ExecutionMode.Standard;
    private ExecutionMode RunMode => BackgroundModeRadio.IsChecked == true ? ExecutionMode.Background
        : SwitchModeRadio.IsChecked == true ? ExecutionMode.QuickSwitch : ExecutionMode.Standard;
    private void UpdateTargetUi()
    {
        if (WindowPanel == null || RunModePanel == null || PreviewCard == null || TimingPanel == null) return;
        bool sequence = Mode == 2, mouse = !KeyboardTarget;
        PointRow.Visibility = !sequence && mouse ? Visibility.Visible : Visibility.Collapsed;
        UsePointBox.Content = Mode == 1 ? "Hold at a chosen point" : "Click at a chosen point";
        UsePointBox.IsEnabled = _window != null;
        PointText.Text = _window == null ? "Pick a point first" : UsePointBox.IsChecked == true ? $"{_pointX}, {_pointY} in {_window.Label}" : _window.Label;
        WindowText.Text = _window == null ? "None · positions use screen coordinates" : _window.Label;
        WindowText.ToolTip = _window == null ? null : $"{_window.Label}\nClass: {_window.ClassName}\nClient size when picked: {_window.ClientWidth} × {_window.ClientHeight}";
        ClearWindowButton.IsEnabled = ScaleBox.IsEnabled = _window != null;
        RunModePanel.Visibility = ShowRunMode ? Visibility.Visible : Visibility.Collapsed;
        TestButton.Visibility = RunMode != ExecutionMode.Standard ? Visibility.Visible : Visibility.Collapsed;
        RestoreCursorBox.Visibility = RunMode == ExecutionMode.QuickSwitch ? Visibility.Visible : Visibility.Collapsed;
        RunModeHint.Text = RunMode switch
        {
            ExecutionMode.Background => "Sends input to the target window only, so your mouse stays free. Many games, Roblox included, ignore it. Test it first.",
            ExecutionMode.QuickSwitch => "For games that ignore Background, like Roblox. Each input brings the window to the front for a moment, then gives your window and pointer back. Best for inputs a second or more apart. Keep the game open, it can sit behind other windows.",
            _ => "Uses the real pointer and keyboard. Moves can collide with yours."
        };
    }
    private void RunMode_Changed(object sender, RoutedEventArgs e) => UpdateTargetUi();
    private void UsePoint_Changed(object sender, RoutedEventArgs e) => UpdateTargetUi();
    private void SetWindow(WindowTarget target)
    {
        bool changed = _window != null && (_window.ProcessName != target.ProcessName || _window.Title != target.Title);
        _window = target;
        if (changed && _steps.Any(a => a.ActionType == MacroActionType.MousePosition && a.Space == CoordinateSpace.Window))
            Say($"Target changed to {target.Label}. Existing window positions now refer to it.");
        UpdateTargetUi();
    }
    private void ClearWindow_Click(object sender, RoutedEventArgs e)
    {
        _window = null; UsePointBox.IsChecked = false; UpdateTargetUi();
    }
    /// <summary>Hides the window while the person clicks a point in another one. Null when cancelled.</summary>
    private async Task<PickedPoint?> PickAsync()
    {
        if (_picker != null || _recording != null) return null;
        Say("");
        _picker = new PointPicker();
        PickedPoint? picked = null;
        var host = Host;
        MacroForm.IsEnabled = false;
        if (host != null) host.WindowState = WindowState.Minimized;
        try { picked = await _picker.PickAsync(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Say(ex.Message, true); }
        finally
        {
            _picker?.Dispose(); _picker = null;
            MacroForm.IsEnabled = true;
            if (host != null && IsLoaded) { host.WindowState = WindowState.Normal; host.Activate(); }
        }
        return IsLoaded ? picked : null;
    }
    /// <summary>Repeat and hold: sets the point. Sequences: adds a move there, with a click unless the menu item says move only.</summary>
    private async void PickPoint_Click(object sender, RoutedEventArgs e)
    {
        bool click = sender is not FrameworkElement { Tag: "Move" };
        var picked = await PickAsync();
        if (picked == null) return;
        SetWindow(picked.Target);
        if (Mode == 2)
        {
            Attempt(() =>
            {
                InsertStep(new() { ActionType = MacroActionType.MousePosition, Space = CoordinateSpace.Window, X = picked.X, Y = picked.Y });
                if (click) InsertStep(new() { ActionType = MacroActionType.MouseClick, MouseButton = "Left" });
            });
            if (ErrorText.Text.Length == 0) Say($"Added {(click ? "a click" : "a move")} at {picked.X}, {picked.Y} in {picked.Target.Label}.");
        }
        else
        {
            _pointX = picked.X; _pointY = picked.Y;
            MouseRadio.IsChecked = true;
            UsePointBox.IsChecked = true;
            UpdateTargetUi();
        }
    }
    /// <summary>Moves the picked Move to step to a newly clicked point. Screen positions stay on screen; others follow the window.</summary>
    private async void RepickPoint_Click(object sender, RoutedEventArgs e)
    {
        if (ActionsListBox.SelectedItem is not MacroAction { ActionType: MacroActionType.MousePosition } step) return;
        var picked = await PickAsync();
        int i = _steps.IndexOf(step);
        if (picked == null || i < 0) return;
        var moved = step.Clone();
        if (step.Space == CoordinateSpace.Screen) (moved.X, moved.Y) = WindowSystem.Instance.ClientToScreen(picked.Window, picked.X, picked.Y);
        else { SetWindow(picked.Target); moved.Space = CoordinateSpace.Window; moved.X = picked.X; moved.Y = picked.Y; }
        Remember();
        _selecting = true;
        try { _steps[i] = moved; }
        finally { _selecting = false; }
        Select([i]); FocusStep(i);
        Say($"Step {StepNumber(i)} now moves to {moved.X}, {moved.Y}{(moved.Space == CoordinateSpace.Screen ? " on screen" : $" in {picked.Target.Label}")}.");
    }
    private bool HasClickAfter(int i) => i >= 0 && i + 1 < _steps.Count && _steps[i + 1].ActionType == MacroActionType.MouseClick;
    /// <summary>Adds a left click right after the picked move, or takes it away.</summary>
    private void ThenClick_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingStep || ActionsListBox.SelectedItem is not MacroAction { ActionType: MacroActionType.MousePosition } step) return;
        int i = _steps.IndexOf(step);
        bool want = ThenClickBox.IsChecked == true;
        if (i < 0 || HasClickAfter(i) == want) return;
        Attempt(() =>
        {
            if (want) EnsureRoom(1);
            Remember();
            _selecting = true;
            try
            {
                if (want) _steps.Insert(i + 1, new MacroAction { ActionType = MacroActionType.MouseClick, MouseButton = "Left" });
                else _steps.RemoveAt(i + 1);
            }
            finally { _selecting = false; }
            if (want) AnimateStep(_steps[i + 1]);
            Select([i]);
        });
    }
    private string StepNumber(int i) => (_steps.Take(i).Count(s => !s.IsGroupMarker) + 1).ToString("N0");
    private void Strip_StepPicked(int index)
    {
        if (index < 0 || index >= _steps.Count) return;
        Select([index]); FocusStep(index);
    }
    private async void TestRun_Click(object sender, RoutedEventArgs e) => await RunTestAsync();
    /// <summary>Plays what is in the editor once, for real. Nothing is saved.</summary>
    private async Task RunTestAsync()
    {
        if (TestRun == null) return;
        MacroProfile test;
        try { Say(""); test = ReadForm(new MacroProfile { Id = Profile.Id }); }
        catch (ArgumentException ex) { Say(ex.Message, true); return; }
        test.RepeatCount = 1;
        try { await TestRun(test); if (IsLoaded) Say("Test run finished. Nothing was saved."); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { Say(ex.Message, true); }
    }
    /// <summary>Copies target settings from the form into a profile.</summary>
    private void ApplyTarget(MacroProfile p)
    {
        p.Window = _window;
        p.UsePoint = _window != null && UsePointBox.IsChecked == true;
        p.PointX = _pointX; p.PointY = _pointY;
        p.ScaleWithWindow = ScaleBox.IsChecked == true;
        p.ExecutionMode = RunMode;
        p.RestoreCursor = RestoreCursorBox.IsChecked == true;
    }
    private void TestBackground_Click(object sender, RoutedEventArgs e) => Attempt(() =>
    {
        if (_window == null) throw new ArgumentException("Pick a point in the target window first.");
        IntPtr window = WindowSystem.Instance.Find(_window);
        if (window == IntPtr.Zero) throw new ArgumentException($"{_window.Label} is not open.");
        var probe = new MacroProfile { Window = _window, ScaleWithWindow = ScaleBox.IsChecked == true };
        var resolver = new PointerResolver(WindowSystem.Instance, window, probe);
        bool quick = RunMode == ExecutionMode.QuickSwitch;
        IInputDriver driver = quick ? new FocusSwitchDriver(new InputSimulator(), window) { RestoreCursor = RestoreCursorBox.IsChecked == true } : new WindowMessageDriver(window);
        string sent;
        if (Mode != 2 && KeyboardTarget)
        {
            var keys = KeyResolver.Resolve(TargetKeyTextBox.Text.Trim());
            foreach (var key in keys) driver.Key(key, true);
            foreach (var key in keys.Reverse()) driver.Key(key, false);
            sent = $"Sent {TargetKeyTextBox.Text.Trim()}";
        }
        else
        {
            // Sequences test their first window position; repeat input tests the chosen point.
            var first = _steps.FirstOrDefault(a => a.ActionType == MacroActionType.MousePosition && a.Space == CoordinateSpace.Window);
            var (x, y) = Mode == 2 && first != null ? (first.X, first.Y) : (_pointX, _pointY);
            string button = Mode == 2 ? "Left" : SelectedButton;
            var (sx, sy) = resolver.Resolve(CoordinateSpace.Window, x, y);
            driver.Position(sx, sy); driver.Mouse(button, true); driver.Mouse(button, false);
            sent = $"Sent a {button.ToLowerInvariant()} click at {x}, {y}";
        }
        (driver as IBurstDriver)?.EndBurst();
        Say(quick ? $"{sent} to {_window.Label} with a quick switch. If nothing happened there, try Standard mode."
            : $"{sent} to {_window.Label} without moving your pointer. If nothing happened there, this app ignores background input. Try Quick switch.");
    });
    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (SheetLayer.For(this) is { IsOpen: true }) return;
        Say("");
        var p = new MacroProfile { Name = MacroNameTextBox.Text.Trim().Length > 0 ? MacroNameTextBox.Text.Trim() : "Macro", Actions = new(_steps.Select(a => a.Clone())) };
        ApplyTarget(p);
        if (SheetLayer.For(this) is { } sheets) await sheets.ShowAsync(new PreviewView(p, null, TestRun == null ? null : RunTestAsync), 680);
    }
    private List<MacroAction> _played = [];
    /// <summary>Rings the spots of the picked steps on the route, every time a group plays them.</summary>
    private void ShowRouteFocus()
    {
        var picked = new HashSet<MacroAction>(ActionsListBox.SelectedItems.Cast<MacroAction>(), ReferenceEqualityComparer.Instance);
        // A picked group rings everything inside it.
        foreach (int i in WholeGroups(SelectedIndices())) picked.Add(_steps[i]);
        Thumbnail.Highlight = picked.Count == 0 ? [] : Enumerable.Range(0, _played.Count).Where(i => picked.Contains(_played[i])).ToHashSet();
    }
    private void BindSteps(ObservableCollection<MacroAction> steps)
    {
        if (_steps != null) _steps.CollectionChanged -= Steps_Changed;
        _steps = steps;
        ActionsListBox.ItemsSource = _steps;
        _steps.CollectionChanged += Steps_Changed;
        StepsRevision++;
        RefreshThumbnail();
    }
    private void Steps_Changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        StepsRevision++;
        // Coalesce bursts of edits into one rebuild.
        if (_thumbnailQueued) return;
        _thumbnailQueued = true;
        Dispatcher.BeginInvoke(RefreshThumbnail, DispatcherPriority.Background);
    }
    /// <summary>Redraws the run strip, and the route when the macro moves the pointer; without one the list gets the room.</summary>
    private void RefreshThumbnail()
    {
        _thumbnailQueued = false;
        var index = new Dictionary<MacroAction, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < _steps.Count; i++) if (!_steps[i].IsGroupMarker) index.TryAdd(_steps[i], i);
        var played = (StepGroups.PlayedCount(_steps) <= StepGroups.MaxPlayedSteps ? StepGroups.Expand(_steps) : _steps.Where(a => !a.IsGroupMarker)).ToList();
        _played = played;
        var timeline = MacroTimeline.Build(played);
        Thumbnail.Timeline = timeline;
        ShowRouteFocus();
        // One position is a spot, not a route; the strip already shows it.
        bool route = timeline.Points.Select(p => (p.X, p.Y)).Distinct().Skip(1).Any();
        PreviewCard.Visibility = route ? Visibility.Visible : Visibility.Collapsed;
        RouteColumn.Width = new GridLength(route ? 190 : 0);
        ThumbnailInfo.Text = $"{timeline.Duration / 1000:0.0} s";
        Strip.Show(played, played.Select(a => index.TryGetValue(a, out int i) ? i : -1).ToArray());
        Strip.Picked = ActionsListBox.SelectedItems.Count == 1 ? ActionsListBox.SelectedIndex : -1;
        StripEnd.Text = StepLayoutConverter.FormatTime(timeline.Duration);
        int count = _steps.Count(a => !a.IsGroupMarker);
        StripPanel.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        StepCountText.Text = count == 0 ? "" : $"{count:N0} {(count == 1 ? "step" : "steps")} · {StepLayoutConverter.FormatTime(timeline.Duration)} per run";
        NoStepsText.Visibility = _steps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_steps.Count == 0) StepEditor.Visibility = BulkEditor.Visibility = GroupEditor.Visibility = Visibility.Collapsed;
    }
}
