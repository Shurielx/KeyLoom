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
        (Profile.ExecutionMode == ExecutionMode.Background ? BackgroundModeRadio : StandardModeRadio).IsChecked = true;
        Unloaded += (_, _) => { _picker?.Dispose(); _picker = null; };
    }
    /// <summary>Background mode stays hidden unless experimental features are on, or this macro already uses it.</summary>
    private bool ShowRunMode => AppSettings.Current.ShowExperimental || Profile.ExecutionMode == ExecutionMode.Background;
    private bool IsBackgroundMode => BackgroundModeRadio.IsChecked == true;
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
        TestButton.Visibility = IsBackgroundMode ? Visibility.Visible : Visibility.Collapsed;
        RunModeHint.Text = IsBackgroundMode
            ? "Experimental. Sends input to the target window only, so you can keep using your mouse. Many games ignore it — test first."
            : "Uses the real pointer and keyboard. Moves can collide with yours.";
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
    private async void PickPoint_Click(object sender, RoutedEventArgs e)
    {
        if (_picker != null || _recording != null) return;
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
        if (picked == null || !IsLoaded) return;
        SetWindow(picked.Target);
        if (Mode == 2)
        {
            Attempt(() =>
            {
                InsertStep(new() { ActionType = MacroActionType.MousePosition, Space = CoordinateSpace.Window, X = picked.X, Y = picked.Y });
                InsertStep(new() { ActionType = MacroActionType.MouseClick, MouseButton = "Left" });
            });
            if (ErrorText.Text.Length == 0) Say($"Added a click at {picked.X}, {picked.Y} in {picked.Target.Label}.");
        }
        else
        {
            _pointX = picked.X; _pointY = picked.Y;
            MouseRadio.IsChecked = true;
            UsePointBox.IsChecked = true;
            UpdateTargetUi();
        }
    }
    /// <summary>Copies target settings from the form into a profile.</summary>
    private void ApplyTarget(MacroProfile p)
    {
        p.Window = _window;
        p.UsePoint = _window != null && UsePointBox.IsChecked == true;
        p.PointX = _pointX; p.PointY = _pointY;
        p.ScaleWithWindow = ScaleBox.IsChecked == true;
        p.ExecutionMode = IsBackgroundMode ? ExecutionMode.Background : ExecutionMode.Standard;
    }
    private void TestBackground_Click(object sender, RoutedEventArgs e) => Attempt(() =>
    {
        if (_window == null) throw new ArgumentException("Pick a point in the target window first.");
        IntPtr window = WindowSystem.Instance.Find(_window);
        if (window == IntPtr.Zero) throw new ArgumentException($"{_window.Label} is not open.");
        var probe = new MacroProfile { Window = _window, ScaleWithWindow = ScaleBox.IsChecked == true };
        var resolver = new PointerResolver(WindowSystem.Instance, window, probe);
        var driver = new WindowMessageDriver(window);
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
        Say($"{sent} to {_window.Label} without moving your pointer. If nothing happened there, this app ignores background input — use Standard mode.");
    });
    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        Say("");
        var p = new MacroProfile { Name = MacroNameTextBox.Text.Trim().Length > 0 ? MacroNameTextBox.Text.Trim() : "Macro", Actions = new(_steps.Select(a => a.Clone())) };
        ApplyTarget(p);
        if (SheetLayer.For(this) is { } sheets) await sheets.ShowAsync(new PreviewView(p), 660);
    }
    private void BindSteps(ObservableCollection<MacroAction> steps)
    {
        if (_steps != null) _steps.CollectionChanged -= Steps_Changed;
        _steps = steps;
        ActionsListBox.ItemsSource = _steps;
        _steps.CollectionChanged += Steps_Changed;
        RefreshThumbnail();
    }
    private void Steps_Changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Coalesce bursts of edits into one rebuild.
        if (_thumbnailQueued) return;
        _thumbnailQueued = true;
        Dispatcher.BeginInvoke(RefreshThumbnail, DispatcherPriority.Background);
    }
    private void RefreshThumbnail()
    {
        _thumbnailQueued = false;
        var timeline = MacroTimeline.Build(_steps);
        Thumbnail.Timeline = timeline;
        ThumbnailEmpty.Visibility = timeline.HasPointer ? Visibility.Collapsed : Visibility.Visible;
        ThumbnailInfo.Text = $"{timeline.Duration / 1000:0.0} s";
        StepCountText.Text = _steps.Count == 0 ? "" : $"{_steps.Count:N0} · {timeline.Duration / 1000:0.0} s";
        NoStepsText.Visibility = _steps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_steps.Count == 0) StepEditor.Visibility = Visibility.Collapsed;
    }
}
