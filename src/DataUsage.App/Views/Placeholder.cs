using DataUsage.Core.Collect;
using DataUsage.Core.Query;
using DataUsage.Core.View;

namespace DataUsage.App.Views;

/// <summary>
/// Stand-in data for the loading skeletons (<see cref="Skeleton"/>): a page is
/// built by its own <c>Build()</c> from these, then turned into a skeleton, so
/// every height in it comes from the real layout code.
/// </summary>
/// <remarks>
/// Shaped like a typical history - 100 days, the top eight apps and Other, a
/// handful of networks, eleven runs - because what can differ from the real
/// page is only what depends on the data: how many rows a list has, whether a
/// legend wraps. Nothing here is anyone's usage.
/// </remarks>
public static class Placeholder
{
    private const long GB = 1L << 30, MB = 1L << 20;

    private static readonly string[] Apps =
        ["qBittorrent", "Microsoft Edge", "Google Drive", "Microsoft Teams", "Chrome", "Discord", "Node.js", "VS Code"];

    private static string Day(int daysAgo) => DateOnly.FromDateTime(DateTime.Now).AddDays(-daysAgo).ToString("yyyy-MM-dd");

    public static List<DailyPoint> Daily(int days = 100) =>
        Enumerable.Range(0, days).Reverse().Select(i =>
        {
            var received = (2 + (i * 7 % 11)) * GB;
            return new DailyPoint(Day(i), received / 6, received, received + received / 6);
        }).ToList();

    public static List<HourPoint> Hourly() =>
        Enumerable.Range(0, 24).Select(h => new HourPoint(h, (2 + h % 5) * GB, (20 + h % 7) * GB)).ToList();

    public static List<(string Date, long Total)> Heat(int days = 100) =>
        Enumerable.Range(0, days).Select(i => (Day(i), (1 + i % 9) * GB)).ToList();

    public static OverviewData Overview(string? split)
    {
        var daily = Daily();
        return new OverviewData(
            Today: new Totals(140 * MB, 755 * MB),
            Week: new Totals(10 * GB, 44 * GB),
            Month: new Totals(32 * GB, 146 * GB),
            All: new Totals(120 * GB, 562 * GB),
            Coverage: new Coverage(daily[0].Date, daily[^1].Date, daily.Count),
            Peak: new Peak(daily[40].Date, 28 * GB),
            Daily: daily,
            SplitApp: split,
            SplitFocus: split is null ? 0 : 362 * GB,
            SplitOther: split is null ? 0 : 319 * GB,
            LatestDate: daily[^1].Date,
            MeanDaily: 6.0 * GB);
    }

    public static TimelineData Timeline()
    {
        var series = Apps.Append("Other").ToList();
        var points = Daily().Select(d => (d.Date, series.Select((_, i) => (long?)((i + 1) * 100 * MB)).ToArray())).ToList();
        return new TimelineData(points, series, Hourly());
    }

    public static NetworkBreakdown Networks() => new(
        681 * GB,
        [(LinkKind.Wifi, 681 * GB), (LinkKind.Wired, 0)],
        [
            new NetworkRow("1", "HomeNet-5G", true, ["HomeNet"], 660 * GB),
            new NetworkRow("2", "Corner Café Guest", true, [], 19 * GB),
            new NetworkRow("3", "Wi-Fi", false, [], 1 * GB),
        ]);

    public static ByAppData ByApp()
    {
        var rows = Apps.Concat(["System and Windows Update", "Telegram", "Zoom", "Microsoft Store", "Git", "DNS Client", "Cryptographic Services", "Setup"])
            .Select((name, i) => new AppRow
            {
                Key = "placeholder-" + i,
                Name = name,
                BaseName = name,
                Kind = "path",
                Sent = (16 - i) * GB / 8,
                Received = (16 - i) * GB,
                Share = 100.0 / (i + 2),
                Rows = 1000 + i,
                Days = 90,
                // The last is too small for a page, as one usually is: that is
                // what puts the table's "Show all" footer under it.
                Detailed = i < 15,
            }).ToList();
        return new ByAppData(rows, 681 * GB, 896 * MB, 682 * GB);
    }

    public static AppDetail App(string key) => new(
        key, "Microsoft Edge", "Microsoft Edge", "path", new Totals(8 * GB, 129 * GB), 20.2, 94, 2838,
        Day(99), Day(0), new Peak(Day(40), 2 * GB), Daily(), Hourly(),
        [new AppNetwork("1", "HomeNet-5G", 130 * GB), new AppNetwork("2", "Corner Café Guest", 6 * GB), new AppNetwork("3", "Wi-Fi, unnamed", 185 * MB)],
        [], []);

    public static SyncData Sync()
    {
        var runs = Enumerable.Range(0, 11).Select(i =>
        {
            var at = DateTime.UtcNow.AddDays(-7 * i).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            return new SyncRun(11 - i, at, at, "success", 11000, 1200, 9800, at, at, "ok", 24000, null);
        }).ToList();
        return new SyncData(runs, runs.Count, runs[0], 1, 18315, new Coverage(Day(96), Day(0), 97), 0);
    }

    public static TaskInfo Task() => new(true, false, DateTime.UtcNow, 0, DateTime.UtcNow.AddMinutes(15));
}
