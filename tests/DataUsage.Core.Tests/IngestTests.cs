using DataUsage.Core.Data;
using DataUsage.Core.Query;
using DataUsage.Core.Srum;

namespace DataUsage.Core.Tests;

public class IngestTests
{
    private const string Edge = @"\device\harddiskvolume4\program files (x86)\microsoft\edge\application\msedge.exe";
    private const string Drive1 = @"\device\harddiskvolume4\program files\google\drive file stream\128.0.0.0\googledrivefs.exe";
    private const string Drive2 = @"\device\harddiskvolume4\program files\google\drive file stream\129.0.1.0\googledrivefs.exe";

    [Fact]
    public void ReingestingTheSameRowsInsertsNothing()
    {
        using var t = new TestDb();
        var rows = new[] { TestDb.Row("2026-09-01T10:01:00Z", 617, Edge, 10, 90), TestDb.Row("2026-09-01T10:01:00Z", 1, "", 10, 90) };
        Assert.Equal(2, UsageDb.InsertRows(t.Conn, rows));
        Assert.Equal(0, UsageDb.InsertRows(t.Conn, rows));
    }

    [Fact]
    public void OffCadenceFlushWithDifferentBytesIsKept()
    {
        // Trap 6: same app, hour, adapter and profile - a distinct measurement.
        using var t = new TestDb();
        var rows = new[] { TestDb.Row("2026-09-01T18:40:00Z", 617, Edge, 11810, 0), TestDb.Row("2026-09-01T18:40:00Z", 617, Edge, 977002503, 0) };
        Assert.Equal(2, UsageDb.InsertRows(t.Conn, rows));
    }

    [Fact]
    public void SameMomentOnAnotherProfileIsKept()
    {
        using var t = new TestDb();
        var rows = new[] { TestDb.Row("2026-09-01T10:01:00Z", 617, Edge, 5, 5, "268435457"), TestDb.Row("2026-09-01T10:01:00Z", 617, Edge, 5, 5, "268435483") };
        Assert.Equal(2, UsageDb.InsertRows(t.Conn, rows));
    }

    [Fact]
    public void TotalsComeFromTheAggregateAndAreNeverAddedToApps()
    {
        // Trap 4: AppId 1 equals the sum of the apps. Summing all rows doubles it.
        using var t = new TestDb();
        UsageDb.InsertRows(t.Conn,
        [
            TestDb.Row("2026-09-01T10:01:00Z", 617, Edge, 100, 900),
            TestDb.Row("2026-09-01T10:01:00Z", 700, Drive1, 50, 50),
            TestDb.Row("2026-09-01T10:01:00Z", 1, "", 151, 951),
        ]);
        var q = new UsageQueries(t.Path_);
        var o = q.Overview(Scope.All);
        Assert.Equal(1102, o.All.Total);
        var apps = q.ByApp(Scope.All);
        Assert.Equal(1100, apps.NamedTotal);
        Assert.Equal(2, apps.Unattributed);
        Assert.Equal(1102, apps.HeadlineTotal);
    }

    [Fact]
    public void VersionedInstallPathsAreOneAppWithDaysCountedOnce()
    {
        using var t = new TestDb();
        UsageDb.InsertRows(t.Conn,
        [
            TestDb.Row("2026-09-01T10:01:00Z", 700, Drive1, 1, 1),
            TestDb.Row("2026-09-01T11:01:00Z", 701, Drive2, 1, 1),
            TestDb.Row("2026-09-02T11:01:00Z", 701, Drive2, 1, 1),
        ]);
        var apps = new UsageQueries(t.Path_).ByApp(Scope.All).Apps;
        var drive = Assert.Single(apps);
        Assert.Equal("Google Drive", drive.Name);
        Assert.Equal(2, drive.Days);
        Assert.Equal(6, drive.Total);
    }

    [Fact]
    public void RefusesTheSystemDrive()
    {
        var path = Path.Combine(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!, "DataUsageNative-guard-test", "x.db");
        Assert.Throws<InvalidOperationException>(() => UsageDb.Open(path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [Fact]
    public void BackupIsOneSelfContainedFile()
    {
        using var t = new TestDb();
        UsageDb.InsertRows(t.Conn, [TestDb.Row("2026-09-01T10:01:00Z", 1, "", 1, 1)]);
        var backup = Path.Combine(t.Dir, "data-usage.db");
        Assert.Equal("ok", UsageDb.Backup(t.Conn, backup));
        Assert.True(File.Exists(backup));
        Assert.False(File.Exists(backup + "-wal"));
        using var b = UsageDb.OpenRead(backup);
        using var cmd = b.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM usage_records";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
    }

    [Theory]
    [InlineData(1, "", "aggregate")]
    [InlineData(5, "", "unknown")]
    [InlineData(5, @"\device\harddiskvolume4\x\a.exe", "path")]
    [InlineData(5, "OpenAI.Codex_26.814.5517.0_x64__2p2nqsd0c76g0", "appx")]
    [InlineData(5, "DoSvc", "service")]
    public void ClassifiesTheThreeIdentityKinds(long appId, string identity, string kind) =>
        Assert.Equal(kind, SrumFields.ClassifyApp(appId, identity));

    [Fact]
    public void DecodesSidsAndInterfaceTypes()
    {
        // An invented account SID, laid out as SRUM stores one.
        var sid = new byte[] { 1, 5, 0, 0, 0, 0, 0, 5 }
            .Concat(BitConverter.GetBytes(21u)).Concat(BitConverter.GetBytes(1111111111u))
            .Concat(BitConverter.GetBytes(2222222222u)).Concat(BitConverter.GetBytes(3333333333u)).Concat(BitConverter.GetBytes(1001u)).ToArray();
        Assert.Equal("S-1-5-21-1111111111-2222222222-3333333333-1001", SrumFields.DecodeSid(sid));
        Assert.Equal("IF_TYPE_IEEE80211", SrumFields.InterfaceType(19985273102270464));
        Assert.Equal("0", SrumFields.InterfaceType(0));
        Assert.Equal("IF_TYPE_ETHERNET_CSMACD", SrumFields.InterfaceType(6L << 48 | 3));
    }

    [Fact]
    public void LocalBucketsUseTheLocalZoneNotUtc()
    {
        var dhaka = TimeZoneInfo.CreateCustomTimeZone("UTC+6", TimeSpan.FromHours(6), "UTC+6", "UTC+6");
        Assert.Equal(("2026-09-02", 2), SrumFields.LocalBuckets(new DateTime(2026, 9, 1, 20, 1, 0, DateTimeKind.Utc), dhaka));
    }
}
