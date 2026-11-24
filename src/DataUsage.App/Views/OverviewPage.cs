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
    private PhoneExtras? _phone;
    private List<(string Date, long Total)> _heat = [];
    // Daily by app's own range; every other figure covers all history.
    private int _range = Scope.DefaultChartDays;

    public void Load()
    {
        var q = ctx.State.Current!;
        var scope = Scope.All;
        _data = q.Overview(scope);
        _timeline = q.Timeline();
        _heat = q.HeatmapDays();
        if (q is PhoneQueries phone)
        {
            _phone = phone.Extras(scope);
            _networks = null;
        }
        else
        {
            _phone = null;
            _networks = ((UsageQueries)q).Networks(scope);
        }
    }

    public void Placeholder()
    {
        _data = Views.Placeholder.Overview(ctx.State.Device.Split);
        _timeline = Views.Placeholder.Timeline();
        _networks = Views.Placeholder.Networks();
        _phone = ctx.State.Phone is null ? null : new PhoneExtras(_networks.Total * 9 / 10, _networks.Total / 10, 0, []);
        _heat = Views.Placeholder.Heat();
    }

    public UIElement Build()
    {
        var data = _data!;
        var state = ctx.State;
        var page = new StackPanel();

        // All history: no newest day means nothing was ever collected.
        if (data.LatestDate is null) return Shell.NoData(ctx);

        // ---- Head ----
        var phone = _phone;
        var sub = $"Latest data {Format.DayLong(data.LatestDate)}";
        var hours = phone is null ? state.Sync?.HoursSinceSuccess : state.PhoneHoursSinceSuccess;
        if (hours is { } h) sub += $" · collected {Format.Relative(h)}";
        // A late phone has lost nothing (Android keeps ~90 days): Sync Status
        // says how much runway is left, and the overview raises no alarm.
        var stale = phone is null && hours > 48;
        var coverage = data.Coverage is { } c ? $"{Format.DayShort(c.First)} – {Format.DayShort(c.Last)} · {c.Days} days" : null;
        page.Children.Add(Parts.PageHead(state.DeviceLabel, sub, after: stale ? Ui.Badge("collector may be stalled", Ui.BadgeKind.Bad) : null, aside: coverage));

        // ---- Score cards: widest window first. "All" ignores the range, and
        // leads with the accent: it is the figure this app exists to preserve.
        var today = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
        var latestLabel = data.LatestDate == today ? "Today" : $"Latest day · {Format.DayShort(data.LatestDate)}";
        var cards = Parts.Grid(215,
            Parts.StatCard("All", data.All, accent: true),
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
        page.Children.Add(Ui.Panel("Activity", ActivityNote(phone is not null), null, ActivityBody()));

        // ---- Daily by app ----
        // Every range arrives ready-made (each ranks its own top apps), so the
        // menu only picks one.
        var timeline = _timeline!;
        var stack = timeline.Ranges.GetValueOrDefault(_range) ?? timeline.Ranges.Values.First();
        var body = new StackPanel();
        body.Children.Add(new StackedAreaChart(stack.Points, stack.Series, state.Colors));
        body.Children.Add(AppIconView.Legend(stack.Series, state.Colors, state.Icons));
        var range = Parts.RangeBox(_range, d =>
        {
            _range = d;
            ctx.Redraw();
        }, "Daily by app range");
        page.Children.Add(Ui.Panel("Daily by app", $"Top {stack.Series.Count(s => s != "Other")} apps stacked; everything else grouped as Other", range, body));

        // ---- Hour of day ----
        page.Children.Add(HourPanel(timeline.Hourly, phone is null ? "Local time, summed over all history" : "Local time on the phone, in Android's two-hour buckets"));

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
        page.Children.Add(Ui.Panel("Where it went", null, null, phone is null ? NetworkBody(_networks!) : PhoneNetworkBody(phone)));

        // ---- Tethering, last: a caveat about figures already read, kept a
        // full card because it is the one thing that would make someone add
        // two totals that must not be added.
        if (phone is { Tethering: > 0 })
            page.Children.Add(Ui.Panel("Tethering is counted twice", "Excluded from every figure on this page, including the app list.", null,
                Ui.Paragraph($"{Format.Bytes(phone.Tethering)} of traffic was relayed by the phone for other devices. If this PC tethers off the phone, those same bytes are also recorded on the PC, under Windows app names. Adding the two devices' totals together would count them twice, so this figure is kept out of the totals rather than folded in.", 15, Palette.TextMutedBrush)));
        return page;
    }

    /// <summary>The Activity card's note: what an outlined day means on this device.</summary>
    public static string ActivityNote(bool phone) => phone
        ? "Daily totals. Outlined days are before this phone started reporting - no data was recorded, which is not the same as a quiet day."
        : "Daily totals. Outlined days were never collected - before collection started, or lost before a run read them - which is not the same as a quiet day.";

    /// <summary>A phone's "Where it went": Wi-Fi against mobile data, as the web dashboard's phone page has it.</summary>
    private static StackPanel PhoneNetworkBody(PhoneExtras phone)
    {
        var total = phone.Wifi + phone.Mobile;
        double Pct(long b) => total > 0 ? (double)b / total * 100 : 0;
        var body = new StackPanel();
        body.Children.Add(TwoPartBar([(Pct(phone.Wifi) / 100, Palette.AccentBrush), (Pct(phone.Mobile) / 100, Palette.WiredBrush)]));
        UIElement Cell(LinkKind kind, string label, long bytes, Brush value)
        {
            var cell = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            head.Children.Add(new FontIcon { Glyph = Parts.LinkGlyph(kind), FontSize = 16, Foreground = value, VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(Ui.Text(label, 15, 400, Palette.TextMutedBrush));
            cell.Children.Add(head);
            var figure = Ui.Text(Format.Bytes(bytes), 44, 650, value, -0.035, numeric: true);
            figure.Margin = new Thickness(0, 5, 0, 0);
            cell.Children.Add(figure);
            cell.Children.Add(Ui.Text($"{Format.Percent(Pct(bytes))} of all traffic", 15, 400, Palette.TextMutedBrush));
            return cell;
        }
        var grid = Parts.Grid(340,
            Cell(LinkKind.Wifi, "Wi-Fi", phone.Wifi, Palette.AccentBrightBrush),
            Cell(LinkKind.Mobile, "Mobile data", phone.Mobile, Palette.WiredBrush));
        grid.Margin = new Thickness(0, 25.6, 0, 0);
        body.Children.Add(grid);
        return body;
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
        var otherColor = Palette.Ink(AppColors.EverythingElse);
        var body = new StackPanel();
        body.Children.Add(TwoPartBar([(pct / 100, new SolidColorBrush(focusColor)), (1 - pct / 100, new SolidColorBrush(otherColor))]));

        FrameworkElement Half(string label, long bytes, double share, Windows.UI.Color color, Windows.UI.Color ink, bool icon)
        {
            var half = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            head.Children.Add(icon ? AppIconView.Create(label, state.Colors, state.Icons, 15) : Ui.Swatch(color));
            head.Children.Add(Ui.Text(label, 15, 400, Palette.TextMutedBrush));
            half.Children.Add(head);
            // The figure in the text band, which on white is darker than the fill.
            var value = Parts.BytesValue(bytes, new SolidColorBrush(ink));
            value.Margin = new Thickness(0, 5, 0, 0);
            half.Children.Add(value);
            half.Children.Add(Ui.Text($"{Format.Percent(share)} of named traffic", 15, 400, Palette.TextMutedBrush));
            return half;
        }
        var grid = Parts.Grid(340,
            Half(app, focus, pct, focusColor, Palette.AppText(state.Colors, app), true),
            Half("Everything else", other, 100 - pct, otherColor, Palette.InkText(AppColors.EverythingElse), false));
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

    // Slice shades: Wi-Fi networks step down the accent and wired ones the
    // wired colour, largest first, mixed toward the card so each step works on
    // both themes; an unnamed profile is the faint grey its row is dimmed to.
    private static readonly double[] WifiSteps = [1, 0.74, 0.56, 0.42, 0.32, 0.24];
    private static readonly double[] WiredSteps = [1, 0.7, 0.48];

    private static List<Brush> SliceBrushes(IReadOnlyList<NetworkRow> networks)
    {
        int wifi = 0, wired = 0;
        return networks.Select(n =>
        {
            if (!n.Named && n.Id != "0") return (Brush)Palette.TextFaintBrush;
            if (n.Kind == LinkKind.Wifi) return new SolidColorBrush(Palette.Mix(Palette.Surface, Palette.Accent, WifiSteps[Math.Min(wifi++, WifiSteps.Length - 1)]));
            if (n.Kind == LinkKind.Wired) return new SolidColorBrush(Palette.Mix(Palette.Surface, Palette.Wired, WiredSteps[Math.Min(wired++, WiredSteps.Length - 1)]));
            return Palette.TextFaintBrush;
        }).ToList();
    }

    /// <summary>
    /// "Where it went": by kind of link, each with its icon, then the networks
    /// as a ring beside their list - all from the aggregate rows, so the parts
    /// add up to the score cards. Wi-Fi and wired always; mobile and other only
    /// when present.
    /// </summary>
    private static StackPanel NetworkBody(NetworkBreakdown networks)
    {
        var kinds = networks.ByKind.Where(k => k.Kind is LinkKind.Wifi or LinkKind.Wired || k.Total > 0).ToList();
        double Pct(long b) => networks.Total > 0 ? (double)b / networks.Total * 100 : 0;

        var body = new StackPanel();
        var grid = Parts.Grid(Math.Min(4, kinds.Count) switch { 4 => 215, 3 => 260, _ => 340 }, kinds.Select(k =>
        {
            var cell = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            head.Children.Add(new FontIcon { Glyph = Parts.LinkGlyph(k.Kind), FontSize = 16, Foreground = LinkBrushes(k.Kind).Value, VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(Ui.Text(LinkLabel[k.Kind], 15, 400, Palette.TextMutedBrush));
            cell.Children.Add(head);
            var value = Ui.Text(Format.Bytes(k.Total), 44, 650, LinkBrushes(k.Kind).Value, -0.035, numeric: true);
            value.Margin = new Thickness(0, 5, 0, 0);
            cell.Children.Add(value);
            cell.Children.Add(Ui.Text($"{Format.Percent(Pct(k.Total))} of all traffic", 15, 400, Palette.TextMutedBrush));
            return (UIElement)cell;
        }).ToArray());
        body.Children.Add(grid);

        if (networks.Networks.Count == 0) return body;

        var block = new StackPanel { Margin = new Thickness(0, 25.6, 0, 0), Padding = new Thickness(0, 22, 0, 0), BorderBrush = Palette.BorderBrush, BorderThickness = new Thickness(0, 1, 0, 0) };
        var brushes = SliceBrushes(networks.Networks);
        string NameOf(NetworkRow n) => n.Named || n.Id == "0" ? n.Label : $"{n.Label}, unnamed";

        var split = new Grid { ColumnSpacing = 35 };
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var count = networks.Networks.Count;
        var donut = new DonutChart(
            networks.Networks.Select((n, i) => new DonutSlice(NameOf(n), n.Total, brushes[i], Parts.LinkGlyph(n.Kind))).ToList(),
            networks.Total, $"{count} {(count == 1 ? "network" : "networks")}")
        { VerticalAlignment = VerticalAlignment.Center };
        split.Children.Add(donut);

        var table = new Grid { ColumnSpacing = 19, VerticalAlignment = VerticalAlignment.Center };
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        var r = 0;
        foreach (var n in networks.Networks)
        {
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, Margin = new Thickness(0, 7, 0, 7) };
            name.Children.Add(new FontIcon { Glyph = Parts.LinkGlyph(n.Kind), FontSize = 15, Foreground = brushes[r], VerticalAlignment = VerticalAlignment.Center });
            var label = Ui.Text(NameOf(n), 15.5, 400, n.Named ? Palette.TextBrush : Palette.TextFaintBrush);
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
            var pct = Ui.Text(Format.Percent(Pct(n.Total)), 15.5, 400, Palette.TextMutedBrush, numeric: true);
            pct.HorizontalAlignment = HorizontalAlignment.Right;
            pct.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(pct, r);
            Grid.SetColumn(pct, 2);
            table.Children.Add(pct);
            r++;
        }
        Grid.SetColumn(table, 1);
        split.Children.Add(table);
        block.Children.Add(split);

        // Windows' own page is scoped to one network profile, so an
        // all-networks total legitimately reads higher. Said here, beside the
        // row that WILL match, or the gap reads as a bug.
        var note = Ui.Text("Windows' own Data usage page shows one network at a time, so its figure matches one row here rather than the total. Names are learned by watching which network the laptop is on; an unnamed one fills in the next time it is seen.", 13, 400, Palette.TextFaintBrush, wrap: true);
        note.LineHeight = 21;
        note.Margin = new Thickness(0, 18, 0, 0);
        block.Children.Add(note);
        body.Children.Add(block);
        return body;
    }
}
