using DataUsage.App.Theme;
using DataUsage.Core.View;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace DataUsage.App.Charts;

/// <summary>The axis furniture every cartesian chart here shares.</summary>
public static class Axes
{
    /// <summary>Byte ticks stay narrow: whole units only ("15 GB").</summary>
    public static string Tick(double bytes) => Format.Bytes(bytes, 0);

    /// <summary>
    /// Horizontal grid lines and their labels. Recharts' nice ticks on the raw
    /// byte values, as the web dashboard drew them - so 0 B / 7 GB / 15 GB.
    /// Returns the value mapped to the top of the plot.
    /// </summary>
    public static double YGrid(Canvas canvas, double max, double plotLeft, double plotRight, double plotTop, double plotBottom)
    {
        var ticks = ChartKit.NiceTicks(max);
        var top = ticks[^1];
        foreach (var tick in ticks)
        {
            var y = plotBottom - (top > 0 ? tick / top : 0) * (plotBottom - plotTop);
            canvas.Children.Add(ChartKit.HLine(plotLeft, plotRight, y, Palette.GridBrush));
            ChartKit.Label(canvas, Tick(tick), plotLeft - 8, y, 1);
        }
        return top;
    }

    /// <summary>Date labels under a category axis, thinned so none overlap, the last kept.</summary>
    public static void DateLabels(Canvas canvas, IReadOnlyList<string> dates, IReadOnlyList<double> centres, double y, double width, double gap = 28)
    {
        var labels = dates.Select(Format.DayShort).ToList();
        var widths = labels.Select(l => ChartKit.MeasureText(l)).ToList();
        var keep = ChartKit.ThinLabels(centres, widths, gap, 0, width);
        for (var i = 0; i < labels.Count; i++)
            if (keep.Contains(i)) ChartKit.Label(canvas, labels[i], ChartKit.ClampCentre(centres[i], widths[i], 0, width), y, 0);
    }

    /// <summary>Grows a layer up from the baseline.</summary>
    public static void GrowUp(Microsoft.UI.Xaml.UIElement layer, double baseline, int ms = 800)
    {
        var scale = new ScaleTransform { ScaleY = 0, CenterY = baseline };
        layer.RenderTransform = scale;
        var story = new Storyboard();
        var grow = new DoubleAnimation { From = 0, To = 1, Duration = new Microsoft.UI.Xaml.Duration(TimeSpan.FromMilliseconds(ms)), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(grow, scale);
        Storyboard.SetTargetProperty(grow, "ScaleY");
        story.Children.Add(grow);
        story.Begin();
    }

    /// <summary>Reveals a layer from the left, the way the web's areas drew in.</summary>
    public static void RevealLeft(Microsoft.UI.Xaml.UIElement layer, double width, int ms = 900, int delayMs = 0)
    {
        var transform = new ScaleTransform { ScaleX = 0, CenterX = 0 };
        layer.Clip = new RectangleGeometry { Rect = new Rect(0, -10, width + 20, 10_000), Transform = transform };
        var story = new Storyboard();
        var grow = new DoubleAnimation
        {
            From = 0,
            To = 1,
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            Duration = new Microsoft.UI.Xaml.Duration(TimeSpan.FromMilliseconds(ms)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(grow, transform);
        Storyboard.SetTargetProperty(grow, "ScaleX");
        story.Children.Add(grow);
        story.Begin();
    }

    /// <summary>
    /// Consecutive runs of non-null values. A day never collected is a BREAK:
    /// the line stops, and never slopes across a gap as if it had data.
    /// </summary>
    public static List<List<int>> Runs(IReadOnlyList<bool> present)
    {
        var runs = new List<List<int>>();
        List<int>? current = null;
        for (var i = 0; i < present.Count; i++)
        {
            if (!present[i])
            {
                current = null;
                continue;
            }
            if (current is null) runs.Add(current = []);
            current.Add(i);
        }
        return runs;
    }

    /// <summary>A rectangle with only its top corners rounded.</summary>
    public static Geometry RoundedTop(Rect r, double radius)
    {
        radius = Math.Max(0, Math.Min(radius, Math.Min(r.Width / 2, r.Height)));
        var figure = new PathFigure { StartPoint = new Point(r.Left, r.Bottom), IsClosed = true };
        figure.Segments.Add(new LineSegment { Point = new Point(r.Left, r.Top + radius) });
        figure.Segments.Add(new ArcSegment { Point = new Point(r.Left + radius, r.Top), Size = new Size(radius, radius), SweepDirection = SweepDirection.Clockwise });
        figure.Segments.Add(new LineSegment { Point = new Point(r.Right - radius, r.Top) });
        figure.Segments.Add(new ArcSegment { Point = new Point(r.Right, r.Top + radius), Size = new Size(radius, radius), SweepDirection = SweepDirection.Clockwise });
        figure.Segments.Add(new LineSegment { Point = new Point(r.Right, r.Bottom) });
        return new PathGeometry { Figures = { figure } };
    }

    /// <summary>A rectangle with only its right corners rounded.</summary>
    public static Geometry RoundedRight(Rect r, double radius)
    {
        radius = Math.Max(0, Math.Min(radius, Math.Min(r.Width, r.Height / 2)));
        var figure = new PathFigure { StartPoint = new Point(r.Left, r.Top), IsClosed = true };
        figure.Segments.Add(new LineSegment { Point = new Point(r.Right - radius, r.Top) });
        figure.Segments.Add(new ArcSegment { Point = new Point(r.Right, r.Top + radius), Size = new Size(radius, radius), SweepDirection = SweepDirection.Clockwise });
        figure.Segments.Add(new LineSegment { Point = new Point(r.Right, r.Bottom - radius) });
        figure.Segments.Add(new ArcSegment { Point = new Point(r.Right - radius, r.Bottom), Size = new Size(radius, radius), SweepDirection = SweepDirection.Clockwise });
        figure.Segments.Add(new LineSegment { Point = new Point(r.Left, r.Bottom) });
        return new PathGeometry { Figures = { figure } };
    }

    /// <summary>"No data collected": a day and an idle day are different answers.</summary>
    public static Microsoft.UI.Xaml.Controls.TextBlock NoData() => Ui.Text("No data collected", 14, 400, Palette.TextMutedBrush);
}
