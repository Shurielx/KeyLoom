using System.Windows;
using System.Windows.Media;
using Keyloom.Engine;

namespace Keyloom.Views;

/// <summary>
/// Draws a macro route as one frozen geometry plus input markers, and a single moving marker on a separate layer.
/// Changing <see cref="Time"/> redraws only the marker, so playback cost does not grow with the macro length.
/// </summary>
public sealed class PathPreview : FrameworkElement
{
    private static readonly Color AccentColor = Color.FromRgb(15, 108, 189);
    private static readonly Brush Accent = Frozen(new SolidColorBrush(AccentColor));
    private static readonly Brush ClickFill = Frozen(new SolidColorBrush(Color.FromRgb(210, 65, 47)));
    private static readonly Brush StartFill = Frozen(new SolidColorBrush(Color.FromRgb(46, 158, 107)));
    private static readonly Pen RoutePen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 15, 108, 189))), 1.6) { LineJoin = PenLineJoin.Round });
    private static readonly Pen LightHalo = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x90, 255, 255, 255))), 4) { LineJoin = PenLineJoin.Round });
    private static readonly Pen DarkHalo = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x70, 10, 14, 20))), 4) { LineJoin = PenLineJoin.Round });
    private static readonly Pen DarkRoutePen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0xD0, 90, 176, 247))), 1.6) { LineJoin = PenLineJoin.Round });
    private static readonly Pen WhitePen = Frozen(new Pen(Brushes.White, 1.5));
    private static readonly Pen HoldPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(210, 65, 47))), 1.5));
    private readonly VisualCollection _layers;
    private readonly DrawingVisual _route = new(), _marker = new();
    private MacroTimeline? _timeline;
    private Func<double, double, Point>? _map;
    private double _time;
    public PathPreview()
    {
        _layers = new(this) { _route, _marker };
        // The route is drawn into a texture once, so playback only composes it instead of re-tessellating every frame.
        _route.CacheMode = new BitmapCache();
        Loaded += (_, _) => Services.ThemeManager.Changed += Redraw;
        Unloaded += (_, _) => Services.ThemeManager.Changed -= Redraw;
    }
    /// <summary>Maps screen pixels to local DIPs. Null fits the route into the control.</summary>
    public Func<double, double, Point>? Map { get => _map; set { _map = value; Redraw(); } }
    public MacroTimeline? Timeline { get => _timeline; set { _timeline = value; Redraw(); } }
    public bool ShowMarker { get; set; } = true;
    public double Time
    {
        get => _time;
        set { _time = value; DrawMarker(); }
    }
    protected override int VisualChildrenCount => _layers.Count;
    protected override Visual GetVisualChild(int index) => _layers[index];
    protected override void OnRenderSizeChanged(SizeChangedInfo info) { base.OnRenderSizeChanged(info); Redraw(); }
    public void Redraw() { DrawRoute(); DrawMarker(); }
    private Point Project(double x, double y)
    {
        if (_map != null) return _map(x, y);
        var (left, top, right, bottom) = _timeline!.Bounds;
        const double pad = 12;
        double width = Math.Max(1, ActualWidth - 2 * pad), height = Math.Max(1, ActualHeight - 2 * pad);
        double spanX = right - left, spanY = bottom - top;
        double scale = Math.Min(spanX > 0 ? width / spanX : double.PositiveInfinity, spanY > 0 ? height / spanY : double.PositiveInfinity);
        if (double.IsInfinity(scale)) scale = 1;
        double offsetX = pad + (width - spanX * scale) / 2, offsetY = pad + (height - spanY * scale) / 2;
        return new(offsetX + (x - left) * scale, offsetY + (y - top) * scale);
    }
    private void DrawRoute()
    {
        using var dc = _route.RenderOpen();
        if (_timeline is not { HasPointer: true } t || ActualWidth <= 0) return;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            var last = Project(t.Points[0].X, t.Points[0].Y);
            g.BeginFigure(last, false, false);
            for (int i = 1; i < t.Points.Count; i++)
            {
                // Segments shorter than a pixel add cost without adding anything visible.
                var next = Project(t.Points[i].X, t.Points[i].Y);
                if (i < t.Points.Count - 1 && Math.Abs(next.X - last.X) < .75 && Math.Abs(next.Y - last.Y) < .75) continue;
                g.LineTo(next, true, true); last = next;
            }
        }
        geometry.Freeze();
        bool dark = Services.ThemeManager.IsDark;
        dc.DrawGeometry(null, dark ? DarkHalo : LightHalo, geometry);
        dc.DrawGeometry(null, dark ? DarkRoutePen : RoutePen, geometry);
        // Input markers share frozen geometries per kind instead of thousands of elements.
        var clicks = new GeometryGroup(); var holds = new GeometryGroup();
        var drawn = new HashSet<(int, int, TimelineEventKind)>();
        foreach (var e in t.Events)
        {
            if (e.Kind is TimelineEventKind.Key or TimelineEventKind.Text) continue;
            var p = Project(e.X, e.Y);
            if (!drawn.Add(((int)p.X, (int)p.Y, e.Kind))) continue;
            if (e.Kind == TimelineEventKind.Click) clicks.Children.Add(new EllipseGeometry(p, 4, 4));
            else if (e.Kind == TimelineEventKind.Down) clicks.Children.Add(new EllipseGeometry(p, 3.5, 3.5));
            else holds.Children.Add(new EllipseGeometry(p, 6, 6));
        }
        clicks.Freeze(); holds.Freeze();
        dc.DrawGeometry(null, HoldPen, holds);
        dc.DrawGeometry(ClickFill, WhitePen, clicks);
        dc.DrawEllipse(StartFill, WhitePen, Project(t.Points[0].X, t.Points[0].Y), 4.5, 4.5);
    }
    private void DrawMarker()
    {
        using var dc = _marker.RenderOpen();
        if (!ShowMarker || _timeline is not { HasPointer: true } t || ActualWidth <= 0 || t.PositionAt(_time) is not { } at) return;
        var p = Project(at.X, at.Y);
        foreach (var e in t.EventsBetween(_time - 350, _time))
        {
            if (e.Kind is TimelineEventKind.Key or TimelineEventKind.Text) continue;
            double age = (_time - e.Time) / 350;
            var ring = new Pen(new SolidColorBrush(Color.FromArgb((byte)(200 * (1 - age)), 210, 65, 47)), 2);
            dc.DrawEllipse(null, ring, Project(e.X, e.Y), 6 + 16 * age, 6 + 16 * age);
        }
        if (t.DelayAt(_time) is { } delay)
        {
            // Remaining wait shown as a shrinking arc around the marker.
            dc.DrawEllipse(null, new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x40, 15, 108, 189))), 3), p, 13, 13);
            dc.DrawGeometry(null, new Pen(Accent, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Arc(p, 13, 1 - delay.Progress));
        }
        dc.DrawEllipse(Frozen(new SolidColorBrush(Color.FromArgb(0x33, 15, 108, 189))), null, p, 10, 10);
        dc.DrawEllipse(Accent, WhitePen, p, 6, 6);
    }
    private static Geometry Arc(Point center, double radius, double fraction)
    {
        fraction = Math.Clamp(fraction, 0.001, 0.999);
        double angle = fraction * 2 * Math.PI;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(start, false, false);
            c.ArcTo(end, new Size(radius, radius), 0, fraction > .5, SweepDirection.Clockwise, true, false);
        }
        g.Freeze(); return g;
    }
    private static T Frozen<T>(T value) where T : Freezable { value.Freeze(); return value; }
}
