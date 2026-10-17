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
    /// <summary>Plays a macro once with real input; set by the window that hosts the editor.</summary>
    public Func<MacroProfile, Task>? TestRun { get; init; }
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
        L.Apply(this);
        Profile = profile; _others = others ?? []; IsNew = isNew;
        _steps = new(profile.Actions.Select(a => a.Clone()));
        MacroNameTextBox.Text = profile.Name;
        _key = profile.PrimaryVk; _modifiers = profile.PrimaryModifiers; UpdateShortcut();
        (profile.TargetType == TargetInputType.Keyboard ? KeyboardRadio : MouseRadio).IsChecked = true;
        SelectButton(profile.TargetMouseButton);
        TargetKeyTextBox.Text = profile.TargetKey;
        StartDelayTextBox.Text = TimeText.Format(profile.StartDelayMs);
        SettleTextBox.Text = TimeText.Format(profile.PointerSettleMs);
        IntervalTextBox.Text = TimeText.Format(profile.SpamIntervalMs);
        RunsTextBox.Text = profile.RepeatCount.ToString();
        StepTypeBox.ItemsSource = new[]
        {
            new StepChoice(MacroActionType.MouseClick, L.T("Click")), new(MacroActionType.MouseDown, L.T("Hold button")),
            new(MacroActionType.MouseUp, L.T("Release button")), new(MacroActionType.KeyPress, L.T("Press key")),
            new(MacroActionType.KeyDown, L.T("Hold key")), new(MacroActionType.KeyUp, L.T("Release key")),
            new(MacroActionType.Delay, L.T("Wait")), new(MacroActionType.MouseMove, L.T("Move cursor by")),
            new(MacroActionType.MouseScroll, L.T("Scroll")), new(MacroActionType.Text, L.T("Type text")),
            new(MacroActionType.MousePosition, L.T("Cursor position")),
            new(MacroActionType.ClickTarget, L.T("Click on target")), new(MacroActionType.WaitUntil, L.T("Wait until")),
            new(MacroActionType.ClickCell, L.T("Click a cell")), new(MacroActionType.DragPair, L.T("Drag a matching pair"))
        };
        _loadingStep = true; StepTypeBox.SelectedIndex = 0; StepMouseBox.SelectedIndex = 0; _loadingStep = false;
        BindSteps(_steps);
        InitializeStepList();
        InitializeGapButton();
        InitializeTarget();
        InitializeIcon();
        SetMode((int)profile.Behavior);
        InitializeRecording();
        InitializeBuilder();
        InitializeVision();
        InitializeGrids();
        InitializeScript();
        MoreToggle.IsChecked = profile.Window != null && profile.Behavior == MacroBehaviorType.Sequence || profile.ExecutionMode != ExecutionMode.Standard;
        Loaded += (_, _) => { if (isNew) { MacroNameTextBox.Focus(); MacroNameTextBox.SelectAll(); } };
    }
    /// <summary>0 repeat, 1 hold, 2 sequence, 3 script: the order of <see cref="MacroBehaviorType"/>.</summary>
    private int Mode => ScriptRadio.IsChecked == true ? 3 : SequenceRadio.IsChecked == true ? 2 : HoldRadio.IsChecked == true ? 1 : 0;
    private void SetMode(int mode) => (mode == 3 ? ScriptRadio : mode == 2 ? SequenceRadio : mode == 1 ? HoldRadio : RepeatRadio).IsChecked = true;
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
        RefreshScriptControls();
    }
    private void Shortcut_Focused(object sender, KeyboardFocusChangedEventArgs e) => ShortcutTextBox.Text = L.T("Press a shortcut...");
    private void Shortcut_Unfocused(object sender, KeyboardFocusChangedEventArgs e) => UpdateShortcut();
    private void ClearShortcut_Click(object sender, RoutedEventArgs e) { _key = 0; _modifiers = 0; UpdateShortcut(); }
    private void Shortcut_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape || key == Key.Tab) { Keyboard.ClearFocus(); MacroNameTextBox.Focus(); return; }
        if (Shortcut.IsModifier(key)) return;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key), modifiers = Shortcut.CurrentModifiers();
        if (vk == 0) return;
        if (Shortcut.Problem(modifiers, vk) is { } problem) { Say(problem, true); return; }
        _key = vk; _modifiers = modifiers; Say(""); UpdateShortcut();
    }

    private void Mode_Changed(object sender, RoutedEventArgs e) => UpdateMode();
    private void Target_Changed(object sender, RoutedEventArgs e) => UpdateMode();
    private void UpdateMode()
    {
        if (!IsInitialized || TimingPanel == null) return;
        bool sequence = Mode == 2, hold = Mode == 1, script = Mode == 3, keyboard = KeyboardTarget;
        TargetScroll.Visibility = sequence || script ? Visibility.Collapsed : Visibility.Visible;
        SequencePanel.Visibility = sequence ? Visibility.Visible : Visibility.Collapsed;
        RunButtons.Visibility = sequence || script ? Visibility.Visible : Visibility.Collapsed;
        PreviewButton.Visibility = sequence ? Visibility.Visible : Visibility.Collapsed;
        ShowScriptMode(script);
        PreviewCard.Visibility = _hasRoute && sequence ? Visibility.Visible : Visibility.Collapsed;
        // Repeat and hold have a few settings; their card only grows as tall as they are.
        Inspector.VerticalAlignment = sequence || script ? VerticalAlignment.Stretch : VerticalAlignment.Top;
        ModeText.Text = script ? L.T("Does what a script you write says") : sequence ? L.T("Plays steps you record or build") : hold ? L.T("Keeps a key or button held down") : L.T("Clicks or presses again and again");
        ButtonLabel.Visibility = MouseButtonPanel.Visibility = keyboard ? Visibility.Collapsed : Visibility.Visible;
        KeyLabel.Visibility = KeyPanel.Visibility = keyboard ? Visibility.Visible : Visibility.Collapsed;
        IntervalPanel.Visibility = RunsPanel.Visibility = hold ? Visibility.Collapsed : Visibility.Visible;
        IntervalLabel.Text = sequence || script ? L.T("Pause between runs") : L.T("Every");
        // Only pointer input can follow a move.
        SettlePanel.Visibility = sequence || script || !keyboard ? Visibility.Visible : Visibility.Collapsed;
        ModeHintText.Visibility = hold ? Visibility.Visible : Visibility.Collapsed;
        UpdateTargetUi();
        UpdateIconPreview();
        ShowSelection();
    }
    /// <summary>Below this width the inspector moves under the steps instead of beside them.</summary>
    private const double StackBelow = 720;
    private void Body_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool stacked = e.NewSize.Width < StackBelow;
        InspectorColumn.Width = new GridLength(stacked ? 0 : 320);
        InspectorRow.Height = stacked ? GridLength.Auto : new GridLength(0);
        Grid.SetColumn(Inspector, stacked ? 0 : 1);
        Grid.SetRow(Inspector, stacked ? 1 : 0);
        Inspector.Margin = stacked ? new Thickness(0, 10, 0, 0) : new Thickness(10, 0, 0, 0);
        // Stacked, the steps keep at least half of the height.
        Inspector.MaxHeight = stacked ? Math.Max(140, e.NewSize.Height * 0.45) : double.PositiveInfinity;
    }

    private MacroActionType StepType => StepTypeBox.SelectedValue is MacroActionType kind ? kind : MacroActionType.MouseClick;
    private static string StepHint(MacroActionType type) => type switch
    {
        MacroActionType.KeyPress or MacroActionType.KeyDown or MacroActionType.KeyUp => L.T("Examples: Space, F8, Ctrl+C. Use Plus for the + key."),
        MacroActionType.Delay => L.T("Wait time in milliseconds."),
        MacroActionType.MouseMove => L.T("Relative movement: x, y in pixels. Example: 20, -10."),
        MacroActionType.MousePosition => L.T("x, y in pixels. Screen positions can be negative on extra monitors; window positions follow the target window."),
        MacroActionType.MouseScroll => L.T("Positive scrolls up; negative scrolls down. Example: -3."),
        MacroActionType.Text => L.T("Types this text into the focused app."),
        MacroActionType.ClickTarget => L.T("Finds the target on the screen and clicks its middle, wherever it is."),
        MacroActionType.WaitUntil => L.T("Holds the macro here until the screen shows the target, or until it is gone."),
        MacroActionType.ClickCell => L.T("Reads the numbers in the grid and clicks the cell you choose."),
        MacroActionType.DragPair => L.T("Reads the grid and drags one of two equal numbers onto the other, the lowest pair first."),
        _ => L.T("Choose a mouse button.")
    };
    private void StepType_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (StepValueTextBox == null) return;
        bool looks = StepType is MacroActionType.ClickTarget or MacroActionType.WaitUntil;
        bool grid = StepType is MacroActionType.ClickCell or MacroActionType.DragPair;
        bool mouse = StepType is MacroActionType.MouseClick or MacroActionType.MouseDown or MacroActionType.MouseUp or MacroActionType.ClickTarget || grid;
        StepMouseBox.Visibility = mouse ? Visibility.Visible : Visibility.Collapsed;
        StepValueLabel.Visibility = StepValuePanel.Visibility = StepType == MacroActionType.WaitUntil ? Visibility.Collapsed : Visibility.Visible;
        ShowTargetOptions(looks);
        ShowGridOptions(grid);
        MoveOptions.Visibility = StepType == MacroActionType.MousePosition ? Visibility.Visible : Visibility.Collapsed;
        StepValueLabel.Text = StepType switch
        {
            MacroActionType.KeyPress or MacroActionType.KeyDown or MacroActionType.KeyUp => L.T("Key"),
            MacroActionType.Delay => L.T("Wait (ms)"),
            MacroActionType.MouseMove => L.T("Move by x, y"),
            MacroActionType.MousePosition => L.T("Position x, y"),
            MacroActionType.MouseScroll => L.T("Notches"),
            MacroActionType.Text => L.T("Text"),
            _ => L.T("Button")
        };
        StepValueTextBox.Visibility = mouse ? Visibility.Collapsed : Visibility.Visible;
        StepValueTextBox.AcceptsReturn = StepType == MacroActionType.Text;
        StepValueTextBox.TextWrapping = StepType == MacroActionType.Text ? TextWrapping.Wrap : TextWrapping.NoWrap;
        if (_loadingStep) return;
        _loadingStep = true;
        if (looks) PrepareTargetStep();
        if (grid) PrepareGridStep();
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
        if (!_loadingStep && IsInitialized) CommitLooking();
    }
    /// <summary>The target fields belong to the picked If while one is picked, otherwise to the picked step.</summary>
    private void CommitLooking()
    {
        if (IfPicked) CommitIf(); else CommitStep();
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
        if (group < 0)
        {
            PlaceTargetOptions(false); SetUntilWords(false);
            StopIfMissingBox.Visibility = Visibility.Visible; UntilLabel.Text = L.T("Wait until it");
            ShowTargetOptions(StepType is MacroActionType.ClickTarget or MacroActionType.WaitUntil);
        }
        bool sequence = Mode == 2;
        StepEditor.Visibility = sequence && count == 1 && group < 0 ? Visibility.Visible : Visibility.Collapsed;
        GroupEditor.Visibility = sequence && group >= 0 ? Visibility.Visible : Visibility.Collapsed;
        BulkEditor.Visibility = sequence && count > 1 ? Visibility.Visible : Visibility.Collapsed;
        PickHint.Visibility = sequence && count == 0 && _steps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Strip.Picked = count == 1 ? ActionsListBox.SelectedIndex : -1;
        ShowRouteFocus();
        if (count > 1) { ShowStepHint(""); ShowBulk(); return; }
        if (group >= 0) { ShowGroup(group); return; }
        int i = ActionsListBox.SelectedIndex;
        if (ActionsListBox.SelectedItem is not MacroAction a) return;
        _loadingStep = true;
        StepIndexText.Text = $"Step {_steps.Take(i).Count(s => !s.IsGroupMarker) + 1:N0}";
        StepTypeBox.SelectedValue = a.ActionType;
        StepMouseBox.SelectedIndex = Math.Max(0, Array.IndexOf(ProfileButtons, a.MouseButton));
        StepSpaceBox.SelectedIndex = (int)a.Space;
        ThenClickBox.IsChecked = HasClickAfter(i);
        GlideTextBox.Text = TimeText.Format(a.GlideMs);
        ShowTargetStep(a);
        ShowGridStep(a);
        if (a.ActionType == MacroActionType.MousePosition && (a.Space != CoordinateSpace.Screen || a.GlideMs > 0)) StepMoreToggle.IsChecked = true;
        StepValueTextBox.Text = a.ActionType switch
        {
            MacroActionType.Delay => TimeText.Format(a.DelayMs), MacroActionType.MouseMove or MacroActionType.MousePosition => $"{a.X}, {a.Y}",
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
        if (StepType == MacroActionType.Delay) a.DelayMs = Time(value, L.T("Wait time"));
        if (StepType == MacroActionType.MouseScroll) a.ScrollAmount = Number(value, L.T("Scroll amount"));
        if (StepType is MacroActionType.MouseMove or MacroActionType.MousePosition)
        {
            var parts = value.Split(',');
            if (parts.Length != 2) throw new ArgumentException(L.T("Enter the position as x, y."));
            a.X = Number(parts[0], "X"); a.Y = Number(parts[1], "Y");
            if (StepType == MacroActionType.MousePosition)
            {
                a.Space = (CoordinateSpace)Math.Max(0, StepSpaceBox.SelectedIndex);
                a.GlideMs = Time(GlideTextBox.Text, L.T("Glide"));
            }
        }
        if (StepType is MacroActionType.ClickTarget or MacroActionType.WaitUntil) ReadTargetStep(a);
        if (StepType is MacroActionType.ClickCell or MacroActionType.DragPair) ReadGridStep(a);
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
            throw new ArgumentException(L.T("Another macro already uses this shortcut."));
        ReadForm(Profile);
        Saved?.Invoke();
    });
    /// <summary>Copies the whole form into a profile and validates it. Throws <see cref="ArgumentException"/> for anything invalid.</summary>
    private MacroProfile ReadForm(MacroProfile p)
    {
        if (Mode == 2 && _stepError.Length > 0) throw new ArgumentException($"{StepIndexText.Text}: {_stepError}");
        p.Name = MacroNameTextBox.Text.Trim();
        p.PrimaryVk = _key; p.PrimaryModifiers = _modifiers;
        p.FallbackVk = 0; p.FallbackModifiers = 0;
        p.Behavior = (MacroBehaviorType)Mode;
        p.TargetType = KeyboardTarget ? TargetInputType.Keyboard : TargetInputType.Mouse;
        p.TargetMouseButton = SelectedButton;
        p.TargetKey = TargetKeyTextBox.Text.Trim();
        p.StartDelayMs = Time(StartDelayTextBox.Text, L.T("Start delay"));
        p.PointerSettleMs = Time(SettleTextBox.Text, L.T("Click delay after moving"));
        p.SpamIntervalMs = p.Behavior == MacroBehaviorType.PressAndHold ? Math.Max(1, Profile.SpamIntervalMs) : Time(IntervalTextBox.Text, L.T("Interval"));
        p.RepeatCount = p.Behavior == MacroBehaviorType.PressAndHold ? 0 : Number(RunsTextBox.Text, L.T("Runs"));
        p.Actions = p.Behavior == MacroBehaviorType.Script ? new() : new(_steps.Select(a => a.Clone()));
        p.Script = p.Behavior == MacroBehaviorType.Script ? ScriptText : "";
        ApplyTarget(p);
        ApplyScreenTargets(p);
        ApplyGrids(p);
        ApplyIcon(p);
        ProfileValidator.Validate(p);
        return p;
    }
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
    private static int Number(string value, string label) => int.TryParse(value.Trim(), out int number) ? number : throw new ArgumentException(L.T($"{label} must be a whole number."));
    /// <summary>A time field: a bare number is ms, or write 2s, 10m, 1h, 1h 30m.</summary>
    private static int Time(string value, string label) => TimeText.TryParse(value, out int ms) ? ms : throw new ArgumentException(L.T($"{label}: write a time like 500, 2s, 10m or 1h."));
    /// <summary>The window that hosts the editor; minimized while picking or recording so the target is reachable.</summary>
    private Window? Host => Window.GetWindow(this);
}
