using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using DataUsage.Core;
using DataUsage.Core.Collect;
using DataUsage.Core.Data;
using DataUsage.Core.Naming;
using DataUsage.Core.Query;
using DataUsage.Core.Srum;
using DataUsage.Core.View;

namespace DataUsage.Cli;

/// <summary>
/// <c>datausage</c>: the collector by hand, the diagnostics, the one-time
/// import and the demo data. Everything the app does to the database can be
/// done and checked from here.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        if (args.Length == 0) return Usage();
        var rest = args[1..];
        try
        {
            return args[0] switch
            {
                "collect" => Collect(rest),
                "read-srum" => ReadSrum(rest),
                "stats" => Stats(rest),
                "import-web" => ImportWeb(rest),
                "ingest" => Ingest(rest),
                "demo-data" => DemoCommand.Run(rest),
                "where" => Where(),
                "names" => Names(rest),
                _ => Usage(),
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("""
            datausage collect [--force] [--observe-only] [--keep-scratch] [--no-backup]
                Run the collector now, as the scheduled task does. Unelevated.
            datausage read-srum <SRUDB.dat> [--csv <SrumECmd NetworkUsages csv>]
                Read a CLEAN SRUM copy; with --csv, compare every field with SrumECmd's.
            datausage ingest <SRUDB.dat>
                Ingest a CLEAN SRUM copy by hand (no snapshot, no run log entry).
            datausage stats [--days N]
                Totals and the top apps, for comparing with Windows' Data usage page.
            datausage import-web --db <web backup .db> [--config <collector.json>] [--logos <dir>]
                One-time copy of the web dashboard's history, settings and logos.
            datausage names
                Raw identities -> members -> families, and any duplicate display names.
            datausage demo-data <dir>
                An invented history for screenshots. Prints the variable to point the app at it.
            datausage where
                Where the data, logs and scratch live.
            """);
        return 2;
    }

    private static bool Flag(string[] args, string name) => args.Contains("--" + name);

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, "--" + name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string RequireDataDir() =>
        AppPaths.DataDir ?? throw new InvalidOperationException($"no data folder chosen. Set {AppPaths.DataDirVariable} or run the app once.");

    private static int Where()
    {
        var dir = AppPaths.DataDir;
        Console.WriteLine($"data folder : {dir ?? "(not chosen)"}");
        if (dir is not null)
        {
            Console.WriteLine($"database    : {AppPaths.DatabasePath(dir)}");
            Console.WriteLine($"backup      : {AppPaths.BackupPath(dir)}");
            Console.WriteLine($"logos       : {AppPaths.LogosDir(dir)}");
        }
        Console.WriteLine($"local       : {AppPaths.LocalDir}");
        Console.WriteLine($"scratch     : {AppPaths.ScratchDir}");
        Console.WriteLine($"snapshot    : {AppPaths.SnapshotDir}");
        foreach (var t in new[] { ScheduledTasks.CollectorTask, ScheduledTasks.SnapshotTask })
        {
            var info = ScheduledTasks.Query(t);
            Console.WriteLine($"task        : {t}: {(info.Exists ? $"registered, last run {info.LastRunUtc?.ToLocalTime():yyyy-MM-dd HH:mm} (0x{info.LastResult:X}), next {info.NextRunUtc?.ToLocalTime():yyyy-MM-dd HH:mm}" : "NOT registered")}");
        }
        return 0;
    }

    private static int Collect(string[] args) => Collector.Run(new CollectOptions
    {
        Force = Flag(args, "force"),
        ObserveOnly = Flag(args, "observe-only"),
        KeepScratch = Flag(args, "keep-scratch"),
        SkipBackup = Flag(args, "no-backup"),
        AllowSystemDrive = Flag(args, "allow-system-drive"),
        Log = (level, message) => Console.WriteLine($"[{level}] {message}"),
    });

    /// <summary>
    /// The row-for-row check that justified replacing SrumECmd: every field of
    /// every row, compared as a multiset.
    /// </summary>
    private static int ReadSrum(string[] args)
    {
        if (args.Length == 0) return Usage();
        var sw = Stopwatch.StartNew();
        var rows = SrumReader.ReadNetworkUsage(args[0]);
        Console.WriteLine($"read {rows.Count} rows in {sw.ElapsedMilliseconds} ms");
        if (rows.Count > 0)
            Console.WriteLine($"window {rows.Min(r => r.TimestampUtc)} -> {rows.Max(r => r.TimestampUtc)}; {rows.Count(r => r.IsAggregate)} aggregate rows");

        var csv = Arg(args, "csv");
        if (csv is null) return 0;

        static string Key(UsageRow r) => string.Join("|", r.TimestampUtc, r.LocalDate, r.LocalHour, r.AppId, r.IsAggregate,
            r.AppIdentity, r.AppKind, r.UserId, r.Sid, r.InterfaceLuid, r.InterfaceType, r.L2ProfileId, r.BytesSent, r.BytesReceived);

        var lines = File.ReadAllLines(csv);
        var header = lines[0].TrimStart('﻿').Split(',');
        int Col(string n) => Array.IndexOf(header, n) is var i and >= 0 ? i : throw new InvalidOperationException($"CSV has no {n} column");
        var theirs = new List<string>();
        foreach (var line in lines.Skip(1))
        {
            if (line.Contains('"')) throw new InvalidOperationException("quoted CSV fields are not supported by this check");
            var f = line.Split(',');
            var utc = DateTime.ParseExact(f[Col("Timestamp")], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            var app = long.Parse(f[Col("AppId")], CultureInfo.InvariantCulture);
            var ident = f[Col("ExeInfo")].Trim();
            var (d, h) = SrumFields.LocalBuckets(utc);
            theirs.Add(Key(new UsageRow(SrumFields.IsoUtc(utc), d, h, app, app == SrumFields.AggregateAppId, ident, SrumFields.ClassifyApp(app, ident),
                f[Col("UserId")].Trim(), f[Col("Sid")].Trim(), f[Col("InterfaceLuid")].Trim(), f[Col("InterfaceType")].Trim(), f[Col("L2ProfileId")].Trim(),
                long.Parse(f[Col("BytesSent")], CultureInfo.InvariantCulture), long.Parse(f[Col("BytesReceived")], CultureInfo.InvariantCulture))));
        }

        var a = rows.Select(Key).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var b = theirs.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var onlyMine = a.Where(kv => b.GetValueOrDefault(kv.Key) != kv.Value).Select(kv => kv.Key).ToList();
        var onlyTheirs = b.Where(kv => a.GetValueOrDefault(kv.Key) != kv.Value).Select(kv => kv.Key).ToList();
        Console.WriteLine($"csv  {theirs.Count} rows; differing keys: {onlyMine.Count} here, {onlyTheirs.Count} in the CSV");
        foreach (var k in onlyMine.Take(5)) Console.WriteLine("  here: " + k);
        foreach (var k in onlyTheirs.Take(5)) Console.WriteLine("  csv : " + k);
        return onlyMine.Count + onlyTheirs.Count == 0 ? 0 : 1;
    }

    private static int Ingest(string[] args)
    {
        if (args.Length == 0) return Usage();
        var dir = RequireDataDir();
        var rows = SrumReader.ReadNetworkUsage(args[0]);
        using var db = UsageDb.Open(AppPaths.DatabasePath(dir), AppPaths.ReadLocation().AllowSystemDrive || Flag(args, "allow-system-drive"));
        var inserted = UsageDb.InsertRows(db, rows);
        Console.WriteLine($"read {rows.Count}, inserted {inserted}, already present {rows.Count - inserted}");
        foreach (var note in NetworkNames.Resolve(db)) Console.WriteLine("network: " + note);
        return 0;
    }

    private static int Stats(string[] args)
    {
        var dir = RequireDataDir();
        var days = int.TryParse(Arg(args, "days"), out var n) ? n : Scope.AllDays;
        var q = new UsageQueries(AppPaths.DatabasePath(dir), DeviceSettings.Load(dir).Split);
        if (!q.DatabaseExists) throw new InvalidOperationException("no database yet: " + q.DatabasePath);
        var scope = new Scope(days);
        var o = q.Overview(scope);
        var apps = q.ByApp(scope);
        var sync = q.Sync(5);
        Console.WriteLine($"rows {Format.Count(q.RowCount())}, coverage {o.Coverage?.First} -> {o.Coverage?.Last} ({o.Coverage?.Days} days)");
        Console.WriteLine($"all {Format.Bytes(o.All.Total)} | 30d {Format.Bytes(o.Month.Total)} | 7d {Format.Bytes(o.Week.Total)} | latest {Format.Bytes(o.Today.Total)} ({o.LatestDate})");
        Console.WriteLine($"range {scope.Label}: headline {Format.Bytes(apps.HeadlineTotal)}, named {Format.Bytes(apps.NamedTotal)}, unattributed {Format.Bytes(apps.Unattributed)}");
        Console.WriteLine($"{apps.Apps.Count} families, {apps.Apps.Count(a => a.Detailed)} with a detail page");
        foreach (var a in apps.Apps.Take(15)) Console.WriteLine($"  {a.Name,-32} {Format.Bytes(a.Total),10} {Format.Percent(a.Share),7} {a.Days,4}d");
        if (o.SplitApp is not null) Console.WriteLine($"split: {o.SplitApp} {Format.Bytes(o.SplitFocus)} vs everything else {Format.Bytes(o.SplitOther)}");
        foreach (var r in sync.Runs) Console.WriteLine($"  run {r.Id} {r.StartedAt} {r.Status} read {r.RowsRead} new {r.RowsInserted} backup {r.BackupStatus} {r.Error}");
        return 0;
    }

    private static int ImportWeb(string[] args)
    {
        var source = Arg(args, "db") ?? throw new ArgumentException("--db <web backup .db> is required");
        var dir = RequireDataDir();
        var location = AppPaths.ReadLocation();
        using var db = UsageDb.Open(AppPaths.DatabasePath(dir), location.AllowSystemDrive || Flag(args, "allow-system-drive"));
        var sw = Stopwatch.StartNew();
        var r = WebImport.Run(db, source);
        Console.WriteLine($"usage rows : {r.Rows} in source, {r.RowsNew} new here ({r.Rows - r.RowsNew} already present)");
        Console.WriteLine($"runs {r.Runs}, observations {r.Observations}, network names {r.Names}, renames {r.Renames} ({sw.ElapsedMilliseconds} ms)");

        if (Arg(args, "config") is { } config)
        {
            var json = JsonNode.Parse(File.ReadAllText(config));
            var settings = DeviceSettings.Load(dir);
            settings.DeviceLabel ??= json?["deviceLabel"]?.GetValue<string>();
            settings.SplitApp ??= json?["splitApp"]?.GetValue<string>();
            settings.Save(dir);
            Console.WriteLine($"settings   : device '{settings.DeviceLabel}', split app '{settings.SplitApp}'");
        }
        if (Arg(args, "logos") is { } logos)
            Console.WriteLine($"logos      : {WebImport.CopyLogos(logos, AppPaths.LogosDir(dir))} copied");

        UsageDb.Checkpoint(db);
        Console.WriteLine($"backup     : {UsageDb.Backup(db, AppPaths.BackupPath(dir))}");
        return 0;
    }

    /// <summary>The two-stage naming, measured over the whole history.</summary>
    private static int Names(string[] args)
    {
        var dir = RequireDataDir();
        using var db = UsageDb.OpenRead(AppPaths.DatabasePath(dir));
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT app_identity, app_kind FROM usage_records WHERE is_aggregate = 0";
        var ids = new List<(string, string)>();
        using (var r = cmd.ExecuteReader()) while (r.Read()) ids.Add((r.GetString(0), r.GetString(1)));
        var resolved = ids.Select(x => AppNames.Resolve(x.Item1, x.Item2)).ToList();
        Console.WriteLine($"{ids.Count} raw identities -> {resolved.Select(r => r.MemberKey).Distinct().Count()} members -> {resolved.Select(r => r.GroupKey).Distinct().Count()} families");
        var clashes = resolved.GroupBy(r => r.DisplayName).Where(g => g.Select(r => r.GroupKey).Distinct().Count() > 1).ToList();
        foreach (var g in clashes) Console.WriteLine($"  DUPLICATE NAME '{g.Key}': {string.Join(", ", g.Select(r => r.GroupKey).Distinct())}");
        if (Flag(args, "list"))
            foreach (var g in resolved.GroupBy(r => r.GroupKey).OrderBy(g => g.First().DisplayName))
                Console.WriteLine($"  {g.First().DisplayName} [{g.Key}]: {string.Join(", ", g.Select(r => r.MemberName).Distinct())}");
        return clashes.Count == 0 ? 0 : 1;
    }
}
