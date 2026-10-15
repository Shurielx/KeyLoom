using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Keyloom.Engine;
using Keyloom.Models;
using Keyloom.Services;

namespace Keyloom.Views;

/// <summary>Macro editor shown inside the main window. Raises <see cref="Saved"/> after the profile passes validation.</summary>
public partial class MacroEditorView : UserControl
{
    private sealed record StepChoice(MacroActionType Kind, string Label)
    {
        public override string ToString() => Label;
    }
    public MacroProfile Profile { get; }
    public bool IsNew { get; }
    public event Action? Saved;
    public event Action? Cancelled;
    private readonly IEnumerable<MacroProfile> _others;
    private ObservableCollection<MacroAction> _steps;
    private uint _key, _modifiers;
    private bool _loadingStep, _committing;
    private string _stepError = "";
    public MacroEditorView(MacroProfile profile, IEnumerable<MacroProfile>? others = null, bool isNew = false)
    {
        InitializeComponent();
        Profile = profile; _others = others ?? []; IsNew = isNew;
        _steps = new(profile.Actions.Select(a => a.Clone()));
        MacroNameTextBox.Text = profile.Name;
        _key = profile.PrimaryVk; _modifiers = profile.PrimaryModifiers; UpdateShortcut();
        (profile.TargetType == TargetInputType.Keyboard ? KeyboardRadio : MouseRadio).IsChecked = true;
        SelectButton(profile.TargetMouseButton);
        TargetKeyTextBox.Text = profile.TargetKey;
        StartDelayTextBox.Text = profile.StartDelayMs.ToString();
        IntervalTextBox.Text = profile.SpamIntervalMs.ToString();
        RunsTextBox.Text = profile.RepeatCount.ToString();
        StepTypeBox.ItemsSource = new[]
        {
            new StepChoice(MacroActionType.MouseClick, "Click"), new(MacroActionType.MouseDown, "Hold button"),
            new(MacroActionType.MouseUp, "Release button"), new(MacroActionType.KeyPress, "Press key"),
            new(MacroActionType.KeyDown, "Hold key"), new(MacroActionType.KeyUp, "Release key"),
            new(MacroActionType.Delay, "Wait"), new(MacroActionType.MouseMove, "Move pointer by"),
            new(MacroActionType.MouseScroll, "Scroll"), new(MacroActionType.Text, "Type text"),
            new(MacroActionType.MousePosition, "Move pointer to")
        };
        _loadingStep = true; StepTypeBox.SelectedIndex = 0; StepMouseBox.SelectedIndex = 0; _loadingStep = false;
        BindSteps(_steps);
        InitializeStepList();
        InitializeGapButton();
        InitializeTarget();
        InitializeIcon();
        SetMode((int)profile.Behavior);
        InitializeRecording();
        MoreToggle.IsChecked = profile.Window != null && profile.Behavior == MacroBehaviorType.Sequence || profile.ExecutionMode != ExecutionMode.Standard;
        Loaded += (_, _) => { if (isNew) { MacroNameTextBox.Focus(); MacroNameTextBox.SelectAll(); } };
    }
    /// <summary>0 repeat, 1 hold, 2 sequence: the order of <see cref="MacroBehaviorType"/>.</summary>
    private int Mode => SequenceRadio.IsChecked == true ? 2 : HoldRadio.IsChecked == true ? 1 : 0;
    private void SetMode(int mode) => (mode == 2 ? SequenceRadio : mode == 1 ? HoldRadio : RepeatRadio).IsChecked = true;
    private bool KeyboardTarget => KeyboardRadio.IsChecked == true;
    private IEnumerable<RadioButton> ButtonChoices => ((Panel)MouseButtonPanel.Child).Children.OfType<RadioButton>();
    private string SelectedButton => ButtonChoices.FirstOrDefault(b => b.IsChecked == true)?.Tag as string ?? "Left";
    private void SelectButton(string value) => (ButtonChoices.FirstOrDefault(b => (string)b.Tag == value) ?? ButtonChoices.First()).IsChecked = true;
    public void Say(string text, bool problem = false)
    {
        ErrorText.Text = text;
        ErrorText.SetResourceReference(TextBlock.ForegroundProperty, problem ? "DangerTextBrush" : "SuccessTextBrush");
    }

    private void UpdateShortcut()
    {
        ShortcutTextBox.Text = Shortcut.Format(_modifiers, _key);
        ClearShortcutButton.Visibility = _key == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    private void Shortcut_Focused(object sender, KeyboardFocusChangedEventArgs e) => ShortcutTextBox.Text = "Press a shortcut...";
    private void Shortcut_Unfocused(object sender, KeyboardFocusChangedEventArgs e) => UpdateShortcut();
    private void ClearShortcut_Click(object sender, RoutedEventArgs e) { _key = 0; _modifiers = 0; UpdateShortcut(); }
    private void Shortcut_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape || key == Key.Tab) { Keyboard.ClearFocus(); MacroNameTextBox.Focus(); return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key), modifiers = Shortcut.CurrentModifiers();
        if (vk == 0) return;
        if (Shortcut.IsReserved(modifiers, vk)) { Say("This shortcut is reserved for Keyloom.", true); return; }
        bool functionKey = vk is >= 0x70 and <= 0x87;
        if (!functionKey && (modifiers & (Shortcut.Control | Shortcut.Alt | Shortcut.Win)) == 0)
        { Say("Use Ctrl, Alt, or Win with a key, or choose an F1–F24 key.", true); return; }
        _key = vk; _modifiers = modifiers; Say(""); UpdateShortcut();
    }

    private void Mode_Changed(object sender, RoutedEventArgs e) => UpdateMode();
    private void Target_Changed(object sender, RoutedEventArgs e) => UpdateMode();
    private void UpdateMode()
    {
        if (!IsInitialized || TimingPanel == null) return;
        bool sequence = Mode == 2, hold = Mode == 1, keyboard = KeyboardTarget;
        TargetPanel.Visibility = sequence ? Visibility.Collapsed : Visibility.Visible;
        SequencePanel.Visibility = sequence ? Visibility.Visible : Visibility.Collapsed;
        ButtonLabel.Visibility = MouseButtonPanel.Visibility = keyboard ? Visibility.Collapsed : Visibility.Visible;
        KeyLabel.Visibility = KeyPanel.Visibility = keyboard ? Visibility.Visible : Visibility.Collapsed;
        IntervalPanel.Visibility = RunsPanel.Visibility = hold ? Visibility.Collapsed : Visibility.Visible;
        IntervalLabel.Text = sequence ? "Pause between runs" : "Every";
        ModeHintText.Visibility = hold ? Visibility.Visible : Visibility.Collapsed;
        UpdateTargetUi();
        UpdateIconPreview();
    }

    private MacroActionType StepType => StepTypeBox.SelectedValue is MacroActionType kind ? kind : MacroActionType.MouseClick;
    private static string StepHint(MacroActionType type) => type switch
    {
        MacroActionType.KeyPress or MacroActionType.KeyDown or MacroActionType.KeyUp => "Examples: Space, F8, Ctrl+C. Use Plus for the + key.",
        MacroActionType.Delay => "Wait time in milliseconds.",
        MacroActionType.MouseMove => "Relative movement: x, y in pixels. Example: 20, -10.",
        MacroActionType.MousePosition => "x, y in pixels. Screen positions can be negative on extra monitors; window positions follow the target window.",
        MacroActionType.MouseScroll => "Positive scrolls up; negative scrolls down. Example: -3.",
        MacroActionType.Text => "Types this text into the focused app.",
        _ => "Choose a mouse button."
    };
    private void StepType_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (StepValueTextBox == null) return;
        bool mouse = StepType is MacroActionType.MouseClick or MacroActionType.MouseDown or MacroActionType.MouseUp;
        StepMouseBox.Visibility = mouse ? Visibility.Visible : Visibility.Collapsed;
        StepSpaceBox.Visibility = StepType == MacroActionType.MousePosition ? Visibility.Visible : Visibility.Collapsed;
        StepValueTextBox.Visibility = mouse ? Visibility.Collapsed : Visibility.Visible;
        StepValueTextBox.AcceptsReturn = StepType == MacroActionType.Text;
        StepValueTextBox.TextWrapping = StepType == MacroActionType.Text ? TextWrapping.Wrap : TextWrapping.NoWrap;
        if (_loadingStep) return;
        _loadingStep = true;
        StepValueTextBox.Text = StepType switch
        {
            MacroActionType.Delay => "100", MacroActionType.MouseMove => "20, 0",
            MacroActionType.MousePosition => "0, 0",
            MacroActionType.MouseScroll => "1", MacroActionType.Text => "Hello",
            _ => "Space"
        };
        _loadingStep = false;
        CommitStep();
    }
    private void StepField_Changed(object sender, EventArgs e)
    {
        if (!_loadingStep && IsInitialized) CommitStep();
    }
    /// <summary>Writes the step editor into the selected step. Invalid input stays in the editor and is reported below it.</summary>
    private void CommitStep()
    {
        int i = ActionsListBox.SelectedIndex;
        if (i < 0) return;
        try
        {
            var step = ReadStep();
            if (!_editRemembered) { Remember(); _editRemembered = true; }
            _committing = true;
            _steps[i] = step;
            ActionsListBox.SelectedIndex = i;
            ShowStepHint("");
        }
        catch (ArgumentException ex) { ShowStepHint(ex.Message); }
        finally { _committing = false; }
    }
    private void ShowStepHint(string error)
    {
        _stepError = error;
        StepHintText.Text = error.Length > 0 ? error : StepHint(StepType);
        StepHintText.SetResourceReference(TextBlock.ForegroundProperty, error.Length > 0 ? "DangerTextBrush" : "MutedBrush");
    }
    private void Step_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (_committing || _selecting) return;
        _editRemembered = false;
        ShowSelection();
    }
    /// <summary>One picked step opens the step editor; several open the bulk editor.</summary>
    private void ShowSelection()
    {
        int count = ActionsListBox.SelectedItems.Count;
        IsMultiSelect = count > 1;
        int group = PickedGroup();
        _groupStart = group;
        StepEditor.Visibility = count == 1 && group < 0 ? Visibility.Visible : Visibility.Collapsed;
        GroupEditor.Visibility = group >= 0 ? Visibility.Visible : Visibility.Collapsed;
        BulkEditor.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (count > 1) { ShowStepHint(""); ShowBulk(); return; }
        if (group >= 0) { ShowGroup(group); return; }
        int i = ActionsListBox.SelectedIndex;
        if (ActionsListBox.SelectedItem is not MacroAction a) return;
        _loadingStep = true;
        StepIndexText.Text = $"Step {_steps.Take(i).Count(s => !s.IsGroupMarker) + 1:N0}";
        StepTypeBox.SelectedValue = a.ActionType;
        StepMouseBox.SelectedIndex = Math.Max(0, Array.IndexOf(ProfileButtons, a.MouseButton));
        StepSpaceBox.SelectedIndex = (int)a.Space;
        StepValueTextBox.Text = a.ActionType switch
        {
            MacroActionType.Delay => a.DelayMs.ToString(), MacroActionType.MouseMove or MacroActionType.MousePosition => $"{a.X}, {a.Y}",
            MacroActionType.MouseScroll => a.ScrollAmount.ToString(), MacroActionType.Text => a.Text,
            _ => a.KeyName
        };
        _loadingStep = false;
        ShowStepHint("");
    }
    private static readonly string[] ProfileButtons = ["Left", "Right", "Middle", "X1", "X2"];
    private MacroAction ReadStep()
    {
        string value = StepValueTextBox.Text.Trim();
        var a = new MacroAction { ActionType = StepType, MouseButton = ProfileButtons[Math.Max(0, StepMouseBox.SelectedIndex)], KeyName = value, Text = StepValueTextBox.Text };
        if (StepType == MacroActionType.Delay) a.DelayMs = Number(value, "Wait time");
        if (StepType == MacroActionType.MouseScroll) a.ScrollAmount = Number(value, "Scroll amount");
        if (StepType is MacroActionType.MouseMove or MacroActionType.MousePosition)
        {
            var parts = value.Split(',');
            if (parts.Length != 2) throw new ArgumentException("Enter the position as x, y.");
            a.X = Number(parts[0], "X"); a.Y = Number(parts[1], "Y");
            if (StepType == MacroActionType.MousePosition) a.Space = (CoordinateSpace)Math.Max(0, StepSpaceBox.SelectedIndex);
        }
        ProfileValidator.ValidateAction(a); return a;
    }
    private void AddStepMenu_Click(object sender, RoutedEventArgs e)
    {
        AddStepMenu.PlacementTarget = AddStepButton;
        AddStepMenu.IsOpen = true;
    }
    private void AddStep_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && Enum.TryParse(tag, out MacroActionType kind)) Attempt(() => AddStep(kind));
    }
    /// <summary>Inserts a step with sensible defaults after the selection and opens it for editing.</summary>
    public void AddStep(MacroActionType kind)
    {
        var step = new MacroAction { ActionType = kind, DelayMs = 100, X = kind == MacroActionType.MouseMove ? 20 : 0, Text = "Hello" };
        InsertStep(step);
        if (StepValueTextBox.IsVisible) { StepValueTextBox.Focus(); StepValueTextBox.SelectAll(); }
    }
    private void InsertStep(MacroAction action)
    {
        EnsureRoom(1);
        Remember();
        int index = InsertionAfter(SelectedIndices().LastOrDefault(-1));
        _steps.Insert(index, action); ActionsListBox.SelectedIndex = index;
        ActionsListBox.ScrollIntoView(action);
        AnimateStep(action);
    }
    private void Save_Click(object sender, RoutedEventArgs e) => Attempt(() =>
    {
        if (_key != 0 && _others.Any(p => p.Id != Profile.Id && p.PrimaryVk == _key && p.PrimaryModifiers == _modifiers))
            throw new ArgumentException("Another macro already uses this shortcut.");
        if (Mode == 2 && _stepError.Length > 0) throw new ArgumentException($"{StepIndexText.Text}: {_stepError}");
        Profile.Name = MacroNameTextBox.Text.Trim();
        Profile.PrimaryVk = _key; Profile.PrimaryModifiers = _modifiers;
        Profile.FallbackVk = 0; Profile.FallbackModifiers = 0;
        Profile.Behavior = (MacroBehaviorType)Mode;
        Profile.TargetType = KeyboardTarget ? TargetInputType.Keyboard : TargetInputType.Mouse;
        Profile.TargetMouseButton = SelectedButton;
        Profile.TargetKey = TargetKeyTextBox.Text.Trim();
        Profile.StartDelayMs = Number(StartDelayTextBox.Text, "Start delay");
        Profile.SpamIntervalMs = Profile.Behavior == MacroBehaviorType.PressAndHold ? Math.Max(1, Profile.SpamIntervalMs) : Number(IntervalTextBox.Text, "Interval");
        Profile.RepeatCount = Profile.Behavior == MacroBehaviorType.PressAndHold ? 0 : Number(RunsTextBox.Text, "Runs");
        Profile.Actions = new(_steps.Select(a => a.Clone()));
        ApplyTarget(Profile);
        ApplyIcon(Profile);
        ProfileValidator.Validate(Profile);
        Saved?.Invoke();
    });
    private void Cancel_Click(object sender, RoutedEventArgs e) => Cancel();
    public void Cancel()
    {
        StopRecording();
        Cancelled?.Invoke();
    }
    private void Attempt(Action action)
    {
        try { Say(""); action(); }
        catch (ArgumentException ex) { Say(ex.Message, true); }
    }
    private static int Number(string value, string label) => int.TryParse(value.Trim(), out int number) ? number : throw new ArgumentException($"{label} must be a whole number.");
    /// <summary>The window that hosts the editor; minimized while picking or recording so the target is reachable.</summary>
    private Window? Host => Window.GetWindow(this);
}
