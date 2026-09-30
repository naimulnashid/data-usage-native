using DataUsage.App.Theme;
using DataUsage.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace DataUsage.App.Charts;

/// <summary>
/// Daily traffic by app: the top apps stacked, everything else as Other.
/// Every band breaks together on a day never collected.
/// </summary>
public sealed class StackedAreaChart : ChartSurface
{
    private const double Top = 8, Right = 8, Bottom = 4, Left = 4, YAxisWidth = 64, XAxisHeight = 28;

    private readonly IReadOnlyList<(string Date, long?[] Values)> _points;
    private readonly IReadOnlyList<string> _series;
    private readonly IReadOnlyDictionary<string, string> _colors;
    private Line? _cursor;

    public StackedAreaChart(IReadOnlyList<(string Date, long?[] Values)> points, IReadOnlyList<string> series, IReadOnlyDictionary<string, string> colors, double height = 380, bool animate = true)
        : base(height, animate)
    {
        _points = points;
        _series = series;
        _colors = colors;
    }

    protected override void Draw(bool animate)
    {
        var plotLeft = Left + YAxisWidth;
        var plotRight = W - Right;
        var plotBottom = H - Bottom - XAxisHeight;
        var plotW = Math.Max(1, plotRight - plotLeft);

        var max = _points.Count == 0 ? 0 : _points.Max(p => p.Values.Sum(v => v ?? 0));
        var top = Axes.YGrid(Canvas, max, plotLeft, plotRight, Top, plotBottom);
        double Y(double v) => plotBottom - (top > 0 ? v / top : 0) * (plotBottom - Top);

        var n = _points.Count;
        var xs = Enumerable.Range(0, n).Select(i => n == 1 ? plotLeft + plotW / 2 : plotLeft + i * plotW / (n - 1)).ToList();
        Axes.DateLabels(Canvas, _points.Select(p => p.Date).ToList(), xs, plotBottom + 6 + 11, W);

        var present = _points.Select(p => p.Values.Any(v => v is not null)).ToList();
        var layer = new Microsoft.UI.Xaml.Controls.Canvas();
        foreach (var run in Axes.Runs(present))
        {
            var below = run.Select(i => 0.0).ToArray();
            for (var s = 0; s < _series.Count; s++)
            {
                var lower = run.Select((i, k) => new Point(xs[i], Y(below[k]))).ToList();
                for (var k = 0; k < run.Count; k++) below[k] += _points[run[k]].Values[s] ?? 0;
                var upper = run.Select((i, k) => new Point(xs[i], Y(below[k]))).ToList();
                if (upper.Count < 2) continue;
                var color = Palette.App(_colors, _series[s]);

                // The band: along the top edge, then back along the bottom.
                var figure = ChartKit.MonotoneFigure(upper);
                var back = ChartKit.MonotoneFigure(lower.AsEnumerable().Reverse().ToList());
                figure.Segments.Add(new LineSegment { Point = lower[^1] });
                foreach (var seg in back.Segments.ToList())
                {
                    back.Segments.Remove(seg);
                    figure.Segments.Add(seg);
                }
                figure.IsClosed = true;
                layer.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
                {
                    Data = new PathGeometry { Figures = { figure } },
                    Fill = new SolidColorBrush(Palette.WithAlpha(color, 0.72)),
                });
                layer.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
                {
                    Data = new PathGeometry { Figures = { ChartKit.MonotoneFigure(upper) } },
                    Stroke = new SolidColorBrush(color),
                    StrokeThickness = 1,
                });
            }
        }
        Canvas.Children.Add(layer);
        if (animate) Axes.RevealLeft(layer, W, 700);

        _cursor = new Line { Stroke = Palette.AccentBrush, StrokeThickness = 1, StrokeDashArray = [4, 4], Visibility = Visibility.Collapsed, Y1 = Top, Y2 = plotBottom };
        Canvas.Children.Add(_cursor);
    }

    protected override void OnHover(Point at)
    {
        if (_points.Count == 0 || _cursor is null) return;
        var n = _points.Count;
        var plotLeft = Left + YAxisWidth;
        var plotW = Math.Max(1, W - Right - plotLeft);
        var index = n == 1 ? 0 : (int)Math.Clamp(Math.Round((at.X - plotLeft) / plotW * (n - 1)), 0, n - 1);
        _cursor.X1 = _cursor.X2 = n == 1 ? plotLeft + plotW / 2 : plotLeft + index * plotW / (n - 1);
        _cursor.Visibility = Visibility.Visible;

        var (date, values) = _points[index];
        var body = ChartTooltip.Stack();
        body.MinWidth = 220;
        body.Children.Add(ChartTooltip.Title(Format.DayShort(date)));
        if (values.All(v => v is null))
        {
            body.Children.Add(Axes.NoData());
        }
        else
        {
            // Largest first, zero-byte series dropped: eight apps at "0 B"
            // bury the one that matters.
            var rows = _series.Select((s, i) => (Name: s, Value: values[i] ?? 0)).Where(r => r.Value > 0).OrderByDescending(r => r.Value).ToList();
            var total = ChartTooltip.Big(Format.Bytes(rows.Sum(r => r.Value)), Palette.AccentBrightBrush);
            total.FontSize = 16;
            total.Margin = new Thickness(0, 0, 0, 6);
            body.Children.Add(total);
            foreach (var (name, value) in rows.Take(9))
                body.Children.Add(ChartTooltip.Row(Palette.App(_colors, name), name, Format.Bytes(value)));
        }
        Tooltip.Show(body, at);
    }

    protected override void ClearHover()
    {
        base.ClearHover();
        if (_cursor is not null) _cursor.Visibility = Visibility.Collapsed;
    }
}
