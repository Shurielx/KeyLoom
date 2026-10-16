using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Keyloom.Engine;
using Keyloom.Models;
using Keyloom.Services;

namespace Keyloom.Views;

/// <summary>The one Keyloom window: the macro list, plus the editor, settings and help as pages inside it.</summary>
public partial class MainWindow : Window
{
    private const string ListTitle = "Macros", ListSubtitle = "Shortcuts work everywhere, even while Keyloom sits in the tray.";
    /// <summary>Test runs from the editor use ids with this prefix, so they never count in the statistics.</summary>
    private const string TestPrefix = "test-";
    private readonly MacroExecutor _executor = new();
    private readonly ProfileStorageService _storage = new();
    private readonly ToastOverlayWindow _toast = new();
    private readonly ObservableCollection<MacroProfile> _profiles;
    private readonly UsageStore _usage = UsageStore.Load();
    private readonly Dictionary<MacroProfile, Stopwatch> _running = new();
    private readonly DispatcherTimer _runClock = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly TrayService _tray;
    private HotkeyManager? _hotkeys;
    private MacroEditorView? _activeEditor;
    private TaskCompletionSource? _testDone;
    private bool _exiting, _dialogOpen;
    public MainWindow()
    {
        InitializeComponent();
        Backdrop.Attach(this);
        VersionButton.Content = AppVersion.Display;
        var loaded = _storage.LoadProfiles();
        _profiles = loaded.Profiles;
        StatusText.Text = loaded.Warning;
        ProfilesItemsControl.ItemsSource = _profiles;
        foreach (var p in _profiles) ShowUsage(p);
        _profiles.CollectionChanged += (_, e) =>
        {
            EmptyText.Visibility = _profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var p in e.NewItems?.OfType<MacroProfile>() ?? []) ShowUsage(p);
        };
        EmptyText.Visibility = _profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _executor.ExecutionStateChanged += (p, active) => Dispatcher.BeginInvoke(() => OnRunChanged(p, active));
        _executor.ProgressChanged += (p, count) => Dispatcher.BeginInvoke(() => { p.ExecutionCount = count; ShowRun(p); });
        _executor.ExecutionFailed += message => Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = message;
            if (_testDone != null) _activeEditor?.ShowScriptOutput(message, true);
        });
        _executor.ScriptLogged += line => Dispatcher.BeginInvoke(() => _activeEditor?.ShowScriptOutput(line));
        _runClock.Tick += (_, _) => { foreach (var p in _running.Keys) ShowRun(p); };
        _tray = new(ShowHud, StopAll, () => Run(ExitAsync));
        Loaded += (_, _) =>
        {
            _hotkeys = new(new WindowInteropHelper(this).Handle);
            _hotkeys.ProfileTriggered += p => { if (!_dialogOpen && !_exiting) Run(() => ToggleAsync(p)); };
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

    // Running and statistics
    private void OnRunChanged(MacroProfile p, bool active)
    {
        p.IsActive = active;
        bool test = p.Id.StartsWith(TestPrefix, StringComparison.Ordinal);
        if (active)
        {
            p.ExecutionCount = 0;
            _running[p] = Stopwatch.StartNew();
            if (!test) { _usage.Started(p.Id); ShowUsage(p); }
            ShowRun(p);
            _runClock.Start();
            if (!_exiting) _toast.ShowRunning(test ? $"Test · {p.Name}" : p.Name, () => Count(p));
            return;
        }
        // Already counted when Keyloom exits with the macro still running.
        if (!_running.Remove(p, out var clock)) return;
        var ran = clock.Elapsed;
        if (_running.Count == 0) _runClock.Stop();
        if (!test) { _usage.Stopped(p.Id, ran, p.ExecutionCount); ShowUsage(p); }
        if (!_exiting) _toast.ShowStopped(test ? $"Test · {p.Name}" : p.Name, $"Ran {UsageStore.Clock(ran)} · {Count(p)}");
        if (test) _testDone?.TrySetResult();
    }
    private static string Count(MacroProfile p) => p.Behavior == MacroBehaviorType.PressAndHold ? "held"
        : $"{p.ExecutionCount:N0} {p.CycleNoun}{(p.ExecutionCount == 1 ? "" : "s")}";
    private void ShowRun(MacroProfile p)
    {
        if (_running.TryGetValue(p, out var clock)) p.RunText = $"Running · {UsageStore.Clock(clock.Elapsed)} · {Count(p)}";
    }
    private void ShowUsage(MacroProfile p)
    {
        var u = _usage.For(p.Id);
        p.UsageText = u.Starts == 0 ? "Not used yet" : $"Used {u.Starts:N0}× · {UsageStore.Duration(u.RunMs)}";
    }
    /// <summary>Plays a macro once from the editor with real input. The window gets out of the way and comes back afterwards.</summary>
    private async Task TestRunAsync(MacroProfile p)
    {
        if (_testDone != null) return;
        p.Id = TestPrefix + Guid.NewGuid().ToString("N");
        _testDone = new();
        var state = WindowState;
        WindowState = WindowState.Minimized;
        try
        {
            await _executor.ToggleAsync(p);
            await _testDone.Task;
        }
        finally
        {
            _testDone = null;
            WindowState = state == WindowState.Minimized ? WindowState.Normal : state;
            Activate();
        }
    }

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
        // Fades stop at the end instead of holding 1: a held opacity keeps the page on its own layer, which turns ClearType text grey.
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { FillBehavior = FillBehavior.Stop });
    }
    private bool OnList => PageHost.Content == null;
    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();
    private void GoBack()
    {
        if (_activeEditor != null) _activeEditor.Cancel();
        else ShowPage(null, ListTitle, ListSubtitle);
    }
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowPage(new SettingsView(null, _profiles, _usage), "Settings", "Appearance, usage and where your macros live.");
    private void Help_Click(object sender, RoutedEventArgs e) => ShowPage(new HelpView(), "Help", "How Keyloom works, in a minute.");
    private void About_Click(object sender, RoutedEventArgs e) => ShowPage(new AboutView(), "About Keyloom", "What's new, and whether a newer version is out.");
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !e.Handled)
        {
            if (Sheets.IsOpen) { Sheets.CloseTop(false); e.Handled = true; }
            else if (!OnList) { GoBack(); e.Handled = true; }
        }
        // Undo still works when focus left the steps, like after deleting every step.
        if (!e.Handled && !Sheets.IsOpen && _activeEditor != null && _activeEditor.UndoShortcut(Keyboard.Modifiers, e.Key == Key.System ? e.SystemKey : e.Key)) e.Handled = true;
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
        var editor = new MacroEditorView(p, _profiles, isNew) { TestRun = TestRunAsync };
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
        if (WindowState == WindowState.Normal && ActualHeight > 0 && ActualHeight < 640)
            Height = Math.Min(700, SystemParameters.WorkArea.Height - 40);
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
            _usage.Keep(_profiles.Select(x => x.Id)); _usage.Save();
            IconStore.Prune(_profiles.Select(x => x.IconImage).Where(x => x.Length > 0));
        });
    }
    private void RegisterShortcuts()
    {
        string warning = _hotkeys?.RegisterProfiles(_profiles) ?? "";
        if (warning.Length > 0) StatusText.Text = warning;
    }

    // Changing a macro from the list: name, icon and shortcut, saved right away.
    /// <summary>Applies a change to a copy first, so a change that can't be saved leaves the macro as it was.</summary>
    private bool ChangeInPlace(MacroProfile p, Action<MacroProfile> change)
    {
        int index = _profiles.IndexOf(p);
        if (index < 0) return false;
        try
        {
            var copy = p.Snapshot();
            change(copy);
            ProfileValidator.Validate(copy);
            var next = _profiles.ToArray(); next[index] = copy;
            _storage.SaveProfiles(next);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { StatusText.Text = ex.Message; return false; }
        change(p); p.Refresh();
        StatusText.Text = "";
        return true;
    }
    private void RowName_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: MacroProfile p }) { p.IsRenaming = true; e.Handled = true; }
    }
    private void NameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } box) Dispatcher.BeginInvoke(() => { box.Text = (box.Tag as MacroProfile)?.Name ?? box.Text; box.Focus(); box.SelectAll(); }, DispatcherPriority.Input);
    }
    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { Tag: MacroProfile p } box) return;
        if (e.Key == Key.Enter) { CommitName(p, box); e.Handled = true; }
        else if (e.Key == Key.Escape) { p.IsRenaming = false; e.Handled = true; }
    }
    private void NameBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { Tag: MacroProfile { IsRenaming: true } p } box) CommitName(p, box);
    }
    private void CommitName(MacroProfile p, TextBox box)
    {
        string name = box.Text.Trim();
        p.IsRenaming = false;
        if (name.Length > 0 && name != p.Name) ChangeInPlace(p, c => c.Name = name);
    }

    private MacroProfile? _capturing;
    private void RowShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MacroProfile p } button) return;
        if (_capturing == p) { EndCapture(); return; }
        EndCapture();
        _capturing = p; p.IsCapturing = true;
        // Free every shortcut while listening, so pressing one that is taken reaches Keyloom instead of starting a macro.
        _hotkeys?.RegisterProfiles([]);
        button.Focus();
    }
    private void RowShortcut_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not Button { Tag: MacroProfile p } || _capturing != p) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape || key == Key.Tab) { EndCapture(); return; }
        if (key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            EndCapture();
            ChangeInPlace(p, c => { c.PrimaryVk = 0; c.PrimaryModifiers = 0; });
            RegisterShortcuts(); return;
        }
        if (Shortcut.IsModifier(key)) return;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key), modifiers = Shortcut.CurrentModifiers();
        if (vk == 0) return;
        if (Shortcut.Problem(modifiers, vk) is { } problem) { StatusText.Text = problem; return; }
        if (_profiles.FirstOrDefault(x => x != p && x.PrimaryVk == vk && x.PrimaryModifiers == modifiers) is { } other)
        { StatusText.Text = $"'{other.Name}' already uses {Shortcut.Format(modifiers, vk)}."; return; }
        EndCapture();
        ChangeInPlace(p, c => { c.PrimaryVk = vk; c.PrimaryModifiers = modifiers; c.FallbackVk = 0; c.FallbackModifiers = 0; });
        RegisterShortcuts();
    }
    private void RowShortcut_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is Button { Tag: MacroProfile p } && _capturing == p) EndCapture();
    }
    private void EndCapture()
    {
        if (_capturing == null) return;
        _capturing.IsCapturing = false; _capturing = null;
        if (!_dialogOpen) RegisterShortcuts();
    }

    private MacroProfile? _iconTarget;
    private void RowIcon_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MacroProfile p } button) return;
        _iconTarget = p;
        RowIconPicker.Show(p.IconKind, p.IconGlyph, p.IconImage, p.Behavior, p.TargetType);
        RowIconPopup.PlacementTarget = button;
        RowIconPopup.IsOpen = true;
    }
    private void RowIconPicker_Changed()
    {
        if (_iconTarget is { } p) ChangeInPlace(p, RowIconPicker.ApplyTo);
    }
    private void RowIconPicker_CloseRequested() => RowIconPopup.IsOpen = false;
    private void RowIconPicker_Failed(string message) => StatusText.Text = message;
    private void RowIconPopup_Closed(object? sender, EventArgs e)
    {
        // The file dialog for a picture opens after the popup closes, so keep the target until a new icon is clicked.
        IconStore.Prune(_profiles.Select(x => x.IconImage).Where(x => x.Length > 0));
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
        // The stop event is queued behind this call; record what was running so its time still counts.
        foreach (var (p, clock) in _running.Where(r => !r.Key.Id.StartsWith(TestPrefix, StringComparison.Ordinal)).ToList())
            _usage.Stopped(p.Id, clock.Elapsed, p.ExecutionCount);
        _running.Clear();
        Microsoft.Win32.SystemEvents.SessionEnding -= SessionEnding;
        _tray.Dispose(); _toast.Close();
        Application.Current.Shutdown();
    }
}
