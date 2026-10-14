using Keyloom.Models;

namespace Keyloom.Engine;

public enum TimelineEventKind { Click, Down, Up, Scroll, Key, Text }
public readonly record struct TimelinePoint(double Time, double X, double Y);
public readonly record struct TimelineEvent(double Time, double X, double Y, TimelineEventKind Kind, int Step);
public readonly record struct TimelineDelay(double Start, double Duration, int Step);

/// <summary>
/// One run of a sequence laid out in time: where the pointer is planned to be, where inputs happen and which delays
/// are waiting. Built once per preview; queries are binary searches so scrubbing a 20,000-step macro stays cheap.
/// Positions are planned values only; the target app may react differently.
/// </summary>
public sealed class MacroTimeline
{
    public IReadOnlyList<TimelinePoint> Points { get; }
    public IReadOnlyList<TimelineEvent> Events { get; }
    public IReadOnlyList<TimelineDelay> Delays { get; }
    public double Duration { get; }
    public bool HasPointer => Points.Count > 0;
    public (double Left, double Top, double Right, double Bottom) Bounds { get; }
    private readonly double[] _pointTimes, _delayStarts, _eventTimes;

    private MacroTimeline(List<TimelinePoint> points, List<TimelineEvent> events, List<TimelineDelay> delays, double duration)
    {
        Points = points; Events = events; Delays = delays; Duration = duration;
        _pointTimes = points.Select(p => p.Time).ToArray();
        _delayStarts = delays.Select(d => d.Start).ToArray();
        _eventTimes = events.Select(e => e.Time).ToArray();
        Bounds = points.Count == 0 ? (0, 0, 0, 0) : (points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
    }
    /// <param name="resolve">Maps a stored position to screen pixels; pass null to use stored values as-is.</param>
    public static MacroTimeline Build(IReadOnlyList<MacroAction> steps, Func<CoordinateSpace, int, int, (double X, double Y)>? resolve = null)
    {
        resolve ??= (_, x, y) => (x, y);
        var points = new List<TimelinePoint>();
        var events = new List<TimelineEvent>();
        var delays = new List<TimelineDelay>();
        double time = 0, x = 0, y = 0;
        bool known = false;
        var pending = new List<int>();
        for (int i = 0; i < steps.Count; i++)
        {
            var a = steps[i];
            switch (a.ActionType)
            {
                case MacroActionType.Delay:
                    delays.Add(new(time, a.DelayMs, i)); time += a.DelayMs; break;
                case MacroActionType.MousePosition:
                    (x, y) = resolve(a.Space, a.X, a.Y); Place(); break;
                case MacroActionType.MouseMove:
                    x += a.X; y += a.Y; Place(); break;
                default:
                    var kind = a.ActionType switch
                    {
                        MacroActionType.MouseClick => TimelineEventKind.Click, MacroActionType.MouseDown => TimelineEventKind.Down,
                        MacroActionType.MouseUp => TimelineEventKind.Up, MacroActionType.MouseScroll => TimelineEventKind.Scroll,
                        MacroActionType.Text => TimelineEventKind.Text, _ => TimelineEventKind.Key
                    };
                    events.Add(new(time, x, y, kind, i));
                    if (!known) pending.Add(events.Count - 1);
                    break;
            }
        }
        return new(points, events, delays, time);
        void Place()
        {
            // Inputs before the first known position happen wherever the pointer starts; draw them at the first position.
            if (!known) foreach (int e in pending) events[e] = events[e] with { X = x, Y = y };
            known = true; pending.Clear();
            if (points.Count > 0 && points[^1].Time == time) points[^1] = new(time, x, y);
            else points.Add(new(time, x, y));
        }
    }
    /// <summary>Planned pointer position at a time: the last position reached, since playback jumps between samples.</summary>
    public (double X, double Y)? PositionAt(double time)
    {
        if (Points.Count == 0) return null;
        int i = Array.BinarySearch(_pointTimes, time);
        if (i < 0) i = ~i - 1;
        while (i + 1 < _pointTimes.Length && _pointTimes[i + 1] == time) i++;
        var p = Points[Math.Max(0, i)];
        return (p.X, p.Y);
    }
    /// <summary>The delay running at a time, with how far through it playback is (0..1).</summary>
    public (TimelineDelay Delay, double Progress)? DelayAt(double time)
    {
        int i = Array.BinarySearch(_delayStarts, time);
        if (i < 0) i = ~i - 1;
        // Several delays can start at the same time only if one is zero-length; take the latest that has begun.
        while (i + 1 < _delayStarts.Length && _delayStarts[i + 1] <= time) i++;
        if (i < 0) return null;
        var d = Delays[i];
        if (time >= d.Start + d.Duration || d.Duration <= 0) return null;
        return (d, (time - d.Start) / d.Duration);
    }
    /// <summary>Events in a time window, for click pulses during playback.</summary>
    public IEnumerable<TimelineEvent> EventsBetween(double from, double to)
    {
        int i = Array.BinarySearch(_eventTimes, from);
        if (i < 0) i = ~i;
        while (i > 0 && _eventTimes[i - 1] >= from) i--;
        for (; i < Events.Count && Events[i].Time <= to; i++) yield return Events[i];
    }
    /// <summary>Index of the step that is running (or last ran) at a time.</summary>
    public int StepAt(double time, int fallback = 0)
    {
        var delay = DelayAt(time);
        if (delay != null) return delay.Value.Delay.Step;
        int i = Array.BinarySearch(_eventTimes, time);
        if (i < 0) i = ~i - 1;
        while (i + 1 < _eventTimes.Length && _eventTimes[i + 1] <= time) i++;
        return i >= 0 ? Events[i].Step : fallback;
    }
}
