using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Keyloom.Engine;
using Keyloom.Models;
using Keyloom.Services;

namespace Keyloom.Views;

public partial class MacroEditorDialog : Window
{
    private sealed record StepChoice(MacroActionType Kind, string Label)
    {
        public override string ToString() => Label;
    }
    public MacroProfile Profile { get; }
    private readonly IEnumerable<MacroProfile> _others;
    private ObservableCollection<MacroAction> _steps;
    private uint _key, _modifiers;
    private bool _loadingStep;
    public MacroEditorDialog(MacroProfile profile, IEnumerable<MacroProfile>? others = null, bool isNew = false)
    {
        InitializeComponent();
        Backdrop.Attach(this);
        Profile = profile; _others = others ?? [];
        _steps = new(profile.Actions.Select(a => a.Clone()));
        DialogTitleText.Text = isNew ? "New macro" : "Edit macro";
        MacroNameTextBox.Text = profile.Name;
        _key = profile.PrimaryVk; _modifiers = profile.PrimaryModifiers; UpdateShortcut();
        ModeBox.SelectedIndex = (int)profile.Behavior;
        TargetBox.SelectedIndex = (int)profile.TargetType;
        SelectMouse(MouseButtonBox, profile.TargetMouseButton);
        TargetKeyTextBox.Text = profile.TargetKey;
        StartDelayTextBox.Text = profile.StartDelayMs.ToString();
        IntervalTextBox.Text = profile.SpamIntervalMs.ToString();
        RunsTextBox.Text = profile.RepeatCount.ToString();
        BindSteps(_steps);
        StepTypeBox.ItemsSource = new[]
        {
            new StepChoice(MacroActionType.MouseClick, "Mouse click"), new(MacroActionType.MouseDown, "Mouse down"),
            new(MacroActionType.MouseUp, "Mouse up"), new(MacroActionType.KeyPress, "Key press"),
            new(MacroActionType.KeyDown, "Key down"), new(MacroActionType.KeyUp, "Key up"),
            new(MacroActionType.Delay, "Wait"), new(MacroActionType.MouseMove, "Move mouse"),
            new(MacroActionType.MouseScroll, "Scroll"), new(MacroActionType.Text, "Type text"),
            new(MacroActionType.MousePosition, "Pointer position")
        };
        StepTypeBox.SelectedIndex = 0; StepMouseBox.SelectedIndex = 0;
        InitializeTarget();
        UpdateMode();
        FitHeight();
        Loaded += (_, _) => FitHeight();
        InitializeRecording();
    }
    private void UpdateShortcut() => ShortcutTextBox.Text = Shortcut.Format(_modifiers, _key);
    private void Shortcut_Focused(object sender, KeyboardFocusChangedEventArgs e) => ShortcutTextBox.Text = "Press a shortcut...";
    private void Shortcut_Unfocused(object sender, KeyboardFocusChangedEventArgs e) => UpdateShortcut();
    private void ClearShortcut_Click(object sender, RoutedEventArgs e) { _key = 0; _modifiers = 0; UpdateShortcut(); }
    private void Shortcut_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key), modifiers = Shortcut.CurrentModifiers();
        if (vk == 0) return;
        if (Shortcut.IsReserved(modifiers, vk)) { ErrorText.Text = "This shortcut is reserved for Keyloom."; return; }
        bool functionKey = vk is >= 0x70 and <= 0x87;
        if (!functionKey && (modifiers & (Shortcut.Control | Shortcut.Alt | Shortcut.Win)) == 0)
        { ErrorText.Text = "Use Ctrl, Alt, or Win with a key, or choose an F1–F24 key."; return; }
        _key = vk; _modifiers = modifiers; ErrorText.Text = ""; UpdateShortcut();
    }
    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        UpdateMode();
        if (IsLoaded) FitHeight();
    }
    private void FitHeight()
    {
        double preferred = (ModeBox.SelectedIndex == 2 ? 700 : ModeBox.SelectedIndex == 1 ? 560 : 600) + (RunModePanel.Visibility == Visibility.Visible ? 40 : 0);
        Height = Math.Min(preferred, Math.Max(MinHeight, SystemParameters.WorkArea.Height - 48));
    }
    private void Target_Changed(object sender, SelectionChangedEventArgs e) => UpdateMode();
    private void UpdateMode()
    {
        if (TargetPanel == null || SequencePanel == null || IntervalPanel == null) return;
        bool sequence = ModeBox.SelectedIndex == 2, hold = ModeBox.SelectedIndex == 1;
        TargetPanel.Visibility = sequence ? Visibility.Collapsed : Visibility.Visible;
        SequencePanel.Visibility = sequence ? Visibility.Visible : Visibility.Collapsed;
        MouseButtonBox.Visibility = TargetBox.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        KeyPanel.Visibility = TargetBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        IntervalPanel.Visibility = RunsPanel.Visibility = hold ? Visibility.Collapsed : Visibility.Visible;
        IntervalLabel.Text = sequence ? "Loop pause (ms)" : "Interval (ms)";
        ModeHintText.Visibility = hold ? Visibility.Visible : Visibility.Collapsed;
        ModeHintText.Text = "The input stays held until you stop this macro.";
        UpdateTargetUi();
    }
    private MacroActionType StepType => StepTypeBox.SelectedValue is MacroActionType kind ? kind : MacroActionType.MouseClick;
    private void StepType_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (StepValueTextBox == null) return;
        bool mouse = StepType is MacroActionType.MouseClick or MacroActionType.MouseDown or MacroActionType.MouseUp;
        StepMouseBox.Visibility = mouse ? Visibility.Visible : Visibility.Collapsed;
        StepSpaceBox.Visibility = StepType == MacroActionType.MousePosition ? Visibility.Visible : Visibility.Collapsed;
        StepValueTextBox.Visibility = mouse ? Visibility.Collapsed : Visibility.Visible;
        StepValueTextBox.AcceptsReturn = StepType == MacroActionType.Text;
        StepValueTextBox.TextWrapping = StepType == MacroActionType.Text ? TextWrapping.Wrap : TextWrapping.NoWrap;
        StepHintText.Text = StepType switch
        {
            MacroActionType.KeyPress or MacroActionType.KeyDown or MacroActionType.KeyUp => "Examples: Space, F8, Ctrl+C. Use Plus for the + key.",
            MacroActionType.Delay => "Delay in milliseconds.",
            MacroActionType.MouseMove => "Relative movement: x, y in pixels. Example: 20, -10.",
            MacroActionType.MousePosition => "x, y in pixels. Screen positions support negative coordinates on additional monitors; window positions follow the target window.",
            MacroActionType.MouseScroll => "Positive scrolls up; negative scrolls down. Example: -3.",
            MacroActionType.Text => "Types Unicode text into the focused application.",
            _ => "Choose a mouse button."
        };
        if (!_loadingStep) StepValueTextBox.Text = StepType switch
        {
            MacroActionType.Delay => "50", MacroActionType.MouseMove => "20, 0",
            MacroActionType.MousePosition => "0, 0",
            MacroActionType.MouseScroll => "1", MacroActionType.Text => "",
            _ => "Space"
        };
    }
    private void Step_Selected(object sender, SelectionChangedEventArgs e)
    {
        UpdateStepButton.IsEnabled = ActionsListBox.SelectedIndex >= 0;
        if (ActionsListBox.SelectedItem is not MacroAction a) return;
        _loadingStep = true;
        StepTypeBox.SelectedValue = a.ActionType;
        SelectMouse(StepMouseBox, a.MouseButton);
        StepSpaceBox.SelectedIndex = (int)a.Space;
        StepValueTextBox.Text = a.ActionType switch
        {
            MacroActionType.Delay => a.DelayMs.ToString(), MacroActionType.MouseMove or MacroActionType.MousePosition => $"{a.X}, {a.Y}",
            MacroActionType.MouseScroll => a.ScrollAmount.ToString(), MacroActionType.Text => a.Text,
            _ => a.KeyName
        };
        _loadingStep = false;
    }
    private MacroAction ReadStep()
    {
        string value = StepValueTextBox.Text.Trim();
        var a = new MacroAction { ActionType = StepType, MouseButton = MouseValue(StepMouseBox), KeyName = value, Text = StepValueTextBox.Text };
        if (StepType == MacroActionType.Delay) a.DelayMs = Number(value, "Step delay");
        if (StepType == MacroActionType.MouseScroll) a.ScrollAmount = Number(value, "Scroll amount");
        if (StepType is MacroActionType.MouseMove or MacroActionType.MousePosition)
        {
            var parts = value.Split(',');
            if (parts.Length != 2) throw new ArgumentException("Enter mouse movement as x, y.");
            a.X = Number(parts[0], "X"); a.Y = Number(parts[1], "Y");
            if (StepType == MacroActionType.MousePosition) a.Space = (CoordinateSpace)Math.Max(0, StepSpaceBox.SelectedIndex);
        }
        ProfileValidator.ValidateAction(a); return a;
    }
    private void AddStep_Click(object sender, RoutedEventArgs e) => Attempt(() =>
    {
        InsertStep(ReadStep());
    });
    private void InsertDelay_Click(object sender, RoutedEventArgs e) => Attempt(() => InsertStep(new() { ActionType = MacroActionType.Delay, DelayMs = 100 }));
    private void DuplicateStep_Click(object sender, RoutedEventArgs e) => Attempt(() =>
    {
        if (ActionsListBox.SelectedItem is MacroAction a) InsertStep(a.Clone());
    });
    private void InsertStep(MacroAction action)
    {
        if (_steps.Count >= ProfileValidator.MaxActions) throw new ArgumentException($"A sequence can contain up to {ProfileValidator.MaxActions:N0} steps.");
        int index = ActionsListBox.SelectedIndex >= 0 ? ActionsListBox.SelectedIndex + 1 : _steps.Count;
        _steps.Insert(index, action); ActionsListBox.SelectedIndex = index;
        ActionsListBox.ScrollIntoView(action);
    }
    private void UpdateStep_Click(object sender, RoutedEventArgs e) => Attempt(() =>
    {
        int i = ActionsListBox.SelectedIndex;
        if (i >= 0) { _steps[i] = ReadStep(); ActionsListBox.SelectedIndex = i; }
    });
    private void RemoveStep_Click(object sender, RoutedEventArgs e)
    {
        int i = ActionsListBox.SelectedIndex; if (i >= 0) _steps.RemoveAt(i);
    }
    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(1);
    private void Move(int offset)
    {
        int i = ActionsListBox.SelectedIndex, next = i + offset;
        if (i >= 0 && next >= 0 && next < _steps.Count) { _steps.Move(i, next); ActionsListBox.SelectedIndex = next; }
    }
    private void Save_Click(object sender, RoutedEventArgs e) => Attempt(() =>
    {
        if (_key != 0 && _others.Any(p => p.Id != Profile.Id && p.PrimaryVk == _key && p.PrimaryModifiers == _modifiers))
            throw new ArgumentException("Another macro already uses this shortcut.");
        Profile.Name = MacroNameTextBox.Text.Trim();
        Profile.PrimaryVk = _key; Profile.PrimaryModifiers = _modifiers;
        Profile.FallbackVk = 0; Profile.FallbackModifiers = 0;
        Profile.Behavior = (MacroBehaviorType)ModeBox.SelectedIndex;
        Profile.TargetType = (TargetInputType)TargetBox.SelectedIndex;
        Profile.TargetMouseButton = MouseValue(MouseButtonBox);
        Profile.TargetKey = TargetKeyTextBox.Text.Trim();
        Profile.StartDelayMs = Number(StartDelayTextBox.Text, "Start delay");
        Profile.SpamIntervalMs = Profile.Behavior == MacroBehaviorType.PressAndHold ? Math.Max(1, Profile.SpamIntervalMs) : Number(IntervalTextBox.Text, "Interval");
        Profile.RepeatCount = Profile.Behavior == MacroBehaviorType.PressAndHold ? 0 : Number(RunsTextBox.Text, "Runs");
        Profile.Actions = new(_steps.Select(a => a.Clone()));
        ApplyTarget(Profile);
        ProfileValidator.Validate(Profile);
        DialogResult = true;
    });
    private void Attempt(Action action)
    {
        try { ErrorText.Text = ""; action(); }
        catch (ArgumentException ex) { ErrorText.Text = ex.Message; }
    }
    private static int Number(string value, string label) => int.TryParse(value, out int number) ? number : throw new ArgumentException($"{label} must be a whole number.");
    private static string MouseValue(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Left";
    private static void SelectMouse(ComboBox box, string value)
    {
        box.SelectedIndex = 0;
        foreach (ComboBoxItem item in box.Items) if ((string)item.Content == value) box.SelectedItem = item;
    }
}
