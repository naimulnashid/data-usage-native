using DataUsage.App.Theme;
using DataUsage.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace DataUsage.App.Charts;

/// <summary>One column: its label, and download/upload (null = never collected).</summary>
public sealed record DownUpBar(string Key, string Label, string Title, long? Received, long? Sent)
{
    public long Total => (Received ?? 0) + (Sent ?? 0);
}

/// <summary>
/// Columns stacked download then upload, in two shades of the one accent -
/// the hour-of-day chart and an app's daily chart.
/// </summary>
/// <remarks>
/// Download sits at the base in the darker shade: on this machine it is the
/// larger half almost everywhere, so the heavier colour carries the heavier
/// quantity. With <c>dimAllButPeak</c> the busiest column keeps full strength
/// and the rest are dimmed, so it still reads at a glance.
/// </remarks>
public sealed class DownUpBarChart : ChartSurface
{
    private const double Top = 8, Right = 8, Bottom = 4, Left = 4, YAxisWidth = 64, XAxisHeight = 28;

    private readonly IReadOnlyList<DownUpBar> _bars;
    private readonly bool _dimAllButPeak;
    private readonly bool _labelEvery;
    private readonly double _radius;
    private readonly List<(double X, double Width)> _bands = [];
    private Rectangle? _wash;
    private double _plotBottom;

    public DownUpBarChart(IReadOnlyList<DownUpBar> bars, bool dimAllButPeak, bool labelEvery, double radius, double height, bool animate = true) : base(height, animate)
    {
        _bars = bars;
        _dimAllButPeak = dimAllButPeak;
        _labelEvery = labelEvery;
        _radius = radius;
    }

    /// <summary>Hour of day: every hour labelled, the busiest at full strength.</summary>
    public static DownUpBarChart Hourly(IReadOnlyList<HourPoint> hours, double height = 220) =>
        new(hours.Select(h => new DownUpBar(h.Hour.ToString("00"), h.Hour.ToString("00"), $"{h.Hour:00}:00 - {h.Hour:00}:59", h.Received, h.Sent)).ToList(), true, true, 5, height);

    /// <summary>One app's days; a day never collected is an empty column.</summary>
    public static DownUpBarChart Daily(IReadOnlyList<DailyPoint> days, double height = 300) =>
        new(days.Select(d => new DownUpBar(d.Date, d.Date, Format.DayShort(d.Date), d.Received, d.Sent)).ToList(), false, false, 4, height);

    protected override void Draw(bool animate)
    {
        _bands.Clear();
        var plotLeft = Left + YAxisWidth;
        var plotRight = W - Right;
        _plotBottom = H - Bottom - XAxisHeight;
        var plotW = Math.Max(1, plotRight - plotLeft);

        var peak = _bars.Count == 0 ? 0 : _bars.Max(b => b.Total);
        var top = Axes.YGrid(Canvas, peak, plotLeft, plotRight, Top, _plotBottom);
        double Y(double v) => _plotBottom - (top > 0 ? v / top : 0) * (_plotBottom - Top);

        _wash = new Rectangle { Fill = Palette.HoverWashBrush, Visibility = Visibility.Collapsed, Height = _plotBottom - Top };
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(_wash, Top);
        Canvas.Children.Add(_wash);

        var n = _bars.Count;
        var band = plotW / Math.Max(1, n);
        var gap = band * 0.1;
        var barW = Math.Max(1, band - 2 * gap);
        var layer = new Microsoft.UI.Xaml.Controls.Canvas();
        var centres = new List<double>();
        for (var i = 0; i < n; i++)
        {
            var x = plotLeft + i * band;
            _bands.Add((x, band));
            centres.Add(x + band / 2);
            var b = _bars[i];
            var dim = _dimAllButPeak && b.Total != peak;
            var down = b.Received ?? 0;
            var up = b.Sent ?? 0;
            if (down > 0)
            {
                var rect = new Rectangle { Width = barW, Height = Math.Max(0, Y(0) - Y(down)), Fill = Palette.DownBrush, Opacity = dim ? 0.62 : 1 };
                Microsoft.UI.Xaml.Controls.Canvas.SetLeft(rect, x + gap);
                Microsoft.UI.Xaml.Controls.Canvas.SetTop(rect, Y(down));
                layer.Children.Add(rect);
            }
            if (up > 0 || down > 0)
            {
                // The radius goes on the segment that ends the column.
                var y0 = Y(down);
                var y1 = Y(down + up);
                if (up > 0)
                {
                    layer.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = Axes.RoundedTop(new Rect(x + gap, y1, barW, Math.Max(0.5, y0 - y1)), _radius), Fill = Palette.UpBrush, Opacity = dim ? 0.62 : 1 });
                }
            }
        }
        Canvas.Children.Add(layer);

        if (_labelEvery)
        {
            for (var i = 0; i < n; i++) ChartKit.Label(Canvas, _bars[i].Label, centres[i], _plotBottom + 6 + 11, 0);
        }
        else
        {
            Axes.DateLabels(Canvas, _bars.Select(b => b.Label).ToList(), centres, _plotBottom + 6 + 11, W);
        }

        if (animate) Axes.GrowUp(layer, _plotBottom);
    }

    protected override void OnHover(Point at)
    {
        if (_wash is null) return;
        var index = _bands.FindIndex(b => at.X >= b.X && at.X < b.X + b.Width);
        if (index < 0 || at.Y > _plotBottom)
        {
            ClearHover();
            return;
        }
        _wash.Width = _bands[index].Width;
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(_wash, _bands[index].X);
        _wash.Visibility = Visibility.Visible;

        var bar = _bars[index];
        var body = ChartTooltip.Stack();
        body.Children.Add(ChartTooltip.Title(bar.Title));
        if (bar.Received is null && bar.Sent is null)
        {
            body.Children.Add(Axes.NoData());
        }
        else
        {
            body.Children.Add(Ui.Text(Format.Bytes(bar.Total), 17, 650, Palette.AccentBrightBrush, numeric: true));
            var rows = new Microsoft.UI.Xaml.Controls.StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            rows.Children.Add(ChartTooltip.Row(Palette.Down, "Down", Format.Bytes(bar.Received ?? 0)));
            rows.Children.Add(ChartTooltip.Row(Palette.Up, "Up", Format.Bytes(bar.Sent ?? 0)));
            body.Children.Add(rows);
        }
        Tooltip.Show(body, at);
    }

    protected override void ClearHover()
    {
        base.ClearHover();
        if (_wash is not null) _wash.Visibility = Visibility.Collapsed;
    }
}
