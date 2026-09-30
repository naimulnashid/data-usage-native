using DataUsage.App.Charts;
using DataUsage.App.Controls;
using DataUsage.App.Theme;
using DataUsage.Core.Naming;
using DataUsage.Core.Query;
using DataUsage.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DataUsage.App.Views;

/// <summary>
/// The device's overview, section for section the web dashboard's: the four
/// score cards, Trend, Activity, Daily by app, Hour of day, the split app, and
/// Where it went.
/// </summary>
public sealed class OverviewPage(PageContext ctx) : IPage
{
    private OverviewData? _data;
    private TimelineData? _timeline;
    private NetworkBreakdown? _networks;
    private List<(string Date, long Total)> _heat = [];
    private long _rows;

    public void Load()
    {
        var q = ctx.State.Queries!;
        var scope = ctx.State.Scope;
        _data = q.Overview(scope);
        _timeline = q.Timeline(scope);
        _networks = q.Networks(scope);
        _heat = q.HeatmapDays();
        _rows = _data.LatestDate is null ? q.RowCount() : 1;
    }

    public UIElement Build()
    {
        var data = _data!;
        var state = ctx.State;
        var page = new StackPanel();

        if (data.LatestDate is null)
        {
            if (_rows == 0) return Shell.NoData(ctx);
            var all = Ui.Chip("Show everything", false);
            all.Click += (_, _) => state.SetScope(Scope.All);
            page.Children.Add(Parts.Empty("Nothing in this range", "There is usage history stored, but none in the selected range. Try a longer one.", all));
            return page;
        }

        // ---- Head ----
        var sub = $"Latest data {Format.DayLong(data.LatestDate)}";
        if (state.Sync?.HoursSinceSuccess is { } h) sub += $" · collected {Format.Relative(h)}";
        var stale = state.Sync?.HoursSinceSuccess > 48;
        page.Children.Add(Parts.PageHead(state.Device.Label, sub, after: stale ? Ui.Badge("collector may be stalled", Ui.BadgeKind.Bad) : null));

        // ---- Score cards: widest window first. "All" ignores the range, and
        // leads with the accent: it is the figure this app exists to preserve.
        var today = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
        var latestLabel = data.LatestDate == today ? "Today" : $"Latest day · {Format.DayShort(data.LatestDate)}";
        var coverage = data.Coverage is { } c ? $"{Format.DayShort(c.First)} – {Format.DayShort(c.Last)} · {c.Days} days" : null;
        var cards = Parts.Grid(215,
            Parts.StatCard("All", data.All, accent: true, range: coverage),
            Parts.StatCard("Last 30 days", data.Month, delay: 60),
            Parts.StatCard("Last 7 days", data.Week, delay: 120),
            Parts.StatCard(latestLabel, data.Today, delay: 180));
        cards.Margin = new Thickness(0, 0, 0, 18.4);
        page.Children.Add(cards);

        // ---- Trend ----
        var spikes = data.Daily.Count(d => data.MeanDaily > 0 && d.Total > data.MeanDaily * 2.5);
        var trendSub = $"Daily total across {Days.SpanLabel(data.Daily)}" + (spikes > 0 ? $" · {spikes} day{(spikes > 1 ? "s" : "")} well above trend" : "");
        var heaviest = data.Peak is { } p ? Parts.Callout("Heaviest day", Format.DayShort(p.Date), Format.Bytes(p.Total)) : null;
        page.Children.Add(Ui.Panel("Trend", trendSub, heaviest, new TrendChart(data.Daily, data.MeanDaily)));

        // ---- Activity ----
        page.Children.Add(Ui.Panel("Activity",
            "Daily totals. Outlined days were never collected - before collection started, or lost before a run read them - which is not the same as a quiet day.",
            null, ActivityBody()));

        // ---- Daily by app ----
        var timeline = _timeline!;
        var body = new StackPanel();
        body.Children.Add(new StackedAreaChart(timeline.Points, timeline.Series, state.Colors));
        body.Children.Add(AppIconView.Legend(timeline.Series, state.Colors, state.Icons));
        page.Children.Add(Ui.Panel("Daily by app", $"Top {timeline.Series.Count(s => s != "Other")} apps stacked; everything else grouped as Other", null, body));

        // ---- Hour of day ----
        page.Children.Add(HourPanel(timeline.Hourly, "Local time, summed over the selected range"));

        // ---- The split app ----
        var splitTotal = data.SplitFocus + data.SplitOther;
        if (data.SplitApp is { } app && data.SplitFocus > 0)
        {
            var pct = splitTotal > 0 ? (double)data.SplitFocus / splitTotal * 100 : 0;
            page.Children.Add(Ui.Panel($"{app} vs everything else",
                $"{app} is {Format.Percent(pct)} of named traffic here, so everything else is shown separately at a readable scale",
                null, SplitBody(app, data.SplitFocus, data.SplitOther, pct)));
        }

        // ---- Where it went ----
        page.Children.Add(Ui.Panel("Where it went", "Wi-Fi against wired, and which network, over the selected range", null, NetworkBody(_networks!)));
        return page;
    }

    private StackPanel ActivityBody()
    {
        var block = Heatmap.Recent(_heat);
        var body = new StackPanel();
        body.Children.Add(new HeatmapView(block, block.Peak));
        // Always offered (the user's choice, 2026-09-30): a control that
        // appears only once the history is long enough is one nobody finds.
        var expand = Ui.Chip("Expand", false, 14);
        expand.Click += (_, _) => ctx.Navigate(new Route(PageKind.Activity));
        body.Children.Add(HeatmapView.Legend(block.Total, block.ActiveDays, "in the last 6 months", expand));
        return body;
    }

    /// <summary>Hour of day, with the busiest hour as the head's callout. Shared with the app page.</summary>
    public static Border HourPanel(IReadOnlyList<HourPoint> hours, string sub)
    {
        var busiest = hours.Where(x => x.Total > 0).OrderByDescending(x => x.Total).FirstOrDefault();
        var callout = busiest is null ? null : Parts.Callout("Busiest hour", Format.Hour(busiest.Hour), Format.Bytes(busiest.Total));
        var body = new StackPanel();
        body.Children.Add(DownUpBarChart.Hourly(hours));
        var legend = new WrapPanel { HorizontalSpacing = 18, Margin = new Thickness(0, 14, 0, 0) };
        foreach (var (color, label) in new[] { (Palette.Down, "Download"), (Palette.Up, "Upload") })
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            item.Children.Add(Ui.Swatch(color));
            item.Children.Add(Ui.Text(label, 15, 400, Palette.TextMutedBrush));
            legend.Children.Add(item);
        }
        body.Children.Add(legend);
        return Ui.Panel("Hour of day", sub, callout, body);
    }

    private StackPanel SplitBody(string app, long focus, long other, double pct)
    {
        var state = ctx.State;
        var focusColor = Palette.App(state.Colors, app);
        var otherColor = Palette.Hex(AppColors.EverythingElse);
        var body = new StackPanel();
        body.Children.Add(TwoPartBar([(pct / 100, new SolidColorBrush(focusColor)), (1 - pct / 100, new SolidColorBrush(otherColor))]));

        FrameworkElement Half(string label, long bytes, double share, Windows.UI.Color color, bool icon)
        {
            var half = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            head.Children.Add(icon ? AppIconView.Create(label, state.Colors, state.Icons, 15) : Ui.Swatch(color));
            head.Children.Add(Ui.Text(label, 15, 400, Palette.TextMutedBrush));
            half.Children.Add(head);
            var value = Parts.BytesValue(bytes, new SolidColorBrush(color));
            value.Margin = new Thickness(0, 5, 0, 0);
            half.Children.Add(value);
            half.Children.Add(Ui.Text($"{Format.Percent(share)} of named traffic", 15, 400, Palette.TextMutedBrush));
            return half;
        }
        var grid = Parts.Grid(340, Half(app, focus, pct, focusColor, true), Half("Everything else", other, 100 - pct, otherColor, false));
        grid.Margin = new Thickness(0, 25.6, 0, 0);
        body.Children.Add(grid);
        return body;
    }

    /// <summary>A 16px rounded bar split into parts, each growing in from the left.</summary>
    public static Grid TwoPartBar(IReadOnlyList<(double Fraction, Brush Fill)> parts)
    {
        var track = new Grid { Height = 16, CornerRadius = new CornerRadius(8), Background = Palette.InsetBrush };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        track.Children.Add(row);
        var fills = parts.Select(p => new Border { Background = p.Fill }).ToList();
        foreach (var f in fills) row.Children.Add(f);
        track.SizeChanged += (_, e) =>
        {
            for (var i = 0; i < parts.Count; i++) fills[i].Width = Math.Max(0, parts[i].Fraction) * e.NewSize.Width;
        };
        for (var i = 0; i < fills.Count; i++) Ui.GrowX(fills[i], 800, i * 120);
        return track;
    }

    private static readonly Dictionary<LinkKind, string> LinkLabel = new()
    {
        [LinkKind.Wifi] = "Wi-Fi",
        [LinkKind.Wired] = "Wired",
        [LinkKind.Mobile] = "Mobile broadband",
        [LinkKind.Other] = "Other",
    };

    private static (Brush Fill, Brush Value) LinkBrushes(LinkKind k) => k switch
    {
        LinkKind.Wifi => (Palette.AccentBrush, Palette.AccentBrightBrush),
        LinkKind.Other => (Palette.TextFaintBrush, Palette.TextMutedBrush),
        _ => (Palette.WiredBrush, Palette.WiredBrush),
    };

    /// <summary>
    /// "Where it went": by kind of link, then by network - all from the
    /// aggregate rows, so the parts add up to the score cards. Wi-Fi and wired
    /// always; mobile and other only when present.
    /// </summary>
    private static StackPanel NetworkBody(NetworkBreakdown networks)
    {
        var kinds = networks.ByKind.Where(k => k.Kind is LinkKind.Wifi or LinkKind.Wired || k.Total > 0).ToList();
        double Pct(long b) => networks.Total > 0 ? (double)b / networks.Total * 100 : 0;

        var body = new StackPanel();
        body.Children.Add(TwoPartBar(kinds.Select(k => (Pct(k.Total) / 100, LinkBrushes(k.Kind).Fill)).ToList()));

        var grid = Parts.Grid(Math.Min(4, kinds.Count) switch { 4 => 215, 3 => 260, _ => 340 }, kinds.Select(k =>
        {
            var cell = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            var sw = new Border { Width = 11, Height = 11, CornerRadius = new CornerRadius(3), Background = LinkBrushes(k.Kind).Fill, VerticalAlignment = VerticalAlignment.Center };
            head.Children.Add(sw);
            head.Children.Add(Ui.Text(LinkLabel[k.Kind], 15, 400, Palette.TextMutedBrush));
            cell.Children.Add(head);
            var value = Ui.Text(Format.Bytes(k.Total), 44, 650, LinkBrushes(k.Kind).Value, -0.035, numeric: true);
            value.Margin = new Thickness(0, 5, 0, 0);
            cell.Children.Add(value);
            cell.Children.Add(Ui.Text($"{Format.Percent(Pct(k.Total))} of all traffic", 15, 400, Palette.TextMutedBrush));
            return (UIElement)cell;
        }).ToArray());
        grid.Margin = new Thickness(0, 22, 0, 0);
        body.Children.Add(grid);

        if (networks.Networks.Count == 0) return body;

        var block = new StackPanel { Margin = new Thickness(0, 25.6, 0, 0), Padding = new Thickness(0, 17.6, 0, 0), BorderBrush = Palette.BorderBrush, BorderThickness = new Thickness(0, 1, 0, 0) };
        var ssidHead = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 0, 0, 8) };
        ssidHead.Children.Add(Ui.Text("Networks", 15, 600));
        var count = Ui.Text($"{networks.Networks.Count} {(networks.Networks.Count == 1 ? "network" : "networks")}", 13, 400, Palette.TextFaintBrush);
        count.VerticalAlignment = VerticalAlignment.Bottom;
        ssidHead.Children.Add(count);
        block.Children.Add(ssidHead);

        var table = new Grid { ColumnSpacing = 19, RowSpacing = 0 };
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.40, GridUnitType.Star) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.15, GridUnitType.Star) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.45, GridUnitType.Star) });
        var r = 0;
        foreach (var n in networks.Networks)
        {
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var nameText = n.Named || n.Id == "0" ? n.Label : $"{n.Label}, unnamed";
            var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(0, 6.4, 0, 6.4) };
            var label = Ui.Text(nameText, 15.5, 400, n.Named ? Palette.TextBrush : Palette.TextFaintBrush);
            if (!n.Named) label.FontStyle = Windows.UI.Text.FontStyle.Italic;
            name.Children.Add(label);
            if (n.Aliases.Count > 0)
            {
                var more = Ui.Text($"+{n.Aliases.Count}", 13, 400, Palette.TextFaintBrush);
                more.VerticalAlignment = VerticalAlignment.Center;
                name.Children.Add(more);
                Ui.SetTip(name, $"{n.Label} - also seen as {string.Join(", ", n.Aliases)}");
            }
            Grid.SetRow(name, r);
            table.Children.Add(name);
            var total = Ui.Text(Format.Bytes(n.Total), 15.5, 400, numeric: true);
            total.HorizontalAlignment = HorizontalAlignment.Right;
            total.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(total, r);
            Grid.SetColumn(total, 1);
            table.Children.Add(total);
            var bar = Ui.ShareBar(Pct(n.Total) / 100, n.Named ? Palette.AccentBrush : Palette.TextFaintBrush, delayMs: r * 40);
            Grid.SetRow(bar, r);
            Grid.SetColumn(bar, 2);
            table.Children.Add(bar);
            r++;
        }
        block.Children.Add(table);

        // Windows' own page is scoped to one network profile, so an
        // all-networks total legitimately reads higher. Said here, beside the
        // row that WILL match, or the gap reads as a bug.
        var note = Ui.Text("Windows' own Data usage page shows one network at a time, so its figure matches one row here rather than the total. Names are learned by watching which network the laptop is on; an unnamed one fills in the next time it is seen.", 13, 400, Palette.TextFaintBrush, wrap: true);
        note.LineHeight = 21;
        note.Margin = new Thickness(0, 14, 0, 0);
        block.Children.Add(note);
        body.Children.Add(block);
        return body;
    }
}
