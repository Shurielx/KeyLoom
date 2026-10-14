using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Keyloom.Engine;
using Keyloom.Models;
using Keyloom.Views;

namespace Keyloom.Recording;

/// <summary>Recorded steps plus the window they were recorded against, when positions are window-relative.</summary>
public sealed record RecordingResult(IReadOnlyList<MacroAction> Steps, string Reason, WindowTarget? Window);

public sealed class RecordingSession : IDisposable
{
    private readonly MacroRecorder _recorder = new();
    private readonly RecordingWindow _window = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly CancellationTokenSource _countdown = new();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    private bool _finished;
    private RecordingOptions _options = new();
    private IntPtr _target;
    private (int X, int Y) _origin;
    public event Action<RecordingResult>? Completed;
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
        _options = options;
        try
        {
            _window.Show();
            for (int seconds = 3; seconds > 0; seconds--)
            {
                _window.Countdown(seconds);
                await Task.Delay(1000, _countdown.Token);
            }
            if (_finished) return;
            CaptureOrigin();
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
        IReadOnlyList<MacroAction> steps = _recorder.Stop();
        if (_recorder.RemovalWarning.Length > 0) reason = _recorder.RemovalWarning;
        _window.Close();
        WindowTarget? target = null;
        if (_options.Space == CoordinateSpace.Window && HasTarget())
        {
            target = WindowSystem.Describe(_target);
            steps = RecordingTransform.ToSpace(steps, CoordinateSpace.Window, _origin.X, _origin.Y);
        }
        else if (_options.Space == CoordinateSpace.Start) steps = RecordingTransform.ToSpace(steps, CoordinateSpace.Start, _origin.X, _origin.Y);
        else if (_options.Space == CoordinateSpace.Window && steps.Count > 0 && reason.Length == 0)
            reason = "Keyloom was in front when recording started, so positions use screen coordinates.";
        if (_options.Simplify) steps = RecordingTransform.Simplify(steps);
        Completed?.Invoke(new(steps, reason, target));
    }
    private bool HasTarget() => WindowSystem.Instance.IsAlive(_target) && !WindowSystem.IsOwn(_target);
    /// <summary>Window-relative recordings measure from the client corner of the window in front when capture starts; start-relative ones from the pointer.</summary>
    private void CaptureOrigin()
    {
        if (_options.Space == CoordinateSpace.Start) { _origin = WindowSystem.Instance.Cursor(); return; }
        if (_options.Space != CoordinateSpace.Window) return;
        _target = GetForegroundWindow();
        if (HasTarget()) _origin = WindowSystem.Instance.ClientToScreen(_target, 0, 0);
    }
    public void Dispose()
    {
        Stop();
        _recorder.Dispose(); _countdown.Dispose();
    }
}
