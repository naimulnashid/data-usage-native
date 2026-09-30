using DataUsage.Core.Data;
using DataUsage.Core.Naming;
using DataUsage.Core.Query;
using DataUsage.Core.View;

namespace DataUsage.Core.Tests;

public class ViewTests
{
    [Fact]
    public void FillMarksQuietDaysZeroAndUncollectedDaysNull()
    {
        var rows = new[] { DailyPoint.Of("2026-09-01", 1, 1), DailyPoint.Of("2026-09-05", 2, 2) };
        var known = new List<DayRange> { new("2026-09-01", "2026-09-03") };
        var filled = Days.Fill(rows, "2026-09-01", "2026-09-05", known);
        Assert.Equal(5, filled.Count);
        Assert.Equal(0, filled[1].Total);          // known and quiet
        Assert.Equal(0, filled[2].Total);
        Assert.Null(filled[3].Total);               // never collected
        Assert.Equal(4, filled[4].Total);           // a row is proof enough
        Assert.Equal("4 days, 2 with traffic", Days.SpanLabel(filled));
    }

    [Fact]
    public void CoveredDaysNeedTheWholeDay()
    {
        var utc = TimeZoneInfo.Utc;
        string Local(DateTime t) => Srum.SrumFields.LocalBuckets(t, utc).Date;
        var windows = new[]
        {
            (new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 5, 10, 0, 0, DateTimeKind.Utc)),
            (new DateTime(2026, 9, 4, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 8, 3, 0, 0, DateTimeKind.Utc)),
            (new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc)),
        };
        var ranges = Days.Covered(windows, Local);
        // Overlapping windows merge; partial first and last days stay out.
        Assert.Equal([new DayRange("2026-09-02", "2026-09-07"), new DayRange("2026-09-20", "2026-09-21")], ranges);
    }

    [Fact]
    public void HoursFillToTwentyFour()
    {
        var hours = Days.FillHours([new HourPoint(5, 1, 1), new HourPoint(8, 1, 1)]);
        Assert.Equal(24, hours.Count);
        Assert.Equal(0, hours[6].Total);
        Assert.Empty(Days.FillHours([]));
    }

    [Fact]
    public void HeatmapBlocksAreContiguousAndSaturdayFirst()
    {
        var today = new DateOnly(2026, 9, 30);
        var recent = Heatmap.Recent([("2026-09-29", 10)], today);
        Assert.Equal(Heatmap.Weeks * 7, recent.Cells.Count);
        Assert.Equal(DayOfWeek.Saturday, Days.Parse(recent.Cells[0].Date).DayOfWeek);
        Assert.True(recent.Cells.Single(c => c.Date == "2026-10-01").Hidden);
        Assert.True(recent.Cells.Single(c => c.Date == "2026-09-29").Known);

        // The page opens on the earliest day with data, not on a fixed date.
        var blocks = Heatmap.Expanded([("2026-06-27", 5)], "2026-06-27", today);
        Assert.Single(blocks);
        Assert.Equal("2026-06-27", blocks[0].First);
        Assert.False(blocks[0].Cells[0].Hidden);
        Assert.Equal("Jun 27 – Dec 25, 2026", Heatmap.BlockLabel(blocks[0]));

        var later = Heatmap.Expanded([], "2026-06-27", new DateOnly(2027, 1, 10));
        Assert.Equal(2, later.Count);
        Assert.Equal(Days.Add(later[0].Last, 1), later[1].Cells[0].Date);

        // Mid-week: the days before it in its first week are hidden, not "no data".
        var midWeek = Heatmap.Expanded([], "2026-07-01", today);
        Assert.Equal("2026-07-01", midWeek[0].First);
        Assert.True(midWeek[0].Cells.Where(c => string.CompareOrdinal(c.Date, "2026-07-01") < 0).All(c => c.Hidden));
        Assert.Equal("2026-06-27", midWeek[0].Cells[0].Date);

        Assert.Single(Heatmap.Expanded([], null, today));
    }

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(1536, "1.50 KB")]
    [InlineData(15 * 1024 * 1024, "15.0 MB")]
    [InlineData(1_164.4 * 1024 * 1024 * 1024, "1.14 TB")]
    public void BytesAreBinaryUnits(double bytes, string expected) => Assert.Equal(expected, Format.Bytes(bytes));

    [Fact]
    public void UploadTintDarkensNearWhiteAndLightensTheRest()
    {
        Assert.Equal("#888888", AppColors.UploadTint("#DCDCDC"));
        Assert.Equal("#ff4747", AppColors.UploadTint("#FF0000"));
        Assert.Equal("var(--x)", AppColors.UploadTint("var(--x)"));
    }

    [Fact]
    public void ColoursAreBrandFirstThenByRank()
    {
        var map = AppColors.Assign(["qBittorrent", "Some App", "Other", "Another"]);
        Assert.Equal("#2F6790", map["qBittorrent"]);
        Assert.Equal("#ec4899", map["Some App"]);
        Assert.Equal(AppColors.Other, map["Other"]);
        Assert.Equal("#22d3ee", map["Another"]);
    }

    [Fact]
    public void DetailPageGate()
    {
        Assert.True(UsageQueries.EarnsDetailPage(300L << 20, 1));     // volume
        Assert.True(UsageQueries.EarnsDetailPage(11L << 20, 56));     // persistence
        Assert.False(UsageQueries.EarnsDetailPage(59L << 20, 1));     // one-shot installer
        Assert.False(UsageQueries.EarnsDetailPage(9L << 20, 30));
    }

    [Fact]
    public void NetworkObservationsResolveOnlyInACleanHour()
    {
        using var t = new TestDb();
        UsageDb.InsertRows(t.Conn,
        [
            TestDb.Row("2026-09-01T10:01:00Z", 1, "", 1, 1, "268435457"),
            TestDb.Row("2026-09-01T11:01:00Z", 1, "", 1, 1, "268435457"),
            TestDb.Row("2026-09-01T11:30:00Z", 1, "", 1, 1, "268435483"),
            // Profiles are listed from app rows.
            TestDb.Row("2026-09-01T10:01:00Z", 5, "DoSvc", 1, 1, "268435457"),
            TestDb.Row("2026-09-01T11:30:00Z", 5, "DoSvc", 1, 1, "268435483"),
        ]);
        NetworkNames.Record(t.Conn, [new Connection("HomeNet-5G", "Wi-Fi")], "2026-09-01T10:20:00.000Z");
        NetworkNames.Record(t.Conn, [new Connection("Cafe", "Wi-Fi")], "2026-09-01T11:20:00.000Z");
        NetworkNames.Record(t.Conn, [new Connection("Later", "Wi-Fi")], "2026-09-01T15:20:00.000Z");
        Assert.False(NetworkNames.Record(t.Conn, [new Connection("A", "Wi-Fi"), new Connection("B", "Ethernet")], "2026-09-01T10:30:00.000Z"));

        var notes = NetworkNames.Resolve(t.Conn);
        Assert.Equal(2, notes.Count);   // "Later" waits for its hour
        var profiles = new UsageQueries(t.Path_).Profiles();
        Assert.Contains(profiles, p => p.Id == "268435457" && p.Label == "HomeNet-5G" && p.Named);
        Assert.Contains(profiles, p => p.Id == "268435483" && !p.Named);  // ambiguous hour: not named
    }

    [Fact]
    public void RenamesAreValidatedAndKeepTheOriginalColour()
    {
        using var t = new TestDb();
        UsageDb.InsertRows(t.Conn,
        [
            TestDb.Row("2026-09-01T10:01:00Z", 617, @"\device\harddiskvolume4\x\msedge.exe", 1, 100),
            TestDb.Row("2026-09-01T10:01:00Z", 618, @"\device\harddiskvolume4\x\chrome.exe", 1, 10),
        ]);
        var q = new UsageQueries(t.Path_);
        var current = q.AppNamesByKey().ToDictionary(kv => kv.Key, kv => kv.Value.Name);
        Assert.Equal(RenameError.Taken, Renames.Save(t.Conn, "edge", "chrome", current, "Microsoft Edge").Error);
        Assert.Equal(RenameError.Reserved, Renames.Save(t.Conn, "edge", "Other", current, "Microsoft Edge").Error);
        Assert.Equal(RenameError.BadName, Renames.Save(t.Conn, "edge", new string('x', 61), current, "Microsoft Edge").Error);
        Assert.Equal(RenameError.UnknownApp, Renames.Save(t.Conn, "nope", "x", current, "x").Error);
        Assert.Equal((RenameError.None, "Edge"), Renames.Save(t.Conn, "edge", "  Edge\t ", current, "Microsoft Edge"));

        Assert.Equal("Edge", q.ByApp(Scope.All).Apps[0].Name);
        Assert.Equal("#0078D7", q.ColorMap()["Edge"]);

        Renames.Save(t.Conn, "edge", "", current, "Microsoft Edge");
        Assert.Equal("Microsoft Edge", q.ByApp(Scope.All).Apps[0].Name);
    }

    [Theory]
    [InlineData(6, 12, "1 … 4 5 6 7 8 … 12")]
    [InlineData(2, 12, "1 2 3 4 … 12")]
    [InlineData(12, 12, "1 … 10 11 12")]
    [InlineData(4, 12, "1 2 3 4 5 6 … 12")]
    [InlineData(3, 5, "1 2 3 4 5")]
    [InlineData(1, 1, "1")]
    [InlineData(99, 12, "1 … 10 11 12")]
    public void PagerShowsTheEndsAndItsNeighbours(int page, int count, string expected) =>
        Assert.Equal(expected, string.Join(" ", Pages.Items(page, count).Select(i => i is { } n ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : "…")));

    [Theory]
    [InlineData("#2F80ED", "#2f80ed")]
    [InlineData("2f80ed", "#2f80ed")]
    [InlineData(" #abc ", "#aabbcc")]
    [InlineData("red", null)]
    [InlineData("#12345", null)]
    [InlineData("#1234567", null)]
    [InlineData("#2f80ed;x", null)]
    [InlineData("", null)]
    public void ColoursAcceptOnlyAHexCode(string raw, string? expected) =>
        Assert.Equal(expected, ColorOverrides.Clean(raw));

    [Fact]
    public void AChosenColourWinsAndSurvivesARename()
    {
        using var t = new TestDb();
        UsageDb.InsertRows(t.Conn,
        [
            TestDb.Row("2026-09-01T10:01:00Z", 617, @"\device\harddiskvolume4\x\msedge.exe", 1, 100),
            TestDb.Row("2026-09-01T10:01:00Z", 618, @"\device\harddiskvolume4\x\chrome.exe", 1, 10),
        ]);
        var q = new UsageQueries(t.Path_);
        var known = q.AppNamesByKey().Keys.ToHashSet();

        Assert.Equal(ColorError.UnknownApp, ColorOverrides.Save(t.Conn, "nope", "#123456", known).Error);
        Assert.Equal(ColorError.BadColor, ColorOverrides.Save(t.Conn, "edge", "blue", known).Error);
        Assert.Equal((ColorError.None, "#e91e63"), ColorOverrides.Save(t.Conn, "edge", "#E91E63", known));
        Assert.Equal("#e91e63", q.ColorMap()["Microsoft Edge"]);
        Assert.Equal("#e91e63", q.ColorOverrideMap()["edge"]);

        // Under a new name too: the override is keyed by the family, not the label.
        var current = q.AppNamesByKey().ToDictionary(kv => kv.Key, kv => kv.Value.Name);
        Renames.Save(t.Conn, "edge", "Edge", current, "Microsoft Edge");
        Assert.Equal("#e91e63", q.ColorMap()["Edge"]);

        // An empty colour clears it, back to the brand.
        Assert.Equal((ColorError.None, null), ColorOverrides.Save(t.Conn, "edge", "", known));
        Assert.Equal("#0078D7", q.ColorMap()["Edge"]);
        Assert.Empty(q.ColorOverrideMap());
    }
}
