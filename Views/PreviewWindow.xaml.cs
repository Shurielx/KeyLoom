using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Keyloom.Engine;
using Keyloom.Models;

namespace Keyloom.Views;

public partial class PreviewWindow : Window
{
    private static readonly double[] Speeds = [0.25, 0.5, 1, 2, 4];
    private readonly PreviewPlayer _player = new();
    private readonly MacroTimeline _timeline;
    private readonly IReadOnlyList<MacroAction> _steps;
    private ScreenPreviewOverlay? _overlay;
    private bool _syncing;
    private long _overlayFrame;
    public PreviewWindow(MacroProfile profile, IWindowSystem? windows = null)
    {
        InitializeComponent();
        Backdrop.Attach(this);
        windows ??= WindowSystem.Instance;
        _steps = profile.Actions.ToArray();
        TitleText.Text = $"Preview · {profile.Name}";
        IntPtr window = profile.Window != null ? windows.Find(profile.Window) : IntPtr.Zero;
        var resolver = new PointerResolver(windows, window, profile);
        _timeline = MacroTimeline.Build(_steps, (space, x, y) => resolver.Resolve(space, x, y));
        Route.Timeline = _timeline;
        EmptyText.Visibility = _timeline.HasPointer ? Visibility.Collapsed : Visibility.Visible;
        ScreenBox.IsEnabled = _timeline.HasPointer;
        if (profile.Window != null && window == IntPtr.Zero && _steps.Any(a => a.Space == CoordinateSpace.Window))
            NoteText.Text = $"{profile.Window.Label} is not open, so window positions are drawn from the top-left of the main screen. " + NoteText.Text;
        _player.Duration = _timeline.Duration;
        TimeSlider.Maximum = Math.Max(1, _timeline.Duration);
        _player.Changed += Sync;
        DelayTrack.SizeChanged += (_, _) => Sync();
        Sync();
        Closed += (_, _) => { _player.Dispose(); _overlay?.Close(); };
    }
    private void Sync()
    {
        _syncing = true;
        double time = _player.Time;
        TimeSlider.Value = time;
        TimeText.Text = $"{time / 1000:0.0} s / {_timeline.Duration / 1000:0.0} s";
        Route.Time = time;
        // The on-screen marker runs at about 30 fps; it is a hint, and transparent windows are costly to recompose.
        if (_overlay != null && (!_player.IsPlaying || Environment.TickCount64 - _overlayFrame >= 33))
        { _overlay.Time = time; _overlayFrame = Environment.TickCount64; }
        PlayText.Text = _player.IsPlaying ? "Pause" : "Play";
        PlayIcon.Data = (Geometry)FindResource(_player.IsPlaying ? "IconStop" : "IconPlay");
        int step = _steps.Count == 0 ? -1 : _timeline.StepAt(time);
        StepText.Text = step < 0 ? "No steps" : $"Step {step + 1:N0} of {_steps.Count:N0} · {_steps[step].DisplayText}";
        DelayBar.Width = _timeline.DelayAt(time) is { } d ? DelayTrack.ActualWidth * (1 - d.Progress) : 0;
        _syncing = false;
    }
    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_player.IsPlaying) _player.Pause(); else _player.Play();
    }
    private void Time_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_syncing) _player.Seek(e.NewValue);
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
