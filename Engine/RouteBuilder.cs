using Keyloom.Models;

namespace Keyloom.Engine;

/// <summary>
/// A point on the route as the on-screen builder shows it: one cursor position step and the steps that follow it up to
/// the next point or group edge (clicks, holds, waits, keys). Indices point into the flat step list.
/// </summary>
public sealed record RoutePoint(int Index, int End, int Group)
{
    /// <summary>The steps that belong to the point, after its move.</summary>
    public IEnumerable<int> Steps => Enumerable.Range(Index + 1, End - Index - 1);
}

/// <summary>One row of the builder: a single step, or a hold (press, wait, release) shown as one.</summary>
public readonly record struct StepUnit(int Start, int Length)
{
    public int End => Start + Length;
    public bool IsHold => Length == 3;
}

/// <summary>Reads and changes a sequence as points on the screen. Changes swap in copies, so undo copies stay untouched.</summary>
public static class RouteBuilder
{
    public const int DefaultWaitMs = 300, DefaultHoldMs = 200;
    public static List<RoutePoint> Points(IReadOnlyList<MacroAction> steps)
    {
        var points = new List<RoutePoint>();
        var groups = new Stack<int>();
        for (int i = 0; i < steps.Count; i++)
        {
            var a = steps[i];
            if (a.ActionType == MacroActionType.GroupStart) { groups.Push(i); continue; }
            if (a.ActionType == MacroActionType.GroupEnd) { if (groups.Count > 0) groups.Pop(); continue; }
            if (a.ActionType != MacroActionType.MousePosition) continue;
            int end = i + 1;
            while (end < steps.Count && steps[end].ActionType is not (MacroActionType.MousePosition or MacroActionType.GroupStart or MacroActionType.GroupEnd)) end++;
            points.Add(new(i, end, groups.Count > 0 ? groups.Peek() : -1));
        }
        return points;
    }
    /// <summary>Steps before the first point, outside any group: what happens before the pointer first moves.</summary>
    public static (int Start, int End) Opening(IReadOnlyList<MacroAction> steps)
    {
        int end = 0;
        while (end < steps.Count && steps[end].ActionType is not (MacroActionType.MousePosition or MacroActionType.GroupStart or MacroActionType.GroupEnd)) end++;
        return (0, end);
    }
    /// <summary>How long the run stays at a point: its waits and holds.</summary>
    public static int WaitMs(IReadOnlyList<MacroAction> steps, RoutePoint point) =>
        point.Steps.Where(i => steps[i].ActionType == MacroActionType.Delay).Sum(i => steps[i].DelayMs);
    /// <summary>The rows between two indices, with each hold as one row.</summary>
    public static List<StepUnit> Units(IReadOnlyList<MacroAction> steps, int start, int end)
    {
        var units = new List<StepUnit>();
        for (int i = start; i < end; i += units[^1].Length) units.Add(new(i, IsHoldAt(steps, i, end) ? 3 : 1));
        return units;
    }
    /// <summary>A press, a wait and the matching release in a row.</summary>
    public static bool IsHoldAt(IReadOnlyList<MacroAction> steps, int i, int end) =>
        i + 2 < end && steps[i + 1].ActionType == MacroActionType.Delay && (
            steps[i].ActionType == MacroActionType.MouseDown && steps[i + 2].ActionType == MacroActionType.MouseUp && steps[i].MouseButton == steps[i + 2].MouseButton ||
            steps[i].ActionType == MacroActionType.KeyDown && steps[i + 2].ActionType == MacroActionType.KeyUp && steps[i].KeyName == steps[i + 2].KeyName);
    /// <summary>The wait a point ends with, which is the wait before the next point, or -1.</summary>
    public static int TrailingWait(IReadOnlyList<MacroAction> steps, RoutePoint point)
    {
        var units = Units(steps, point.Index + 1, point.End);
        return units.Count > 0 && units[^1].Length == 1 && steps[units[^1].Start].ActionType == MacroActionType.Delay ? units[^1].Start : -1;
    }
    /// <summary>Swaps a row with the one before or after it.</summary>
    public static bool ShiftUnit(List<MacroAction> steps, IReadOnlyList<StepUnit> units, int unit, int offset)
    {
        int other = unit + offset;
        if (other < 0 || other >= units.Count) return false;
        var (first, second) = offset < 0 ? (units[other], units[unit]) : (units[unit], units[other]);
        var moved = steps.GetRange(second.Start, second.Length).Concat(steps.GetRange(first.Start, first.Length)).ToList();
        steps.RemoveRange(first.Start, first.Length + second.Length);
        steps.InsertRange(first.Start, moved);
        return true;
    }

    /// <summary>Validates a changed copy of a step and swaps it in.</summary>
    public static void Change(List<MacroAction> steps, int index, Action<MacroAction> change)
    {
        var copy = steps[index].Clone(); change(copy);
        if (copy.IsGroupMarker) StepGroups.Validate(steps.Select((a, i) => i == index ? copy : a).ToList());
        else ProfileValidator.ValidateAction(copy);
        steps[index] = copy;
    }
    public static void Move(List<MacroAction> steps, RoutePoint point, int x, int y) => Change(steps, point.Index, a => { a.X = x; a.Y = y; });
    /// <summary>Removes the point with every step that belongs to it.</summary>
    public static void Remove(List<MacroAction> steps, RoutePoint point) => steps.RemoveRange(point.Index, point.End - point.Index);
    /// <summary>Adds a point after another one, or at the end. Returns the index of its move.</summary>
    public static int AddPoint(List<MacroAction> steps, RoutePoint? after, MacroAction move, IReadOnlyList<MacroAction> then)
    {
        int at = after?.End ?? steps.Count;
        Insert(steps, at, [move, .. then]);
        return at;
    }
    /// <summary>Inserts steps at a point, before the wait it ends with, or before the first point when there is none.</summary>
    public static int AddTo(List<MacroAction> steps, RoutePoint? point, IReadOnlyList<MacroAction> added)
    {
        int at = point == null ? Opening(steps).End : TrailingWait(steps, point) is >= 0 and int wait ? wait : point.End;
        Insert(steps, at, added);
        return at;
    }
    public static void Insert(List<MacroAction> steps, int at, IReadOnlyList<MacroAction> added)
    {
        if (steps.Count + added.Count > ProfileValidator.MaxActions) throw new ArgumentException($"A sequence can contain up to {ProfileValidator.MaxActions:N0} steps.");
        foreach (var a in added) ProfileValidator.ValidateAction(a);
        steps.InsertRange(at, added);
    }
    /// <summary>A press, a wait and a release.</summary>
    public static MacroAction[] Hold(string button, int ms = DefaultHoldMs) =>
    [
        new() { ActionType = MacroActionType.MouseDown, MouseButton = button },
        new() { ActionType = MacroActionType.Delay, DelayMs = ms },
        new() { ActionType = MacroActionType.MouseUp, MouseButton = button }
    ];
    public static MacroAction[] HoldKey(string key, int ms = DefaultHoldMs) =>
    [
        new() { ActionType = MacroActionType.KeyDown, KeyName = key },
        new() { ActionType = MacroActionType.Delay, DelayMs = ms },
        new() { ActionType = MacroActionType.KeyUp, KeyName = key }
    ];

    /// <summary>Puts points that sit side by side in one group. They must share the same parent group.</summary>
    public static int Group(List<MacroAction> steps, IReadOnlyCollection<RoutePoint> points, string name)
    {
        if (points.Count == 0) throw new ArgumentException("Pick the points to group first.");
        if (points.Select(p => p.Group).Distinct().Count() > 1) throw new ArgumentException("Points in different groups can't be grouped together.");
        int start = points.Min(p => p.Index), end = points.Max(p => p.End);
        int depth = 0;
        for (int i = start; i < end; i++)
        {
            if (steps[i].ActionType == MacroActionType.GroupStart) depth++;
            else if (steps[i].ActionType == MacroActionType.GroupEnd && --depth < 0) break;
        }
        if (depth != 0) throw new ArgumentException("Those points cross a group edge. Pick points from the same group.");
        var grouped = steps.ToList();
        grouped.Insert(end, new MacroAction { ActionType = MacroActionType.GroupEnd });
        grouped.Insert(start, new MacroAction { ActionType = MacroActionType.GroupStart, Name = name, Repeat = 1 });
        StepGroups.Validate(grouped);
        steps.Clear(); steps.AddRange(grouped);
        return start;
    }
    public static void Ungroup(List<MacroAction> steps, int groupStart)
    {
        int end = StepGroups.EndOf(steps, groupStart);
        if (end < 0) throw new ArgumentException("That group has no end.");
        steps.RemoveAt(end); steps.RemoveAt(groupStart);
    }
}

/// <summary>Converts between screen pixels and the space a position is stored in, the reverse of <see cref="PointerResolver"/>.</summary>
public sealed class RouteSpace(IWindowSystem windows, IntPtr window, MacroProfile profile)
{
    private readonly PointerResolver _resolver = new(windows, window, profile);
    private readonly (int X, int Y) _start = windows.Cursor();
    public bool HasWindow => window != IntPtr.Zero;
    /// <summary>The target window, for finding targets that look inside it.</summary>
    public IntPtr Window => window;
    public IWindowSystem Windows => windows;
    public (int X, int Y) ToScreen(MacroAction step) => step.Space == CoordinateSpace.Start
        ? (_start.X + step.X, _start.Y + step.Y) : _resolver.Resolve(step.Space, step.X, step.Y);
    public (int X, int Y) FromScreen(CoordinateSpace space, int x, int y)
    {
        switch (space)
        {
            case CoordinateSpace.Start: return (x - _start.X, y - _start.Y);
            case CoordinateSpace.Window when window != IntPtr.Zero:
                var origin = windows.ClientToScreen(window, 0, 0);
                double cx = x - origin.X, cy = y - origin.Y;
                if (profile.ScaleWithWindow && profile.Window is { ClientWidth: > 0, ClientHeight: > 0 } t)
                {
                    var (width, height) = windows.ClientSize(window);
                    if (width > 0 && height > 0) { cx = cx * t.ClientWidth / width; cy = cy * t.ClientHeight / height; }
                }
                return ((int)Math.Round(cx), (int)Math.Round(cy));
            default: return (x, y);
        }
    }
    /// <summary>New points go in the target window when they land inside it, otherwise on the screen.</summary>
    public CoordinateSpace SpaceAt(int x, int y)
    {
        if (window == IntPtr.Zero) return CoordinateSpace.Screen;
        var origin = windows.ClientToScreen(window, 0, 0);
        var (width, height) = windows.ClientSize(window);
        return x >= origin.X && y >= origin.Y && x < origin.X + width && y < origin.Y + height ? CoordinateSpace.Window : CoordinateSpace.Screen;
    }
}
