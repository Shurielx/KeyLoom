using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Keyloom.Engine;
using Keyloom.Models;
using Keyloom.Recording;
using Keyloom.Services;
using Keyloom.Views;

internal static partial class Program
{
    private static async Task RecordingTests()
    {
        var buffer = new RecordingBuffer(new());
        buffer.Movement(-1800, 100, 0);
        buffer.Key(87, "W", true, 30);
        buffer.Key(87, "W", true, 40);
        buffer.Movement(-1790, 110, 45);
        buffer.Button("Left", true, -1780, 120, 50);
        buffer.Button("Left", false, -1780, 120, 70);
        buffer.Key(87, "W", false, 100);
        var steps = buffer.Complete(120);
        Check(steps.Count(a => a.ActionType == MacroActionType.KeyDown) == 1 && steps.Count(a => a.ActionType == MacroActionType.KeyUp) == 1, "recording captures hold duration without keyboard autorepeat duplicates");
        Check(steps.Where(a => a.ActionType == MacroActionType.Delay).Sum(a => a.DelayMs) == 120, "recorded delays preserve event timing");
        Check(steps.Any(a => a.ActionType == MacroActionType.MousePosition && a.X == -1780 && a.Y == 120), "recorded clicks include their exact pointer position");
        Check(steps.Count(a => a.ActionType == MacroActionType.MouseDown) == 1 && steps.Count(a => a.ActionType == MacroActionType.MouseUp) == 1, "recorded clicks keep balanced down/up events");
        var driver = new FakeDriver(); await using var executor = new MacroExecutor(driver);
        var p = Profile(MacroBehaviorType.Sequence); p.RepeatCount = 1; p.Actions = new(steps);
        await executor.ToggleAsync(p); await Until(() => executor.ActiveProfile == null);
        Check(driver.Events.Contains("position:-1780,120") && driver.Events.Contains("K87:down") && driver.Events.Contains("K87:up"), "recorded mouse and keyboard sequence replays through the executor");
        buffer = new(new());
        buffer.Key(162, "LeftCtrl", true, 10);
        buffer.Button("Right", true, 400, 500, 20);
        steps = buffer.Complete(60);
        Check(steps.TakeLast(2).Select(a => a.ActionType).SequenceEqual(new[] { MacroActionType.KeyUp, MacroActionType.MouseUp }), "stopping recording adds releases for held inputs");
        Check(buffer.Complete(90).Count == steps.Count, "recording completion is idempotent");
        buffer = new(new(MouseMovement: false, MouseButtons: false, Keyboard: true, Timing: false));
        buffer.Movement(1, 2, 100); buffer.Button("Left", true, 1, 2, 110);
        buffer.Key(65, "A", true, 120); buffer.Key(65, "A", false, 500);
        steps = buffer.Complete(1000);
        Check(steps.Count == 2 && steps.All(a => a.ActionType is MacroActionType.KeyDown or MacroActionType.KeyUp), "recording options independently disable mouse capture and delays");
        buffer = new(new());
        for (int i = 0; i <= 30; i++) buffer.Movement(i, i, i);
        steps = buffer.Complete(31);
        Check(steps.Count(a => a.ActionType == MacroActionType.MousePosition) == 3 && steps.Last(a => a.ActionType == MacroActionType.MousePosition).X == 30, "mouse sampling bounds event volume and preserves the final position");
        buffer = new(new());
        buffer.Key(65, "A", true, 5); buffer.Pause(20);
        buffer.Key(65, "A", false, 30);
        buffer.Key(66, "B", true, 40); buffer.Key(66, "B", false, 50);
        steps = buffer.Complete(50);
        Check(steps.Count(a => a.ActionType == MacroActionType.KeyUp && a.KeyName == "A") == 1, "pause closes held inputs and ignores orphan releases");
        buffer = new(new(), 32);
        buffer.Key(65, "A", true, 1);
        for (int i = 1; i < 100; i++) buffer.Movement(i, i, i * 20);
        steps = buffer.Complete(2100);
        Check(buffer.LimitReached && steps.Count <= 32 && steps.Last().ActionType == MacroActionType.KeyUp, "recording limit reserves enough capacity to release held inputs");
        Check(InputSimulator.NormalizePosition(-1920, -1920, 3840) < 20 &&
              InputSimulator.NormalizePosition(1919, -1920, 3840) > 65510 &&
              InputSimulator.NormalizePosition(-5000, -1920, 3840) == 0, "absolute pointer coordinates cover negative multi-monitor origins and clip out-of-range points");
        string folder = Path.Combine(Path.GetTempPath(), "keyloom-recording-" + Guid.NewGuid().ToString("N"));
        var storage = new ProfileStorageService(folder);
        p.Actions = new() { new() { ActionType = MacroActionType.MousePosition, X = -1200, Y = 250 }, new() { ActionType = MacroActionType.Delay, DelayMs = 20 }, new() { ActionType = MacroActionType.KeyPress, KeyName = "W" } };
        storage.SaveProfiles(new[] { p });
        Check(storage.LoadProfiles().Profiles[0].Actions[0].X == -1200, "recorded pointer positions round-trip through profile storage");
        Directory.Delete(folder, true);
    }
    private static void RecordingUiTests()
    {
        var editor = new MacroEditorDialog(Profile(MacroBehaviorType.Sequence));
        var recording = new MacroAction[]
        {
            new() { ActionType = MacroActionType.MousePosition, X = 500, Y = 300 },
            new() { ActionType = MacroActionType.KeyDown, KeyName = "W" },
            new() { ActionType = MacroActionType.Delay, DelayMs = 100 },
            new() { ActionType = MacroActionType.KeyUp, KeyName = "W" }
        };
        Invoke(editor, "ApplyRecording", recording, false, "");
        var list = (ListBox)editor.FindName("ActionsListBox");
        Check(list.Items.Count == 4 && ((TextBox)editor.FindName("RunsTextBox")).Text == "1", "recording imports into the editor and defaults to a single run");
        Check(((ComboBox)editor.FindName("StepTypeBox")).SelectedValue is MacroActionType.MousePosition &&
              ((TextBox)editor.FindName("StepValueTextBox")).Text == "500, 300", "recorded pointer coordinates remain editable");
        list.SelectedIndex = 1;
        Invoke(editor, "InsertDelay_Click", editor, new RoutedEventArgs());
        Check(((MacroAction)list.Items[2]).ActionType == MacroActionType.Delay && ((MacroAction)list.Items[2]).DelayMs == 100, "quick Delay inserts after the selected step");
        Invoke(editor, "DuplicateStep_Click", editor, new RoutedEventArgs());
        Check(list.Items.Count == 6 && !ReferenceEquals(list.Items[2], list.Items[3]), "duplicate step creates an independent action");
        Invoke(editor, "ApplyRecording", new MacroAction[] { new() { ActionType = MacroActionType.Delay, DelayMs = 50 } }, true, "");
        Check(list.Items.Count == 7, "recording can append to an existing sequence");
        Render((FrameworkElement)editor.Content, 718, (int)editor.Height - 2, "recorded-sequence.png");
        editor.Close();
        var options = new RecordingOptionsDialog();
        Render((FrameworkElement)options.Content, 394, 320, "record-options.png");
        ((CheckBox)options.FindName("MovementBox")).IsChecked = false;
        ((CheckBox)options.FindName("ButtonsBox")).IsChecked = false;
        ((CheckBox)options.FindName("KeyboardBox")).IsChecked = false;
        Invoke(options, "Start_Click", options, new RoutedEventArgs());
        Check(((TextBlock)options.FindName("ErrorText")).Text.Contains("Choose"), "record options reject an empty input selection");
        ((CheckBox)options.FindName("KeyboardBox")).IsChecked = true;
        ((TextBox)options.FindName("SampleTextBox")).Text = "0";
        Invoke(options, "Start_Click", options, new RoutedEventArgs());
        Check(((TextBlock)options.FindName("ErrorText")).Text.Contains("8"), "record options reject invalid movement sampling");
        options.Close();
        var panel = new RecordingWindow(); panel.Status(false, 65000, 245);
        Check(((TextBlock)panel.FindName("DetailsText")).Text.Contains("01:05") && ((Button)panel.FindName("PauseButton")).IsEnabled, "recording panel displays elapsed time, count and pause");
        Render((FrameworkElement)panel.Content, 408, 82, "record-panel.png");
        panel.Status(true, 65000, 245);
        Check((string)((Button)panel.FindName("PauseButton")).Content == "Resume", "recording pause switches to Resume");
        Render((FrameworkElement)panel.Content, 408, 82, "record-panel-paused.png"); panel.Close();
        using var session = new RecordingSession();
        int completed = 0; session.Completed += (_, _) => completed++;
        session.Start(new(), 20000);
        session.Stop(); session.Stop();
        Check(completed == 1, "canceling countdown completes once without installing capture hooks");
    }
    private static void RecorderHookTests()
    {
        using var recorder = new MacroRecorder();
        recorder.Start(new(MouseMovement: true, MouseButtons: false, Keyboard: false));
        Check(recorder.HasInstalledHooks && recorder.IsRecording, "Windows accepts scoped recording hook registration");
        recorder.TogglePause();
        Check(recorder.IsPaused, "recorder pauses without removing its stop controls");
        var recording = recorder.Stop();
        Check(!recorder.HasInstalledHooks && !recorder.IsRecording && recording.Count == 0, "stopping recording removes both native hooks");
        recorder.Start(new(MouseMovement: true, MouseButtons: false, Keyboard: false));
        int stopped = 0; recorder.StopRequested += _ => stopped++;
        var data = Marshal.AllocHGlobal(24);
        try
        {
            for (int i = 0; i < 24; i++) Marshal.WriteByte(data, i, 0);
            Marshal.WriteInt32(data, 0, 0x77);
            var callback = typeof(MacroRecorder).GetMethod("KeyboardHook", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var down = (IntPtr)callback.Invoke(recorder, new object[] { 0, new IntPtr(0x100), data })!;
            var up = (IntPtr)callback.Invoke(recorder, new object[] { 0, new IntPtr(0x101), data })!;
            Check(down == new IntPtr(1) && up == new IntPtr(1) && stopped == 1 && recorder.Count == 0, "F8 stop callback consumes its key without adding it to the recording");
        }
        finally { Marshal.FreeHGlobal(data); recorder.Stop(); }
        Check(!recorder.HasInstalledHooks, "recorder can restart and release its hooks again");
    }
}
