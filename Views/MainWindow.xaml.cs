using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Keyloom.Engine;
using Keyloom.Models;
using Keyloom.Services;

namespace Keyloom.Views;

/// <summary>The one Keyloom window: the macro list, plus the editor, settings and help as pages inside it.</summary>
public partial class MainWindow : Window
{
    private const string ListTitle = "Macros", ListSubtitle = "Shortcuts work everywhere, even while Keyloom sits in the tray.";
    private readonly MacroExecutor _executor = new();
    private readonly ProfileStorageService _storage = new();
    private readonly ToastOverlayWindow _toast = new();
    private readonly ObservableCollection<MacroProfile> _profiles;
    private readonly TrayService _tray;
    private HotkeyManager? _hotkeys;
    private MacroEditorView? _activeEditor;
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

    // Pages
    private void ShowPage(FrameworkElement? page, string title, string subtitle)
    {
        bool list = page == null;
        PageTitle.Text = title; PageSubtitle.Text = subtitle;
        BackButton.Visibility = list ? Visibility.Collapsed : Visibility.Visible;
        ListActions.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
        PageHost.Content = page;
        PageHost.Visibility = list ? Visibility.Collapsed : Visibility.Visible;
        ListPage.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
        if (!list) StatusText.Text = "";
        SlideIn(list ? ListPage : PageHost, list ? -1 : 1);
        SlideIn(TitlePanel, list ? -1 : 1);
    }
    /// <summary>Pages enter from the side they are navigated toward.</summary>
    private static void SlideIn(FrameworkElement element, int direction)
    {
        if (element.RenderTransform is not TranslateTransform shift) element.RenderTransform = shift = new TranslateTransform();
        var ease = new QuarticEase { EasingMode = EasingMode.EaseOut };
        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(18 * direction, 0, TimeSpan.FromMilliseconds(360)) { EasingFunction = ease });
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
    }
    private bool OnList => PageHost.Content == null;
    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();
    private void GoBack()
    {
        if (_activeEditor != null) _activeEditor.Cancel();
        else ShowPage(null, ListTitle, ListSubtitle);
    }
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowPage(new SettingsView(), "Settings", "Appearance, features and where your macros live.");
    private void Help_Click(object sender, RoutedEventArgs e) => ShowPage(new HelpView(), "Help", "How Keyloom works, in a minute.");
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !e.Handled)
        {
            if (Sheets.IsOpen) { Sheets.CloseTop(false); e.Handled = true; }
            else if (!OnList) { GoBack(); e.Handled = true; }
        }
        base.OnKeyDown(e);
    }

    // Editing
    private void Add_Click(object sender, RoutedEventArgs e) => Run(async () =>
    {
        await _executor.StopAsync();
        var p = new MacroProfile { Name = $"Macro {_profiles.Count + 1}" };
        OpenEditor(p, true, () =>
        {
            _storage.SaveProfiles(_profiles.Append(p).ToArray());
            _profiles.Add(p);
        });
    });
    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MacroProfile p }) return;
        Run(async () =>
        {
            await _executor.StopAsync();
            var copy = p.Snapshot();
            OpenEditor(copy, false, () =>
            {
                int index = _profiles.IndexOf(p);
                if (index < 0) throw new InvalidOperationException("This macro was removed while you edited it.");
                var next = _profiles.ToArray(); next[index] = copy;
                _storage.SaveProfiles(next);
                _profiles[index] = copy;
            });
        });
    }
    /// <summary>Shows the editor as a page. Shortcuts are paused while it is open; commit stores the saved profile.</summary>
    private void OpenEditor(MacroProfile p, bool isNew, Action commit)
    {
        _dialogOpen = true;
        _hotkeys?.RegisterProfiles([]);
        var editor = new MacroEditorView(p, _profiles, isNew);
        editor.Saved += () =>
        {
            try { commit(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            { editor.Say("The macro could not be saved: " + ex.Message, true); return; }
            IconStore.Prune(_profiles.Select(x => x.IconImage).Where(x => x.Length > 0));
            CloseEditor();
        };
        editor.Cancelled += CloseEditor;
        _activeEditor = editor;
        // The editor needs more room than the list; grow a small window once.
        if (WindowState == WindowState.Normal && ActualHeight > 0 && ActualHeight < 600)
            Height = Math.Min(640, SystemParameters.WorkArea.Height - 40);
        ShowPage(editor, isNew ? "New macro" : "Edit macro", isNew ? "Choose what it does, set a shortcut, then save." : p.Name);
    }
    private void CloseEditor()
    {
        _activeEditor = null;
        _dialogOpen = false;
        RegisterShortcuts();
        ShowPage(null, ListTitle, ListSubtitle);
    }
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MacroProfile p }) return;
        Run(async () =>
        {
            await _executor.StopAsync();
            if (!await Sheets.ConfirmAsync("Delete this macro?", $"'{p.Name}' and its shortcut will be removed. This can't be undone.", "Delete", danger: true)) return;
            _storage.SaveProfiles(_profiles.Where(x => x != p));
            _profiles.Remove(p); RegisterShortcuts();
            IconStore.Prune(_profiles.Select(x => x.IconImage).Where(x => x.Length > 0));
        });
    }
    private void RegisterShortcuts()
    {
        string warning = _hotkeys?.RegisterProfiles(_profiles) ?? "";
        if (warning.Length > 0) StatusText.Text = warning;
    }

    // Window
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
        _activeEditor?.StopRecording();
        _hotkeys?.Dispose();
        await _executor.DisposeAsync();
        Microsoft.Win32.SystemEvents.SessionEnding -= SessionEnding;
        _tray.Dispose(); _toast.Close();
        Application.Current.Shutdown();
    }
}
