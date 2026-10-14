using System.Diagnostics;
using System.Windows.Media;

namespace Keyloom.Views;

/// <summary>Preview clock. Hooks the render loop only while playing, so a paused or closed preview costs nothing.</summary>
public sealed class PreviewPlayer : IDisposable
{
    private readonly Stopwatch _clock = new();
    private double _time;
    public double Duration { get; set; }
    public double Speed { get; set; } = 1;
    public bool IsPlaying { get; private set; }
    public double Time => _time;
    public event Action? Changed;
    public void Play()
    {
        if (IsPlaying) return;
        if (_time >= Duration) _time = 0;
        IsPlaying = true; _clock.Restart();
        CompositionTarget.Rendering += OnRendering;
        Changed?.Invoke();
    }
    public void Pause()
    {
        if (!IsPlaying) return;
        IsPlaying = false; _clock.Stop();
        CompositionTarget.Rendering -= OnRendering;
        Changed?.Invoke();
    }
    public void Seek(double time)
    {
        _time = Math.Clamp(time, 0, Duration); _clock.Restart();
        Changed?.Invoke();
    }
    private void OnRendering(object? sender, EventArgs e)
    {
        _time += _clock.Elapsed.TotalMilliseconds * Speed; _clock.Restart();
        if (_time >= Duration) { _time = Duration; Pause(); return; }
        Changed?.Invoke();
    }
    public void Dispose() { Pause(); Changed = null; }
}
