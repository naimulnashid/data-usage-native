using DataUsage.App.Controls;
using DataUsage.App.Theme;
using DataUsage.Core.Query;
using DataUsage.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DataUsage.App.Views;

/// <summary>
/// A phone's Sync Status: whether it is still reporting. The web dashboard's
/// phone page, section for section.
/// </summary>
/// <remarks>
/// The PC's page exists because a stopped collector is invisible until a
/// reset has destroyed the history. <b>This page is not that</b>, and it does
/// not borrow that page's alarm: Android keeps ~90 days of its own, so a phone
/// that has not reported for a week has lost nothing. What it leads with is
/// runway - how long the phone can stay away before Android deletes buckets
/// that were never collected.
/// </remarks>
public sealed class PhoneSyncPage(PageContext ctx) : IPage
{
    private const int PageSize = 25;
    private PhoneSyncData? _data;
    private int _page = 1;

    public void Load()
    {
        var q = ctx.State.PhoneQueries!;
        var first = q.Sync(PageSize, 0);
        var pages = Math.Max(1, (int)Math.Ceiling(first.TotalUploads / (double)PageSize));
        _page = Math.Clamp(_page, 1, pages);
        _data = _page == 1 ? first : q.Sync(PageSize, (_page - 1) * PageSize);
    }

    public void Placeholder() => _data = Views.Placeholder.PhoneSync();

    public UIElement Build()
    {
        var data = _data!;
        var phone = ctx.State.Phone;
        var page = new StackPanel();
        // The Android version lives here rather than on the overview: it is a
        // property of the device, and the one time it matters is when uploads
        // start failing, beside the history that would show it.
        page.Children.Add(Parts.PageHead("Sync Status",
            phone is null ? null : $"Android {phone.Release} (API {phone.Sdk}) · {phone.Label} pushes on its own schedule, set in its app, through the cloud inbox; this PC empties the inbox every 15 minutes."));

        var hours = data.HoursSinceSuccess;
        var late = hours > PhoneQueries.SyncWarnHours;
        var critical = hours > PhoneQueries.SyncCriticalHours;
        var failing = data.ConsecutiveFailures >= 2;
        var runwayLow = data.RunwayDays < 14;

        if (critical || failing)
            page.Children.Add(Parts.Alert(failing ? "Uploads are being rejected" : "History is about to be lost",
                failing
                    ? $"The last {data.ConsecutiveFailures} uploads failed. See the reason in the upload history below."
                    : $"Nothing has been collected for {Math.Round(hours!.Value / 24)} days, and Android deletes its own per-app history after about {PhoneQueries.RetentionDays}.",
                "Open Data Usage Reporter on the phone, check that usage access is still granted, and tap Sync now."));

        var cards = Parts.Grid(260,
            Parts.FigureCard("Last upload",
                Ui.Text(hours is { } h ? Format.Relative(h) : "never", 30.4, 650, critical ? Palette.WarnBrush : late ? Palette.TextBrush : Palette.AccentBrightBrush, -0.035),
                data.LastSuccessAt is { } at ? Format.DateTimeLocal(at) : "no successful upload recorded"),
            // The figure that matters, and why this page does not copy the
            // PC's: being late is only a problem against what Android deletes.
            Parts.FigureCard("Collection runway",
                Ui.Text(data.RunwayDays is { } r ? $"{Math.Max(0, Math.Round(r))} days" : "—", 30.4, 650, runwayLow ? Palette.WarnBrush : Palette.TextBrush, -0.035, numeric: true),
                "before Android deletes anything not yet collected", 70),
            Parts.FigureCard("Stored",
                Ui.Text(Format.Count(data.Records), 30.4, 650, spacing: -0.035, numeric: true),
                $"{data.Days} days · {data.Apps} uids", 140));
        cards.Margin = new Thickness(0, 0, 0, 18.4);
        page.Children.Add(cards);

        page.Children.Add(UploadsPanel(data));
        page.Children.Add(Ui.Panel("How this differs from the collector", "Why a late phone is not the same emergency it would be on the PC.", null, Notes()));
        return page;
    }

    private Border UploadsPanel(PhoneSyncData data)
    {
        var first = (_page - 1) * PageSize + 1;
        var last = Math.Min(_page * PageSize, (int)data.TotalUploads);
        var aside = data.TotalUploads > 0 ? Ui.Text($"{first}–{last} of {data.TotalUploads}", 15, 500, Palette.TextMutedBrush, numeric: true) : null;

        var table = new DataTable([new("Sent"), new("Status", Left: true), new("Buckets sent"), new("New"), new("Updated")]) { MinWidth = 760, RowPadding = 11 };
        foreach (var u in data.Uploads)
        {
            var when = new StackPanel();
            when.Children.Add(Parts.MonoCell(Format.DateTimeLocal(u.ReceivedAt), Palette.TextBrush, 14));
            if (u.LandedAt is { } landed)
            {
                var via = Ui.Text($"via inbox · landed {Format.DateTimeLocal(landed)}", 12.5, 400, Palette.TextFaintBrush);
                when.Children.Add(via);
            }
            var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            status.Children.Add(Ui.Badge(u.Status, u.Status == "success" ? Ui.BadgeKind.Ok : Ui.BadgeKind.Bad));
            if (u.Error is { } error)
            {
                var text = Ui.Mono(error.Length > 60 ? error[..60] + "…" : error, 13, Palette.TextFaintBrush);
                text.VerticalAlignment = VerticalAlignment.Center;
                Ui.SetTip(text, error);
                status.Children.Add(text);
            }
            table.AddRow([
                when,
                status,
                DataTable.Cell(Format.Count(u.BucketsSent)),
                u.RowsWritten > 0 ? DataTable.Cell($"+{Format.Count(u.RowsWritten)}", Palette.AccentBrightBrush, 600) : DataTable.Cell("0", Palette.TextFaintBrush),
                DataTable.Cell(Format.Count(u.RowsUpdated), Palette.TextMutedBrush),
            ]);
        }

        var body = new StackPanel();
        body.Children.Add(table.Build());
        var pages = Math.Max(1, (int)Math.Ceiling(data.TotalUploads / (double)PageSize));
        if (pages > 1) body.Children.Add(Parts.Pager(_page, pages, n =>
        {
            _page = n;
            ctx.Redraw();
        }, "uploads"));
        return Ui.Panel("Upload history",
            "An upload that stores 0 new rows is normal and healthy: the phone re-sends the most recent bucket every time, because it was still filling when it was last read. \"Updated\" is that bucket being replaced with its finished value.",
            aside, body);
    }

    private static StackPanel Notes()
    {
        var list = new StackPanel { Spacing = 10 };
        foreach (var (head, rest) in new[]
                 {
                     ($"Android keeps its own history for about {PhoneQueries.RetentionDays} days.", "The phone only has to be seen before that window closes, not promptly: collect faster than eviction, not faster than writing."),
                     ("The phone pushes; this PC only collects.", "Sync now empties the cloud inbox straight away, but it cannot make the phone upload. That happens on the phone's own schedule, or its Sync now."),
                     ("Unmetered networks only.", "A phone on mobile data for days will not have uploaded, by design: backfilling over the connection this app exists to measure would be a self-inflicted wound."),
                     ("Usage access can be revoked silently.", "If Android comes back with it off, uploads keep succeeding but carry only the reporter's own traffic. The phone app's own screen says so."),
                 })
        {
            var line = Ui.Text("", 15, 400, Palette.TextMutedBrush, wrap: true);
            line.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = head + " ", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Palette.TextBrush });
            line.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = rest });
            list.Children.Add(line);
        }
        return list;
    }
}
