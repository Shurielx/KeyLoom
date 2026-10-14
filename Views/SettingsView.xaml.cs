using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Keyloom.Services;

namespace Keyloom.Views;

/// <summary>App preferences. Changes apply and save immediately.</summary>
public partial class SettingsView : UserControl
{
    private readonly string? _path;
    private bool _loading = true;
    public SettingsView(string? path = null)
    {
        InitializeComponent();
        _path = path;
        ExperimentalBox.IsChecked = AppSettings.Current.ShowExperimental;
        (AppSettings.Current.Theme switch { AppTheme.Light => LightThemeRadio, AppTheme.Dark => DarkThemeRadio, _ => SystemThemeRadio }).IsChecked = true;
        VersionText.Text = $"Keyloom {AppVersion.Display}";
        _loading = false;
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
    private void Save(Action<AppSettings> change)
    {
        try { AppSettings.Current.With(change).Save(_path); ErrorText.Text = ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ErrorText.Text = "Settings could not be saved: " + ex.Message; }
    }
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Keyloom");
        try { Directory.CreateDirectory(folder); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { ErrorText.Text = ex.Message; }
    }
}
