using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Keyloom.Engine;
using Keyloom.Models;

namespace Keyloom.Views;

/// <summary>
/// One run of a sequence on a single line: a mark for every input at the time it happens, coloured like its step,
/// waits as the space between them. Shows the picked step and, in the preview, a playhead. Hover names the step; click picks it.
/// Marks that land on the same pixel are drawn once, so long recordings stay cheap.
/// </summary>
public sealed class RunStrip : FrameworkElement
{
    private const double Pad = 5;
    private IReadOnlyList<MacroAction> _played = [];
    private int[] _source = [];
    private double[] _times = [];
    private double _duration;
    private int _picked = -1;
    private double? _playhead;
    private readonly ToolTip _tip = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.Relative };
    /// <summary>Raised with the index, in the editor's step list, of the step under a click.</summary>
    public event Action<int>? StepPicked;
    public RunStrip()
    {
        // The tip follows the mark under the pointer, so it is opened by hand rather than by the tooltip service.
        Cursor = Cursors.Hand;
        Loaded += (_, _) => Services.ThemeManager.Changed += InvalidateVisual;
        Unloaded += (_, _) => Services.ThemeManager.Changed -= InvalidateVisual;
    }
    public double Duration => _duration;
    /// <summary>
    /// Lays out one run. <paramref name="played"/> is what plays, in order; <paramref name="source"/> gives, for each played step,
    /// its index in the editor's list (-1 for waits that groups add).
    /// </summary>
    public void Show(IReadOnlyList<MacroAction> played, int[] source)
    {
        _played = played; _source = source;
        _times = new double[played.Count];
        double time = 0;
        for (int i = 0; i < played.Count; i++)
        {
            _times[i] = time;
            if (played[i].ActionType == MacroActionType.Delay) time += played[i].DelayMs;
        }
        _duration = time;
        InvalidateVisual();
    }
    /// <summary>The step picked in the editor, as an index into its list; -1 for none.</summary>
    public int Picked { get => _picked; set { if (_picked != value) { _picked = value; InvalidateVisual(); } } }
    /// <summary>Where playback is, in milliseconds; null hides the playhead.</summary>
    public double? Playhead { get => _playhead; set { _playhead = value; InvalidateVisual(); } }

    /// <summary>With no waits at all, every input happens at once; marks are then spread out by order instead.</summary>
    private bool ByOrder => _duration <= 0;
    private double X(int i)
    {
        double width = Math.Max(1, ActualWidth - 2 * Pad);
        double at = ByOrder ? (_played.Count <= 1 ? 0 : (double)i / (_played.Count - 1)) : _times[i] / _duration;
        return Pad + at * width;
    }
    private double XAt(double time) => Pad + (ByOrder ? 0 : time / _duration) * Math.Max(1, ActualWidth - 2 * Pad);
    private Brush Tone(MacroAction a) => (Brush)(TryFindResource(a.ActionType switch
    {
        MacroActionType.MouseClick => "StepPointerBrush",
        MacroActionType.KeyPress => "StepKeyBrush",
        MacroActionType.MouseDown or MacroActionType.MouseUp or MacroActionType.KeyDown or MacroActionType.KeyUp => "StepHoldBrush",
        MacroActionType.MouseMove or MacroActionType.MousePosition or MacroActionType.MouseScroll => "StepMoveBrush",
        MacroActionType.Text => "StepTextBrush",
        _ => "MutedBrush"
    }) ?? Brushes.Gray);
    protected override void OnRender(DrawingContext dc)
    {
        double h = ActualHeight, mid = Math.Round(h / 2);
        var track = TryFindResource("DividerBrush") as Brush ?? Brushes.LightGray;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, h));
        dc.DrawRoundedRectangle(track, null, new Rect(Pad, mid - 1.5, Math.Max(0, ActualWidth - 2 * Pad), 3), 1.5, 1.5);
        if (_played.Count == 0) return;
        // Waits are the gaps; inputs are marks. Moves are short, clicks and keys full height.
        var drawn = new HashSet<(int, string)>();
        for (int i = 0; i < _played.Count; i++)
        {
            var a = _played[i];
            if (a.ActionType == MacroActionType.Delay) continue;
            double x = Math.Round(X(i)) + .5;
            if (!drawn.Add(((int)x, a.ActionType.ToString()))) continue;
            bool move = a.ActionType is MacroActionType.MouseMove or MacroActionType.MousePosition;
            double half = move ? 4 : 7;
            dc.DrawLine(new Pen(Tone(a), move ? 1.5 : 2), new Point(x, mid - half), new Point(x, mid + half));
        }
        if (_picked >= 0 && Array.IndexOf(_source, _picked) is var first and >= 0)
        {
            var accent = TryFindResource("AccentBrush") as Brush ?? Brushes.SteelBlue;
            double x = Math.Round(X(first)) + .5;
            dc.DrawEllipse(accent, new Pen(TryFindResource("SheetBrush") as Brush ?? Brushes.White, 1.5), new Point(x, mid), 4.5, 4.5);
        }
        if (_playhead is { } t)
        {
            var ink = TryFindResource("InkBrush") as Brush ?? Brushes.Black;
            double x = Math.Round(XAt(Math.Clamp(t, 0, _duration))) + .5;
            dc.DrawLine(new Pen(ink, 1.5), new Point(x, 1), new Point(x, h - 1));
        }
    }
    /// <summary>The played step nearest the pointer.</summary>
    private int Nearest(double x)
    {
        int best = -1; double distance = double.MaxValue;
        for (int i = 0; i < _played.Count; i++)
        {
            if (_played[i].ActionType == MacroActionType.Delay && _played.Count > 1) continue;
            double d = Math.Abs(X(i) - x);
            if (d < distance) { distance = d; best = i; }
        }
        return distance <= 12 ? best : -1;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var at = e.GetPosition(this);
        int i = Nearest(at.X);
        if (i < 0) { _tip.IsOpen = false; return; }
        string when = StepLayoutConverter.FormatTime(_times[i]);
        _tip.Content = $"{when} · {_played[i].DisplayText}";
        _tip.PlacementTarget = this;
        _tip.HorizontalOffset = X(i) - 12; _tip.VerticalOffset = ActualHeight + 2;
        _tip.IsOpen = true;
    }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); _tip.IsOpen = false; }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        int i = Nearest(e.GetPosition(this).X);
        if (i >= 0 && _source[i] >= 0) StepPicked?.Invoke(_source[i]);
    }
}
