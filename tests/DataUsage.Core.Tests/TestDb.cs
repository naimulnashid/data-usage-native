using DataUsage.Core.Data;
using DataUsage.Core.Srum;
using Microsoft.Data.Sqlite;

namespace DataUsage.Core.Tests;

/// <summary>A throwaway database in TEMP, removed afterwards.</summary>
internal sealed class TestDb : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "DataUsageNative-test-" + Guid.NewGuid().ToString("N"));
    public string Path_ => System.IO.Path.Combine(Dir, "live", "data-usage.db");
    public SqliteConnection Conn { get; }

    public TestDb()
    {
        Conn = UsageDb.Open(Path_, allowSystemDrive: true);
    }

    public void Dispose()
    {
        Conn.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, true); } catch (IOException) { }
    }

    /// <summary>A row as SRUM would give it, local buckets computed in UTC for determinism.</summary>
    public static UsageRow Row(string utc, long appId, string identity, long sent, long received, string profile = "268435457", string iface = "IF_TYPE_IEEE80211")
    {
        var t = DateTime.Parse(utc, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
        var (date, hour) = SrumFields.LocalBuckets(t, TimeZoneInfo.Utc);
        return new UsageRow(SrumFields.IsoUtc(t), date, hour, appId, appId == 1, identity, SrumFields.ClassifyApp(appId, identity),
            appId == 1 ? "2" : "511", appId == 1 ? "" : "S-1-5-21-1-2-3-1001", "19985273102270464", iface, profile, sent, received);
    }

    public void Run(string oldestUtc, string newestUtc)
    {
        var id = UsageDb.StartRun(Conn);
        UsageDb.FinishRun(Conn, id, new SyncResult { Status = "success", SrumOldestUtc = oldestUtc, SrumNewestUtc = newestUtc });
    }
}
