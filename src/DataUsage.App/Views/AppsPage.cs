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
/// By App: the Top 10, then every app. The table opens on the apps that earn
/// a detail page - they carry nearly all the traffic - and says how many more
/// it is hiding rather than pretending they do not exist.
/// </summary>
public sealed class AppsPage(PageContext ctx) : IPage
{
    private ByAppData? _data;
    private string _sort = "total";
    private bool _asc;
    private bool _expanded;

    public void Load() => _data = ctx.State.Queries!.ByApp(ctx.State.Scope);

    public void Placeholder() => _data = Views.Placeholder.ByApp();

    public UIElement Build()
    {
        var data = _data!;
        var page = new StackPanel();
        var pct = data.HeadlineTotal > 0 ? (double)data.Unattributed / data.HeadlineTotal * 100 : 0;
        page.Children.Add(Parts.PageHead("By App",
            $"{data.Apps.Count} apps · {Format.Bytes(data.NamedTotal)} attributed · {Format.Bytes(data.Unattributed)} unattributed ({Format.Percent(pct)})"));

        if (data.Apps.Count == 0)
        {
            page.Children.Add(Parts.Empty("Nothing in this range", "No app moved any data in the selected range."));
            return page;
        }

        page.Children.Add(Ui.Panel("Top 10", "Largest consumers. Each bar is download, then upload in a tint of the same colour.", null,
            new TopAppsChart(data.Apps.Take(10).ToList(), ctx.State.Colors, open: a => ctx.Navigate(new Route(PageKind.App, a.Key)))));
        page.Children.Add(Ui.Panel("Apps",
            "Percentages are of attributed traffic, so they will not quite reach the headline total — the remainder is traffic SRUM could not attribute to a process.",
            null, Table(data)));
        return page;
    }

    private StackPanel Table(ByAppData data)
    {
        var state = ctx.State;
        var visible = _expanded ? data.Apps.ToList() : data.Apps.Where(a => a.Detailed).ToList();
        IEnumerable<AppRow> ordered = _sort switch
        {
            "name" => visible.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
            "sent" => visible.OrderBy(a => a.Sent),
            "received" => visible.OrderBy(a => a.Received),
            "rows" => visible.OrderBy(a => a.Rows),
            _ => visible.OrderBy(a => a.Total),
        };
        if (!_asc) ordered = ordered.Reverse();
        var rows = ordered.ToList();
        var max = Math.Max(1, data.Apps.Max(a => a.Total));

        Column Sortable(string header, string key, bool left = false, double? width = null) =>
            new(header, Width: width, Left: left, OnSort: () =>
            {
                if (_sort == key) _asc = !_asc;
                else
                {
                    _sort = key;
                    _asc = key == "name";
                }
                ctx.Redraw();
            }, SortMark: _sort == key ? (_asc ? "↑" : "↓") : null);

        var table = new DataTable([
            Sortable("App", "name", left: true),
            Sortable("Sent", "sent"),
            Sortable("Received", "received"),
            Sortable("Total", "total"),
            Sortable("Records", "rows"),
            new("Share", Width: 250, Left: true),
        ]) { MinWidth = 900, RowPadding = 12 };

        foreach (var a in rows)
        {
            var share = new Grid { ColumnSpacing = 11, Tag = "stretch", HorizontalAlignment = HorizontalAlignment.Stretch };
            share.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            share.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            share.Children.Add(Ui.ShareBar((double)a.Total / max, new SolidColorBrush(Palette.App(state.Colors, a.Name))));
            var pct = Ui.Text(Format.Percent(a.Share), 15, 400, Palette.TextMutedBrush, numeric: true);
            pct.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(pct, 1);
            share.Children.Add(pct);
            table.AddRow([
                AppActions.NameCell(ctx, a.Key, a.Name, a.BaseName, a.Kind, a.Detailed),
                DataTable.Cell(Format.Bytes(a.Sent)),
                DataTable.Cell(Format.Bytes(a.Received)),
                DataTable.Cell(Format.Bytes(a.Total), weight: 600),
                DataTable.Cell(Format.Count(a.Rows), Palette.TextFaintBrush),
                share,
            ]);
        }

        var body = new StackPanel();
        body.Children.Add(table.Build());
        var hidden = data.Apps.Count - data.Apps.Count(a => a.Detailed);
        if (hidden > 0)
        {
            var more = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(0, 18, 0, 0) };
            var toggle = Ui.Chip(_expanded ? "Show fewer" : $"Show all {data.Apps.Count} apps", false);
            toggle.Click += (_, _) =>
            {
                _expanded = !_expanded;
                ctx.Redraw();
            };
            more.Children.Add(toggle);
            if (!_expanded)
            {
                var note = Ui.Text($"{hidden} more moved too little to be worth a page.", 15, 400, Palette.TextMutedBrush);
                note.VerticalAlignment = VerticalAlignment.Center;
                more.Children.Add(note);
            }
            body.Children.Add(more);
        }
        return body;
    }
}
