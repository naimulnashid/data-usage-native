using DataUsage.Core.Data;
using DataUsage.Core.Naming;
using DataUsage.Core.Query;

namespace DataUsage.Core.Tests;

/// <summary>Hiding and merging apps (Naming/AppPrefs.cs).</summary>
public sealed class PrefsTests
{
    private static (TestDb Db, UsageQueries Q, string Chrome, string Brave) Three()
    {
        var t = new TestDb();
        UsageDb.InsertRows(t.Conn,
        [
            TestDb.Row("2026-09-01T10:01:00Z", 617, @"\device\harddiskvolume4\x\msedge.exe", 1, 100),
            TestDb.Row("2026-09-01T10:01:00Z", 618, @"\device\harddiskvolume4\x\chrome.exe", 1, 10),
            TestDb.Row("2026-09-02T10:01:00Z", 619, @"\device\harddiskvolume4\x\brave.exe", 1, 5),
        ]);
        var q = new UsageQueries(t.Path_);
        var names = q.AppNamesByKey();
        return (t, q, names.First(kv => kv.Value.Base == "Chrome").Key, names.First(kv => kv.Value.Base == "Brave").Key);
    }

    [Fact]
    public void AMergedAppIsReadAsPartOfItsTarget()
    {
        var (t, q, chrome, _) = Three();
        using var _t = t;
        Assert.Equal(MergeError.None, AppPrefs.SetMerge(t.Conn, chrome, "edge", q.AppKeys()));

        var apps = q.ByApp(Scope.All).Apps;
        Assert.Equal(2, apps.Count);
        var edge = apps.Single(a => a.Key == "edge");
        Assert.Equal(112, edge.Total);
        var detail = q.AppDetailFor("edge", Scope.All)!;
        Assert.Contains(detail.Members, m => m.MergedFrom?.Key == chrome);
        Assert.Equal("edge", q.MergedInto(chrome));
        // Totals do not move: the headline is the aggregate rows'.
        Assert.Equal(118, q.ByApp(Scope.All).NamedTotal);

        Assert.Equal(MergeError.None, AppPrefs.SetMerge(t.Conn, chrome, null, q.AppKeys()));
        Assert.Equal(3, q.ByApp(Scope.All).Apps.Count);
    }

    [Fact]
    public void MergesStayOneLevelDeep()
    {
        var (t, q, chrome, brave) = Three();
        using var _t = t;
        var keys = q.AppKeys();
        AppPrefs.SetMerge(t.Conn, chrome, "edge", keys);
        // Into a merged app: stored against its target.
        AppPrefs.SetMerge(t.Conn, brave, chrome, keys);
        Assert.Equal("edge", AppPrefs.ReadMerges(t.Conn)[brave]);
        Assert.Single(q.ByApp(Scope.All).Apps);
        // Edge into an app merged into Edge would be a loop: refused.
        Assert.Equal(MergeError.SameApp, AppPrefs.SetMerge(t.Conn, "edge", chrome, keys));
        // Separating one leaves the other where it was.
        AppPrefs.SetMerge(t.Conn, chrome, null, keys);
        Assert.Equal("edge", AppPrefs.ReadMerges(t.Conn)[brave]);
        // Merging the target away carries what was merged into it along.
        AppPrefs.SetMerge(t.Conn, "edge", chrome, keys);
        var merges = AppPrefs.ReadMerges(t.Conn);
        Assert.Equal(chrome, merges["edge"]);
        Assert.Equal(chrome, merges[brave]);
        Assert.Equal(MergeError.UnknownApp, AppPrefs.SetMerge(t.Conn, "nope", "edge", keys));
    }

    [Fact]
    public void HidingFlagsTheAppAndChangesNoFigure()
    {
        var (t, q, chrome, _) = Three();
        using var _t = t;
        var before = q.ByApp(Scope.All);
        AppPrefs.SetHidden(t.Conn, chrome, true);
        var after = q.ByApp(Scope.All);
        Assert.True(after.Apps.Single(a => a.Key == chrome).Hidden);
        Assert.Equal(before.NamedTotal, after.NamedTotal);
        // A merge takes the app off the hidden list: it is no longer listed.
        AppPrefs.SetMerge(t.Conn, chrome, "edge", q.AppKeys());
        Assert.Empty(AppPrefs.ReadHidden(t.Conn));
    }

    [Fact]
    public void EveryRangeOfDailyByAppRanksItsOwnTopApps()
    {
        var (t, q, _, _) = Three();
        using var _t = t;
        var timeline = q.Timeline(topN: 1);
        Assert.Equal(Scope.Ranges.OrderBy(x => x), timeline.Ranges.Keys.OrderBy(x => x));
        // The newest day held only Brave; the seven-day range still reaches
        // the day before, where Edge is the largest.
        Assert.Equal("Microsoft Edge", timeline.Ranges[7].Series[0]);
    }
}
