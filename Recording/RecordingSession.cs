using System.Windows;
using System.Windows.Threading;
using Keyloom.Models;
using Keyloom.Views;

namespace Keyloom.Recording;

public sealed class RecordingSession : IDisposable
{
    private readonly MacroRecorder _recorder = new();
    private readonly RecordingWindow _window = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly CancellationTokenSource _countdown = new();
    private bool _finished;
    public event Action<IReadOnlyList<MacroAction>, string>? Completed;
    public RecordingSession()
    {
        _window.StopRequested += () => Stop();
        _window.PauseRequested += () => { _recorder.TogglePause(); Refresh(); };
        _window.Closed += (_, _) => Stop();
        _recorder.StopRequested += reason => _window.Dispatcher.BeginInvoke(() => Stop(reason));
        _timer.Tick += (_, _) =>
        {
            if (_recorder.ElapsedMilliseconds >= 600000) Stop("Recording stopped after 10 minutes.");
            else Refresh();
        };
    }
    public async void Start(RecordingOptions options, int maxSteps)
    {
        try
        {
            _window.Show();
            for (int seconds = 3; seconds > 0; seconds--)
            {
                _window.Countdown(seconds);
                await Task.Delay(1000, _countdown.Token);
            }
            if (_finished) return;
            _recorder.Start(options, maxSteps); Refresh(); _timer.Start();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Stop(ex.Message); }
    }
    private void Refresh()
    {
        if (_recorder.IsRecording) _window.Status(_recorder.IsPaused, _recorder.ElapsedMilliseconds, _recorder.Count);
    }
    public void Stop(string reason = "")
    {
        if (_finished) return;
        _finished = true; _countdown.Cancel(); _timer.Stop();
        var steps = _recorder.Stop();
        if (_recorder.RemovalWarning.Length > 0) reason = _recorder.RemovalWarning;
        _window.Close();
        Completed?.Invoke(steps, reason);
    }
    public void Dispose()
    {
        Stop();
        _recorder.Dispose(); _countdown.Dispose();
    }
}
