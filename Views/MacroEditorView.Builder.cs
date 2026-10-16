using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Interop;
using Keyloom.Engine;
using Keyloom.Models;
using Keyloom.Services;

namespace Keyloom.Views;

/// <summary>Experimental: the on-screen macro builder, opened from the steps card.</summary>
public partial class MacroEditorView
{
    private MacroBuilderWindow? _builder;
    private void InitializeBuilder()
    {
        BuilderButton.Visibility = AppSettings.Current.MacroBuilder ? Visibility.Visible : Visibility.Collapsed;
        Unloaded += (_, _) => _builder?.Close();
    }
    private void Builder_Click(object sender, RoutedEventArgs e) => Attempt(() =>
    {
        if (_builder != null) { _builder.Activate(); return; }
        var p = new MacroProfile { Actions = new(_steps.Select(a => a.Clone())) };
        ApplyTarget(p);
        IntPtr window = _window != null ? WindowSystem.Instance.Find(_window) : IntPtr.Zero;
        // The builder covers the monitor the target window is on, or the one Keyloom is on.
        IntPtr place = window != IntPtr.Zero ? window : Window.GetWindow(this) is { } own ? new WindowInteropHelper(own).Handle : IntPtr.Zero;
        var screen = (place != IntPtr.Zero ? System.Windows.Forms.Screen.FromHandle(place) : System.Windows.Forms.Screen.PrimaryScreen!).Bounds;
        string name = MacroNameTextBox.Text.Trim().Length > 0 ? MacroNameTextBox.Text.Trim() : "Macro";
        int settle = int.TryParse(SettleTextBox.Text.Trim(), out int ms) ? ms : Profile.PointerSettleMs;
        var builder = new MacroBuilderWindow(name, _steps, new RouteSpace(WindowSystem.Instance, window, p), new Int32Rect(screen.X, screen.Y, screen.Width, screen.Height), settle);
        // Keyloom steps out of the way while you build, and comes back when you're done.
        var host = Host;
        var state = host?.WindowState ?? WindowState.Normal;
        builder.Finished += result =>
        {
            _builder = null;
            if (host != null && IsLoaded) { host.WindowState = state == WindowState.Minimized ? WindowState.Normal : state; host.Activate(); }
            if (result != null && IsLoaded) Attempt(() => ApplyBuilt(result));
        };
        _builder = builder;
        builder.Show();
        if (host != null) host.WindowState = WindowState.Minimized;
    });
    /// <summary>Takes the steps from the builder as one change, so one Ctrl+Z brings back what was there before.</summary>
    private void ApplyBuilt(BuilderResult result)
    {
        var steps = result.Steps;
        bool delayChanged = SettleTextBox.Text.Trim() != result.ClickDelayMs.ToString();
        SettleTextBox.Text = result.ClickDelayMs.ToString();
        if (steps.Count == _steps.Count && steps.Zip(_steps).All(pair => Same(pair.First, pair.Second)))
        {
            Say(delayChanged ? $"Click delay after moving is now {result.ClickDelayMs} ms. Save to keep it." : "Nothing changed in the builder.");
            return;
        }
        if (steps.Count > ProfileValidator.MaxActions) throw new ArgumentException($"A sequence can contain up to {ProfileValidator.MaxActions:N0} steps.");
        foreach (var step in steps) ProfileValidator.ValidateAction(step);
        Remember();
        _selecting = true;
        try { BindSteps(new ObservableCollection<MacroAction>(steps)); }
        finally { _selecting = false; }
        Select([]);
        int points = RouteBuilder.Points(steps).Count;
        Say($"Built on screen: {points:N0} {(points == 1 ? "point" : "points")}. Save to keep it; Ctrl+Z brings back the steps from before.");
    }
    private static bool Same(MacroAction a, MacroAction b) =>
        a.ActionType == b.ActionType && a.MouseButton == b.MouseButton && a.KeyName == b.KeyName && a.DelayMs == b.DelayMs && a.X == b.X && a.Y == b.Y
        && a.Space == b.Space && a.ScrollAmount == b.ScrollAmount && a.Text == b.Text && a.Name == b.Name && a.Repeat == b.Repeat && a.GapMs == b.GapMs && a.Collapsed == b.Collapsed;
}
