using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;
using Keyloom.Engine;
using Keyloom.Models;
using Keyloom.Services;

namespace Keyloom.Views;

/// <summary>Target window, chosen point, run mode and route thumbnail.</summary>
public partial class MacroEditorDialog
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
        RunModeBox.SelectedIndex = (int)Profile.ExecutionMode;
        Closed += (_, _) => _picker?.Dispose();
    }
    /// <summary>Background mode stays hidden unless experimental features are on, or this macro already uses it.</summary>
    private bool ShowRunMode => AppSettings.Current.ShowExperimental || Profile.ExecutionMode == ExecutionMode.Background;
    private bool IsBackgroundMode => RunModeBox.SelectedIndex == 1;
    private void UpdateTargetUi()
    {
        if (WindowPanel == null || RunModePanel == null || PreviewCard == null) return;
        bool sequence = ModeBox.SelectedIndex == 2, mouse = TargetBox.SelectedIndex == 0;
        UsePointBox.Visibility = !sequence && mouse ? Visibility.Visible : Visibility.Collapsed;
        UsePointBox.Content = ModeBox.SelectedIndex == 1 ? "Hold at the chosen point" : "Click at the chosen point";
        UsePointBox.IsEnabled = _window != null;
        string point = !sequence && mouse && UsePointBox.IsChecked == true ? $" · point {_pointX}, {_pointY}" : "";
        WindowText.Text = _window == null ? "None · positions use screen coordinates" : _window.Label + point;
        WindowText.ToolTip = _window == null ? null : $"{_window.Label}\nClass: {_window.ClassName}\nClient size when picked: {_window.ClientWidth} × {_window.ClientHeight}";
        ClearWindowButton.IsEnabled = ScaleBox.IsEnabled = _window != null;
        PickButton.ToolTip = sequence ? "Click a point in another window to add it as a click step. Esc cancels." : "Click a point in another window. Esc cancels.";
        RunModePanel.Visibility = ShowRunMode ? Visibility.Visible : Visibility.Collapsed;
        TestButton.Visibility = IsBackgroundMode ? Visibility.Visible : Visibility.Collapsed;
        RunModeHint.Text = IsBackgroundMode
            ? "Experimental. Sends input to the target window only, so you can keep using your mouse. Many games ignore it — test first."
            : "Uses the real pointer and keyboard. Moves can collide with yours.";
        PreviewCard.Visibility = sequence ? Visibility.Visible : Visibility.Collapsed;
    }
    private void RunMode_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) { UpdateTargetUi(); if (IsLoaded) FitHeight(); }
    private void UsePoint_Changed(object sender, RoutedEventArgs e) => UpdateTargetUi();
    private void SetWindow(WindowTarget target)
    {
        bool changed = _window != null && (_window.ProcessName != target.ProcessName || _window.Title != target.Title);
        _window = target;
        if (changed && _steps.Any(a => a.ActionType == MacroActionType.MousePosition && a.Space == CoordinateSpace.Window))
            ErrorText.Text = $"Target changed to {target.Label}. Existing window positions now refer to it.";
        UpdateTargetUi();
    }
    private void ClearWindow_Click(object sender, RoutedEventArgs e)
    {
        _window = null; UsePointBox.IsChecked = false; UpdateTargetUi();
    }
    private async void PickPoint_Click(object sender, RoutedEventArgs e)
    {
        if (_picker != null || _recording != null) return;
        ErrorText.Text = "";
        _picker = new PointPicker();
        PickedPoint? picked = null;
        MacroForm.IsEnabled = false;
        WindowState = WindowState.Minimized;
        try { picked = await _picker.PickAsync(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { ErrorText.Text = ex.Message; }
        finally
        {
            _picker.Dispose(); _picker = null;
            MacroForm.IsEnabled = true;
            if (!_closed) { WindowState = WindowState.Normal; Activate(); }
        }
        if (picked == null || _closed) return;
        SetWindow(picked.Target);
        if (ModeBox.SelectedIndex == 2)
        {
            Attempt(() =>
            {
                InsertStep(new() { ActionType = MacroActionType.MousePosition, Space = CoordinateSpace.Window, X = picked.X, Y = picked.Y });
                InsertStep(new() { ActionType = MacroActionType.MouseClick, MouseButton = "Left" });
            });
            if (ErrorText.Text.Length == 0) ErrorText.Text = $"Added a click at {picked.X}, {picked.Y} in {picked.Target.Label}.";
        }
        else
        {
            _pointX = picked.X; _pointY = picked.Y;
            TargetBox.SelectedIndex = 0;
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
        if (ModeBox.SelectedIndex != 2 && TargetBox.SelectedIndex == 1)
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
            var (x, y) = ModeBox.SelectedIndex == 2 && first != null ? (first.X, first.Y) : (_pointX, _pointY);
            string button = ModeBox.SelectedIndex == 2 ? "Left" : MouseValue(MouseButtonBox);
            var (sx, sy) = resolver.Resolve(CoordinateSpace.Window, x, y);
            driver.Position(sx, sy); driver.Mouse(button, true); driver.Mouse(button, false);
            sent = $"Sent a {button.ToLowerInvariant()} click at {x}, {y}";
        }
        ErrorText.Text = $"{sent} to {_window.Label} without moving your pointer. If nothing happened there, this app ignores background input — use Standard mode.";
    });
    private void Preview_Click(object sender, RoutedEventArgs e) => Attempt(() =>
    {
        var p = new MacroProfile { Name = MacroNameTextBox.Text.Trim().Length > 0 ? MacroNameTextBox.Text.Trim() : "Macro", Actions = new(_steps.Select(a => a.Clone())) };
        ApplyTarget(p);
        new PreviewWindow(p) { Owner = this }.ShowDialog();
    });
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
    }
}
