using DataUsage.App.Theme;
using DataUsage.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace DataUsage.App.Charts;

/// <summary>
/// Daily total: a smooth area over every day in the range, broken where a day
/// was never collected. A day well above trend turns the warning colour in its
/// tooltip; the card's subtitle counts them.
/// </summary>
/// <remarks>
/// A spike is more than 2.5x the mean of ACTIVE days. Red is reserved for
/// genuine anomalies, so the threshold is high enough that a busy day does not
/// trip it; and a mean over days the laptop was off would turn ordinary days
/// into "anomalies".
/// </remarks>
public sealed class TrendChart : ChartSurface
{
    private const double Top = 8, Right = 8, Bottom = 4, Left = 4, YAxisWidth = 64, XAxisHeight = 28;

    private readonly IReadOnlyList<DailyPoint> _days;
    private readonly double _spikeAt;
    private readonly List<Point?> _points = [];
    private Line? _cursor;
    private Ellipse? _activeDot;

    public TrendChart(IReadOnlyList<DailyPoint> daily, double meanActive, double height = 300, bool animate = true) : base(height, animate)
    {
        _days = daily;
        _spikeAt = meanActive * 2.5;
    }

    private bool Spike(DailyPoint d) => _spikeAt > 0 && d.Total > _spikeAt;

    protected override void Draw(bool animate)
    {
        _points.Clear();
        var plotLeft = Left + YAxisWidth;
        var plotRight = W - Right;
        var plotBottom = H - Bottom - XAxisHeight;
        var plotW = Math.Max(1, plotRight - plotLeft);

        var max = _days.Count == 0 ? 0 : _days.Max(d => d.Total ?? 0);
        var top = Axes.YGrid(Canvas, max, plotLeft, plotRight, Top, plotBottom);
        double Y(double v) => plotBottom - (top > 0 ? v / top : 0) * (plotBottom - Top);

        var n = _days.Count;
        var xs = Enumerable.Range(0, n).Select(i => n == 1 ? plotLeft + plotW / 2 : plotLeft + i * plotW / (n - 1)).ToList();
        for (var i = 0; i < n; i++) _points.Add(_days[i].Total is { } t ? new Point(xs[i], Y(t)) : null);
        Axes.DateLabels(Canvas, _days.Select(d => d.Date).ToList(), xs, plotBottom + 6 + 11, W);

        var layer = new Microsoft.UI.Xaml.Controls.Canvas();
        var gradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop { Color = Palette.WithAlpha(Palette.Accent, 0.55), Offset = 0 },
                new GradientStop { Color = Palette.WithAlpha(Palette.Accent, 0.02), Offset = 1 },
            },
        };
        foreach (var run in Axes.Runs(_points.Select(p => p is not null).ToList()))
        {
            var pts = run.Select(i => _points[i]!.Value).ToList();
            if (pts.Count == 1)
            {
                layer.Children.Add(Dot(pts[0], 2.5, Palette.AccentBrush));
                continue;
            }
            var fill = ChartKit.MonotoneFigure(pts);
            fill.Segments.Add(new LineSegment { Point = new Point(pts[^1].X, plotBottom) });
            fill.Segments.Add(new LineSegment { Point = new Point(pts[0].X, plotBottom) });
            fill.IsClosed = true;
            layer.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = new PathGeometry { Figures = { fill } }, Fill = gradient });
            layer.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = new PathGeometry { Figures = { ChartKit.MonotoneFigure(pts) } },
                Stroke = Palette.AccentBrush,
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round,
            });
        }
        Canvas.Children.Add(layer);
        if (animate) Axes.RevealLeft(layer, W);

        _cursor = new Line { Stroke = Palette.AccentBrush, StrokeThickness = 1, StrokeDashArray = [4, 4], Visibility = Visibility.Collapsed, Y1 = Top, Y2 = plotBottom };
        _activeDot = Dot(new Point(0, 0), 5, Palette.AccentBrightBrush);
        _activeDot.Visibility = Visibility.Collapsed;
        Canvas.Children.Add(_cursor);
        Canvas.Children.Add(_activeDot);
    }

    private static Ellipse Dot(Point at, double r, Brush fill)
    {
        var dot = new Ellipse { Width = r * 2, Height = r * 2, Fill = fill, Stroke = Palette.BgBrush, StrokeThickness = 2 };
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(dot, at.X - r);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(dot, at.Y - r);
        return dot;
    }

    protected override void OnHover(Point at)
    {
        if (_days.Count == 0 || _cursor is null || _activeDot is null) return;
        var n = _days.Count;
        var plotLeft = Left + YAxisWidth;
        var plotW = Math.Max(1, W - Right - plotLeft);
        var index = n == 1 ? 0 : (int)Math.Clamp(Math.Round((at.X - plotLeft) / plotW * (n - 1)), 0, n - 1);
        var x = n == 1 ? plotLeft + plotW / 2 : plotLeft + index * plotW / (n - 1);
        _cursor.X1 = _cursor.X2 = x;
        _cursor.Visibility = Visibility.Visible;

        var day = _days[index];
        var body = ChartTooltip.Stack();
        body.Children.Add(ChartTooltip.Title(Format.DayShort(day.Date)));
        if (_points[index] is { } p)
        {
            Microsoft.UI.Xaml.Controls.Canvas.SetLeft(_activeDot, p.X - 5);
            Microsoft.UI.Xaml.Controls.Canvas.SetTop(_activeDot, p.Y - 5);
            _activeDot.Visibility = Visibility.Visible;
            var spike = Spike(day);
            var big = new Microsoft.UI.Xaml.Controls.StackPanel { Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal, Spacing = 6 };
            big.Children.Add(Ui.Text(Format.Bytes(day.Total!.Value), 17, 650, spike ? Palette.WarnBrush : Palette.AccentBrightBrush, numeric: true));
            if (spike)
            {
                var note = Ui.Text("above trend", 12, 500, Palette.WarnBrush);
                note.VerticalAlignment = VerticalAlignment.Bottom;
                note.Margin = new Thickness(0, 0, 0, 2);
                big.Children.Add(note);
            }
            body.Children.Add(big);
            body.Children.Add(ChartTooltip.Muted($"↑ {Format.Bytes(day.Sent ?? 0)}   ↓ {Format.Bytes(day.Received ?? 0)}", 4));
            Tooltip.SetWarn(spike);
        }
        else
        {
            _activeDot.Visibility = Visibility.Collapsed;
            body.Children.Add(Axes.NoData());
            Tooltip.SetWarn(false);
        }
        Tooltip.Show(body, at);
    }

    protected override void ClearHover()
    {
        base.ClearHover();
        if (_cursor is not null) _cursor.Visibility = Visibility.Collapsed;
        if (_activeDot is not null) _activeDot.Visibility = Visibility.Collapsed;
    }
}
