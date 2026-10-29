using System.Globalization;
using System.Text.RegularExpressions;
using DataUsage.Core.Data;
using DataUsage.Core.Naming;
using DataUsage.Core.Srum;
using DataUsage.Core.View;
using Microsoft.Data.Sqlite;

namespace DataUsage.Core.Query;

/// <summary>
/// Every read the app performs.
/// </summary>
/// <remarks>
/// The rule that governs this whole file:
/// <b>TOTALS come from is_aggregate = 1. PER-APP figures come from
/// is_aggregate = 0. The two are NEVER added together.</b> The aggregate row's
/// bytes equal the sum of every named app in its hour; mixing them
/// double-counts. If a page ever shows roughly twice what Windows reports,
/// this is the first place to look.
/// </remarks>
public sealed partial class UsageQueries(string databasePath, string? splitApp = null)
{
    public string DatabasePath { get; } = databasePath;

    public bool DatabaseExists => File.Exists(DatabasePath);

    private T With<T>(Func<SqliteConnection, T> fn)
    {
        using var db = UsageDb.OpenRead(DatabasePath);
        return fn(db);
    }

    private static List<T> Rows<T>(SqliteConnection db, string sql, Func<SqliteDataReader, T> map, params object?[] args)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        for (var i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue($"$p{i}", args[i] ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    private static object? Scalar(SqliteConnection db, string sql, params object?[] args)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        for (var i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue($"$p{i}", args[i] ?? DBNull.Value);
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    private static long L(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : r.GetInt64(i);

    private static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    /// <summary>
    /// A resolved app plus the name it would have without the user's rename,
    /// and the family it came from before any merge the user made
    /// (<see cref="SourceKey"/>, equal to the group key when not merged).
    /// </summary>
    public sealed record Named(ResolvedApp App, string DisplayName, string BaseName, string SourceKey, string SourceName);

    /// <summary>
    /// <see cref="AppNames.Resolve"/> with the user's merges and renames laid
    /// over the FAMILY (Naming/AppPrefs.cs, Naming/Renames.cs). Every query that
    /// groups or names an app goes through this - never AppNames.Resolve
    /// directly - so a merge or a rename reaches tables, charts, legends and
    /// the detail page from one place. A merged family takes its target's key,
    /// name and kind; its members keep their own.
    /// </summary>
    private static Func<string, string, Named> Resolver(SqliteConnection db)
    {
        var renames = Renames.Read(db);
        var merges = AppPrefs.ReadMerges(db);
        Dictionary<string, ResolvedApp>? families = null;
        ResolvedApp? FamilyOf(string key)
        {
            if (families is null)
            {
                families = [];
                foreach (var (i, k) in Rows(db, "SELECT DISTINCT app_identity, app_kind FROM usage_records WHERE is_aggregate = 0", r => (r.GetString(0), r.GetString(1))))
                {
                    var x = AppNames.Resolve(i, k);
                    families.TryAdd(x.GroupKey, x);
                }
            }
            return families.GetValueOrDefault(key);
        }
        var cache = new Dictionary<(string, string), Named>();
        return (identity, kind) =>
        {
            if (cache.TryGetValue((identity, kind), out var hit)) return hit;
            var r = AppNames.Resolve(identity, kind);
            var family = merges.TryGetValue(r.GroupKey, out var into) && FamilyOf(into) is { } t ? t : r;
            var app = r with { GroupKey = family.GroupKey, DisplayName = family.DisplayName, Kind = family.Kind };
            var named = new Named(app, renames.GetValueOrDefault(family.GroupKey) ?? family.DisplayName, family.DisplayName,
                r.GroupKey, renames.GetValueOrDefault(r.GroupKey) ?? r.DisplayName);
            cache[(identity, kind)] = named;
            return named;
        };
    }

    /// <summary>The family a merged app now belongs to, or null when it is not merged.</summary>
    public string? MergedInto(string groupKey) => With(db => AppPrefs.ReadMerges(db).GetValueOrDefault(groupKey));

    /// <summary>
    /// Every family key BEFORE the user's merges: what a hide or a merge may
    /// name. <see cref="AppNamesByKey"/> folds merged families into their targets.
    /// </summary>
    public HashSet<string> AppKeys() => With(db =>
        Rows(db, "SELECT DISTINCT app_identity, app_kind FROM usage_records WHERE is_aggregate = 0", r => AppNames.Resolve(r.GetString(0), r.GetString(1)).GroupKey).ToHashSet());

    private static string? Newest(SqliteConnection db) => Scalar(db, "SELECT MAX(local_date) FROM usage_records") as string;

    /// <summary>
    /// The first day a scope admits. Anchored on the newest day held, not on
    /// today: a collector broken for a week should still show 30 days of real
    /// data - Sync Status is what surfaces the staleness.
    /// </summary>
    private static string ScopeFrom(SqliteConnection db, Scope scope)
    {
        var newest = Newest(db) ?? Days.Iso(DateOnly.FromDateTime(DateTime.Now));
        return Days.Add(newest, -(Math.Min(scope.Days, Scope.AllDays) - 1));
    }

    /// <summary>
    /// The local days SRUM is KNOWN to have held, from every successful run's
    /// recorded window. On these days no aggregate rows means the laptop moved
    /// nothing; outside them nothing is known.
    /// </summary>
    private static List<DayRange> CollectedDays(SqliteConnection db)
    {
        var runs = Rows(db, "SELECT srum_oldest_utc, srum_newest_utc FROM sync_log WHERE status = 'success' AND srum_oldest_utc IS NOT NULL AND srum_newest_utc IS NOT NULL",
            r => (S(r, 0)!, S(r, 1)!));
        var windows = new List<(DateTime, DateTime)>();
        foreach (var (a, b) in runs)
            if (Format.TryInstant(a, out var ta) && Format.TryInstant(b, out var tb)) windows.Add((ta, tb));
        return Days.Covered(windows, t => SrumFields.LocalBuckets(t).Date);
    }

    private static DayRange? LaptopSpan(SqliteConnection db)
    {
        var r = Rows(db, "SELECT MIN(local_date), MAX(local_date) FROM usage_records WHERE is_aggregate = 1", x => (S(x, 0), S(x, 1))).First();
        return r.Item1 is not null && r.Item2 is not null ? new DayRange(r.Item1, r.Item2) : null;
    }

    private static List<DailyPoint> FilledDaily(SqliteConnection db, IEnumerable<DailyPoint> rows, string from)
    {
        var span = LaptopSpan(db);
        return span is null ? [] : Days.Fill(rows, Days.LaterOf(from, span.Value.First), span.Value.Last, CollectedDays(db));
    }

    private static Totals TotalsSince(SqliteConnection db, string from) =>
        Rows(db, "SELECT SUM(bytes_sent), SUM(bytes_received) FROM usage_records WHERE is_aggregate = 1 AND local_date >= $p0",
            r => new Totals(L(r, 0), L(r, 1)), from).First();

    /// <summary>Distinguishes an empty database from an empty range.</summary>
    public long RowCount() => With(db => (long)(Scalar(db, "SELECT COUNT(*) FROM usage_records") ?? 0L));

    // ---- Overview ---------------------------------------------------------------

    public OverviewData Overview(Scope scope) => With(db =>
    {
        var newest = Newest(db);
        if (newest is null) return new OverviewData(Totals.Empty, Totals.Empty, Totals.Empty, Totals.Empty, null, null, [], null, 0, 0, null, 0);

        var from = ScopeFrom(db, scope);
        var today = TotalsSince(db, newest);
        var week = TotalsSince(db, Days.Add(newest, -6));
        var month = TotalsSince(db, Days.Add(newest, -29));

        // Days with rows only: the mean and the peak are read off these, so a
        // day the laptop was off neither lowers the bar for "well above trend"
        // nor counts as a day of use.
        var rowDays = Rows(db, "SELECT local_date, SUM(bytes_sent), SUM(bytes_received) FROM usage_records WHERE is_aggregate = 1 AND local_date >= $p0 GROUP BY local_date ORDER BY local_date",
            r => DailyPoint.Of(r.GetString(0), L(r, 1), L(r, 2)), from);
        var daily = FilledDaily(db, rowDays, from);

        // The split uses PER-APP rows - it is a breakdown of named traffic, so
        // it totals slightly less than the headline; the gap is unattributed.
        var resolve = Resolver(db);
        string? focusLabel = splitApp;
        long focus = 0, other = 0;
        foreach (var (i, k, b) in Rows(db, "SELECT app_identity, app_kind, SUM(bytes_sent + bytes_received) FROM usage_records WHERE is_aggregate = 0 AND local_date >= $p0 GROUP BY app_identity, app_kind",
                     r => (r.GetString(0), r.GetString(1), L(r, 2)), from))
        {
            // splitApp names the app as the code resolves it; show whatever it
            // has been renamed to.
            var n = resolve(i, k);
            if (splitApp is not null && n.BaseName == splitApp)
            {
                focus += b;
                focusLabel = n.DisplayName;
            }
            else other += b;
        }

        var mean = rowDays.Count > 0 ? rowDays.Average(d => (double)d.Total!.Value) : 0;

        // "All" deliberately ignores the range: it answers "how much history do
        // we hold", which a range control must not change.
        var all = TotalsSince(db, "");
        var cov = Rows(db, "SELECT MIN(local_date), MAX(local_date), COUNT(DISTINCT local_date) FROM usage_records WHERE is_aggregate = 1",
            r => (S(r, 0), S(r, 1), (int)L(r, 2))).First();
        var peak = rowDays.Count == 0 ? null : rowDays.Aggregate((a, d) => d.Total > a.Total ? d : a) is var p ? new Peak(p.Date, p.Total!.Value) : null;

        return new OverviewData(today, week, month, all,
            cov.Item1 is not null && cov.Item2 is not null ? new Coverage(cov.Item1, cov.Item2, cov.Item3) : null,
            peak, daily, focusLabel, focus, other, newest, mean);
    });

    // ---- Heat map ---------------------------------------------------------------

    /// <summary>
    /// Daily totals for the heat map, ignoring the range: it always shows its
    /// own window. Days with rows, plus 0 for every day SRUM is known to have
    /// held - and nothing for a day it never held, which the map draws as "no
    /// data collected" rather than inventing a quiet day.
    /// </summary>
    public List<(string Date, long Total)> HeatmapDays(int? weeks = Heatmap.Weeks) => With(db =>
    {
        var since = weeks is null ? "" : Days.Iso(DateOnly.FromDateTime(DateTime.Now).AddDays(-weeks.Value * 7 - 7));
        var totals = Rows(db, "SELECT local_date, SUM(bytes_sent + bytes_received) FROM usage_records WHERE is_aggregate = 1 AND local_date >= $p0 GROUP BY local_date",
            r => (r.GetString(0), L(r, 1)), since).ToDictionary(x => x.Item1, x => x.Item2);
        var span = LaptopSpan(db);
        if (span is null) return [];
        var known = CollectedDays(db);
        return Days.Each(Days.LaterOf(since, span.Value.First), span.Value.Last)
            .Where(d => totals.ContainsKey(d) || Days.IsKnown(d, known))
            .Select(d => (d, totals.GetValueOrDefault(d)))
            .ToList();
    });

    // ---- By app -------------------------------------------------------------------

    private const long DetailMinBytes = 250L * 1024 * 1024;
    private const int DetailMinDays = 7;
    private const long DetailPersistentMinBytes = 10L * 1024 * 1024;

    /// <summary>
    /// Does an app earn a detail page? A page shows behaviour over time, so the
    /// question is whether there is a shape to look at - by volume (a model
    /// download: gigabytes in two days) or by persistence (a certificate
    /// service: megabytes across weeks). What both reject is the one-shot
    /// installer, whose page would be one bar. Judged on the FAMILY.
    /// </summary>
    public static bool EarnsDetailPage(long totalBytes, int activeDays) =>
        totalBytes >= DetailMinBytes || (activeDays >= DetailMinDays && totalBytes >= DetailPersistentMinBytes);

    public ByAppData ByApp(Scope scope) => With(db =>
    {
        var from = ScopeFrom(db, scope);
        var raw = Rows(db, "SELECT app_identity, app_kind, SUM(bytes_sent), SUM(bytes_received), COUNT(*) FROM usage_records WHERE is_aggregate = 0 AND local_date >= $p0 GROUP BY app_identity, app_kind",
            r => (I: r.GetString(0), K: r.GetString(1), S: L(r, 2), R: L(r, 3), N: L(r, 4)), from);

        // Days counted per GROUP, never summed per identity: two versions of
        // one app share days.
        var resolve = Resolver(db);
        var daysByGroup = new Dictionary<string, HashSet<string>>();
        foreach (var (i, k, d) in Rows(db, "SELECT DISTINCT app_identity, app_kind, local_date FROM usage_records WHERE is_aggregate = 0 AND local_date >= $p0",
                     r => (r.GetString(0), r.GetString(1), r.GetString(2)), from))
        {
            var key = resolve(i, k).App.GroupKey;
            if (!daysByGroup.TryGetValue(key, out var set)) daysByGroup[key] = set = [];
            set.Add(d);
        }

        var hidden = AppPrefs.ReadHidden(db);
        var grouped = new Dictionary<string, AppRow>();
        foreach (var x in raw)
        {
            var n = resolve(x.I, x.K);
            if (!grouped.TryGetValue(n.App.GroupKey, out var row))
                grouped[n.App.GroupKey] = row = new AppRow { Key = n.App.GroupKey, Name = n.DisplayName, BaseName = n.BaseName, Kind = n.App.Kind, Hidden = hidden.Contains(n.App.GroupKey) };
            row.Sent += x.S;
            row.Received += x.R;
            row.Rows += x.N;
        }

        var headline = TotalsSince(db, from).Total;
        var apps = grouped.Values.OrderByDescending(a => a.Total).ToList();
        var named = apps.Sum(a => a.Total);
        foreach (var a in apps)
        {
            a.Share = named > 0 ? (double)a.Total / named * 100 : 0;
            a.Days = daysByGroup.GetValueOrDefault(a.Key)?.Count ?? 0;
            a.Detailed = EarnsDetailPage(a.Total, a.Days);
        }
        return new ByAppData(apps, named, Math.Max(0, headline - named), headline);
    });

    // ---- One app --------------------------------------------------------------------

    /// <summary>
    /// Does this app exist anywhere, ignoring the range? "No such app" and
    /// "nothing in this range" are different answers, and telling the reader
    /// the first when the second is true sends them hunting for nothing.
    /// </summary>
    public bool AppExists(string groupKey) => With(db =>
    {
        var resolve = Resolver(db);
        return Rows(db, "SELECT DISTINCT app_identity, app_kind FROM usage_records WHERE is_aggregate = 0", r => (r.GetString(0), r.GetString(1)))
            .Any(x => resolve(x.Item1, x.Item2).App.GroupKey == groupKey);
    });

    public AppDetail? AppDetailFor(string groupKey, Scope scope)
    {
        // Profiles first: its own connection, like the original's nested read.
        var profileNames = Profiles().ToDictionary(p => p.Id, p => p.Named ? p.Label : $"{p.Label}, unnamed");
        return With(db =>
        {
            var from = ScopeFrom(db, scope);
            var resolve = Resolver(db);
            var mine = Rows(db, "SELECT app_identity, app_kind, SUM(bytes_sent), SUM(bytes_received), COUNT(*) FROM usage_records WHERE is_aggregate = 0 AND local_date >= $p0 GROUP BY app_identity, app_kind",
                    r => (I: r.GetString(0), K: r.GetString(1), S: L(r, 2), R: L(r, 3), N: L(r, 4)), from)
                .Where(x => resolve(x.I, x.K).App.GroupKey == groupKey)
                .ToList();
            if (mine.Count == 0) return null;

            var resolved = resolve(mine[0].I, mine[0].K);
            var identities = mine.Select(x => new AppIdentity(x.I, x.K, x.S + x.R)).OrderByDescending(x => x.Bytes).ToList();

            // Members: the programs a person would name. Days counted per
            // member, for the same reason the table counts them per group.
            var memberDays = new Dictionary<string, HashSet<string>>();
            foreach (var (i, k, d) in Rows(db, "SELECT DISTINCT app_identity, app_kind, local_date FROM usage_records WHERE is_aggregate = 0 AND local_date >= $p0",
                         r => (r.GetString(0), r.GetString(1), r.GetString(2)), from))
            {
                var res = resolve(i, k).App;
                if (res.GroupKey != groupKey) continue;
                if (!memberDays.TryGetValue(res.MemberKey, out var set)) memberDays[res.MemberKey] = set = [];
                set.Add(d);
            }
            var byMember = new Dictionary<string, (string Name, long Bytes, (string, string)? From)>();
            foreach (var x in mine)
            {
                var n = resolve(x.I, x.K);
                var res = n.App;
                (string, string)? source = n.SourceKey == groupKey ? null : (n.SourceKey, n.SourceName);
                var cur = byMember.GetValueOrDefault(res.MemberKey, (res.MemberName, 0, source));
                byMember[res.MemberKey] = (cur.Item1, cur.Item2 + x.S + x.R, cur.Item3);
            }
            var members = byMember.Select(m => new AppMember(m.Key, m.Value.Name, m.Value.Bytes, memberDays.GetValueOrDefault(m.Key)?.Count ?? 0, m.Value.From))
                .OrderByDescending(m => m.Bytes).ToList();

            var sent = mine.Sum(x => x.S);
            var received = mine.Sum(x => x.R);
            var rows = mine.Sum(x => x.N);

            // Bind the identity list into the remaining queries: these series
            // span tens of thousands of rows.
            var ids = mine.Select(x => (object?)x.I).ToList();
            var inList = string.Join(",", ids.Select((_, n) => $"$p{n + 1}"));
            object?[] Args() => [from, .. ids];

            // `days`, `first`, `last` and `peak` read these, never the filled
            // series: the detail gate and the per-day figure mean ACTIVE days.
            var rowDays = Rows(db, $"SELECT local_date, SUM(bytes_sent), SUM(bytes_received) FROM usage_records WHERE is_aggregate = 0 AND local_date >= $p0 AND app_identity IN ({inList}) GROUP BY local_date ORDER BY local_date",
                r => DailyPoint.Of(r.GetString(0), L(r, 1), L(r, 2)), Args());
            var daily = FilledDaily(db, rowDays, from);

            var hourly = Days.FillHours(Rows(db, $"SELECT local_hour, SUM(bytes_sent), SUM(bytes_received) FROM usage_records WHERE is_aggregate = 0 AND local_date >= $p0 AND app_identity IN ({inList}) GROUP BY local_hour ORDER BY local_hour",
                r => new HourPoint((int)L(r, 0), L(r, 1), L(r, 2)), Args()));

            var networks = Rows(db, $"SELECT l2_profile_id, SUM(bytes_sent + bytes_received) b FROM usage_records WHERE is_aggregate = 0 AND local_date >= $p0 AND app_identity IN ({inList}) GROUP BY l2_profile_id ORDER BY b DESC",
                    r => (r.GetString(0), L(r, 1)), Args())
                .Where(x => x.Item1.Length > 0 && x.Item1 != "0")
                .Select(x => new AppNetwork(x.Item1, profileNames.GetValueOrDefault(x.Item1) ?? "Unknown network", x.Item2))
                .ToList();

            var attributed = (long?)Scalar(db, "SELECT SUM(bytes_sent + bytes_received) FROM usage_records WHERE is_aggregate = 0 AND local_date >= $p0", from) ?? 0;
            var total = sent + received;
            Peak? peak = rowDays.Count == 0 ? null : rowDays.Aggregate((a, d) => d.Total > a.Total ? d : a) is var p ? new Peak(p.Date, p.Total!.Value) : null;

            return new AppDetail(groupKey, resolved.DisplayName, resolved.BaseName, resolved.App.Kind,
                new Totals(sent, received), attributed > 0 ? (double)total / attributed * 100 : 0,
                rowDays.Count, rows, rowDays.FirstOrDefault()?.Date, rowDays.LastOrDefault()?.Date, peak,
                daily, hourly, networks, identities, members);
        });
    }

    // ---- Timeline ---------------------------------------------------------------------

    /// <summary>
    /// Daily by app for every range the chart's menu offers, from ONE read of
    /// the per-day rows; each range ranks its own top apps rather than slicing
    /// the all-time stack. Hour of day covers all history.
    /// </summary>
    public TimelineData Timeline(int topN = 8) => With(db =>
    {
        var resolve = Resolver(db);
        var rows = Rows(db, "SELECT local_date, app_identity, app_kind, SUM(bytes_sent + bytes_received) FROM usage_records WHERE is_aggregate = 0 GROUP BY local_date, app_identity, app_kind",
                r => (D: r.GetString(0), I: r.GetString(1), K: r.GetString(2), B: L(r, 3)))
            .Select(x => (x.D, Name: resolve(x.I, x.K).DisplayName, x.B))
            .ToList();
        var span = LaptopSpan(db);
        var known = CollectedDays(db);

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

            // One point per day: 0 across the stack on a quiet collected day,
            // null on a day never collected - drawn as a break, never a slope.
            var points = span is null
                ? []
                : Days.Fill<(string Date, long?[] Values)>(rowPoints, p => p.Date, Days.LaterOf(from, span.Value.First), span.Value.Last, known,
                    d => (d, Enumerable.Repeat((long?)0, series.Count).ToArray()),
                    d => (d, new long?[series.Count]));
            return new TimelineRange(points, series);
        }

        var ranges = Scope.Ranges.ToDictionary(d => d, Build);
        var hourly = Days.FillHours(Rows(db, "SELECT local_hour, SUM(bytes_sent), SUM(bytes_received) FROM usage_records WHERE is_aggregate = 1 GROUP BY local_hour ORDER BY local_hour",
            r => new HourPoint((int)L(r, 0), L(r, 1), L(r, 2))));
        return new TimelineData(ranges, hourly);
    });

    // ---- Sync status ---------------------------------------------------------------------

    private const string RunColumns = "id, started_at, finished_at, status, rows_read, rows_inserted, rows_skipped, srum_oldest_utc, srum_newest_utc, backup_status, duration_ms, error";

    private static SyncRun ToRun(SqliteDataReader r) => new(
        L(r, 0), r.GetString(1), S(r, 2), r.GetString(3), L(r, 4), L(r, 5), L(r, 6), S(r, 7), S(r, 8), S(r, 9), r.IsDBNull(10) ? null : r.GetInt64(10), S(r, 11));

    /// <summary>
    /// Run history, one page at a time. <c>LastSuccess</c> and
    /// <c>ConsecutiveFailures</c> are queried across the WHOLE table, never
    /// derived from the page.
    /// </summary>
    public SyncData Sync(int limit = 30, int offset = 0) => With(db =>
    {
        var runs = Rows(db, $"SELECT {RunColumns} FROM sync_log ORDER BY id DESC LIMIT $p0 OFFSET $p1", ToRun, limit, offset);
        var totalRuns = (long)(Scalar(db, "SELECT COUNT(*) FROM sync_log") ?? 0L);
        var lastSuccess = Rows(db, $"SELECT {RunColumns} FROM sync_log WHERE status = 'success' ORDER BY id DESC LIMIT 1", ToRun).FirstOrDefault();

        // A run left 'running' for over an hour did not finish.
        runs = runs.Select(r => r.Status == "running" && Format.TryInstant(r.StartedAt, out var t) && DateTime.UtcNow - t > TimeSpan.FromHours(1) ? r with { Status = "interrupted" } : r).ToList();

        // One failed run is noise; three in a row means the schedule is broken.
        var failures = (long)(Scalar(db, "SELECT COUNT(*) FROM sync_log WHERE status = 'failed' AND id > (SELECT COALESCE(MAX(id), 0) FROM sync_log WHERE status = 'success')") ?? 0L);
        var totalRows = (long)(Scalar(db, "SELECT COUNT(*) FROM usage_records") ?? 0L);
        var cov = Rows(db, "SELECT MIN(local_date), MAX(local_date), COUNT(DISTINCT local_date) FROM usage_records", r => (S(r, 0), S(r, 1), (int)L(r, 2))).First();

        double? hours = lastSuccess is not null && Format.TryInstant(lastSuccess.StartedAt, out var ls) ? (DateTime.UtcNow - ls).TotalHours : null;
        return new SyncData(runs, totalRuns, lastSuccess, hours, totalRows,
            cov.Item1 is not null && cov.Item2 is not null ? new Coverage(cov.Item1, cov.Item2, cov.Item3) : null, failures);
    });

    // ---- Colours and names -------------------------------------------------------------------

    /// <summary>
    /// App to colour, from ALL-TIME totals, so an app keeps its colour when the
    /// range or the page changes. Ranked by ORIGINAL names so a brand colour
    /// survives a rename; each renamed app then takes its original's colour.
    /// </summary>
    public Dictionary<string, string> ColorMap() => With(db =>
    {
        var resolve = Resolver(db);
        var totals = new Dictionary<string, long>();
        var renamed = new Dictionary<string, string>();
        var shownAs = new Dictionary<string, string>(); // group key -> display name
        foreach (var (i, k, b) in Rows(db, "SELECT app_identity, app_kind, SUM(bytes_sent + bytes_received) FROM usage_records WHERE is_aggregate = 0 GROUP BY app_identity, app_kind",
                     r => (r.GetString(0), r.GetString(1), L(r, 2))))
        {
            var n = resolve(i, k);
            totals[n.BaseName] = totals.GetValueOrDefault(n.BaseName) + b;
            if (n.DisplayName != n.BaseName) renamed[n.DisplayName] = n.BaseName;
            shownAs[n.App.GroupKey] = n.DisplayName;
        }
        var map = AppColors.Assign(totals.OrderByDescending(kv => kv.Value).Select(kv => kv.Key));
        foreach (var (name, @base) in renamed)
            if (map.TryGetValue(@base, out var c)) map[name] = c;
        // The user's own colours win over brand and palette alike, under
        // whatever name the app shows. See Naming/ColorOverrides.cs.
        foreach (var (key, color) in ColorOverrides.Read(db))
            if (shownAs.TryGetValue(key, out var name)) map[name] = color;
        return map;
    });

    /// <summary>Family group key to the colour the user chose for it.</summary>
    public Dictionary<string, string> ColorOverrideMap() => With(ColorOverrides.Read);

    /// <summary>Every family ever seen: as shown, and as it would be without a rename.</summary>
    public Dictionary<string, (string Name, string Base)> AppNamesByKey() => With(db =>
    {
        var resolve = Resolver(db);
        var map = new Dictionary<string, (string, string)>();
        foreach (var (i, k) in Rows(db, "SELECT DISTINCT app_identity, app_kind FROM usage_records WHERE is_aggregate = 0", r => (r.GetString(0), r.GetString(1))))
        {
            var n = resolve(i, k);
            map.TryAdd(n.App.GroupKey, (n.DisplayName, n.BaseName));
        }
        return map;
    });

    // ---- Where it went -------------------------------------------------------------------------

    [GeneratedRegex("IEEE80211", RegexOptions.IgnoreCase)]
    private static partial Regex WifiType();

    [GeneratedRegex("ETHERNET", RegexOptions.IgnoreCase)]
    private static partial Regex WiredType();

    [GeneratedRegex("WWANPP|MOBILE", RegexOptions.IgnoreCase)]
    private static partial Regex MobileType();

    public static LinkKind KindOf(string interfaceType) =>
        WifiType().IsMatch(interfaceType) ? LinkKind.Wifi
        : WiredType().IsMatch(interfaceType) ? LinkKind.Wired
        : MobileType().IsMatch(interfaceType) ? LinkKind.Mobile
        : LinkKind.Other;

    public static string PrettyInterface(string t) =>
        WifiType().IsMatch(t) ? "Wi-Fi" : WiredType().IsMatch(t) ? "Ethernet" : MobileType().IsMatch(t) ? "Mobile" : t.Length > 0 ? t : "Unknown";

    /// <summary>
    /// "Where it went": the range's traffic by kind of link and by network.
    /// Every figure reads the AGGREGATE rows, so the rows sum to the headline.
    /// Profile 0 - traffic with no profile at all - is listed, not dropped, or
    /// the rows would not add up.
    /// </summary>
    public NetworkBreakdown Networks(Scope scope)
    {
        var labels = Profiles().ToDictionary(p => p.Id);
        return With(db =>
        {
            var from = ScopeFrom(db, scope);
            var kinds = new Dictionary<LinkKind, long> { [LinkKind.Wifi] = 0, [LinkKind.Wired] = 0 };
            var perProfile = new Dictionary<string, (long Total, string Type)>();
            long total = 0;
            foreach (var (t, p, b) in Rows(db, "SELECT interface_type, l2_profile_id, SUM(bytes_sent + bytes_received) FROM usage_records WHERE is_aggregate = 1 AND local_date >= $p0 GROUP BY interface_type, l2_profile_id",
                         r => (r.GetString(0), r.GetString(1), L(r, 2)), from))
            {
                if (b == 0) continue;
                total += b;
                var kind = KindOf(t);
                kinds[kind] = kinds.GetValueOrDefault(kind) + b;
                var id = p.Length > 0 && p != "0" ? p : "0";
                perProfile[id] = perProfile.TryGetValue(id, out var cur) ? (cur.Total + b, cur.Type) : (b, t);
            }

            var networks = perProfile.Select(kv =>
                kv.Key == "0" ? new NetworkRow("0", "No network profile", false, [], kv.Value.Total, KindOf(kv.Value.Type))
                : labels.TryGetValue(kv.Key, out var pr) ? new NetworkRow(kv.Key, pr.Label, pr.Named, pr.Aliases, kv.Value.Total, KindOf(kv.Value.Type))
                : new NetworkRow(kv.Key, PrettyInterface(kv.Value.Type), false, [], kv.Value.Total, KindOf(kv.Value.Type)))
                .OrderByDescending(n => n.Total).ToList();

            return new NetworkBreakdown(total, kinds.Select(kv => (kv.Key, kv.Value)).ToList(), networks);
        });
    }

    /// <summary>
    /// Every network profile, named by its most-voted observed SSID; runner-up
    /// names are kept as aliases so the label stops flip-flopping.
    /// </summary>
    public List<ProfileOption> Profiles() => With(db =>
    {
        var named = new Dictionary<string, (string Name, List<string> Aliases)>();
        foreach (var (p, name) in Rows(db, "SELECT l2_profile_id, name FROM network_names ORDER BY l2_profile_id, votes DESC, last_seen DESC", r => (r.GetString(0), r.GetString(1))))
        {
            if (named.TryGetValue(p, out var cur)) cur.Aliases.Add(name);
            else named[p] = (name, []);
        }

        return Rows(db, "SELECT l2_profile_id, interface_type, MAX(NULLIF(profile_name, '')), SUM(bytes_sent + bytes_received) b FROM usage_records WHERE is_aggregate = 0 GROUP BY l2_profile_id, interface_type ORDER BY b DESC",
                r => (P: r.GetString(0), T: r.GetString(1), N: S(r, 2), B: L(r, 3)))
            .Where(r => r.P.Length > 0 && r.P != "0")
            .Select(r =>
            {
                var hasLearned = named.TryGetValue(r.P, out var learned);
                var fallback = !string.IsNullOrWhiteSpace(r.N) ? r.N.Trim() : PrettyInterface(r.T);
                return new ProfileOption(r.P, hasLearned ? learned.Name : fallback, hasLearned || !string.IsNullOrWhiteSpace(r.N),
                    hasLearned ? learned.Aliases : [], r.B, r.T);
            })
            .ToList();
    });

    /// <summary>For the tray: the newest day held, and its totals.</summary>
    public (string? Date, Totals Totals) Latest() => With(db =>
    {
        var newest = Newest(db);
        return newest is null ? (null, Totals.Empty) : (newest, TotalsSince(db, newest));
    });

    public static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
