using Keyloom.Models;

namespace Keyloom.Engine;

/// <summary>
/// Groups are a GroupStart and a GroupEnd marker around steps in the flat step list. A group can play its steps several times
/// and add a wait between them. Playback, the preview and limits all use <see cref="Expand"/>, so groups live in one place.
/// </summary>
public static class StepGroups
{
    public const int MaxRepeat = 10000, MaxDepth = 8;
    /// <summary>The most steps one run may play once groups are expanded, so a large repeat cannot run away.</summary>
    public const long MaxPlayedSteps = 1_000_000;

    /// <summary>Index of the GroupEnd matching the GroupStart at <paramref name="start"/>, or -1 when it has none.</summary>
    public static int EndOf(IReadOnlyList<MacroAction> steps, int start)
    {
        int depth = 0;
        for (int i = start; i < steps.Count; i++)
        {
            if (steps[i].ActionType == MacroActionType.GroupStart) depth++;
            else if (steps[i].ActionType == MacroActionType.GroupEnd && --depth == 0) return i;
        }
        return -1;
    }
    /// <summary>Index of the GroupStart matching the GroupEnd at <paramref name="end"/>, or -1 when it has none.</summary>
    public static int StartOf(IReadOnlyList<MacroAction> steps, int end)
    {
        int depth = 0;
        for (int i = end; i >= 0; i--)
        {
            if (steps[i].ActionType == MacroActionType.GroupEnd) depth++;
            else if (steps[i].ActionType == MacroActionType.GroupStart && --depth == 0) return i;
        }
        return -1;
    }
    /// <summary>How many groups are open just before <paramref name="index"/>. A GroupEnd counts its own group; a GroupStart does not.</summary>
    public static int DepthAt(IReadOnlyList<MacroAction> steps, int index)
    {
        int depth = 0;
        for (int i = 0; i < index && i < steps.Count; i++)
        {
            if (steps[i].ActionType == MacroActionType.GroupStart) depth++;
            else if (steps[i].ActionType == MacroActionType.GroupEnd) depth = Math.Max(0, depth - 1);
        }
        return depth;
    }
    /// <summary>True when a collapsed group hides the step; the collapsed group's own header stays visible.</summary>
    public static bool IsHidden(IReadOnlyList<MacroAction> steps, int index)
    {
        var open = new Stack<bool>();
        for (int i = 0; i < index && i < steps.Count; i++)
        {
            if (steps[i].ActionType == MacroActionType.GroupStart) open.Push(steps[i].Collapsed);
            else if (steps[i].ActionType == MacroActionType.GroupEnd && open.Count > 0) open.Pop();
        }
        return open.Contains(true);
    }

    /// <summary>Checks that every group is closed, not nested too deep and has sensible settings.</summary>
    public static void Validate(IReadOnlyList<MacroAction> steps)
    {
        int depth = 0;
        foreach (var a in steps)
        {
            if (a.ActionType == MacroActionType.GroupStart)
            {
                if (++depth > MaxDepth) throw new ArgumentException($"Groups can be nested up to {MaxDepth} levels.");
                if (a.Name is { Length: > 80 }) throw new ArgumentException("Use a group name up to 80 characters.");
                if (a.Repeat is < 0 or > MaxRepeat) throw new ArgumentException($"A group can run 1 to {MaxRepeat:N0} times.");
                if (a.GapMs is < 0 or > 3600000) throw new ArgumentException("The wait between group steps must be between 0 and 3600000 ms.");
            }
            else if (a.ActionType == MacroActionType.GroupEnd && --depth < 0) throw new ArgumentException("A group end has no matching group start.");
        }
        if (depth > 0) throw new ArgumentException("A group has no end. Undo the last change or remove the group.");
        if (PlayedCount(steps) > MaxPlayedSteps)
            throw new ArgumentException($"Groups repeat too much: one run would play over {MaxPlayedSteps:N0} steps. Lower a group's run count.");
    }
    /// <summary>Steps one run plays once groups are expanded, including the waits groups add.</summary>
    public static long PlayedCount(IReadOnlyList<MacroAction> steps)
    {
        int i = 0; return Count(steps, ref i, null);
    }
    private static long Count(IReadOnlyList<MacroAction> steps, ref int i, MacroAction? group)
    {
        long count = 0, children = 0;
        for (; i < steps.Count; i++)
        {
            var a = steps[i];
            if (a.ActionType == MacroActionType.GroupEnd) { if (group != null) break; continue; }
            children++;
            if (a.ActionType == MacroActionType.GroupStart) { i++; count += Count(steps, ref i, a); }
            else count++;
            if (count > MaxPlayedSteps) return count;
        }
        if (group == null) return count;
        long gaps = group.GapMs > 0 && children > 0 ? children * group.Runs - 1 : 0;
        return Math.Min(count * group.Runs + gaps, MaxPlayedSteps + 1);
    }

    /// <summary>
    /// The steps one run plays, in order: groups repeat and add their waits, markers disappear. Enumerated lazily so a long run
    /// does not build a huge list. Added waits are new <see cref="MacroAction"/> objects; every other step is the original object.
    /// </summary>
    public static IEnumerable<MacroAction> Expand(IReadOnlyList<MacroAction> steps) => Play(Parse(steps));

    /// <summary>
    /// When each step first plays in one run, in milliseconds from the start, counting waits and the waits groups add.
    /// Group markers take the time of the step after them. With more played steps than <see cref="MaxPlayedSteps"/>
    /// the times count each step once. With <paramref name="clickDelayMs"/>, the first click or hold after a cursor move
    /// starts no earlier than that long after the move, as it plays.
    /// </summary>
    public static double[] StartTimes(IReadOnlyList<MacroAction> steps, int clickDelayMs = 0)
    {
        var times = new double[steps.Count];
        var index = new Dictionary<MacroAction, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < steps.Count; i++) if (!steps[i].IsGroupMarker) index.TryAdd(steps[i], i);
        var seen = new bool[steps.Count];
        int left = index.Count;
        double time = 0, moved = double.NegativeInfinity;
        var played = PlayedCount(steps) <= MaxPlayedSteps ? Expand(steps) : steps.Where(a => !a.IsGroupMarker);
        foreach (var step in played)
        {
            if (left == 0) break;
            bool press = step.ActionType is MacroActionType.MouseClick or MacroActionType.MouseDown;
            if (press) { time = Math.Max(time, moved + clickDelayMs); moved = double.NegativeInfinity; }
            if (index.TryGetValue(step, out int i) && !seen[i]) { seen[i] = true; times[i] = time; left--; }
            time += step.DurationMs;
            if (step.ActionType is MacroActionType.MousePosition or MacroActionType.MouseMove) moved = time;
        }
        double next = time;
        for (int i = steps.Count - 1; i >= 0; i--)
        {
            if (steps[i].IsGroupMarker) times[i] = next;
            else next = times[i];
        }
        return times;
    }

    private sealed record Node(MacroAction Step, List<Node>? Children);
    private static List<Node> Parse(IReadOnlyList<MacroAction> steps)
    {
        var root = new List<Node>();
        var open = new Stack<List<Node>>();
        var current = root;
        foreach (var a in steps)
        {
            if (a.ActionType == MacroActionType.GroupStart)
            {
                var group = new Node(a, []);
                current.Add(group); open.Push(current); current = group.Children!;
            }
            // An end without a start is ignored; a start without an end runs to the end of the list.
            else if (a.ActionType == MacroActionType.GroupEnd) { if (open.Count > 0) current = open.Pop(); }
            else current.Add(new Node(a, null));
        }
        return root;
    }
    private static IEnumerable<MacroAction> Play(List<Node> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.Children == null) { yield return node.Step; continue; }
            var group = node.Step;
            bool first = true;
            for (int run = 0; run < group.Runs; run++)
                foreach (var child in node.Children)
                {
                    if (!first && group.GapMs > 0) yield return new MacroAction { ActionType = MacroActionType.Delay, DelayMs = group.GapMs };
                    first = false;
                    foreach (var step in Play([child])) yield return step;
                }
        }
    }
}
