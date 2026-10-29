using DataUsage.App.Charts;
using DataUsage.App.Controls;
using DataUsage.App.Theme;
using DataUsage.Core.Query;
using DataUsage.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace DataUsage.App.Views;

/// <summary>
/// By App: the Top 10, then every app, as a table or a grid of cards. The list
/// opens on the apps that earn a detail page - they carry nearly all the
/// traffic - and says how many more it is leaving out rather than pretending
/// they do not exist.
/// </summary>
/// <remarks>
/// Apps the user hid (Naming/AppPrefs.cs) leave the list and the Top 10 and
/// nothing else; "Show N hidden" lists them again to bring one back. The table
/// carried a Share column until 0.3.0: the Top 10 shows the large shares, and
/// the small ones drew as nothing.
/// </remarks>
public sealed class AppsPage(PageContext ctx) : IPage
{
    private ByAppData? _data;
    private string _sort = "total";
    private bool _asc;
    private bool _expanded;
    private bool _showHidden;

    public void Load() => _data = ctx.State.Queries!.ByApp(Scope.All);

    public void Placeholder() => _data = Views.Placeholder.ByApp();

    public UIElement Build()
    {
        var data = _data!;
        var page = new StackPanel();
        var pct = data.HeadlineTotal > 0 ? (double)data.Unattributed / data.HeadlineTotal * 100 : 0;
        page.Children.Add(Parts.PageHead("By App",
            $"{data.Apps.Count} apps · {Format.Bytes(data.NamedTotal)} attributed" + (data.Unattributed > 0 ? $" · {Format.Bytes(data.Unattributed)} unattributed ({Format.Percent(pct)})" : "")));

        if (data.Apps.Count == 0) return Shell.NoData(ctx);

        page.Children.Add(Ui.Panel("Top 10", "Largest consumers. Each bar is download, then upload in a tint of the same colour.", null,
            new TopAppsChart(data.Apps.Where(a => !a.Hidden).Take(10).ToList(), ctx.State.Colors, open: a => ctx.Navigate(new Route(PageKind.App, a.Key)))));
        page.Children.Add(Ui.Panel("Apps", null, ViewToggle(), List(data)));
        return page;
    }

    /// <summary>Table or Grid, remembered with the app's preferences.</summary>
    private StackPanel ViewToggle()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var (view, label, glyph) in new[] { ("table", "Table", ""), ("grid", "Grid", "") })
        {
            var active = ctx.State.AppsView == view;
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 13, Foreground = active ? Palette.OnAccentFillBrush : Palette.TextMutedBrush, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(Ui.Text(label, 14, 500, active ? Palette.OnAccentFillBrush : Palette.TextMutedBrush, selectable: false));
            var chip = Ui.Chip("", active, 14);
            chip.Content = content;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chip, $"Show apps as a {view}");
            chip.Click += (_, _) =>
            {
                if (ctx.State.AppsView == view) return;
                ctx.State.AppsView = view;
                ctx.Redraw();
            };
            row.Children.Add(chip);
        }
        return row;
    }

    private StackPanel List(ByAppData data)
    {
        var listed = _showHidden ? data.Apps.ToList() : data.Apps.Where(a => !a.Hidden).ToList();
        var visible = _expanded ? listed : listed.Where(a => a.Detailed).ToList();
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
        var candidates = data.Apps.Select(a => (a.Key, a.Name)).ToList();

        var body = new StackPanel();
        body.Children.Add(ctx.State.AppsView == "grid" ? Grid(rows, listed, candidates) : Table(rows, candidates));

        var hidden = data.Apps.Count(a => a.Hidden);
        var small = listed.Count - listed.Count(a => a.Detailed);
        if (small == 0 && hidden == 0) return body;
        var more = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(0, 18, 0, 0) };
        if (small > 0)
        {
            var toggle = Ui.Chip(_expanded ? $"Show only the {listed.Count - small} with detail pages" : $"Show all {listed.Count} apps", false);
            toggle.Click += (_, _) =>
            {
                _expanded = !_expanded;
                ctx.Redraw();
            };
            more.Children.Add(toggle);
            var note = Ui.Text(_expanded ? $"{small} of these have too little activity for a detail page." : $"{small} more moved too little to be worth a page.", 15, 400, Palette.TextMutedBrush);
            note.VerticalAlignment = VerticalAlignment.Center;
            more.Children.Add(note);
        }
        if (hidden > 0)
        {
            var show = Ui.Chip(_showHidden ? $"Leave out the {hidden} hidden" : $"Show {hidden} hidden", false);
            show.Click += (_, _) =>
            {
                _showHidden = !_showHidden;
                ctx.Redraw();
            };
            more.Children.Add(show);
        }
        body.Children.Add(more);
        return body;
    }

    private UIElement Table(List<AppRow> rows, IReadOnlyList<(string Key, string Name)> candidates)
    {
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
        ]) { MinWidth = 760, RowPadding = 12 };

        foreach (var a in rows)
        {
            var name = AppActions.NameCell(ctx, a.Key, a.Name, a.BaseName, a.Kind, a.Detailed, hidden: a.Hidden, candidates: candidates);
            if (a.Hidden) name.Opacity = 0.6;
            table.AddRow([
                name,
                DataTable.Cell(Format.Bytes(a.Sent)),
                DataTable.Cell(Format.Bytes(a.Received)),
                DataTable.Cell(Format.Bytes(a.Total), weight: 600),
                DataTable.Cell(Format.Count(a.Rows), Palette.TextFaintBrush),
            ]);
        }
        return table.Build();
    }

    /// <summary>
    /// Each app a card: its logo large, then the figures. The whole card opens
    /// the detail page when the app has one; its own buttons are left alone.
    /// </summary>
    private FitGrid Grid(List<AppRow> rows, List<AppRow> listed, IReadOnlyList<(string Key, string Name)> candidates)
    {
        var state = ctx.State;
        var max = Math.Max(1, listed.Count == 0 ? 1 : listed.Max(a => a.Total));
        var cards = rows.Select((a, i) =>
        {
            var stack = new StackPanel();
            var top = new Grid { Margin = new Thickness(0, 0, 0, 13) };
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var mark = AppIconView.Create(a.Name, state.Colors, state.Icons, 52);
            mark.HorizontalAlignment = HorizontalAlignment.Left;
            top.Children.Add(mark);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            var pencil = AppActions.Pencil(ctx, a.Key, a.Name, a.BaseName);
            var menu = AppActions.Menu(ctx, a.Key, a.Name, a.Hidden, candidates);
            actions.Children.Add(pencil);
            actions.Children.Add(menu);
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(actions, 1);
            top.Children.Add(actions);
            stack.Children.Add(top);

            var title = Ui.Text(a.Name, 16.5, 600, selectable: false);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            Ui.SetTip(title, a.Detailed ? $"{a.Name} - open detail" : $"{a.Name} - too little activity for a detail page");
            stack.Children.Add(title);
            if (a.Kind != "path" || a.Hidden)
            {
                var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(0, 6, 0, 0) };
                if (a.Kind != "path") badges.Children.Add(Ui.Badge(a.Kind == "appx" ? "store" : a.Kind, size: 11.2, padding: new Thickness(7, 1.5, 7, 2)));
                if (a.Hidden) badges.Children.Add(Ui.Badge("hidden", size: 11.2, padding: new Thickness(7, 1.5, 7, 2)));
                stack.Children.Add(badges);
            }
            var total = Ui.Text(Format.Bytes(a.Total), 25, 650, Palette.TextBrush, -0.02, numeric: true);
            total.Margin = new Thickness(0, 11, 0, 0);
            stack.Children.Add(total);
            var io = Parts.Io(a.Sent, a.Received);
            io.Margin = new Thickness(0, 2, 0, 0);
            stack.Children.Add(io);
            stack.Children.Add(Ui.Text($"{a.Days} active {(a.Days == 1 ? "day" : "days")}", 13, 400, Palette.TextFaintBrush));
            var bar = Ui.ShareBar((double)a.Total / max, new SolidColorBrush(Palette.App(state.Colors, a.Name)), delayMs: Math.Min(i * 22, 360));
            bar.Margin = new Thickness(0, 13, 0, 0);
            stack.Children.Add(bar);

            var card = new Border
            {
                Background = Palette.InsetBrush,
                BorderBrush = Palette.BorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(Ui.RadiusSmall),
                Padding = new Thickness(18, 17, 18, 16),
                Child = stack,
                Opacity = a.Hidden ? 0.6 : 1,
            };
            Ui.HoverLift(card, 3);
            card.PointerEntered += (_, _) =>
            {
                card.Background = Palette.SurfaceBrush;
                if (a.Detailed) card.BorderBrush = Palette.AccentBorderBrush;
                pencil.Opacity = menu.Opacity = 1;
                if (a.Hidden) card.Opacity = 1;
            };
            card.PointerExited += (_, _) =>
            {
                card.Background = Palette.InsetBrush;
                if (pencil.FocusState == FocusState.Unfocused) pencil.Opacity = 0;
                if (menu.FocusState == FocusState.Unfocused) menu.Opacity = 0;
                if (a.Hidden) card.Opacity = 0.6;
            };
            if (a.Detailed)
            {
                card.Tapped += (_, e) =>
                {
                    if (FromButton(e.OriginalSource as DependencyObject, card)) return;
                    ctx.Navigate(new Route(PageKind.App, a.Key));
                };
            }
            AppActions.AcceptLogoDrop(ctx, card, a.Name);
            Ui.Rise(card, Math.Min(i * 22, 360));
            return (UIElement)card;
        }).ToArray();
        return Parts.Grid(205, cards);
    }

    /// <summary>Whether a tap began on one of the card's own buttons.</summary>
    private static bool FromButton(DependencyObject? from, DependencyObject card)
    {
        for (var d = from; d is not null && d != card; d = VisualTreeHelper.GetParent(d))
            if (d is ButtonBase) return true;
        return false;
    }
}
