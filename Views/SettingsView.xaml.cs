using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Keyloom.Models;
using Keyloom.Services;

namespace Keyloom.Views;

/// <summary>One macro's line in the usage list. Share and Rest split the bar by run time.</summary>
public sealed record UsageRow(MacroProfile Profile, string Totals, string Last, GridLength Share, GridLength Rest);

/// <summary>App preferences and usage statistics. Changes apply and save immediately.</summary>
public partial class SettingsView : UserControl
{
    private readonly string? _path;
    private readonly IReadOnlyList<MacroProfile> _profiles;
    private readonly UsageStore? _usage;
    private bool _loading = true;
    public SettingsView(string? path = null, IReadOnlyList<MacroProfile>? profiles = null, UsageStore? usage = null)
    {
        InitializeComponent();
        _path = path; _profiles = profiles ?? []; _usage = usage;
        var settings = AppSettings.Current;
        ExperimentalBox.IsChecked = settings.ShowExperimental;
        BuilderBox.IsChecked = settings.MacroBuilder;
        (settings.Theme switch { AppTheme.Light => LightThemeRadio, AppTheme.Dark => DarkThemeRadio, _ => SystemThemeRadio }).IsChecked = true;
        IndicatorBox.IsChecked = settings.ShowRunIndicator;
        SoundBox.IsChecked = settings.ClickSounds;
        (settings.IndicatorPlace switch { IndicatorPlace.TopRight => PlaceTopRightRadio, IndicatorPlace.BottomRight => PlaceBottomRightRadio, _ => PlaceTopRadio }).IsChecked = true;
        VersionText.Text = $"Keyloom {AppVersion.Display}";
        _loading = false;
        ShowUsage();
        if (_usage != null)
        {
            _usage.Changed += ShowUsage;
            Unloaded += (_, _) => _usage.Changed -= ShowUsage;
        }
    }
    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var theme = sender == LightThemeRadio ? AppTheme.Light : sender == DarkThemeRadio ? AppTheme.Dark : AppTheme.System;
        ThemeManager.Apply(theme);
        Save(s => s.Theme = theme);
    }
    private void Experimental_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading) Save(s => s.ShowExperimental = ExperimentalBox.IsChecked == true);
    }
    private void Builder_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading) Save(s => s.MacroBuilder = BuilderBox.IsChecked == true);
    }
    private void Indicator_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading) Save(s => s.ShowRunIndicator = IndicatorBox.IsChecked == true);
    }
    private void Sound_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading) Save(s => s.ClickSounds = SoundBox.IsChecked == true);
    }
    private void Place_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var place = sender == PlaceTopRightRadio ? IndicatorPlace.TopRight : sender == PlaceBottomRightRadio ? IndicatorPlace.BottomRight : IndicatorPlace.Top;
        Save(s => s.IndicatorPlace = place);
    }
    private void Save(Action<AppSettings> change)
    {
        try { AppSettings.Current.With(change).Save(_path); ErrorText.Text = ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ErrorText.Text = "Settings could not be saved: " + ex.Message; }
    }

    private void ShowUsage()
    {
        var usage = _usage ?? new UsageStore();
        var used = _profiles.Select(p => (Profile: p, Usage: usage.For(p.Id))).Where(x => x.Usage.Starts > 0)
            .OrderByDescending(x => x.Usage.RunMs).ThenByDescending(x => x.Usage.Starts).ToList();
        long total = Math.Max(1, used.Sum(x => x.Usage.RunMs));
        StartsText.Text = usage.TotalStarts.ToString("N0");
        RunTimeText.Text = UsageStore.Duration(usage.TotalRunMs);
        TopText.Text = used.Count == 0 ? "None yet" : used.MaxBy(x => x.Usage.Starts).Profile.Name;
        UsageList.ItemsSource = used.Select(x =>
        {
            double share = (double)x.Usage.RunMs / total;
            string cycles = x.Usage.Cycles > 0 && x.Profile.Behavior != MacroBehaviorType.PressAndHold ? $" · {x.Usage.Cycles:N0} {x.Profile.CycleNoun}{(x.Usage.Cycles == 1 ? "" : "s")}" : "";
            return new UsageRow(x.Profile, $"{x.Usage.Starts:N0}× · {UsageStore.Duration(x.Usage.RunMs)}{cycles}", "Last " + UsageStore.When(x.Usage.LastRun),
                new GridLength(Math.Max(.01, share), GridUnitType.Star), new GridLength(Math.Max(.01, 1 - share), GridUnitType.Star));
        }).ToList();
        UsageList.Visibility = used.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        SinceText.Text = used.Count == 0 ? "Start a macro and it shows up here: how often it ran and for how long." : $"Counting since {usage.Since.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)}. Test runs from the editor don't count.";
        ResetUsageButton.IsEnabled = used.Count > 0;
    }
    private async void ResetUsage_Click(object sender, RoutedEventArgs e)
    {
        if (_usage == null) return;
        if (SheetLayer.For(this) is { } sheets && !await sheets.ConfirmAsync("Reset usage?", "Run counts and times start again from zero. Your macros stay as they are.", "Reset", danger: true)) return;
        _usage.Reset();
    }
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Keyloom");
        try { Directory.CreateDirectory(folder); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { ErrorText.Text = ex.Message; }
    }
}
