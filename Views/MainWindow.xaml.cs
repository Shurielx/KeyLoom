using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Keyloom.Engine;
using Keyloom.Models;
using Keyloom.Services;

namespace Keyloom.Views;

public partial class MainWindow : Window
{
    private readonly MacroExecutor _executor = new();
    private readonly ProfileStorageService _storage = new();
    private readonly ToastOverlayWindow _toast = new();
    private readonly ObservableCollection<MacroProfile> _profiles;
    private readonly TrayService _tray;
    private HotkeyManager? _hotkeys;
    private MacroEditorDialog? _activeEditor;
    private bool _exiting, _dialogOpen;
    public MainWindow()
    {
        InitializeComponent();
        Backdrop.Attach(this);
        VersionText.Text = AppVersion.Display;
        var loaded = _storage.LoadProfiles();
        _profiles = loaded.Profiles;
        StatusText.Text = loaded.Warning;
        ProfilesItemsControl.ItemsSource = _profiles;
        _profiles.CollectionChanged += (_, _) => EmptyText.Visibility = _profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = _profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _executor.ExecutionStateChanged += (p, active) => Dispatcher.BeginInvoke(() =>
        {
            p.IsActive = active;
            if (active) p.ExecutionCount = 0;
            if (!_exiting) _toast.ShowStatus(p.Name, active);
        });
        _executor.ProgressChanged += (p, count) => Dispatcher.BeginInvoke(() => p.ExecutionCount = count);
        _executor.ExecutionFailed += message => Dispatcher.BeginInvoke(() => StatusText.Text = message);
        _tray = new(ShowHud, StopAll, () => Run(ExitAsync));
        Loaded += (_, _) =>
        {
            _hotkeys = new(new WindowInteropHelper(this).Handle);
            _hotkeys.ProfileTriggered += p => { if (!_dialogOpen && !_exiting) Run(() => ToggleAsync(p)); };
            _hotkeys.WindowTriggered += () => { if (!_dialogOpen) { if (IsVisible) Hide(); else ShowHud(); } };
            _hotkeys.StopTriggered += StopAll;
            RegisterShortcuts();
        };
        Closing += OnClosing;
        Microsoft.Win32.SystemEvents.SessionEnding += SessionEnding;
    }
    private async void Run(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private Task ToggleAsync(MacroProfile p)
    {
        StatusText.Text = "";
        return _executor.ToggleAsync(p);
    }
    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MacroProfile p }) Run(() => ToggleAsync(p));
    }
    private void Stop_Click(object sender, RoutedEventArgs e) => StopAll();
    private void StopAll() { _activeEditor?.StopRecording(); Run(_executor.StopAsync); }
    private void Add_Click(object sender, RoutedEventArgs e) => Run(async () =>
    {
        await _executor.StopAsync();
        var p = new MacroProfile { Name = $"Macro {_profiles.Count + 1}" };
        if (EditDialog(p, true))
        {
            var next = _profiles.Append(p).ToArray();
            _storage.SaveProfiles(next);
            _profiles.Add(p); RegisterShortcuts();
        }
    });
    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MacroProfile p }) return;
        Run(async () =>
        {
            await _executor.StopAsync();
            var copy = p.Snapshot();
            if (!EditDialog(copy, false)) return;
            int index = _profiles.IndexOf(p);
            var next = _profiles.ToArray(); next[index] = copy;
            _storage.SaveProfiles(next);
            _profiles[index] = copy; RegisterShortcuts();
        });
    }
    private bool EditDialog(MacroProfile p, bool isNew)
    {
        _dialogOpen = true;
        _hotkeys?.RegisterProfiles([]);
        try
        {
            _activeEditor = new MacroEditorDialog(p, _profiles, isNew) { Owner = this };
            return _activeEditor.ShowDialog() == true;
        }
        finally { _activeEditor = null; _dialogOpen = false; RegisterShortcuts(); }
    }
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MacroProfile p }) return;
        Run(async () =>
        {
            await _executor.StopAsync();
            if (MessageBox.Show(this, $"Delete '{p.Name}'?", "Keyloom", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _storage.SaveProfiles(_profiles.Where(x => x != p));
            _profiles.Remove(p); RegisterShortcuts();
        });
    }
    private void RegisterShortcuts()
    {
        string warning = _hotkeys?.RegisterProfiles(_profiles) ?? "";
        if (warning.Length > 0) StatusText.Text = warning;
    }
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        _dialogOpen = true;
        try { new SettingsDialog { Owner = this }.ShowDialog(); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { StatusText.Text = "Settings could not be saved: " + ex.Message; }
        finally { _dialogOpen = false; }
    }
    private void Help_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "Create a macro, choose Repeat input, Hold input, or Sequence, then set a shortcut.\n\nRecord captures mouse and keyboard input after a 3-second countdown. F8 stops recording. Edit the captured steps or insert delays afterward. Preview plays a sequence's route without sending input.\n\nPick point chooses a spot inside another window, so clicks follow that window when it moves.\n\nStart again with the same shortcut to stop playback. Only one macro runs at a time.\n\nCtrl + Shift + F12: stop all\nCtrl + Shift + I: show or hide Keyloom\n\nClosing the window keeps Keyloom in the tray. Use the tray menu to exit.\n\nAny Windows keyboard or mouse works. Some apps reject synthetic input; game rules may restrict automation.",
        "Keyloom help", MessageBoxButton.OK, MessageBoxImage.Information);
    public void ShowHud() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_exiting) { e.Cancel = true; Hide(); }
    }
    private void SessionEnding(object sender, Microsoft.Win32.SessionEndingEventArgs e) => Run(ExitAsync);
    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _hotkeys?.Dispose();
        await _executor.DisposeAsync();
        Microsoft.Win32.SystemEvents.SessionEnding -= SessionEnding;
        _tray.Dispose(); _toast.Close();
        Application.Current.Shutdown();
    }
}
