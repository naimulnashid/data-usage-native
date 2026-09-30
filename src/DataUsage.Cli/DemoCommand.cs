using DataUsage.Core;
using DataUsage.Core.Data;
using DataUsage.Core.Srum;

namespace DataUsage.Cli;

/// <summary>
/// An invented history for screenshots: an invented PC ("My PC"), invented
/// networks, made-up volumes. A screenshot of the real app is a screenshot of
/// somebody's usage.
/// </summary>
/// <remarks>
/// Not a mock: rows go through the real ingest (<see cref="UsageDb.InsertRows"/>),
/// runs through the real run log, and network names through the real
/// observe-then-resolve path - one simulated collector run at a time, over
/// overlapping 60-day windows. So it has to reproduce the traps the code
/// handles: AppId 1 aggregates equal to their hour's apps plus a sliver,
/// identities of all three kinds, one app across two versioned install paths
/// and one AppX package across two versions, DoSvc + BITS + wuauserv as one
/// family, off-cadence flush rows at sleep, a failed run, an alias SSID.
/// Deterministic for a given day.
/// </remarks>
public static class DemoCommand
{
    private const string Marker = ".data-usage-demo";
    private const double MB = 1024 * 1024;
    private const double GB = 1024 * MB;
    private const int HistoryDays = 100;

    private const string Home = "268435457";
    private const string Cafe = "268435459";
    private const string Hotspot = "268435461";
    private const string HomeSsid = "HomeNet-5G";
    private const string HomeAlias = "HomeNet";
    private const string CafeSsid = "Corner Café Guest";
    // (71 << 48) | 1: IF_TYPE_IEEE80211, interface index 1.
    private const string WifiLuid = "19984723346456577";

    private static uint _seed;

    // mulberry32, as the original: a fixed history, not a new one per run.
    private static double Rand()
    {
        unchecked
        {
            _seed += 0x6d2b79f5;
            var t = _seed;
            t = (t ^ (t >> 15)) * (1 | t);
            t = (t + ((t ^ (t >> 7)) * (61 | t))) ^ t;
            return (t ^ (t >> 14)) / 4294967296.0;
        }
    }

    private static double Between(double lo, double hi) => lo + Rand() * (hi - lo);
    private static bool Chance(double p) => Rand() < p;
    private static double Spread() => Math.Exp((Rand() - 0.5) * 1.2);
    private static int[] Range(int a, int b) => Enumerable.Range(a, b - a + 1).ToArray();

    private sealed class DayPlan
    {
        public bool Weekend;
        public readonly SortedDictionary<int, string> Hours = [];
        public double TorrentBytes;
        public bool PatchDay;
    }

    private sealed record AppSpec(long AppId, string Identity, Func<int, DayPlan, double> Use, int[] Peak, double UpShare,
        bool System = false, string[]? Only = null, int? From = null, int? To = null);

    private sealed record Row(DateTime T, long AppId, string Identity, bool System, string Profile, long Rx, long Tx);

    private static DateTime _now;
    private static DateTime DayStart(int i) => _now.Date.AddDays(-(HistoryDays - 1 - i));

    private static string Nt(params string[] parts) => string.Join("\\", new[] { "", "device", "harddiskvolume3" }.Concat(parts));

    private static readonly string[] User = ["users", "you"];

    private static Func<int, DayPlan, double> Usual(double perDay, double weekday, double weekend) =>
        (_, plan) => Chance(plan.Weekend ? weekend : weekday) ? perDay * Spread() : 0;

    private static readonly HashSet<int> OffDays = [23, 24, 61];

    private static DayPlan PlanDay(int i)
    {
        var start = DayStart(i);
        var dow = start.DayOfWeek;
        var plan = new DayPlan
        {
            Weekend = dow is DayOfWeek.Saturday or DayOfWeek.Sunday,
            // The second Tuesday of the month.
            PatchDay = dow == DayOfWeek.Tuesday && start.Day is >= 8 and <= 14,
        };
        if (OffDays.Contains(i)) return plan;
        var first = plan.Weekend ? 10 : 8;
        for (var h = first; h <= 23; h++) if (!Chance(0.07)) plan.Hours[h] = Home;
        if (!plan.Weekend && Chance(0.22)) for (var h = 10; h <= 14; h++) plan.Hours[h] = Cafe;
        if (!plan.Weekend && Chance(0.1)) foreach (var h in new[] { 17, 18 }) plan.Hours[h] = Hotspot;
        if (Chance(0.3))
        {
            plan.TorrentBytes = Between(4, 24) * GB;
            for (var h = 0; h <= 5; h++) plan.Hours[h] = Home;
        }
        return plan;
    }

    private static List<AppSpec> Apps() =>
    [
        new(2001, Nt("program files (x86)", "microsoft", "edge", "application", "msedge.exe"), Usual(1.3 * GB, 0.97, 0.9), [.. Range(9, 12), .. Range(20, 23)], 0.06),
        new(2002, Nt("program files (x86)", "microsoft", "edgewebview", "application", "128.0.2739.79", "msedgewebview2.exe"), Usual(90 * MB, 0.9, 0.7), Range(9, 18), 0.1),
        new(2003, Nt("program files (x86)", "microsoft", "edgeupdate", "microsoftedgeupdate.exe"), Usual(140 * MB, 0.12, 0.12), [11], 0.01, System: true),
        new(2010, Nt("program files", "google", "chrome", "application", "chrome.exe"), Usual(420 * MB, 0.55, 0.4), Range(13, 17), 0.05),
        new(2020, Nt("program files", "qbittorrent", "qbittorrent.exe"), (_, plan) => plan.TorrentBytes, [.. Range(0, 5), .. Range(19, 23)], 0.18, Only: [Home]),
        // Google Drive before and after an update: two install paths, one app.
        new(2030, Nt("program files", "google", "drive file stream", "128.0.0.0", "googledrivefs.exe"), Usual(520 * MB, 0.9, 0.6), Range(9, 22), 0.45, To: 54),
        new(2031, Nt("program files", "google", "drive file stream", "129.0.1.0", "googledrivefs.exe"), Usual(520 * MB, 0.9, 0.6), Range(9, 22), 0.45, From: 55),
        new(2040, Nt([.. User, "appdata", "local", "programs", "microsoft vs code", "code.exe"]), Usual(160 * MB, 0.85, 0.3), Range(10, 18), 0.12),
        new(2041, Nt("program files", "nodejs", "node.exe"), Usual(300 * MB, 0.7, 0.2), Range(10, 18), 0.08),
        new(2042, Nt("program files", "git", "mingw64", "libexec", "git-core", "git-remote-https.exe"), Usual(35 * MB, 0.75, 0.2), Range(10, 18), 0.3),
        // Teams is an AppX package whose full name changes with every update.
        new(2050, "MSTeams_25198.1112.3855.2250_x64__8wekyb3d8bbwe", Usual(650 * MB, 0.8, 0.05), [10, 11, 14, 15], 0.35, To: 69),
        new(2051, "MSTeams_25212.2204.3739.1001_x64__8wekyb3d8bbwe", Usual(650 * MB, 0.8, 0.05), [10, 11, 14, 15], 0.35, From: 70),
        new(2052, Nt([.. User, "appdata", "roaming", "zoom", "bin", "zoom.exe"]), Usual(480 * MB, 0.2, 0.05), [16, 17], 0.4),
        new(2053, Nt([.. User, "appdata", "local", "discord", "app-1.0.9163", "discord.exe"]), Usual(260 * MB, 0.5, 0.8), Range(20, 23), 0.2),
        new(2054, Nt([.. User, "appdata", "roaming", "telegram desktop", "telegram.exe"]), Usual(70 * MB, 0.85, 0.85), [12, 13, .. Range(19, 22)], 0.25),
        new(2060, "Microsoft.WindowsStore_22508.1401.3.0_x64__8wekyb3d8bbwe", Usual(350 * MB, 0.15, 0.2), [12, 13], 0.01),
        // Windows presents these three as one "System and Windows Update".
        new(2070, "DoSvc", (_, plan) => plan.PatchDay ? Between(1.8, 2.6) * GB : Chance(0.15) ? 150 * MB * Spread() : 0, [11, 12, 13], 0.08, System: true),
        new(2071, "BITS", (_, plan) => plan.PatchDay ? Between(300, 500) * MB : Chance(0.2) ? 40 * MB * Spread() : 0, [11, 12, 13], 0.01, System: true),
        new(2072, "wuauserv", Usual(6 * MB, 0.9, 0.9), [11], 0.1, System: true),
        // Tiny but daily: the persistence route to a detail page.
        new(2073, "CryptSvc", Usual(1.2 * MB, 1, 1), Range(8, 22), 0.2, System: true),
        new(2074, "Dnscache", Usual(2 * MB, 1, 1), Range(8, 22), 0.4, System: true),
        // A one-off installer: 118 MB on one day earns no detail page.
        new(2080, Nt([.. User, "downloads", "vs_setup_bootstrapper.exe"]), (day, _) => day == 33 ? 118 * MB : 0, [15], 0.01),
    ];

    /// <summary>SRUM flushes once an hour at a minute or two past; every app shares it.</summary>
    private static DateTime FlushTime(int day, int hour) =>
        DayStart(day).AddHours(hour).AddMinutes(1 + (day * 7 + hour) % 5).AddSeconds((day * 13 + hour * 7) % 60);

    private static Row Aggregate(DateTime t, string profile, List<Row> rows)
    {
        long rx = rows.Sum(r => r.Rx), tx = rows.Sum(r => r.Tx);
        // A sliver more than the apps: traffic from processes that exited
        // before SRUM could attribute it. About 0.1% on a real machine.
        return new Row(t, 1, "", false, profile, (long)Math.Round(rx * (1 + Between(0, 0.0025))), (long)Math.Round(tx * (1 + Between(0, 0.0025))));
    }

    public static int Run(string[] args)
    {
        var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : "demo-data");
        if (Directory.Exists(outDir))
        {
            if (Directory.EnumerateFileSystemEntries(outDir).Any() && !File.Exists(Path.Combine(outDir, Marker)))
                throw new InvalidOperationException($"{outDir} is not empty and was not made by demo-data (no {Marker}). Refusing to clear it.");
            Directory.Delete(outDir, recursive: true);
        }
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, Marker), "Synthetic demo data from `datausage demo-data`. Safe to delete.\n");

        _seed = 20260922;
        _now = DateTime.Now;
        var dataUntil = _now.AddHours(-2);
        var plans = Enumerable.Range(0, HistoryDays).Select(PlanDay).ToList();
        var apps = Apps();

        // ---- rows ----
        var slots = new Dictionary<(int, int), (DateTime T, string Profile, List<Row> Rows)>();
        for (var day = 0; day < HistoryDays; day++)
        {
            var plan = plans[day];
            foreach (var app in apps)
            {
                if ((app.From is { } f && day < f) || (app.To is { } t && day > t)) continue;
                var dayBytes = app.Use(day, plan);
                if (dayBytes <= 0) continue;
                var hours = plan.Hours.Where(h => app.Only is null || app.Only.Contains(h.Value)).ToList();
                if (hours.Count == 0) continue;
                var weights = hours.Select(h => (app.Peak.Contains(h.Key) ? 3 : 0.35) * Between(0.6, 1.4)).ToList();
                var total = weights.Sum();
                for (var k = 0; k < hours.Count; k++)
                {
                    var bytes = dayBytes * weights[k] / total;
                    if (bytes < 20 * 1024) continue;
                    var up = Math.Min(0.9, app.UpShare * Between(0.7, 1.3));
                    var key = (day, hours[k].Key);
                    if (!slots.TryGetValue(key, out var slot)) slots[key] = slot = (FlushTime(day, hours[k].Key), hours[k].Value, []);
                    slot.Rows.Add(new Row(slot.T, app.AppId, app.Identity, app.System, slot.Profile, (long)Math.Round(bytes * (1 - up)), (long)Math.Round(bytes * up)));
                }
            }
        }

        var rows = new List<Row>();
        var kept = new List<(DateTime T, string Profile)>();
        foreach (var slot in slots.Values)
        {
            if (slot.Rows.Count == 0 || slot.T >= dataUntil) continue;
            kept.Add((slot.T, slot.Profile));
            rows.AddRange(slot.Rows);
            rows.Add(Aggregate(slot.T, slot.Profile, slot.Rows));
        }

        // Off-cadence rows at sleep, which the dedup key must keep beside the
        // hourly row of the same app and hour.
        for (var day = 0; day < HistoryDays; day++)
        {
            var last = plans[day].Hours.Keys.DefaultIfEmpty(-1).Max();
            if (last < 20 || !Chance(0.12)) continue;
            var t = DayStart(day).AddHours(last).AddMinutes(40).AddSeconds(12);
            if (t >= dataUntil) continue;
            var profile = plans[day].Hours[last];
            var flush = new List<Row>();
            foreach (var app in new[] { apps[0], apps[14] })
            {
                var bytes = Between(2, 30) * MB;
                flush.Add(new Row(t, app.AppId, app.Identity, false, profile, (long)Math.Round(bytes * 0.93), (long)Math.Round(bytes * 0.07)));
            }
            rows.AddRange(flush);
            rows.Add(Aggregate(t, profile, flush));
        }
        rows.Sort((a, b) => a.T != b.T ? a.T.CompareTo(b.T) : a.AppId.CompareTo(b.AppId));
        kept.Sort((a, b) => a.T.CompareTo(b.T));

        // ---- collector runs ----
        var runs = new List<(DateTime At, string? Failed, string? Observe)>();
        for (int d = 40, k = 0; d < HistoryDays - 1; d += 7, k++)
            runs.Add((DayStart(d).AddHours(3.5), null, k is 2 or 6 ? CafeSsid : k == 4 ? HomeAlias : HomeSsid));
        runs.Add((DayStart(72).AddHours(3.5), "could not read the snapshot (EsentDatabaseDirtyShutdownException; state 'Dirty Shutdown')", null));
        runs.Add((_now.AddMinutes(-50), null, HomeSsid));
        runs.Sort((a, b) => a.At.CompareTo(b.At));

        var dbPath = AppPaths.DatabasePath(outDir);
        var onSystem = AppPaths.IsOnSystemDrive(outDir);
        using (var db = UsageDb.Open(dbPath, allowSystemDrive: onSystem))
        {
            var n = 0;
            foreach (var run in runs)
            {
                n++;
                var startedAt = SrumFields.IsoUtc(run.At.ToUniversalTime());
                if (run.Failed is not null)
                {
                    var id = UsageDb.StartRun(db, startedAt);
                    UsageDb.FinishRun(db, id, new SyncResult { Status = "failed", Error = run.Failed, DurationMs = (long)Between(8000, 14000) });
                    Backdate(db, id, run.At, (long)Between(8000, 14000));
                    Console.WriteLine($"  run {n,2}  {run.At:dd MMM}  failed (recorded)");
                    continue;
                }

                // What SRUM would hold then: ~60 days back, lagging an hour.
                var window = rows.Where(r => r.T >= run.At.AddDays(-60) && r.T < run.At.AddHours(-1)).Select(ToUsage).ToList();
                var runId = UsageDb.StartRun(db, startedAt);
                var inserted = UsageDb.InsertRows(db, window, startedAt);

                // An observation inside an hour the window holds and only one
                // profile used, so it resolves at once.
                var wanted = run.Observe == CafeSsid ? Cafe : Home;
                var hour = kept.LastOrDefault(s => s.Profile == wanted && s.T >= run.At.AddDays(-60) && s.T < run.At.AddHours(-1));
                if (run.Observe is not null && hour.T != default)
                    NetworkNames.Record(db, [new Connection(run.Observe, "Wi-Fi")], SrumFields.IsoUtc(hour.T.ToUniversalTime().AddMinutes(5)));
                NetworkNames.Resolve(db);

                UsageDb.FinishRun(db, runId, new SyncResult
                {
                    Status = "success", RowsRead = window.Count, RowsInserted = inserted, RowsSkipped = window.Count - inserted,
                    SrumOldestUtc = window.Min(r => r.TimestampUtc), SrumNewestUtc = window.Max(r => r.TimestampUtc), BackupStatus = "ok",
                });
                Backdate(db, runId, run.At, (long)Between(19000, 31000));
                Console.WriteLine($"  run {n,2}  {run.At:dd MMM}  {window.Count} rows read, {inserted} inserted, {window.Count - inserted} already present");
            }
            UsageDb.Checkpoint(db);
            UsageDb.Backup(db, AppPaths.BackupPath(outDir));
        }

        new DeviceSettings { DeviceLabel = "My PC", SplitApp = "qBittorrent" }.Save(outDir);

        var aggregate = rows.Where(r => r.AppId == 1).Sum(r => (double)r.Rx + r.Tx);
        Console.WriteLine($"\n  {aggregate / GB:F1} GB over {HistoryDays} days, {rows.Count} SRUM rows");
        Console.WriteLine($"\nPoint the app at it (both variables, so its preferences stay apart from yours too):");
        Console.WriteLine($"  $env:{AppPaths.DataDirVariable} = '{outDir}'");
        Console.WriteLine($"  $env:DATAUSAGE_LOCAL_DIR = '{Path.Combine(outDir, "local")}'");
        return 0;
    }

    private static UsageRow ToUsage(Row r)
    {
        var utc = r.T.ToUniversalTime();
        utc = utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerSecond));
        var (date, hour) = SrumFields.LocalBuckets(utc);
        var aggregate = r.AppId == SrumFields.AggregateAppId;
        return new UsageRow(SrumFields.IsoUtc(utc), date, hour, r.AppId, aggregate, r.Identity, SrumFields.ClassifyApp(r.AppId, r.Identity),
            aggregate ? "2" : r.System ? "3" : "401",
            // A placeholder account SID, obviously not anyone's.
            aggregate ? "" : r.System ? "S-1-5-18" : "S-1-5-21-1000000000-2000000000-3000000000-1001",
            WifiLuid, "IF_TYPE_IEEE80211", r.Profile, r.Tx, r.Rx);
    }

    private static void Backdate(Microsoft.Data.Sqlite.SqliteConnection db, long id, DateTime at, long durationMs) =>
        UsageDb.Exec(db, "UPDATE sync_log SET started_at = $s, finished_at = $f, duration_ms = $d WHERE id = $id",
            ("$s", SrumFields.IsoUtc(at.ToUniversalTime())), ("$f", SrumFields.IsoUtc(at.ToUniversalTime().AddMilliseconds(durationMs))), ("$d", durationMs), ("$id", id));
}
