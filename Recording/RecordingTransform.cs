using Keyloom.Models;

namespace Keyloom.Recording;

/// <summary>Post-processing for recorded steps: coordinate space conversion and path simplification.</summary>
public static class RecordingTransform
{
    /// <summary>Re-expresses screen positions relative to an origin (window client corner or the pointer at recording start).</summary>
    public static IReadOnlyList<MacroAction> ToSpace(IEnumerable<MacroAction> steps, CoordinateSpace space, int originX, int originY) =>
        steps.Select(a =>
        {
            var copy = a.Clone();
            if (copy.ActionType == MacroActionType.MousePosition && space != CoordinateSpace.Screen)
            { copy.X -= originX; copy.Y -= originY; copy.Space = space; }
            return copy;
        }).ToArray();

    /// <summary>
    /// Drops pointer samples that lie within <paramref name="tolerance"/> px of where steady movement between their
    /// neighbours would put them at that moment, while keeping at least one sample every <paramref name="maxGapMs"/>
    /// so playback still glides instead of jumping. Only runs of consecutive positions and delays are touched, so
    /// clicks, holds and key events keep their exact position and time; removed samples hand their delay to the next
    /// kept one, so total duration is unchanged.
    /// </summary>
    public static IReadOnlyList<MacroAction> Simplify(IReadOnlyList<MacroAction> steps, double tolerance = 2, int maxGapMs = 40)
    {
        var result = new List<MacroAction>(steps.Count);
        int i = 0;
        while (i < steps.Count)
        {
            if (!IsPath(steps[i])) { result.Add(steps[i].Clone()); i++; continue; }
            int end = i;
            while (end < steps.Count && IsPath(steps[end])) end++;
            SimplifyRun(steps, i, end, tolerance, maxGapMs, result);
            i = end;
        }
        return result;
    }
    private static bool IsPath(MacroAction a) => a.ActionType is MacroActionType.MousePosition or MacroActionType.Delay;
    private static void SimplifyRun(IReadOnlyList<MacroAction> steps, int start, int end, double tolerance, int maxGapMs, List<MacroAction> result)
    {
        // Each sample keeps the delay that preceded it; a trailing delay stays as-is.
        var samples = new List<(MacroAction Position, long DelayBefore)>();
        long pending = 0;
        for (int i = start; i < end; i++)
        {
            if (steps[i].ActionType == MacroActionType.Delay) pending += steps[i].DelayMs;
            else { samples.Add((steps[i], pending)); pending = 0; }
        }
        var times = new long[samples.Count];
        for (int i = 0; i < samples.Count; i++) times[i] = (i > 0 ? times[i - 1] : 0) + samples[i].DelayBefore;
        // Samples with different coordinate spaces are never merged.
        var keep = new bool[samples.Count];
        int segment = 0;
        for (int i = 1; i <= samples.Count; i++)
        {
            if (i < samples.Count && samples[i].Position.Space == samples[segment].Position.Space) continue;
            Mark(samples, times, segment, i - 1, tolerance, keep);
            segment = i;
        }
        long sinceKept = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            sinceKept += samples[i].DelayBefore;
            if (keep[i]) sinceKept = 0;
            else if (sinceKept >= maxGapMs) { keep[i] = true; sinceKept = 0; }
        }
        long carried = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            carried += samples[i].DelayBefore;
            if (!keep[i]) continue;
            AddDelay(result, carried); carried = 0;
            result.Add(samples[i].Position.Clone());
        }
        AddDelay(result, carried + pending);
    }
    private static void AddDelay(List<MacroAction> result, long total)
    {
        while (total > 0)
        {
            int chunk = (int)Math.Min(total, 3600000);
            result.Add(new() { ActionType = MacroActionType.Delay, DelayMs = chunk });
            total -= chunk;
        }
    }
    private static void Mark(List<(MacroAction Position, long DelayBefore)> samples, long[] times, int first, int last, double tolerance, bool[] keep)
    {
        keep[first] = keep[last] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((first, last));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            double max = -1; int index = -1;
            for (int i = a + 1; i < b; i++)
            {
                double d = Distance(samples[i].Position, times[i], samples[a].Position, times[a], samples[b].Position, times[b]);
                if (d > max) { max = d; index = i; }
            }
            if (index < 0 || max <= tolerance) continue;
            keep[index] = true;
            stack.Push((a, index)); stack.Push((index, b));
        }
    }
    /// <summary>Distance from p to where steady motion from a to b would be at p's time.</summary>
    private static double Distance(MacroAction p, long tp, MacroAction a, long ta, MacroAction b, long tb)
    {
        double t = tb > ta ? (double)(tp - ta) / (tb - ta) : 0;
        return Math.Sqrt(Math.Pow(p.X - (a.X + t * (b.X - a.X)), 2) + Math.Pow(p.Y - (a.Y + t * (b.Y - a.Y)), 2));
    }
}
