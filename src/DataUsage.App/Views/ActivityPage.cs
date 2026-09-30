using DataUsage.App.Charts;
using DataUsage.App.Theme;
using DataUsage.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DataUsage.App.Views;

/// <summary>
/// The full history as 26-week blocks stacked oldest first, from 1 January
/// 2026 or the first data day if older. Growing downward keeps the cells the
/// overview's size however many years accumulate; one colour scale across
/// every block, so a colour means the same bytes in every row.
/// </summary>
public sealed class ActivityPage(PageContext ctx) : IPage
{
    private List<(string Date, long Total)> _days = [];

    public void Load() => _days = ctx.State.Queries!.HeatmapDays(weeks: null);

    public void Placeholder() => _days = Views.Placeholder.Heat();

    public UIElement Build()
    {
        var page = new StackPanel();
        var back = Ui.Link("← Overview", () => ctx.Navigate(new Route(PageKind.Overview)));
        back.Margin = new Thickness(0, 0, 0, 10);
        page.Children.Add(Parts.PageHead("Activity", "Every day this app holds, in blocks of 26 weeks. Outlined days were never collected.", before: back));

        var earliest = _days.Count > 0 ? _days.Min(d => d.Date) : null;
        var blocks = Heatmap.Expanded(_days, earliest);
        var max = blocks.Count == 0 ? 0 : blocks.Max(b => b.Peak);
        var body = new StackPanel();
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var head = new Grid { Margin = new Thickness(7, i == 0 ? 0 : 22, 7, 4) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(Ui.Text(Heatmap.BlockLabel(block), 15, 600));
            var total = Ui.Text(Format.Bytes(block.Total), 15, 500, Palette.TextMutedBrush, numeric: true);
            Grid.SetColumn(total, 1);
            head.Children.Add(total);
            body.Children.Add(head);
            body.Children.Add(new HeatmapView(block, max));
        }
        var span = blocks.Count > 0 ? $"since {Format.DayShort(blocks[0].First)}, {blocks[0].First[..4]}" : "";
        body.Children.Add(HeatmapView.Legend(blocks.Sum(b => b.Total), blocks.Sum(b => b.ActiveDays), span, null));
        page.Children.Add(Ui.Panel("Full history", "Daily totals across everything collected.", null, body));
        return page;
    }
}
