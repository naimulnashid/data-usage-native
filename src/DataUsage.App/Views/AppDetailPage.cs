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

    public void Load()
    {
        var q = ctx.State.Queries!;
        _app = q.AppDetailFor(key, ctx.State.Scope);
        _exists = _app is not null || q.AppExists(key);
        // The same gate, and the same range, as the table's link.
        _eligible = _app is not null && UsageQueries.EarnsDetailPage(_app.Totals.Total, _app.Days);
    }

    public UIElement Build()
    {
        var page = new StackPanel();
        var back = Ui.Link("← All apps", () => ctx.Navigate(new Route(PageKind.Apps)));
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
            var all = Ui.Chip("Show everything", false);
            all.Click += (_, _) => ctx.State.SetScope(Scope.All);
            page.Children.Add(Parts.Empty("Nothing in this range", "This app moved no data in the selected range. Try a longer one.", all));
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

        var heaviest = app.Peak is { } p ? Parts.Callout("Heaviest day", Format.DayShort(p.Date), Format.Bytes(p.Total)) : null;
        page.Children.Add(Ui.Panel("Daily usage", "Download and upload stacked, on the same shading as the rest of the app", heaviest, DownUpBarChart.Daily(app.Daily)));
        page.Children.Add(OverviewPage.HourPanel(app.Hourly, "Local time, summed over the selected range"));

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
            var table = new DataTable([new("Program"), new("Active days"), new("Total"), new($"Share of {app.Name}", Width: 280, Left: true)]) { RowPadding = 11 };
            foreach (var m in app.Members)
            {
                var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
                name.Children.Add(AppIconView.Create(m.Name, state.Colors, state.Icons, 16));
                name.Children.Add(Ui.Text(m.Name, 15.5));
                var bar = Ui.ShareBar((double)m.Bytes / Math.Max(1, max), new SolidColorBrush(color));
                bar.Tag = "stretch";
                bar.HorizontalAlignment = HorizontalAlignment.Stretch;
                table.AddRow([name, DataTable.Cell(Format.Count(m.Days), Palette.TextMutedBrush), DataTable.Cell(Format.Bytes(m.Bytes), weight: 600), bar]);
            }
            page.Children.Add(Ui.Panel("Merged apps",
                $"{app.Members.Count} separate programs are counted together as {app.Name}. Their totals are added; nothing is double-counted.", null, table.Build()));
        }

        var ids = new DataTable([new("SRUM identity"), new("Total")]) { RowPadding = 10 };
        foreach (var id in app.Identities) ids.AddRow([Parts.MonoCell(id.Identity, Palette.TextMutedBrush, 13), DataTable.Cell(Format.Bytes(id.Bytes))]);
        page.Children.Add(Ui.Panel("Grouped from",
            $"{app.Identities.Count} raw SRUM {(app.Identities.Count == 1 ? "identity is" : "identities are")} grouped under this app - usually different installed versions.", null, ids.Build()));
        return page;
    }
}
