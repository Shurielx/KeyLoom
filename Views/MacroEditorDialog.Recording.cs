using System.Collections.ObjectModel;
using System.Windows;
using Keyloom.Engine;
using Keyloom.Models;
using Keyloom.Recording;

namespace Keyloom.Views;

public partial class MacroEditorDialog
{
    private RecordingSession? _recording;
    private bool _closed;
    private void InitializeRecording() => Closed += (_, _) => { _closed = true; _recording?.Dispose(); };
    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_recording != null) return;
        var dialog = new RecordingOptionsDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var options = dialog.Options;
        int available = ProfileValidator.MaxActions - (options.Append ? _steps.Count : 0);
        if (available < 16) { ErrorText.Text = "The sequence is full. Choose Replace sequence or remove some steps."; return; }
        ModeBox.SelectedIndex = 2; FitHeight();
        ErrorText.Text = "";
        var session = new RecordingSession();
        _recording = session;
        session.Completed += (steps, reason) =>
        {
            _recording = null;
            session.Dispose();
            MacroForm.IsEnabled = true;
            if (!_closed && !Dispatcher.HasShutdownStarted)
            {
                ApplyRecording(steps, options.Append, reason);
                WindowState = WindowState.Normal; Activate();
            }
        };
        MacroForm.IsEnabled = false;
        WindowState = WindowState.Minimized;
        session.Start(options, available);
    }
    private void ApplyRecording(IReadOnlyList<MacroAction> recorded, bool append, string reason)
    {
        if (recorded.Count == 0)
        {
            ErrorText.Text = reason.Length > 0 ? reason : "Nothing recorded.";
            return;
        }
        if (recorded.Count + (append ? _steps.Count : 0) > ProfileValidator.MaxActions)
            throw new InvalidOperationException("Recording exceeds the sequence step limit.");
        bool firstRecording = _steps.Count == 0;
        _steps = new ObservableCollection<MacroAction>((append ? _steps : Enumerable.Empty<MacroAction>()).Concat(recorded).Select(a => a.Clone()));
        ActionsListBox.ItemsSource = _steps;
        ActionsListBox.SelectedIndex = append ? _steps.Count - recorded.Count : 0;
        ActionsListBox.ScrollIntoView(ActionsListBox.SelectedItem);
        if (!append || firstRecording) RunsTextBox.Text = "1";
        ErrorText.Text = reason.Length > 0 ? reason : $"Recorded {recorded.Count:N0} {(recorded.Count == 1 ? "step" : "steps")}.";
    }
    public void StopRecording() => _recording?.Stop();
}
