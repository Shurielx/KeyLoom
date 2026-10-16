using System.Collections.ObjectModel;
using System.Windows;
using Keyloom.Engine;
using Keyloom.Models;
using Keyloom.Recording;

namespace Keyloom.Views;

public partial class MacroEditorView
{
    private RecordingSession? _recording;
    private void InitializeRecording() => Unloaded += (_, _) => StopRecording();
    private async void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_recording != null || _picker != null || SheetLayer.For(this) is not { } sheets) return;
        var dialog = new RecordingOptionsView();
        if (!await sheets.ShowAsync(dialog, 420)) return;
        var options = dialog.Options;
        int available = ProfileValidator.MaxActions - (options.Append ? _steps.Count : 0);
        if (available < 16) { Say(L.T("The sequence is full. Choose Replace sequence or remove some steps."), true); return; }
        SetMode(2);
        Say("");
        var session = new RecordingSession();
        _recording = session;
        var host = Host;
        session.Completed += result =>
        {
            _recording = null;
            session.Dispose();
            MacroForm.IsEnabled = true;
            if (IsLoaded && !Dispatcher.HasShutdownStarted)
            {
                if (result.Window != null && result.Steps.Count > 0) SetWindow(result.Window);
                ApplyRecording(result.Steps, options.Append, result.Reason);
                if (host != null) { host.WindowState = WindowState.Normal; host.Activate(); }
            }
        };
        MacroForm.IsEnabled = false;
        if (host != null) host.WindowState = WindowState.Minimized;
        session.Start(options, available);
    }
    private void ApplyRecording(IReadOnlyList<MacroAction> recorded, bool append, string reason)
    {
        if (recorded.Count == 0)
        {
            Say(reason.Length > 0 ? reason : L.T("Nothing recorded."), reason.Length > 0);
            return;
        }
        if (recorded.Count + (append ? _steps.Count : 0) > ProfileValidator.MaxActions)
            throw new InvalidOperationException(L.T("Recording exceeds the sequence step limit."));
        bool firstRecording = _steps.Count == 0;
        Remember();
        BindSteps(new ObservableCollection<MacroAction>((append ? _steps : Enumerable.Empty<MacroAction>()).Concat(recorded).Select(a => a.Clone())));
        ActionsListBox.SelectedIndex = append ? _steps.Count - recorded.Count : 0;
        ActionsListBox.ScrollIntoView(ActionsListBox.SelectedItem);
        if (!append || firstRecording) RunsTextBox.Text = "1";
        Say(reason.Length > 0 ? reason : L.T($"Recorded {L.N(recorded.Count, "{0} step", "{0} steps")}."));
    }
    public void StopRecording() => _recording?.Stop();
}
