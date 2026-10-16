using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls.Primitives;

namespace Keyloom.Services;

/// <summary>A short, quiet "thock" for clicks on Keyloom's own buttons, switches and choices. Off with Click sounds in Settings.</summary>
public static class ClickSound
{
    private const int Rate = 44100;
    private static SoundPlayer? _player;
    private static long _last;

    /// <summary>Plays the sound for every button click in the app. Call once at startup.</summary>
    public static void Hook() => EventManager.RegisterClassHandler(typeof(ButtonBase), ButtonBase.ClickEvent, new RoutedEventHandler(OnClick), true);
    private static void OnClick(object sender, RoutedEventArgs e)
    {
        // A click bubbles through every button around it; play once, for the button that was clicked.
        if (e.OriginalSource != sender || !AppSettings.Current.ClickSounds) return;
        long now = Environment.TickCount64;
        if (now - _last < 40) return;
        _last = now;
        Play();
    }
    public static void Play()
    {
        try
        {
            if (_player == null) { _player = new SoundPlayer(new MemoryStream(Wave())); _player.Load(); }
            _player.Play();
        }
        // No audio device, or the device is busy: a click without sound is fine.
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException) { }
    }

    /// <summary>16-bit mono WAV, about 45 ms: a low tone that drops in pitch and dies away fast, with a soft tick at the start.</summary>
    public static byte[] Wave()
    {
        int count = Rate * 45 / 1000;
        var samples = new short[count];
        var random = new Random(7);
        double phase = 0, noise = 0;
        for (int i = 0; i < count; i++)
        {
            double t = (double)i / Rate;
            phase += 2 * Math.PI * (95 + 85 * Math.Exp(-t / 0.012)) / Rate;
            double body = Math.Sin(phase) * Math.Exp(-t / 0.011);
            // Low-passed noise for the first couple of milliseconds gives it the edge of a key bottoming out.
            noise += (random.NextDouble() * 2 - 1 - noise) * 0.25;
            double tick = noise * Math.Exp(-t / 0.0015);
            double fadeIn = Math.Min(1, i / 40.0);
            samples[i] = (short)(Math.Clamp((body * 0.75 + tick * 0.5) * fadeIn * 0.16, -1, 1) * short.MaxValue);
        }
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write("RIFF"u8); w.Write(36 + count * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(count * 2);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return stream.ToArray();
    }
}
