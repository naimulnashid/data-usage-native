using System.Globalization;
using System.Text.RegularExpressions;
using DataUsage.Core.Data;
using DataUsage.Core.Naming;
using DataUsage.Core.View;
using Microsoft.Data.Sqlite;

namespace DataUsage.Core.Query;

/// <summary>A phone that has reported, for the sidebar. <see cref="Total"/> excludes tethering.</summary>
public sealed record PhoneDevice(string Id, string Slug, string Label, string Release, int Sdk, string LastSeen, long Total);

/// <summary>What only a phone's overview shows. <see cref="Tethering"/> is never in any other figure.</summary>
public sealed record PhoneExtras(long Wifi, long Mobile, long Tethering, IReadOnlyList<HourPoint> Hourly);

public sealed record PhoneUpload(string ReceivedAt, string? LandedAt, string Status, long BucketsSent, long RowsWritten, long RowsUpdated, string? Error);

/// <param name="RunwayDays">Days of Android's ~90-day window left before it deletes something not yet collected; null with no rows.</param>
public sealed record PhoneSyncData(
    IReadOnlyList<PhoneUpload> Uploads,
    long TotalUploads,
    string? LastSuccessAt,
    double? HoursSinceSuccess,
    int ConsecutiveFailures,
    string? NewestBucket,
    long Records,
    long Apps,
    int Days,
    double? RunwayDays);

/// <summary>
/// Every read a phone's pages make, a port of the web dashboard's
/// <c>android-queries.ts</c>, in the PC pages' own models.
/// </summary>
/// <remarks>
/// <para><b>uid -5 is TETHERING, and it is already counted on the PC</b> when
/// the PC tethers off the phone: those bytes are in <c>usage_records</c> too,
/// under Windows app names. So it is kept out of every total and every app
/// list, and shown apart. The phone's analogue of the AppId 1 trap.</para>
/// <para>Every query binds the device id, so two phones never report each
/// other's numbers.</para>
/// <para>A day with no rows inside the phone's history (its first to its last
/// day with a row) moved nothing and is 0; outside it, nothing is known. The
/// span is contiguous by construction: the first sync backfills all Android
/// holds and every later one starts a bucket before the last confirmed.</para>
/// </remarks>
public sealed partial class PhoneQueries(string databasePath, string deviceId) : IDeviceQueries
{
    public const long UidTethering = -5;
    public const long UidRemoved = -4;

    /// <summary>What AOSP keeps of per-uid history.</summary>
    public const int RetentionDays = 90;

    /// <summary>Later than the 6-hour schedule implies, go and look.</summary>
    public const double SyncWarnHours = 72;

    /// <summary>Approaching the point where Android starts deleting what was never collected.</summary>
    public const double SyncCriticalHours = 60 * 24;

    private const string NotTether = "uid != -5";

    /// <summary>
    /// The packages that name a uid they share, standing in for
    /// <c>android:sharedUserLabel</c>, which the phone does not report. Without
    /// it Play services' traffic was listed as "Google Backup Transport".
    /// </summary>
    public static readonly HashSet<string> NamesSharedUid = ["com.google.android.gms"];

    public string DatabasePath { get; } = databasePath;

    public string DeviceId { get; } = deviceId;

    public bool DatabaseExists => File.Exists(DatabasePath);

    public string PrefsDevice => DeviceId;

    private T With<T>(Func<SqliteConnection, T> fn)
    {
        using var db = UsageDb.OpenRead(DatabasePath);
        return fn(db);
    }

    /// <summary>$p0 is always the device id.</summary>
    private List<T> Rows<T>(SqliteConnection db, string sql, Func<SqliteDataReader, T> map, params object?[] args)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$p0", DeviceId);
        for (var i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue($"$p{i + 1}", args[i] ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    private object? Scalar(SqliteConnection db, string sql, params object?[] args) =>
        Rows(db, sql, r => r.IsDBNull(0) ? null : r.GetValue(0), args).FirstOrDefault();

    private static long L(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : r.GetInt64(i);

    private static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    // ---- Devices -------------------------------------------------------------------

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();

    /// <summary>The web dashboard's <c>deviceSlug</c>: also the phone's logo folder.</summary>
    public static string Slug(string label)
    {
        var s = NonSlug().Replace(label.ToLowerInvariant(), "-").Trim('-');
        return s.Length > 0 ? s : "device";
    }

    /// <summary>Every phone that has reported, largest first; empty before any has.</summary>
    public static List<PhoneDevice> Devices(string databasePath)
    {
        if (!File.Exists(databasePath)) return [];
        try
        {
            using var db = UsageDb.OpenRead(databasePath);
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT d.device_id, d.label, d.android_release, d.sdk, d.last_seen,
                       (SELECT COALESCE(SUM(rx_bytes + tx_bytes), 0) FROM android_usage_records u WHERE u.device_id = d.device_id AND u.uid != -5)
                FROM android_devices d ORDER BY d.rowid
                """;
            var rows = new List<(string Id, string Label, string Release, int Sdk, string Seen, long Total)>();
            using (var r = cmd.ExecuteReader())
                while (r.Read()) rows.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetString(4), r.GetInt64(5)));
            // Two phones of one model would collide on a slug and share a
            // logo folder; the second gets a suffix, as on the web.
            var seen = new Dictionary<string, int>();
            return rows.Select(x =>
                {
                    var label = x.Label.Length > 0 ? x.Label : x.Id;
                    var slug = Slug(label);
                    var n = seen[slug] = seen.GetValueOrDefault(slug) + 1;
                    return new PhoneDevice(x.Id, n == 1 ? slug : $"{slug}-{n}", label, x.Release, x.Sdk, x.Seen, x.Total);
                })
                .OrderByDescending(d => d.Total).ToList();
        }
        catch (SqliteException)
        {
            // A database from before schema 4, opened read-only: no phones yet.
            return [];
        }
    }

    // ---- Names ---------------------------------------------------------------------

    private sealed record Names(Dictionary<long, (string Name, int Packages)> ByUid, Dictionary<string, string> Renames, Dictionary<string, string> Merges);

    private sealed record Label(long Uid, string Name, string BaseName, int Packages, bool Special);

    /// <summary>A listed package, then user apps before system ones, then by package name.</summary>
    private static int NamingOrder((string Package, bool IsSystem) a, (string Package, bool IsSystem) b)
    {
        var listed = (NamesSharedUid.Contains(b.Package) ? 1 : 0) - (NamesSharedUid.Contains(a.Package) ? 1 : 0);
        if (listed != 0) return listed;
        if (a.IsSystem != b.IsSystem) return a.IsSystem ? 1 : -1;
        return string.CompareOrdinal(a.Package, b.Package);
    }

    private Names NamesOf(SqliteConnection db)
    {
        var rows = Rows(db, "SELECT uid, label, package, is_system FROM android_apps WHERE device_id = $p0",
            r => (Uid: r.GetInt64(0), Label: r.GetString(1), Package: r.GetString(2), IsSystem: r.GetInt64(3) == 1));
        rows.Sort((a, b) => NamingOrder((a.Package, a.IsSystem), (b.Package, b.IsSystem)));
        var byUid = new Dictionary<long, (string, int)>();
        foreach (var r in rows)
        {
            if (byUid.TryGetValue(r.Uid, out var cur)) byUid[r.Uid] = (cur.Item1, cur.Item2 + 1);
            else byUid[r.Uid] = (r.Label.Length > 0 ? r.Label : r.Package, 1);
        }
        return new Names(byUid, Naming.Renames.Read(db, DeviceId), AppPrefs.ReadMerges(db, DeviceId));
    }

    private static (string Name, int Packages, bool Special) BaseLabel(long uid, Dictionary<long, (string Name, int Packages)> names)
    {
        if (uid == UidTethering) return ("Tethering / hotspot", 0, true);
        if (uid == UidRemoved) return ("Uninstalled apps", 0, true);
        if (names.TryGetValue(uid, out var direct)) return (direct.Name, direct.Packages, false);
        // A cloned app carries user profile 999 in its uid and is absent from
        // the package list under that number; fall back to the base app id.
        if (names.TryGetValue(uid % 100_000, out var b)) return ($"{b.Name} (clone)", b.Packages, false);
        if (uid < 0) return ($"System uid {uid}", 0, true);
        return ($"uid {uid}", 0, false);
    }

    /// <summary>
    /// The uid's label with the user's merge and rename applied. A uid merged
    /// into another is shown AS that one - its uid here is the target's - so
    /// every query naming apps through this folds it in.
    /// </summary>
    private static Label LabelFor(long uid, Names names)
    {
        var shown = names.Merges.TryGetValue(Key(uid), out var into) && long.TryParse(into, CultureInfo.InvariantCulture, out var t) ? t : uid;
        var b = BaseLabel(shown, names.ByUid);
        return new Label(shown, names.Renames.GetValueOrDefault(Key(shown)) ?? b.Name, b.Name, b.Packages, b.Special);
    }

    /// <summary>The uids shown as <paramref name="uid"/>: itself, and every uid merged into it.</summary>
    private static List<long> MembersOf(long uid, Names names)
    {
        var list = new List<long> { uid };
        foreach (var (from, into) in names.Merges)
            if (into == Key(uid) && long.TryParse(from, CultureInfo.InvariantCulture, out var f)) list.Add(f);
        return list;
    }

    private static string Key(long uid) => uid.ToString(CultureInfo.InvariantCulture);

    // ---- Days ----------------------------------------------------------------------

    private string? Newest(SqliteConnection db) => Scalar(db, "SELECT MAX(local_date) FROM android_usage_records WHERE device_id = $p0") as string;

    private DayRange? History(SqliteConnection db)
    {
        var r = Rows(db, "SELECT MIN(local_date), MAX(local_date) FROM android_usage_records WHERE device_id = $p0", x => (S(x, 0), S(x, 1))).First();
        return r.Item1 is not null && r.Item2 is not null ? new DayRange(r.Item1, r.Item2) : null;
    }

    private string ScopeFrom(SqliteConnection db, Scope scope)
    {
        var newest = Newest(db) ?? Days.Iso(DateOnly.FromDateTime(DateTime.Now));
        return Days.Add(newest, -(Math.Min(scope.Days, Scope.AllDays) - 1));
    }

    private List<DailyPoint> FilledDaily(SqliteConnection db, IEnumerable<DailyPoint> rows, string from)
    {
        var h = History(db);
        return h is null ? [] : Days.Fill(rows, Days.LaterOf(from, h.Value.First), h.Value.Last, [h.Value]);
    }

    private Totals TotalsSince(SqliteConnection db, string from) =>
        Rows(db, $"SELECT SUM(tx_bytes), SUM(rx_bytes) FROM android_usage_records WHERE device_id = $p0 AND {NotTether} AND local_date >= $p1",
            r => new Totals(L(r, 0), L(r, 1)), from).First();

    // ---- Overview ------------------------------------------------------------------

    public OverviewData Overview(Scope scope) => With(db =>
    {
        var newest = Newest(db);
        if (newest is null) return new OverviewData(Totals.Empty, Totals.Empty, Totals.Empty, Totals.Empty, null, null, [], null, 0, 0, null, 0);
        var from = ScopeFrom(db, scope);

        // Days with rows only: the mean and the peak read these, so a phone
        // used on 2 days of 86 does not turn its only activity into a spike.
        var rowDays = Rows(db, $"SELECT local_date, SUM(tx_bytes), SUM(rx_bytes) FROM android_usage_records WHERE device_id = $p0 AND {NotTether} AND local_date >= $p1 GROUP BY local_date ORDER BY local_date",
            r => DailyPoint.Of(r.GetString(0), L(r, 1), L(r, 2)), from);
        var cov = Rows(db, "SELECT MIN(local_date), MAX(local_date), COUNT(DISTINCT local_date) FROM android_usage_records WHERE device_id = $p0",
            r => (S(r, 0), S(r, 1), (int)L(r, 2))).First();
        var mean = rowDays.Count > 0 ? rowDays.Average(d => (double)d.Total!.Value) : 0;
        Peak? peak = rowDays.Count == 0 ? null : rowDays.Aggregate((a, d) => d.Total > a.Total ? d : a) is var p ? new Peak(p.Date, p.Total!.Value) : null;

        return new OverviewData(TotalsSince(db, newest), TotalsSince(db, Days.Add(newest, -6)), TotalsSince(db, Days.Add(newest, -29)), TotalsSince(db, ""),
            cov.Item1 is not null && cov.Item2 is not null ? new Coverage(cov.Item1, cov.Item2, cov.Item3) : null,
            peak, FilledDaily(db, rowDays, from), null, 0, 0, newest, mean);
    });

    /// <summary>Wi-Fi against mobile, tethering apart, and hour of day in Android's two-hour buckets.</summary>
    public PhoneExtras Extras(Scope scope) => With(db =>
    {
        var from = ScopeFrom(db, scope);
        var nets = Rows(db, $"SELECT network, SUM(rx_bytes + tx_bytes) FROM android_usage_records WHERE device_id = $p0 AND {NotTether} AND local_date >= $p1 GROUP BY network",
            r => (r.GetString(0), L(r, 1)), from).ToDictionary(x => x.Item1, x => x.Item2);
        var tether = (long?)Scalar(db, "SELECT SUM(rx_bytes + tx_bytes) FROM android_usage_records WHERE device_id = $p0 AND uid = -5 AND local_date >= $p1", from) ?? 0;
        var hourly = Days.FillHours(Rows(db, $"SELECT local_hour, SUM(tx_bytes), SUM(rx_bytes) FROM android_usage_records WHERE device_id = $p0 AND {NotTether} AND local_date >= $p1 GROUP BY local_hour ORDER BY local_hour",
            r => new HourPoint((int)L(r, 0), L(r, 1), L(r, 2)), from), step: 2);
        return new PhoneExtras(nets.GetValueOrDefault("wifi"), nets.GetValueOrDefault("mobile"), tether, hourly);
    });

    public List<(string Date, long Total)> HeatmapDays(int? weeks = Heatmap.Weeks) => With(db =>
    {
        var since = weeks is null ? "" : Days.Iso(DateOnly.FromDateTime(DateTime.Now).AddDays(-weeks.Value * 7 - 7));
        var totals = Rows(db, $"SELECT local_date, SUM(rx_bytes + tx_bytes) FROM android_usage_records WHERE device_id = $p0 AND {NotTether} AND local_date >= $p1 GROUP BY local_date",
            r => (r.GetString(0), L(r, 1)), since).ToDictionary(x => x.Item1, x => x.Item2);
        var h = History(db);
        if (h is null) return [];
        // Every day of the phone's history, quiet ones as 0: one phone showed
        // 84 idle days as never collected before this.
        return Days.Each(Days.LaterOf(since, h.Value.First), h.Value.Last).Select(d => (d, totals.GetValueOrDefault(d))).ToList();
    });

    // ---- By app --------------------------------------------------------------------

    private const long DetailMinBytes = 100L * 1024 * 1024;
    private const int DetailMinDays = 14;
    private const long DetailPersistentMinBytes = 5L * 1024 * 1024;

    /// <summary>
    /// The laptop's two-way test, at a phone's scale: 250 MB is a rounding
    /// error on a busy laptop and a real app on a phone.
    /// </summary>
    public static bool EarnsDetailPage(long totalBytes, int activeDays) =>
        totalBytes >= DetailMinBytes || (activeDays >= DetailMinDays && totalBytes >= DetailPersistentMinBytes);

    public bool Earns(long totalBytes, int activeDays) => EarnsDetailPage(totalBytes, activeDays);

    public ByAppData ByApp(Scope scope) => With(db =>
    {
        var from = ScopeFrom(db, scope);
        var names = NamesOf(db);
        var hidden = AppPrefs.ReadHidden(db, DeviceId);
        var raw = Rows(db, "SELECT uid, SUM(tx_bytes), SUM(rx_bytes), COUNT(*) FROM android_usage_records WHERE device_id = $p0 AND local_date >= $p1 GROUP BY uid",
            r => (Uid: r.GetInt64(0), S: L(r, 1), R: L(r, 2), N: L(r, 3)), from);

        // Folded by the uid each one is SHOWN as; active days recounted from
        // the rows, never added: two uids active on one day are one day.
        var days = new Dictionary<long, HashSet<string>>();
        foreach (var (uid, d) in Rows(db, $"SELECT DISTINCT uid, local_date FROM android_usage_records WHERE device_id = $p0 AND {NotTether} AND local_date >= $p1",
                     r => (r.GetInt64(0), r.GetString(1)), from))
        {
            var shown = LabelFor(uid, names).Uid;
            if (!days.TryGetValue(shown, out var set)) days[shown] = set = [];
            set.Add(d);
        }

        var grouped = new Dictionary<long, AppRow>();
        long tethering = 0;
        foreach (var x in raw)
        {
            if (x.Uid == UidTethering)
            {
                tethering += x.S + x.R;
                continue;
            }
            var label = LabelFor(x.Uid, names);
            if (!grouped.TryGetValue(label.Uid, out var row))
                grouped[label.Uid] = row = new AppRow { Key = Key(label.Uid), Name = label.Name, BaseName = label.BaseName, Kind = label.Special ? "special" : "app", Hidden = hidden.Contains(Key(label.Uid)) };
            row.Sent += x.S;
            row.Received += x.R;
            row.Rows += x.N;
        }

        var apps = grouped.Values.OrderByDescending(a => a.Total).ToList();
        var named = apps.Sum(a => a.Total);
        foreach (var a in apps)
        {
            a.Share = named > 0 ? (double)a.Total / named * 100 : 0;
            a.Days = days.GetValueOrDefault(long.Parse(a.Key, CultureInfo.InvariantCulture))?.Count ?? 0;
            a.Detailed = EarnsDetailPage(a.Total, a.Days);
        }
        return new ByAppData(apps, named, 0, named, tethering);
    });

    // ---- One app -------------------------------------------------------------------

    public bool AppExists(string key) => long.TryParse(key, CultureInfo.InvariantCulture, out var uid) && With(db =>
        Scalar(db, "SELECT 1 FROM android_usage_records WHERE device_id = $p0 AND uid = $p1 LIMIT 1", uid) is not null);

    public string? MergedInto(string key) => With(db => AppPrefs.ReadMerges(db, DeviceId).GetValueOrDefault(key));

    /// <summary>
    /// One uid's page. <see cref="AppDetail.Networks"/> is Wi-Fi against mobile;
    /// <see cref="AppDetail.Identities"/> lists the packages sharing the uid
    /// (bytes 0: Android counts the uid, not the package), the one it is named
    /// after first; <see cref="AppDetail.Members"/> is the uid and those the
    /// user merged into it.
    /// </summary>
    public AppDetail? AppDetailFor(string key, Scope scope)
    {
        if (!long.TryParse(key, CultureInfo.InvariantCulture, out var uid)) return null;
        return With(db =>
        {
            if (Newest(db) is null) return null;
            if (Scalar(db, "SELECT 1 FROM android_usage_records WHERE device_id = $p0 AND uid = $p1 LIMIT 1", uid) is null) return null;
            var from = ScopeFrom(db, scope);
            var names = NamesOf(db);
            var uids = MembersOf(uid, names);
            var inList = string.Join(",", uids.Select((_, n) => $"$p{n + 2}"));
            object?[] Args() => [from, .. uids.Cast<object?>()];

            var totals = Rows(db, $"SELECT SUM(tx_bytes), SUM(rx_bytes), COUNT(*) FROM android_usage_records WHERE device_id = $p0 AND local_date >= $p1 AND uid IN ({inList})",
                r => (S: L(r, 0), R: L(r, 1), N: L(r, 2)), Args()).First();
            // Active days: days, first, last and peak read these, never the
            // filled series - the page's gate and its per-day figure mean ACTIVE days.
            var rowDays = Rows(db, $"SELECT local_date, SUM(tx_bytes), SUM(rx_bytes) FROM android_usage_records WHERE device_id = $p0 AND local_date >= $p1 AND uid IN ({inList}) GROUP BY local_date ORDER BY local_date",
                r => DailyPoint.Of(r.GetString(0), L(r, 1), L(r, 2)), Args());
            var hourly = Days.FillHours(Rows(db, $"SELECT local_hour, SUM(tx_bytes), SUM(rx_bytes) FROM android_usage_records WHERE device_id = $p0 AND local_date >= $p1 AND uid IN ({inList}) GROUP BY local_hour ORDER BY local_hour",
                r => new HourPoint((int)L(r, 0), L(r, 1), L(r, 2)), Args()), step: 2);
            var networks = Rows(db, $"SELECT network, SUM(rx_bytes + tx_bytes) b FROM android_usage_records WHERE device_id = $p0 AND local_date >= $p1 AND uid IN ({inList}) GROUP BY network ORDER BY b DESC",
                    r => (r.GetString(0), L(r, 1)), Args())
                .Select(x => new AppNetwork(x.Item1, x.Item1 == "wifi" ? "Wi-Fi" : x.Item1 == "mobile" ? "Mobile data" : x.Item1, x.Item2))
                .ToList();

            var pkgArgs = uids.Cast<object?>().ToArray();
            var pkgList = string.Join(",", uids.Select((_, n) => $"$p{n + 1}"));
            var packages = Rows(db, $"SELECT package, label, is_system FROM android_apps WHERE device_id = $p0 AND uid IN ({pkgList})",
                r => (Package: r.GetString(0), Label: r.GetString(1), IsSystem: r.GetInt64(2) == 1), pkgArgs);
            packages.Sort((a, b) => NamingOrder((a.Package, a.IsSystem), (b.Package, b.IsSystem)));

            // Per uid, so a merge reads like the PC's Merged apps card.
            var memberRows = Rows(db, $"SELECT uid, SUM(rx_bytes + tx_bytes), COUNT(DISTINCT local_date) FROM android_usage_records WHERE device_id = $p0 AND local_date >= $p1 AND uid IN ({inList}) GROUP BY uid",
                r => (Uid: r.GetInt64(0), B: L(r, 1), D: (int)L(r, 2)), Args()).ToDictionary(x => x.Uid);
            var members = uids.Select(u =>
            {
                var name = BaseLabel(u, names.ByUid).Name;
                var m = memberRows.GetValueOrDefault(u);
                return new AppMember(Key(u), name, m.B, m.D, u == uid ? null : (Key(u), name));
            }).OrderByDescending(m => m.Bytes).ToList();

            var scoped = (long?)Scalar(db, $"SELECT SUM(rx_bytes + tx_bytes) FROM android_usage_records WHERE device_id = $p0 AND {NotTether} AND local_date >= $p1", from) ?? 0;
            var label = LabelFor(uid, names);
            var total = totals.S + totals.R;
            Peak? peak = rowDays.Count == 0 ? null : rowDays.Aggregate((a, d) => d.Total > a.Total ? d : a) is var p ? new Peak(p.Date, p.Total!.Value) : null;
            return new AppDetail(Key(uid), label.Name, label.BaseName, label.Special ? "special" : "app",
                new Totals(totals.S, totals.R), scoped > 0 ? (double)total / scoped * 100 : 0,
                rowDays.Count, totals.N, rowDays.FirstOrDefault()?.Date, rowDays.LastOrDefault()?.Date, peak,
                FilledDaily(db, rowDays, from), hourly, networks,
                packages.Select(x => new AppIdentity(x.Package, x.Label, 0)).ToList(), members);
        });
    }

    // ---- Timeline ------------------------------------------------------------------

    public TimelineData Timeline(int topN = 8) => With(db =>
    {
        var names = NamesOf(db);
        var rows = Rows(db, $"SELECT local_date, uid, SUM(rx_bytes + tx_bytes) FROM android_usage_records WHERE device_id = $p0 AND {NotTether} GROUP BY local_date, uid",
                r => (D: r.GetString(0), Uid: r.GetInt64(1), B: L(r, 2)))
            .Select(x => (x.D, Name: LabelFor(x.Uid, names).Name, x.B)).ToList();
        var h = History(db);

        TimelineRange Build(int days)
        {
            var from = ScopeFrom(db, new Scope(days));
            var totalByApp = new Dictionary<string, long>();
            var perDay = new SortedDictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
            foreach (var (d, name, b) in rows)
            {
                if (string.CompareOrdinal(d, from) < 0) continue;
                totalByApp[name] = totalByApp.GetValueOrDefault(name) + b;
                if (!perDay.TryGetValue(d, out var day)) perDay[d] = day = [];
                day[name] = day.GetValueOrDefault(name) + b;
            }
            var top = totalByApp.OrderByDescending(kv => kv.Value).Take(topN).Select(kv => kv.Key).ToList();
            var topSet = top.ToHashSet();
            var hasOther = totalByApp.Count > top.Count;
            List<string> series = hasOther ? [.. top, "Other"] : top;
            var rowPoints = perDay.Select(kv =>
            {
                var values = new long?[series.Count];
                for (var n = 0; n < top.Count; n++) values[n] = kv.Value.GetValueOrDefault(top[n]);
                if (hasOther) values[^1] = kv.Value.Where(a => !topSet.Contains(a.Key)).Sum(a => a.Value);
                return (Date: kv.Key, Values: values);
            }).ToList();
            var points = h is null
                ? []
                : Days.Fill<(string Date, long?[] Values)>(rowPoints, p => p.Date, Days.LaterOf(from, h.Value.First), h.Value.Last, [h.Value],
                    d => (d, Enumerable.Repeat((long?)0, series.Count).ToArray()),
                    d => (d, new long?[series.Count]));
            return new TimelineRange(points, series);
        }

        var hourly = Days.FillHours(Rows(db, $"SELECT local_hour, SUM(tx_bytes), SUM(rx_bytes) FROM android_usage_records WHERE device_id = $p0 AND {NotTether} GROUP BY local_hour ORDER BY local_hour",
            r => new HourPoint((int)L(r, 0), L(r, 1), L(r, 2))), step: 2);
        return new TimelineData(Scope.Ranges.ToDictionary(d => d, Build), hourly);
    });

    // ---- Sync ----------------------------------------------------------------------

    /// <summary>
    /// The upload history, one page at a time, ordered by when the PHONE sent
    /// each upload: one drained from the inbox gets its id when it lands, after
    /// direct uploads it predates.
    /// </summary>
    public PhoneSyncData Sync(int limit = 25, int offset = 0) => With(db =>
    {
        var uploads = Rows(db, "SELECT received_at, landed_at, status, buckets_sent, rows_written, rows_updated, error FROM android_sync_log WHERE device_id = $p0 ORDER BY received_at DESC, id DESC LIMIT $p1 OFFSET $p2",
            r => new PhoneUpload(r.GetString(0), S(r, 1), r.GetString(2), L(r, 3), L(r, 4), L(r, 5), S(r, 6)), limit, offset);
        var total = (long)(Scalar(db, "SELECT COUNT(*) FROM android_sync_log WHERE device_id = $p0") ?? 0L);
        var lastOk = Scalar(db, "SELECT received_at FROM android_sync_log WHERE device_id = $p0 AND status = 'success' ORDER BY received_at DESC, id DESC LIMIT 1") as string;
        // From the newest backwards, so a failure followed by a success reads as recovered.
        var failures = Rows(db, "SELECT status FROM android_sync_log WHERE device_id = $p0 ORDER BY received_at DESC, id DESC LIMIT 40", r => r.GetString(0))
            .TakeWhile(s => s != "success").Count();
        var span = Rows(db, "SELECT MAX(bucket_start_utc), COUNT(*), COUNT(DISTINCT local_date), COUNT(DISTINCT uid) FROM android_usage_records WHERE device_id = $p0",
            r => (Newest: S(r, 0), N: L(r, 1), D: (int)L(r, 2), U: L(r, 3))).First();
        double? hours = lastOk is not null && Format.TryInstant(lastOk, out var t) ? (DateTime.UtcNow - t).TotalHours : null;
        // From the NEWEST STORED bucket, not the last upload: an upload that
        // stored nothing new bought no runway.
        double? runway = span.Newest is not null && Format.TryInstant(span.Newest, out var nb) ? RetentionDays - (DateTime.UtcNow - nb).TotalDays : null;
        return new PhoneSyncData(uploads, total, lastOk, hours, failures, span.Newest, span.N, span.U, span.D, runway);
    });

    /// <summary>Hours since the last successful upload, for the overview's head line.</summary>
    public double? HoursSinceSuccess() => With(db =>
        Scalar(db, "SELECT received_at FROM android_sync_log WHERE device_id = $p0 AND status = 'success' ORDER BY received_at DESC, id DESC LIMIT 1") is string s
        && Format.TryInstant(s, out var t) ? (DateTime.UtcNow - t).TotalHours : (double?)null);

    // ---- Colours and names ---------------------------------------------------------

    /// <summary>
    /// Ranked by all-time bytes and coloured by the ORIGINAL label, so a brand
    /// colour survives a rename. The phone's own labels ("YouTube", "Chrome")
    /// match the brand table directly.
    /// </summary>
    public Dictionary<string, string> ColorMap() => With(db =>
    {
        var names = NamesOf(db);
        var labels = Rows(db, $"SELECT uid, SUM(rx_bytes + tx_bytes) b FROM android_usage_records WHERE device_id = $p0 AND {NotTether} GROUP BY uid ORDER BY b DESC",
            r => LabelFor(r.GetInt64(0), names));
        var map = AppColors.Assign(labels.Select(l => l.BaseName).Distinct());
        foreach (var l in labels)
            if (l.Name != l.BaseName && map.TryGetValue(l.BaseName, out var c)) map[l.Name] = c;
        foreach (var (uid, color) in ColorOverrides.Read(db, DeviceId))
            // A merged uid wears its target's colour, not its own.
            if (!names.Merges.ContainsKey(uid) && long.TryParse(uid, CultureInfo.InvariantCulture, out var u)) map[LabelFor(u, names).Name] = color;
        return map;
    });

    public Dictionary<string, string> ColorOverrideMap() => With(db => ColorOverrides.Read(db, DeviceId));

    /// <summary>Every uid as shown and as labelled without a rename; merged ones fold into their target.</summary>
    public Dictionary<string, (string Name, string Base)> AppNamesByKey() => With(db =>
    {
        var names = NamesOf(db);
        var map = new Dictionary<string, (string, string)>();
        foreach (var uid in Rows(db, $"SELECT DISTINCT uid FROM android_usage_records WHERE device_id = $p0 AND {NotTether}", r => r.GetInt64(0)))
        {
            if (names.Merges.ContainsKey(Key(uid))) continue;
            var l = LabelFor(uid, names);
            map[Key(uid)] = (l.Name, l.BaseName);
        }
        return map;
    });

    /// <summary>Every uid, merged ones included: what a hide or a merge may name.</summary>
    public HashSet<string> AppKeys() => With(db =>
        Rows(db, $"SELECT DISTINCT uid FROM android_usage_records WHERE device_id = $p0 AND {NotTether}", r => Key(r.GetInt64(0))).ToHashSet());

    public (string? Date, Totals Totals) Latest() => With(db =>
    {
        var newest = Newest(db);
        return newest is null ? (null, Totals.Empty) : (newest, TotalsSince(db, newest));
    });
}
