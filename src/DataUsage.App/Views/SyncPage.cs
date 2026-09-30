using DataUsage.App.Controls;
using DataUsage.App.Theme;
using DataUsage.Core;
using DataUsage.Core.Collect;
using DataUsage.Core.Query;
using DataUsage.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DataUsage.App.Views;

/// <summary>
/// Sync Status: whether the history is actually being kept. The guarantee this
/// app exists for depends on the scheduled task running, and this page is how
/// a broken task gets noticed while SRUM still holds the days it missed.
/// </summary>
public sealed class SyncPage(PageContext ctx) : IPage
{
    private const int PageSize = 25;
    private SyncData? _data;
    private TaskInfo? _collector, _snapshot;
    private int _page = 1;

    public void Load()
    {
        var q = ctx.State.Queries!;
        var first = q.Sync(PageSize, 0);
        var pages = Math.Max(1, (int)Math.Ceiling(first.TotalRuns / (double)PageSize));
        _page = Math.Clamp(_page, 1, pages);
        _data = _page == 1 ? first : q.Sync(PageSize, (_page - 1) * PageSize);
        try
        {
            _collector = ScheduledTasks.Query(ScheduledTasks.CollectorTask);
            _snapshot = ScheduledTasks.Query(ScheduledTasks.SnapshotTask);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            _collector = _snapshot = null;
        }
    }

    public UIElement Build()
    {
        var data = _data!;
        var page = new StackPanel();
        page.Children.Add(Parts.PageHead("Sync Status", "The history survives a Windows reset only if the scheduled task actually runs. This page is how a broken task gets noticed in time."));

        var hours = data.HoursSinceSuccess;
        // Hourly cadence. A few hours is a laptop that slept - the task catches
        // up on waking - while 48h+ means the task itself has stopped.
        var stale = hours > 48;
        var warn = hours > 3 && !stale;
        var broken = data.ConsecutiveFailures >= 2;
        var missing = _collector is { Exists: false } || _snapshot is { Exists: false };

        if (missing)
        {
            page.Children.Add(Parts.Alert("The scheduled tasks are not registered",
                "Nothing is collecting. The tasks live in the Windows task store, which a reset wipes; installing registers them again.",
                "Run `tools\\Install.ps1` from the source folder (one administrator prompt), or reinstall Data Usage."));
        }
        else if (stale || broken)
        {
            page.Children.Add(Parts.Alert(broken ? "Collector is failing" : "Collector may have stopped",
                broken
                    ? $"The last {data.ConsecutiveFailures} runs failed. New usage is not being saved, and SRUM evicts its own history after roughly 30 to 60 days."
                    : $"No successful run in {Math.Round(hours!.Value)} hours. Expected cadence is hourly.",
                $"Check the log in `{AppPaths.LogsDir}`, then use Sync now to try a collection by hand."));
        }

        var lastColor = stale || broken ? Palette.WarnBrush : Palette.AccentBrightBrush;
        var backup = data.LastSuccess?.BackupStatus;
        var cards = Parts.Grid(260,
            Parts.FigureCard("Last successful run", Ui.Text(hours is { } hh ? Format.Relative(hh) : "never", 30.4, 650, lastColor, -0.035), data.LastSuccess is { } ls ? Format.DateTimeLocal(ls.StartedAt) : "no successful run recorded"),
            Parts.FigureCard("Rows stored", Ui.Text(Format.Count(data.TotalRows), 30.4, 650, spacing: -0.035, numeric: true), data.Coverage is { } c ? $"{c.Days} days · {c.First} → {c.Last}" : "—", 70),
            Parts.FigureCard("Last backup", Ui.Text(backup == "ok" ? "OK" : backup ?? "—", 30.4, 650, backup == "ok" ? Palette.GoodBrush : Palette.TextMutedBrush, -0.035), "Restore from the backup, not the live file", 140));
        cards.Margin = new Thickness(0, 0, 0, 18.4);
        page.Children.Add(cards);

        page.Children.Add(RunsPanel(data));
        page.Children.Add(TasksPanel());

        if (warn)
        {
            var note = Ui.Text($"Last run was {Math.Round(hours!.Value)} hours ago, against an hourly cadence. The task catches up when the machine is next on, so after sleep this resolves itself.", 15, 400, Palette.TextMutedBrush, wrap: true);
            page.Children.Add(note);
        }
        return page;
    }

    private Border RunsPanel(SyncData data)
    {
        var first = (_page - 1) * PageSize + 1;
        var last = Math.Min(_page * PageSize, (int)data.TotalRuns);
        var aside = data.TotalRuns > 0 ? Ui.Text($"{first}–{last} of {data.TotalRuns}", 15, 500, Palette.TextMutedBrush, numeric: true) : null;

        var table = new DataTable([
            new("Started"), new("Status", Left: true), new("Read"), new("New"), new("Duplicate"), new("Took"), new("Backup", Left: true),
        ]) { MinWidth = 760, RowPadding = 11 };
        foreach (var r in data.Runs)
        {
            var status = Ui.Badge(r.Status, r.Status == "success" ? Ui.BadgeKind.Ok : r.Status == "failed" ? Ui.BadgeKind.Bad : Ui.BadgeKind.Plain);
            table.AddRow([
                Parts.MonoCell(Format.DateTimeLocal(r.StartedAt), Palette.TextBrush, 14),
                status,
                DataTable.Cell(Format.Count(r.RowsRead)),
                r.RowsInserted > 0 ? DataTable.Cell($"+{Format.Count(r.RowsInserted)}", Palette.AccentBrightBrush, 600) : DataTable.Cell("0", Palette.TextFaintBrush),
                DataTable.Cell(Format.Count(r.RowsSkipped), Palette.TextFaintBrush),
                DataTable.Cell(Format.Duration(r.DurationMs), Palette.TextMutedBrush),
                DataTable.Cell(r.BackupStatus ?? "—", r.BackupStatus == "ok" ? Palette.GoodBrush : Palette.TextMutedBrush, size: 15),
            ]);
        }

        var body = new StackPanel();
        body.Children.Add(table.Build());

        var pages = Math.Max(1, (int)Math.Ceiling(data.TotalRuns / (double)PageSize));
        if (pages > 1)
        {
            var pager = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0) };
            var newer = Ui.Chip("← Newer", false);
            newer.IsEnabled = _page > 1;
            newer.Click += (_, _) => { _page--; ctx.Redraw(); };
            var older = Ui.Chip("Older →", false);
            older.IsEnabled = _page < pages;
            older.Click += (_, _) => { _page++; ctx.Redraw(); };
            pager.Children.Add(newer);
            var label = Ui.Text($"Page {_page} of {pages}", 15, 400, Palette.TextMutedBrush);
            label.VerticalAlignment = VerticalAlignment.Center;
            pager.Children.Add(label);
            pager.Children.Add(older);
            body.Children.Add(pager);
        }

        var errors = data.Runs.Where(r => r.Error is not null).Take(5).ToList();
        if (errors.Count > 0)
        {
            var box = new StackPanel { Margin = new Thickness(0, 22, 0, 0), Spacing = 6 };
            var head = Parts.StatLabel("Recent errors");
            head.Margin = new Thickness(0, 0, 0, 4);
            box.Children.Add(head);
            foreach (var r in errors)
            {
                var line = Ui.Text("", 14, 400, Palette.WarnBrush, wrap: true);
                line.FontFamily = Fonts.Mono;
                line.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = Format.DateTimeLocal(r.StartedAt), Foreground = Palette.TextMutedBrush });
                line.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = " — " + r.Error });
                box.Children.Add(new Border { Background = Palette.WarnDimBrush, CornerRadius = new CornerRadius(Ui.RadiusSmall), Padding = new Thickness(13, 10, 13, 10), Child = line });
            }
            body.Children.Add(box);
        }

        return Ui.Panel("Run history", "A run that inserts 0 rows is normal and healthy — it means nothing new had accumulated since the last one.", aside, body);
    }

    /// <summary>The two scheduled tasks as Windows reports them - the part the web page could not see.</summary>
    private Border TasksPanel()
    {
        var table = new DataTable([new("Task"), new("Runs as", Left: true), new("Last run"), new("Result"), new("Next run")]) { MinWidth = 760, RowPadding = 11 };
        void Row(string name, string runsAs, TaskInfo? info)
        {
            if (info is not { Exists: true })
            {
                table.AddRow([DataTable.Cell(name, numeric: false), DataTable.Cell(runsAs, Palette.TextMutedBrush, numeric: false), DataTable.Cell("not registered", Palette.WarnBrush), null, null]);
                return;
            }
            var ok = info.LastResult == 0;
            table.AddRow([
                DataTable.Cell(name, numeric: false),
                DataTable.Cell(runsAs, Palette.TextMutedBrush, numeric: false),
                DataTable.Cell(info.LastRunUtc is { } t ? Format.DateTimeLocal(t.ToString("o")) : "—", Palette.TextMutedBrush),
                DataTable.Cell(info.Running ? "running" : ok ? "OK" : $"0x{info.LastResult:X}", info.Running ? Palette.AccentBrightBrush : ok ? Palette.GoodBrush : Palette.WarnBrush),
                DataTable.Cell(info.NextRunUtc is { } n ? Format.DateTimeLocal(n.ToString("o")) : "on demand", Palette.TextMutedBrush),
            ]);
        }
        Row(ScheduledTasks.CollectorTask, "you, not elevated", _collector);
        Row(ScheduledTasks.SnapshotTask, "Administrator", _snapshot);
        return Ui.Panel("Scheduled tasks",
            "The collector runs every 15 minutes and reads SRUM hourly. Only the snapshot runs elevated, and it runs nothing but Windows' own cmd.exe and esentutl.exe.",
            null, table.Build());
    }
}
