using Keyloom.Models;

namespace Keyloom.Engine;

/// <summary>
/// Groups are a GroupStart and a GroupEnd marker around steps in the flat step list. A group can play its steps several times
/// or for a time, and add a wait between them. Playback, the preview and limits all use <see cref="Expand"/>, so groups live in one place.
/// </summary>
public static class StepGroups
{
    public const int MaxRepeat = 10000, MaxDepth = 8, MaxForMs = 3600000;
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
        for (int index = 0; index < steps.Count; index++)
        {
            var a = steps[index];
            if (a.ActionType == MacroActionType.GroupStart)
            {
                if (++depth > MaxDepth) throw new ArgumentException(L.T($"Groups can be nested up to {MaxDepth} levels."));
                if (a.Name is { Length: > 80 }) throw new ArgumentException(L.T("Use a group name up to 80 characters."));
                if (a.Repeat is < 0 or > MaxRepeat) throw new ArgumentException(L.T($"A group can run 1 to {MaxRepeat:N0} times."));
                if (a.GapMs is < 0 or > 3600000) throw new ArgumentException(L.T("The wait between group steps must be between 0 and 3600000 ms."));
                if (a.ForMs is < 0 or > MaxForMs) throw new ArgumentException(L.T("A group can repeat for up to 1h."));
                if (a.ForMs > 0 && a.Block != BlockKind.Group) throw new ArgumentException(L.T("An If or an Else plays its steps once. Put a group inside it to repeat them."));
                if (a.ForMs > 0 && PassMs(steps, index) + a.GapMs <= 0)
                    throw new ArgumentException(L.T("A repeat for a time needs a Wait inside it, or a wait between steps, so it knows how fast to go."));
                if (!Enum.IsDefined(a.Block)) throw new ArgumentException(L.T("Unknown kind of block."));
                if (a.Block != BlockKind.Group && a.Repeat > 1) throw new ArgumentException(L.T("An If or an Else plays its steps once. Put a group inside it to repeat them."));
                if (a.Block == BlockKind.If && a.TimeoutMs is < 0 or > 3600000) throw new ArgumentException(L.T("An If can look for its target for 0 to 3600000 ms."));
            }
            else if (a.ActionType == MacroActionType.GroupEnd && --depth < 0) throw new ArgumentException(L.T("A group end has no matching group start."));
        }
        if (depth > 0) throw new ArgumentException(L.T("A group has no end. Undo the last change or remove the group."));
        for (int i = 0; i < steps.Count; i++)
            if (steps[i] is { ActionType: MacroActionType.GroupStart, Block: BlockKind.Else } && !(i > 0 && steps[i - 1].ActionType == MacroActionType.GroupEnd
                && StartOf(steps, i - 1) is var before and >= 0 && steps[before].Block == BlockKind.If))
                throw new ArgumentException(L.T("An Else has to come right after an If."));
        if (PlayedCount(steps) > MaxPlayedSteps)
            throw new ArgumentException(L.T($"Groups repeat too much: one run would play over {MaxPlayedSteps:N0} steps. Lower a group's run count or time."));
    }
    /// <summary>Steps one run plays once groups are expanded, including the waits groups add.</summary>
    public static long PlayedCount(IReadOnlyList<MacroAction> steps)
    {
        int i = 0; return Count(steps, ref i, null);
    }
    /// <summary>How long one pass of the group at <paramref name="start"/> takes, with the waits it adds between its steps.</summary>
    public static double PassMs(IReadOnlyList<MacroAction> steps, int start)
    {
        int end = EndOf(steps, start);
        var once = steps.Skip(start).Take(end < 0 ? steps.Count - start : end - start + 1).ToList();
        once[0] = once[0].Clone(); once[0].Repeat = 0; once[0].ForMs = 0;
        double total = 0;
        long played = 0;
        foreach (var step in Expand(once))
        {
            total += step.DurationMs;
            if (++played > MaxPlayedSteps) break;
        }
        return total;
    }
    /// <summary>How many passes a group plays: its count, or for a timed group as many as start within its time.</summary>
    public static long PassesOf(IReadOnlyList<MacroAction> steps, int start)
    {
        var g = steps[start];
        if (g.ForMs <= 0) return g.Runs;
        double pass = PassMs(steps, start), gap = g.GapMs;
        // Pass k starts after k passes and k - 1 waits between them, and starts only while time is left.
        return pass + gap <= 0 ? MaxPlayedSteps + 1 : Math.Max(1, (long)Math.Ceiling((g.ForMs + gap) / (pass + gap)));
    }
    private static long Count(IReadOnlyList<MacroAction> steps, ref int i, MacroAction? group)
    {
        int head = i - 1;
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
        long runs = group.ForMs > 0 ? PassesOf(steps, head) : group.Runs;
        long gaps = group.GapMs > 0 && children > 0 ? children * runs - 1 : 0;
        return Math.Min(count * runs + gaps, MaxPlayedSteps + 1);
    }

    /// <summary>
    /// The steps one run plays, in order: groups repeat and add their waits, markers disappear. Enumerated lazily so a long run
    /// does not build a huge list. Added waits are new <see cref="MacroAction"/> objects; every other step is the original object.
    /// </summary>
    /// <param name="decide">Answers an If when playback reaches it, after every step before it has played. Without it, as in the
    /// preview and the timing, an If and its Else both play.</param>
    /// <param name="clock">Milliseconds on a running clock, which a group that repeats for a time checks before each pass. Without it,
    /// as in the preview and the timing, time is what the played steps take.</param>
    public static IEnumerable<MacroAction> Expand(IReadOnlyList<MacroAction> steps, Func<MacroAction, bool>? decide = null, Func<double>? clock = null)
        => Play(Parse(steps), decide, new Clock(clock));

    /// <summary>Real time when playing; otherwise the time the steps played so far take.</summary>
    private sealed class Clock(Func<double>? real)
    {
        public double Played;
        public double Now => real?.Invoke() ?? Played;
        public bool Real => real != null;
    }

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
    /// <summary>Plays sibling steps, with <paramref name="gapMs"/> between them, asking each If as it is reached.</summary>
    private static IEnumerable<MacroAction> Play(List<Node> nodes, Func<MacroAction, bool>? decide, Clock clock, int gapMs = 0)
    {
        bool? lastIf = null;
        bool first = true;
        foreach (var node in nodes)
        {
            bool? answer = null;
            bool play = true;
            // Without answers, as in the preview, an If and its Else both play.
            if (node.Children != null && decide != null)
            {
                if (node.Step.Block == BlockKind.If) play = (answer = decide(node.Step)).Value;
                else if (node.Step.Block == BlockKind.Else) play = lastIf == false;
            }
            lastIf = answer;
            if (!play) continue;
            if (!first && gapMs > 0) { yield return new MacroAction { ActionType = MacroActionType.Delay, DelayMs = gapMs }; clock.Played += gapMs; }
            first = false;
            if (node.Children == null) { yield return node.Step; clock.Played += node.Step.DurationMs; continue; }
            var group = node.Step;
            double began = clock.Now;
            for (int run = 0; group.ForMs > 0 ? run == 0 || clock.Now - began < group.ForMs : run < group.Runs; run++)
            {
                double passBegan = clock.Played;
                if (run > 0 && group.GapMs > 0 && node.Children.Count > 0) { yield return new MacroAction { ActionType = MacroActionType.Delay, DelayMs = group.GapMs }; clock.Played += group.GapMs; }
                foreach (var step in Play(node.Children, decide, clock, group.GapMs)) yield return step;
                // Counted time that does not move would repeat forever; validation keeps such groups out, this keeps the preview safe.
                if (group.ForMs > 0 && !clock.Real && clock.Played == passBegan) break;
            }
        }
    }
}
