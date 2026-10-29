using DataUsage.App.Charts;
using DataUsage.App.Controls;
using DataUsage.App.Theme;
using DataUsage.Core.Query;
using DataUsage.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DataUsage.App.Views;

/// <summary>
/// One app over time: the four figures, Daily usage, Hour of day, By network,
/// Merged apps (members, so a merge is never silent) and Grouped from (the raw
/// SRUM identities).
/// </summary>
/// <remarks>
/// The page re-checks eligibility, not just the table's link, and "no such
/// app" is a different answer from "nothing in this range": an app with no
/// rows in the current range still exists, and saying otherwise sends the
/// reader hunting for a page that is right there.
/// </remarks>
public sealed class AppDetailPage(PageContext ctx, string key) : IPage
{
    private AppDetail? _app;
    private bool _exists;
    private bool _eligible;

    public Route Back { get; } = new(PageKind.Apps);

    // Daily usage's own range; every other figure covers all history.
    private int _range = Scope.DefaultChartDays;

    public void Load()
    {
        var q = ctx.State.Queries!;
        // An app the user merged into another is shown as that one.
        var shown = q.MergedInto(key) ?? key;
        _app = q.AppDetailFor(shown, Scope.All);
        _exists = _app is not null || q.AppExists(shown);
        // The same gate, and the same range, as the table's link.
        _eligible = _app is not null && UsageQueries.EarnsDetailPage(_app.Totals.Total, _app.Days);
    }

    public void Placeholder()
    {
        _app = Views.Placeholder.App(key);
        _exists = _eligible = true;
    }

    public UIElement Build()
    {
        var page = new StackPanel();
        var back = Ui.Link("← All apps", () => ctx.Navigate(Back));
        back.Margin = new Thickness(0, 0, 0, 10);

        if (!_exists)
        {
            page.Children.Add(back);
            page.Children.Add(Parts.Empty("No such app", "Nothing in the history resolves to this app."));
            return page;
        }
        if (_app is null)
        {
            page.Children.Add(back);
            page.Children.Add(Parts.Empty("No such app", "Nothing in the history resolves to this app."));
            return page;
        }
        if (!_eligible)
        {
            page.Children.Add(back);
            page.Children.Add(Parts.Empty("Too little to show", $"{_app.Name} moved too little data, on too few days, to have a shape worth a page."));
            return page;
        }

        var app = _app;
        var state = ctx.State;
        var color = Palette.App(state.Colors, app.Name);
        var mark = AppIconView.Create(app.Name, state.Colors, state.Icons, 30);
        var pencil = AppActions.Pencil(ctx, app.Key, app.Name, app.BaseName, 17, alwaysVisible: true);
        pencil.VerticalAlignment = VerticalAlignment.Center;
        var head = Parts.PageHead(app.Name,
            $"{(app.First is { } f && app.Last is { } l ? $"{Format.DayShort(f)} – {Format.DayShort(l)} · " : "")}{app.Days} active days · {Format.Count(app.Rows)} records",
            before: back, titleExtra: mark);
        ((StackPanel)head.Children[1]).Children.Add(pencil);
        AppActions.AcceptLogoDrop(ctx, head, app.Name);
        page.Children.Add(head);

        var t = app.Totals;
        double Of(long part) => t.Total > 0 ? (double)part / t.Total * 100 : 0;
        var cards = Parts.Grid(215,
            Parts.FigureCard("Total", Parts.BytesValue(t.Total, Palette.AccentBrightBrush, 34), $"{Format.Percent(app.Share)} of all attributed traffic"),
            Parts.FigureCard("Downloaded", Parts.BytesValue(t.Received, null, 34), $"{Format.Percent(Of(t.Received))} of this app", 60),
            Parts.FigureCard("Uploaded", Parts.BytesValue(t.Sent, null, 34), $"{Format.Percent(Of(t.Sent))} of this app", 120),
            Parts.FigureCard("Per active day", Parts.BytesValue(app.Days > 0 ? t.Total / app.Days : 0, null, 34), $"across {app.Days} days", 180));
        cards.Margin = new Thickness(0, 0, 0, 18.4);
        page.Children.Add(cards);

        // The series is all history, one entry per day, so a range is its tail;
        // the heaviest day is the range's own and sits in the subtitle, which
        // leaves the head's right slot to the menu.
        var shownDays = app.Daily.TakeLast(Math.Min(_range, app.Daily.Count)).ToList();
        var peak = shownDays.Where(d => d.Total > 0).OrderByDescending(d => d.Total).FirstOrDefault();
        var range = Parts.RangeBox(_range, d =>
        {
            _range = d;
            ctx.Redraw();
        }, "Daily usage range");
        page.Children.Add(Ui.Panel("Daily usage",
            peak is null ? "Nothing moved in this range" : $"Heaviest day {Format.DayShort(peak.Date)} · {Format.Bytes(peak.Total!.Value)}",
            range, DownUpBarChart.Daily(shownDays)));
        page.Children.Add(OverviewPage.HourPanel(app.Hourly, "Local time, summed over all history"));

        if (app.Networks.Count > 0)
        {
            var max = app.Networks.Max(n => n.Bytes);
            var table = new DataTable([new("Network"), new("Total"), new("Share", Width: 380, Left: true)]) { RowPadding = 11 };
            foreach (var n in app.Networks)
            {
                var bar = Ui.ShareBar((double)n.Bytes / Math.Max(1, max), n.Label.EndsWith(", unnamed") || n.Label == "Unknown network" ? Palette.TextFaintBrush : Palette.AccentBrush);
                bar.Tag = "stretch";
                bar.HorizontalAlignment = HorizontalAlignment.Stretch;
                table.AddRow([DataTable.Cell(n.Label, numeric: false), DataTable.Cell(Format.Bytes(n.Bytes)), bar]);
            }
            page.Children.Add(Ui.Panel("By network", "Which network this app used. Unnamed profiles fill in as the collector sees them again.", null, table.Build()));
        }

        if (app.Members.Count > 1)
        {
            var max = app.Members.Max(m => m.Bytes);
            var userMerged = app.Members.Any(m => m.MergedFrom is not null);
            List<Column> columns = [new("Program"), new("Active days"), new("Total"), new($"Share of {app.Name}", Width: 280, Left: true)];
            if (userMerged) columns.Add(new("", Width: 54));
            var table = new DataTable(columns) { RowPadding = 11 };
            foreach (var m in app.Members)
            {
                var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
                name.Children.Add(AppIconView.Create(m.Name, state.Colors, state.Icons, 16, colorName: app.Name));
                name.Children.Add(Ui.Text(m.Name, 15.5));
                if (m.MergedFrom is { } from)
                {
                    var badge = Ui.Badge("merged", size: 11.2, padding: new Thickness(7, 1.5, 7, 2));
                    Ui.SetTip(badge, $"Merged by you, from {from.Name}");
                    name.Children.Add(badge);
                }
                var bar = Ui.ShareBar((double)m.Bytes / Math.Max(1, max), new SolidColorBrush(color));
                bar.Tag = "stretch";
                bar.HorizontalAlignment = HorizontalAlignment.Stretch;
                List<UIElement?> cells = [name, DataTable.Cell(Format.Count(m.Days), Palette.TextMutedBrush), DataTable.Cell(Format.Bytes(m.Bytes), weight: 600), bar];
                if (userMerged) cells.Add(m.MergedFrom is { } source ? AppActions.Separate(ctx, source.Key, source.Name) : null);
                table.AddRow(cells);
            }
            page.Children.Add(Ui.Panel("Merged apps",
                $"{app.Members.Count} separate programs are counted together as {app.Name}. Their totals are added; nothing is double-counted.{(userMerged ? " Those you merged can be separated again." : "")}", null, table.Build()));
        }

        // Only when there is more than one: that is the evidence for why
        // several raw strings are shown as one app. The web's rule.
        if (app.Identities.Count > 1)
        {
            var ids = new DataTable([new("SRUM identity"), new("Total")]) { RowPadding = 10 };
            foreach (var id in app.Identities) ids.AddRow([Parts.MonoCell(id.Identity, Palette.TextMutedBrush, 13), DataTable.Cell(Format.Bytes(id.Bytes))]);
            page.Children.Add(Ui.Panel("Grouped from",
                $"{app.Identities.Count} raw SRUM identities are grouped under this app - usually different installed versions.", null, ids.Build()));
        }
        return page;
    }
}
