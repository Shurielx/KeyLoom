using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Keyloom.Engine;
using Keyloom.Models;

namespace Keyloom.Views;

/// <summary>How a sequence step reads in the list: a colour tone, an icon, a short title and the detail that matters.</summary>
public sealed record StepView(string Tone, Geometry? Icon, string Title, string Detail, bool IsWait, bool IsKey, bool IsGroup = false, bool IsGroupEnd = false, bool Collapsed = false)
{
    public static StepView From(MacroAction a)
    {
        string button = a.MouseButton switch { "X1" => L.T("Back button"), "X2" => L.T("Forward button"), "Left" => L.T("Left button"), "Right" => L.T("Right button"), "Middle" => L.T("Middle button"), var b => b };
        return a.ActionType switch
        {
            MacroActionType.MouseClick => new("Pointer", Find("IconMouse"), L.T("Click"), button, false, false),
            MacroActionType.MouseDown => new("Hold", Find("IconHold"), L.T("Hold"), button, false, false),
            MacroActionType.MouseUp => new("Hold", Find("IconRelease"), L.T("Release"), button, false, false),
            MacroActionType.KeyPress => new("Key", Find("IconKeyboard"), L.T("Press"), a.KeyName, false, true),
            MacroActionType.KeyDown => new("Hold", Find("IconHold"), L.T("Hold"), a.KeyName, false, true),
            MacroActionType.KeyUp => new("Hold", Find("IconRelease"), L.T("Release"), a.KeyName, false, true),
            MacroActionType.Delay => new("Wait", Find("IconClock"), L.T("Wait"), $"{a.DelayMs:N0} ms", true, false),
            MacroActionType.MouseMove => new("Move", Find("IconMove"), L.T("Move by"), $"{a.X:+0;−0;0}, {a.Y:+0;−0;0} px", false, false),
            MacroActionType.MousePosition => new("Move", Find("IconTarget"), a.GlideMs > 0 ? L.T("Glide to") : L.T("Cursor to"), a.Space switch
            {
                CoordinateSpace.Window => L.T($"{a.X}, {a.Y} · in window"),
                CoordinateSpace.Start => L.T($"{a.X:+0;−0;0}, {a.Y:+0;−0;0} · from start"),
                _ => L.T($"{a.X}, {a.Y} · on screen")
            } + (a.GlideMs > 0 ? L.T($" · {a.GlideMs:N0} ms") : ""), false, false),
            MacroActionType.MouseScroll => new("Move", Find("IconScroll"), L.T("Scroll"),
                a.ScrollAmount < 0 ? L.N(Math.Abs(a.ScrollAmount), "{0} notch down", "{0} notches down") : L.N(a.ScrollAmount, "{0} notch up", "{0} notches up"), false, false),
            MacroActionType.GroupStart when a.Block == BlockKind.If => new("Group", Find(a.AsksAi ? "IconAi" : "IconTarget"), L.T("If"),
                a.Condition + (a.TimeoutMs > 0 ? L.T($" · looks up to {a.TimeoutMs:N0} ms") : ""), false, false, IsGroup: true, Collapsed: a.Collapsed),
            MacroActionType.GroupStart when a.Block == BlockKind.Else => new("Group", Find("IconGroup"), L.T("Else"), L.T("when the If above does not play"),
                false, false, IsGroup: true, Collapsed: a.Collapsed),
            MacroActionType.GroupStart => new("Group", Find(a.Repeats ? "IconRepeat" : "IconGroup"), a.GroupName, string.Join(" · ", new[]
            {
                a.RepeatText, a.GapMs > 0 ? L.T($"{a.GapMs:N0} ms between steps") : ""
            }.Where(s => s.Length > 0)), false, false, IsGroup: true, Collapsed: a.Collapsed),
            MacroActionType.GroupEnd => new("Group", null, "", L.T("end of group"), false, false, IsGroupEnd: true),
            MacroActionType.ClickTarget => new("Pointer", Find("IconTarget"), L.T("Click on"), string.Join(" · ", new[]
            {
                a.TargetName, a.MouseButton == "Left" ? "" : button, a.TimeoutMs > 0 ? L.T($"looks {a.TimeoutMs:N0} ms") : "", a.StopIfMissing ? L.T("stops if missing") : ""
            }.Where(x => x.Length > 0)), false, false),
            MacroActionType.ClickCell => new("Pointer", Find("IconGrid"), L.T("Click cell"), string.Join(" · ", new[]
            {
                a.PickText, a.GridName, a.MouseButton == "Left" ? "" : button, a.StopIfMissing ? L.T("stops if none") : ""
            }.Where(x => x.Length > 0)), false, false),
            MacroActionType.DragPair => new("Pointer", Find("IconGrid"), L.T("Drag pair"), string.Join(" · ", new[]
            {
                L.T($"two equal numbers in {a.GridName}"), a.GlideMs > 0 ? $"{a.GlideMs:N0} ms" : "", a.StopIfMissing ? L.T("stops if none") : ""
            }.Where(x => x.Length > 0)), false, false),
            MacroActionType.WaitUntil => new("Wait", Find("IconClock"), L.T("Wait until"), string.Join(" · ", new[]
            {
                a.Gone ? L.T($"{a.TargetName} is gone") : L.T($"{a.TargetName} shows"), a.TimeoutMs > 0 ? L.T($"up to {a.TimeoutMs:N0} ms") : "", a.StopIfMissing ? L.T("stops if time is up") : ""
            }.Where(x => x.Length > 0)), false, false),
            _ => new("Text", Find("IconText"), L.T("Type"), $"“{a.Text.Replace("\r", "").Replace("\n", " ⏎ ")}”", false, false)
        };
    }
    private static Geometry? Find(string key) => Application.Current?.TryFindResource(key) as Geometry;
}

/// <summary>Wraps a step in its <see cref="StepView"/>.</summary>
public sealed class StepViewConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is MacroAction a ? StepView.From(a) : null;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public enum StepLayoutPart { Number, Indent, Rail, Visibility, Children, Time }

/// <summary>
/// Where a step sits in the list: its number (group markers have none), how far groups indent it, the group rail beside it,
/// whether a collapsed group hides it, and for a group header how many steps it holds (for a group end, which group it closes).
/// Values: the step, the list, and a revision that changes with every edit so the layout follows moves.
/// </summary>
public sealed class StepLayoutConverter : IMultiValueConverter
{
    public const double IndentStep = 22;
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var part = parameter is StepLayoutPart p ? p : Enum.Parse<StepLayoutPart>((string)parameter);
        if (values.Length < 2 || values[0] is not MacroAction step || values[1] is not IList<MacroAction> list || list.IndexOf(step) is not (>= 0 and var i))
            return part switch { StepLayoutPart.Indent => new Thickness(), StepLayoutPart.Rail => StepRailInfo.None, StepLayoutPart.Visibility => Visibility.Visible, _ => "" };
        var steps = (IReadOnlyList<MacroAction>)list;
        if (part == StepLayoutPart.Time)
            return step.ActionType == MacroActionType.GroupEnd ? "" : FormatTime(TimesFor(steps, values.Length > 2 ? values[2] : null)[i]);
        int indent = StepGroups.DepthAt(steps, i) - (step.ActionType == MacroActionType.GroupEnd ? 1 : 0);
        return part switch
        {
            StepLayoutPart.Number => step.IsGroupMarker ? "" : (steps.Take(i).Count(a => !a.IsGroupMarker) + 1).ToString("N0", culture),
            StepLayoutPart.Indent => new Thickness(Math.Max(0, indent) * IndentStep, 0, 0, 0),
            // A folded group draws no line down to steps that are hidden.
            StepLayoutPart.Rail => new StepRailInfo(Math.Max(0, indent), step is { ActionType: MacroActionType.GroupStart, Collapsed: true } ? MacroActionType.Delay : step.ActionType),
            StepLayoutPart.Visibility => StepGroups.IsHidden(steps, i) ? Visibility.Collapsed : Visibility.Visible,
            _ => step.ActionType switch
            {
                MacroActionType.GroupStart => Children(steps, i),
                MacroActionType.GroupEnd when StepGroups.StartOf(steps, i) is var start and >= 0 => L.T($"end of {steps[start].GroupName}"),
                _ => ""
            }
        };
    }
    /// <summary>Start times are worked out once per list and revision, not once per row.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Tuple<object?, double[]>> Times = new();
    private static double[] TimesFor(IReadOnlyList<MacroAction> steps, object? revision)
    {
        if (Times.TryGetValue(steps, out var cached) && Equals(cached.Item1, revision) && cached.Item2.Length == steps.Count) return cached.Item2;
        var times = StepGroups.StartTimes(steps);
        Times.AddOrUpdate(steps, Tuple.Create(revision, times));
        return times;
    }
    /// <summary>0.25 s, 12.40 s, 1:05.2 for a minute or more.</summary>
    public static string FormatTime(double ms)
    {
        if (ms < 60_000) return (ms / 1000).ToString("0.00", CultureInfo.InvariantCulture) + " s";
        var t = TimeSpan.FromMilliseconds(ms);
        return $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 100}";
    }
    private static string Children(IReadOnlyList<MacroAction> steps, int start)
    {
        int end = StepGroups.EndOf(steps, start);
        int count = (end < 0 ? steps.Skip(start + 1) : steps.Skip(start + 1).Take(end - start - 1)).Count(a => !a.IsGroupMarker);
        return count == 1 ? L.T("1 step") : $"{count:N0} steps";
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed record StepRailInfo(int Indent, MacroActionType Kind)
{
    public static readonly StepRailInfo None = new(0, MacroActionType.Delay);
}

/// <summary>Draws the vertical lines that tie a group's steps to its header, and the corner that closes it.</summary>
public sealed class StepRail : FrameworkElement
{
    public static readonly DependencyProperty InfoProperty = DependencyProperty.Register(nameof(Info), typeof(StepRailInfo), typeof(StepRail),
        new FrameworkPropertyMetadata(StepRailInfo.None, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BrushProperty = DependencyProperty.Register(nameof(Brush), typeof(Brush), typeof(StepRail),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public StepRailInfo Info { get => (StepRailInfo)GetValue(InfoProperty); set => SetValue(InfoProperty, value); }
    public Brush? Brush { get => (Brush?)GetValue(BrushProperty); set => SetValue(BrushProperty, value); }
    /// <summary>Lines sit under the centre of the group header's icon tile.</summary>
    private const double TileCentre = 12, Thickness = 1.5;
    protected override void OnRender(DrawingContext dc)
    {
        if (Brush == null) return;
        var pen = new Pen(Brush, Thickness);
        double h = ActualHeight, mid = Math.Round(h / 2);
        double X(int level) => level * StepLayoutConverter.IndentStep + TileCentre;
        // Groups this row sits inside run straight through it.
        for (int level = 0; level < Info.Indent; level++) dc.DrawLine(pen, new Point(X(level), -1), new Point(X(level), h + 1));
        if (Info.Kind == MacroActionType.GroupStart) dc.DrawLine(pen, new Point(X(Info.Indent), mid + 9), new Point(X(Info.Indent), h + 1));
        else if (Info.Kind == MacroActionType.GroupEnd)
        {
            double x = X(Info.Indent);
            dc.DrawLine(pen, new Point(x, -1), new Point(x, mid));
            dc.DrawLine(pen, new Point(x, mid), new Point(x + 10, mid));
        }
    }
}

/// <summary>Collapses an element while a toggle is on, like the summary under an open More options.</summary>
public sealed class HiddenWhenTrueConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
