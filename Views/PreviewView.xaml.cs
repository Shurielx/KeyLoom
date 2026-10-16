using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Keyloom.Engine;
using Keyloom.Models;

namespace Keyloom.Views;

/// <summary>
/// A dry run of a sequence inside a sheet: the route when there is one, otherwise the step playing now, and a strip with every
/// input in time. Sends no input. Disposing stops playback and removes the on-screen overlay.
/// </summary>
public partial class PreviewView : UserControl, IDisposable
{
    private static readonly double[] Speeds = [0.25, 0.5, 1, 2, 4];
    private readonly PreviewPlayer _player = new();
    private readonly MacroTimeline _timeline;
    private readonly IReadOnlyList<MacroAction> _steps;
    private readonly double[] _starts;
    private readonly Func<Task>? _testRun;
    private ScreenPreviewOverlay? _overlay;
    private bool _syncing;
    private long _overlayFrame;
    private int _shownStep = -2;
    public PreviewView(MacroProfile profile, IWindowSystem? windows = null, Func<Task>? testRun = null)
    {
        InitializeComponent();
        L.Apply(this);
        windows ??= WindowSystem.Instance;
        _testRun = testRun;
        TestButton.Visibility = testRun != null ? Visibility.Visible : Visibility.Collapsed;
        // The preview follows what plays: groups expanded into their repeats and waits.
        _steps = StepGroups.Expand(profile.Actions).ToArray();
        TitleText.Text = L.T($"Preview · {profile.Name}");
        IntPtr window = profile.Window != null ? windows.Find(profile.Window) : IntPtr.Zero;
        var resolver = new PointerResolver(windows, window, profile);
        _timeline = MacroTimeline.Build(_steps, (space, x, y) => resolver.Resolve(space, x, y));
        _starts = new double[_steps.Count];
        for (int i = 0, t = 0; i < _steps.Count; i++) { _starts[i] = t; t += _steps[i].DurationMs; }
        Route.Timeline = _timeline;
        Route.Visibility = _timeline.HasPointer ? Visibility.Visible : Visibility.Collapsed;
        NowPanel.Visibility = _timeline.HasPointer ? Visibility.Collapsed : Visibility.Visible;
        ScreenBox.IsEnabled = _timeline.HasPointer;
        Strip.Show(_steps, Enumerable.Range(0, _steps.Count).ToArray());
        if (profile.Window != null && window == IntPtr.Zero && _steps.Any(a => a.Space == CoordinateSpace.Window))
            NoteText.Text = L.T($"{profile.Window.Label} is not open, so window positions are drawn from the top-left of the main screen.") + " " + NoteText.Text;
        _player.Duration = _timeline.Duration;
        TimeSlider.Maximum = Math.Max(1, _timeline.Duration);
        _player.Changed += Sync;
        DelayTrack.SizeChanged += (_, _) => Sync();
        Sync();
    }
    public void Dispose()
    {
        _player.Dispose();
        _overlay?.Close(); _overlay = null;
    }
    private void Close_Click(object sender, RoutedEventArgs e) => SheetLayer.Close(this, false);
    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (_testRun == null) return;
        SheetLayer.Close(this, false);
        await _testRun();
    }
    private void Sync()
    {
        _syncing = true;
        double time = _player.Time;
        TimeSlider.Value = time;
        TimeText.Text = $"{time / 1000:0.0} s / {_timeline.Duration / 1000:0.0} s";
        Route.Time = time;
        Strip.Playhead = time;
        // The on-screen marker runs at about 30 fps; it is a hint, and transparent windows are costly to recompose.
        if (_overlay != null && (!_player.IsPlaying || Environment.TickCount64 - _overlayFrame >= 33))
        { _overlay.Time = time; _overlayFrame = Environment.TickCount64; }
        PlayText.Text = _player.IsPlaying ? L.T("Pause") : L.T("Play");
        PlayIcon.Data = (Geometry)FindResource(_player.IsPlaying ? "IconStop" : "IconPlay");
        int step = _steps.Count == 0 ? -1 : _timeline.StepAt(time);
        StepText.Text = step < 0 ? L.T("No steps") : L.T($"Step {step + 1:N0} of {_steps.Count:N0} · {_steps[step].DisplayText}");
        DelayBar.Width = _timeline.DelayAt(time) is { } d ? DelayTrack.ActualWidth * (1 - d.Progress) : 0;
        if (step != _shownStep) ShowNow(step);
        _syncing = false;
    }
    /// <summary>The step playing now, with the ones before and after it, for sequences without a route.</summary>
    private void ShowNow(int step)
    {
        _shownStep = step;
        if (NowPanel.Visibility != Visibility.Visible) return;
        NowStep.DataContext = step >= 0 ? StepView.From(_steps[step]) : null;
        PreviousText.Text = step > 0 ? L.T($"before · {_steps[step - 1].DisplayText}") : " ";
        NextText.Text = step >= 0 && step + 1 < _steps.Count ? L.T($"next at {StepLayoutConverter.FormatTime(_starts[step + 1])} · {_steps[step + 1].DisplayText}") : L.T("end of the run");
    }
    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_player.IsPlaying) _player.Pause(); else _player.Play();
    }
    private void Time_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_syncing) _player.Seek(e.NewValue);
    }
    private void Strip_StepPicked(int index)
    {
        if (index >= 0 && index < _starts.Length) _player.Seek(_starts[index]);
    }
    private void Speed_Changed(object sender, SelectionChangedEventArgs e) => _player.Speed = Speeds[Math.Max(0, SpeedBox.SelectedIndex)];
    private void Screen_Changed(object sender, RoutedEventArgs e)
    {
        if (ScreenBox.IsChecked == true && _overlay == null)
        {
            _overlay = new ScreenPreviewOverlay(_timeline) { Time = _player.Time };
            _overlay.Show();
        }
        else if (ScreenBox.IsChecked != true && _overlay != null) { _overlay.Close(); _overlay = null; }
    }
}
