using DataUsage.App.Theme;
using DataUsage.Core.Naming;
using DataUsage.Core.Query;
using DataUsage.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace DataUsage.App.Charts;

/// <summary>
/// The largest apps as horizontal bars, download then upload STACKED, upload a
/// tint of the app's own colour (<see cref="AppColors.UploadTint"/>).
/// </summary>
/// <remarks>
/// Stacked rather than grouped: grouped doubled the height and made comparing
/// ten apps by size harder, which is the one thing a Top 10 is for. The full
/// bar is still the total; what stacking adds is the sent/received ratio -
/// a torrent client and a browser have visibly different shapes. Download
/// comes first to match every other down-then-up order, not because it is the
/// larger half: Google Drive is mostly upload, so its brand colour can be a
/// sliver of its own bar, and that is the chart working.
/// </remarks>
public sealed class TopAppsChart : ChartSurface
{
    private const double Top = 4, Right = 20, Bottom = 4, Left = 4, CategoryWidth = 140, XAxisHeight = 28, BarSize = 30;

    private readonly IReadOnlyList<AppRow> _apps;
    private readonly IReadOnlyDictionary<string, string> _colors;
    private readonly List<(double Y, double H)> _bands = [];
    private Rectangle? _wash;
    private double _plotLeft;

    public TopAppsChart(IReadOnlyList<AppRow> apps, IReadOnlyDictionary<string, string> colors, bool animate = true)
        : base(Math.Max(260, apps.Count * 44) + 32, animate)
    {
        _apps = apps;
        _colors = colors;
    }

    protected override void Draw(bool animate)
    {
        _bands.Clear();
        _plotLeft = Left + CategoryWidth;
        var plotRight = W - Right;
        var plotBottom = H - Bottom - XAxisHeight;
        var plotW = Math.Max(1, plotRight - _plotLeft);

        var ticks = ChartKit.NiceTicks(_apps.Count == 0 ? 0 : _apps.Max(a => a.Total));
        var top = ticks[^1];
        double X(double v) => _plotLeft + (top > 0 ? v / top : 0) * plotW;
        foreach (var tick in ticks)
        {
            Canvas.Children.Add(ChartKit.VLine(X(tick), Top, plotBottom, Palette.GridBrush));
            ChartKit.Label(Canvas, Axes.Tick(tick), X(tick), plotBottom + 6 + 11, 0);
        }

        _wash = new Rectangle { Fill = Palette.HoverWashBrush, Visibility = Visibility.Collapsed, Width = plotW };
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(_wash, _plotLeft);
        Canvas.Children.Add(_wash);

        var band = (plotBottom - Top) / Math.Max(1, _apps.Count);
        var layer = new Microsoft.UI.Xaml.Controls.Canvas();
        for (var i = 0; i < _apps.Count; i++)
        {
            var app = _apps[i];
            var y = Top + i * band;
            _bands.Add((y, band));
            var centre = y + band / 2;
            CategoryLabel(app.Name, centre);

            var color = AppColors.Of(_colors, app.Name);
            var downW = Math.Max(0, X(app.Received) - _plotLeft);
            var upW = Math.Max(0, X(app.Total) - X(app.Received));
            var barH = Math.Min(BarSize, band * 0.8);
            if (downW > 0)
            {
                var rect = new Rectangle { Width = downW, Height = barH, Fill = new SolidColorBrush(Palette.Hex(color)) };
                Microsoft.UI.Xaml.Controls.Canvas.SetLeft(rect, _plotLeft);
                Microsoft.UI.Xaml.Controls.Canvas.SetTop(rect, centre - barH / 2);
                layer.Children.Add(rect);
            }
            if (upW > 0)
                layer.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
                {
                    Data = Axes.RoundedRight(new Rect(_plotLeft + downW, centre - barH / 2, upW, barH), 6),
                    Fill = new SolidColorBrush(Palette.HexOrOther(AppColors.UploadTint(color))),
                });
        }
        Canvas.Children.Add(layer);

        if (animate)
        {
            var scale = new ScaleTransform { ScaleX = 0, CenterX = _plotLeft };
            layer.RenderTransform = scale;
            var story = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            var grow = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(800)), EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut } };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(grow, scale);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(grow, "ScaleX");
            story.Children.Add(grow);
            story.Begin();
        }
    }

    /// <summary>Right-aligned against the bars, wrapping to two lines as Recharts' tick did.</summary>
    private void CategoryLabel(string text, double centre)
    {
        var block = Ui.Text(text, 13, 400, Palette.TextFaintBrush, wrap: true, selectable: false);
        block.TextAlignment = TextAlignment.Right;
        block.MaxLines = 2;
        block.TextTrimming = TextTrimming.CharacterEllipsis;
        block.LineHeight = 14;
        block.Width = CategoryWidth - 12;
        block.Measure(new Size(CategoryWidth - 12, double.PositiveInfinity));
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(block, Left);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(block, centre - block.DesiredSize.Height / 2);
        Canvas.Children.Add(block);
    }

    protected override void OnHover(Point at)
    {
        if (_wash is null) return;
        var index = _bands.FindIndex(b => at.Y >= b.Y && at.Y < b.Y + b.H);
        if (index < 0)
        {
            ClearHover();
            return;
        }
        _wash.Height = _bands[index].H;
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(_wash, _bands[index].Y);
        _wash.Visibility = Visibility.Visible;

        var app = _apps[index];
        var color = Palette.App(_colors, app.Name);
        var body = ChartTooltip.Stack();
        var head = new Microsoft.UI.Xaml.Controls.StackPanel { Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 6) };
        head.Children.Add(Ui.Swatch(color));
        head.Children.Add(Ui.Text(app.Name, 14, 600));
        body.Children.Add(head);
        var big = new Microsoft.UI.Xaml.Controls.StackPanel { Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal, Spacing = 7 };
        big.Children.Add(Ui.Text(Format.Bytes(app.Total), 17, 650, new SolidColorBrush(color), numeric: true));
        var share = Ui.Text($"{Format.Percent(app.Share)} of traffic", 13, 500, Palette.TextMutedBrush);
        share.VerticalAlignment = VerticalAlignment.Bottom;
        share.Margin = new Thickness(0, 0, 0, 2);
        big.Children.Add(share);
        body.Children.Add(big);
        var down = app.Total > 0 ? (double)app.Received / app.Total * 100 : 0;
        body.Children.Add(ChartTooltip.Muted($"↓ {Format.Bytes(app.Received)}   {Format.Percent(down)}"));
        body.Children.Add(ChartTooltip.Muted($"↑ {Format.Bytes(app.Sent)}   {Format.Percent(100 - down)}", 3));
        Tooltip.Show(body, at);
    }

    protected override void ClearHover()
    {
        base.ClearHover();
        if (_wash is not null) _wash.Visibility = Visibility.Collapsed;
    }
}
