using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Keyloom.Engine;
using Keyloom.Models;

namespace Keyloom.Views;

/// <summary>Screen targets: capturing a picture or a color, and the Click on target and Wait until steps that look for them.</summary>
public partial class MacroEditorView
{
    /// <summary>"Only look near where it was" looks this far around the spot, in screen pixels.</summary>
    private const int NearMargin = 80;
    private List<ScreenTarget> _targets = [];
    /// <summary>Where each target was last seen, so "Only look near where it was" can be turned on again.</summary>
    private readonly Dictionary<ScreenTarget, ScreenArea> _seenAt = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, BitmapSource> _thumbnails = new();
    private bool _snipping;

    private void InitializeVision()
    {
        _targets = Profile.Targets.Select(t => t.Clone()).ToList();
        foreach (var t in _targets.Where(t => t.Area is { Space: CoordinateSpace.Screen })) _seenAt[t] = t.Area!;
        RefreshTargetBox(null);
    }
    private ScreenTarget? CurrentTarget => TargetBox.SelectedItem is string name ? _targets.FirstOrDefault(t => t.Name == name) : null;
    private void RefreshTargetBox(string? select)
    {
        bool loading = _loadingStep;
        _loadingStep = true;
        TargetBox.ItemsSource = _targets.Select(t => t.Name).ToList();
        TargetBox.SelectedItem = select;
        _loadingStep = loading;
        ShowTargetCard();
    }

    private void ShowTargetOptions(bool looks)
    {
        TargetOptions.Visibility = looks ? Visibility.Visible : Visibility.Collapsed;
        if (!looks) return;
        bool wait = StepType == MacroActionType.WaitUntil;
        UntilPanel.Visibility = wait ? Visibility.Visible : Visibility.Collapsed;
        TimeoutLabel.Text = wait ? "Give up after" : "Keep looking for";
        TimeoutUnit.Text = wait ? "ms · 0 waits as long as it takes" : "ms · 0 looks once";
        StopIfMissingBox.Content = wait ? "Stop the macro when time is up" : "Stop the macro if it isn't found";
    }
    /// <summary>A step just turned into one that looks for a target: start from the first target and sensible times.</summary>
    private void PrepareTargetStep()
    {
        TargetBox.SelectedItem = _targets.FirstOrDefault()?.Name;
        TimeoutTextBox.Text = StepType == MacroActionType.ClickTarget ? "1000" : "0";
        UntilBox.SelectedIndex = 0;
        StopIfMissingBox.IsChecked = false;
        ShowTargetCard();
    }
    private void ShowTargetStep(MacroAction a)
    {
        if (a.ActionType is not (MacroActionType.ClickTarget or MacroActionType.WaitUntil)) return;
        TargetBox.SelectedItem = _targets.FirstOrDefault(t => string.Equals(t.Name, a.Target, StringComparison.OrdinalIgnoreCase))?.Name;
        UntilBox.SelectedIndex = a.Gone ? 1 : 0;
        TimeoutTextBox.Text = a.TimeoutMs.ToString();
        StopIfMissingBox.IsChecked = a.StopIfMissing;
        FindResultText.Text = "";
        ShowTargetCard();
    }
    private void ReadTargetStep(MacroAction a)
    {
        a.Target = TargetBox.SelectedItem as string ?? throw new ArgumentException("Pick what to look for, or take a new picture or color from the screen.");
        a.Gone = UntilBox.SelectedIndex == 1;
        a.TimeoutMs = Number(TimeoutTextBox.Text, "Time to look");
        a.StopIfMissing = StopIfMissingBox.IsChecked == true;
    }
    /// <summary>Keeps the targets that steps use; one captured and then left unused goes away on save.</summary>
    private void ApplyScreenTargets(MacroProfile p)
    {
        var used = _steps.Where(a => a.ActionType is MacroActionType.ClickTarget or MacroActionType.WaitUntil)
            .Select(a => a.Target ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        p.Targets = _targets.Where(t => used.Contains(t.Name)).Select(t => t.Clone()).ToList();
    }

    private void ShowTargetCard()
    {
        var t = CurrentTarget;
        TargetCard.Visibility = t == null ? Visibility.Collapsed : Visibility.Visible;
        if (t == null) return;
        bool loading = _loadingStep;
        _loadingStep = true;
        TargetNameTextBox.Text = t.Name;
        if (t.Kind == ScreenTargetKind.Color)
        {
            uint c = TargetPicture.ParseColor(t.Color);
            TargetSwatch.Background = new SolidColorBrush(Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c));
            TargetSwatch.Visibility = Visibility.Visible; TargetImage.Source = null;
            TargetInfoText.Text = $"Color {t.Color.ToUpperInvariant()}";
        }
        else
        {
            TargetSwatch.Visibility = Visibility.Collapsed;
            try
            {
                if (!_thumbnails.TryGetValue(t.Picture, out var bitmap)) _thumbnails[t.Picture] = bitmap = ScreenSnipWindow.ToBitmap(TargetPicture.Decode(t.Picture));
                TargetImage.Source = bitmap;
                TargetInfoText.Text = $"Picture · {bitmap.PixelWidth} × {bitmap.PixelHeight} px";
            }
            catch (ArgumentException ex) { TargetImage.Source = null; TargetInfoText.Text = ex.Message; }
        }
        ToleranceSlider.Value = Math.Min(t.Tolerance, ToleranceSlider.Maximum);
        ToleranceText.Text = ToleranceWords(t.Tolerance);
        NearBox.IsChecked = t.Area != null;
        NearBox.IsEnabled = t.Area != null || _seenAt.ContainsKey(t);
        _loadingStep = loading;
    }
    private static string ToleranceWords(int tolerance) => tolerance switch
    {
        0 => "0 · exact", <= 6 => $"{tolerance} · strict", <= 18 => $"{tolerance} · normal", <= 35 => $"{tolerance} · loose", _ => $"{tolerance} · very loose"
    };

    private void TargetBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingStep || !IsInitialized) return;
        FindResultText.Text = "";
        ShowTargetCard();
        CommitStep();
    }
    private void Tolerance_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingStep || !IsInitialized || CurrentTarget is not { } t) return;
        t.Tolerance = (int)ToleranceSlider.Value;
        ToleranceText.Text = ToleranceWords(t.Tolerance);
    }
    private void Near_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingStep || !IsInitialized || CurrentTarget is not { } t) return;
        if (NearBox.IsChecked == true && _seenAt.TryGetValue(t, out var seen))
            t.Area = new(seen.X - NearMargin, seen.Y - NearMargin, seen.Width + NearMargin * 2, seen.Height + NearMargin * 2);
        else if (NearBox.IsChecked != true)
        {
            if (t.Area is { Space: CoordinateSpace.Screen } area && !_seenAt.ContainsKey(t))
                _seenAt[t] = new(area.X + NearMargin, area.Y + NearMargin, Math.Max(1, area.Width - NearMargin * 2), Math.Max(1, area.Height - NearMargin * 2));
            t.Area = null;
        }
    }
    private void TargetName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; RenameTarget(); }
    }
    private void TargetName_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => RenameTarget();
    /// <summary>Renames the target and every step that looks for it.</summary>
    private void RenameTarget()
    {
        if (CurrentTarget is not { } t) return;
        string name = TargetNameTextBox.Text.Trim(), old = t.Name;
        if (name == old) return;
        if (name.Length == 0 || _targets.Any(x => x != t && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            Say(name.Length == 0 ? "A target needs a name." : $"Another target is already called '{name}'.", true);
            TargetNameTextBox.Text = old; return;
        }
        var picked = SelectedIndices();
        t.Name = name;
        _selecting = true;
        try
        {
            for (int i = 0; i < _steps.Count; i++)
                if (string.Equals(_steps[i].Target, old, StringComparison.OrdinalIgnoreCase)) { var c = _steps[i].Clone(); c.Target = name; _steps[i] = c; }
        }
        finally { _selecting = false; }
        RefreshTargetBox(name);
        Select(picked);
    }

    private void AddTargetStep_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || !Enum.TryParse(tag, out MacroActionType kind)) return;
        Attempt(() => InsertStep(new() { ActionType = kind, Target = _targets.FirstOrDefault()?.Name, TimeoutMs = kind == MacroActionType.ClickTarget ? 1000 : 0 }));
        if (_targets.Count == 0) Dispatcher.BeginInvoke(() => CaptureAsync(false));
    }
    private void CapturePicture_Click(object sender, RoutedEventArgs e) => _ = CaptureAsync(false);
    private void PickColor_Click(object sender, RoutedEventArgs e) => _ = CaptureAsync(true);
    /// <summary>Takes a new picture or color from the screen and makes it the picked step's target.</summary>
    private async Task CaptureAsync(bool color)
    {
        var snip = await OutOfTheWay(() => ScreenSnipWindow.TakeAsync(color));
        if (snip == null) return;
        string stem = color ? "Color" : "Picture";
        int n = 1;
        while (_targets.Any(t => string.Equals(t.Name, $"{stem} {n}", StringComparison.OrdinalIgnoreCase))) n++;
        var target = new ScreenTarget
        {
            Name = $"{stem} {n}", Kind = color ? ScreenTargetKind.Color : ScreenTargetKind.Picture,
            Color = color ? TargetPicture.ColorText(snip.Color) : "#000000", Picture = snip.Picture is { } p ? TargetPicture.Encode(p) : ""
        };
        if (color) target.Tolerance = 4;
        _targets.Add(target);
        _seenAt[target] = new(snip.Bounds.X, snip.Bounds.Y, snip.Bounds.Width, snip.Bounds.Height);
        if (StepEditor.IsVisible && StepType is MacroActionType.ClickTarget or MacroActionType.WaitUntil)
        {
            RefreshTargetBox(target.Name);
            CommitStep();
        }
        else RefreshTargetBox(CurrentTarget?.Name);
        Say($"Added {target.Name}. Rename it so the steps read well, like \"Play button\".");
        TargetNameTextBox.Focus(); TargetNameTextBox.SelectAll();
    }
    private async void FindNow_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentTarget is not { } t) return;
        var clock = new Stopwatch();
        try
        {
            var found = await OutOfTheWay(() =>
            {
                IntPtr window = _window != null ? WindowSystem.Instance.Find(_window) : IntPtr.Zero;
                var locator = new TargetLocator(ScreenCapture.Instance, WindowSystem.Instance, window, [t]);
                clock.Start();
                var match = locator.Locate(t.Name);
                clock.Stop();
                return Task.FromResult(match);
            });
            if (found is { } m)
            {
                _seenAt[t] = new(m.X, m.Y, m.Width, m.Height);
                NearBox.IsEnabled = true;
                var (x, y) = m.Center;
                FindResultText.Text = $"Found at {x}, {y} on the screen, in {clock.ElapsedMilliseconds} ms.";
                FindResultText.SetResourceReference(TextBlock.ForegroundProperty, "SuccessTextBrush");
            }
            else
            {
                FindResultText.Text = "Not on the screen right now. Open what it belongs to and try again, raise the tolerance, or take a new picture.";
                FindResultText.SetResourceReference(TextBlock.ForegroundProperty, "DangerTextBrush");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            FindResultText.Text = ex.Message;
            FindResultText.SetResourceReference(TextBlock.ForegroundProperty, "DangerTextBrush");
        }
    }
    /// <summary>Gets Keyloom off the screen while something is taken from it, then brings it back.</summary>
    private async Task<T?> OutOfTheWay<T>(Func<Task<T?>> work)
    {
        if (_snipping || _picker != null || _recording != null) return default;
        _snipping = true;
        Say("");
        var host = Host;
        MacroForm.IsEnabled = false;
        try
        {
            if (host != null) { host.WindowState = WindowState.Minimized; await Task.Delay(350); }
            return await work();
        }
        finally
        {
            _snipping = false;
            MacroForm.IsEnabled = true;
            if (host != null && IsLoaded) { host.WindowState = WindowState.Normal; host.Activate(); }
        }
    }
}
