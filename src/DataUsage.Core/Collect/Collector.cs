using System.Diagnostics;
using System.Globalization;
using DataUsage.Core.Data;
using DataUsage.Core.Srum;

namespace DataUsage.Core.Collect;

public sealed class CollectOptions
{
    /// <summary>Read SRUM even if the last success was recent (the Sync button).</summary>
    public bool Force { get; init; }

    /// <summary>Record the network only.</summary>
    public bool ObserveOnly { get; init; }

    /// <summary>Keep scratch afterwards. It holds real usage data.</summary>
    public bool KeepScratch { get; init; }

    public bool SkipBackup { get; init; }

    /// <summary>Tests and the demo only: their databases live in TEMP or the repo.</summary>
    public bool AllowSystemDrive { get; init; }

    public Action<string, string>? Log { get; init; }
}

/// <summary>
/// One collector run: observe the network, and - when due - snapshot, recover,
/// read, ingest, resolve network names and back up.
/// </summary>
/// <remarks>
/// <para>Runs every 15 minutes, unelevated, from the collector task. SRUM is
/// read at most hourly: SRUM writes hourly and keeps 30+ days, so reading it
/// more often than eviction is about freshness, not loss - and each read is a
/// 99 MB VSS copy. The 15-minute cadence exists for the network sample, which
/// only names a network when it lands in a clean hour.</para>
/// <para>Idempotent: the dedup key makes a re-read insert nothing, so there is
/// no watermark to keep.</para>
/// </remarks>
public static class Collector
{
    /// <summary>How long a successful SRUM read counts as current.</summary>
    public static readonly TimeSpan SrumInterval = TimeSpan.FromMinutes(55);

    /// <summary>Held for the length of a run, across sessions: the task runs in its own.</summary>
    public const string MutexName = @"Global\DataUsageNative.Collector";

    private const string ScratchMarker = ".data-usage-scratch";

    public static int Run(CollectOptions options)
    {
        var started = Stopwatch.StartNew();
        using var log = new RunLog(options.Log);
        using var mutex = new Mutex(false, MutexName);
        bool owned;
        try { owned = mutex.WaitOne(TimeSpan.FromMinutes(options.Force ? 10 : 0)); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            log.Write("another collector run is in progress; nothing to do", "INFO");
            return 0;
        }

        try
        {
            return RunLocked(options, log, started);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static int RunLocked(CollectOptions options, RunLog log, Stopwatch started)
    {
        var location = AppPaths.ReadLocation();
        var dataDir = AppPaths.DataDir;
        if (dataDir is null)
        {
            log.Write("no data folder chosen yet; open Data Usage once to choose one", "ERROR");
            return 1;
        }
        var allowSystem = options.AllowSystemDrive || location.AllowSystemDrive;
        var dbPath = AppPaths.DatabasePath(dataDir);

        Microsoft.Data.Sqlite.SqliteConnection db;
        try { db = UsageDb.Open(dbPath, allowSystem); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            log.Write(ex.Message, "ERROR");
            return 1;
        }

        using (db)
        {
            // 1. The network, every run.
            var observedAt = UsageDb.NowIso();
            try
            {
                var connections = NetworkNames.Current();
                if (NetworkNames.Record(db, connections, observedAt)) log.Write($"connected to: {connections[0].Name}", "OK");
                else if (connections.Count == 0) log.Write("no active network connection to record", "INFO");
                else log.Write($"{connections.Count} connections at once ({string.Join(", ", connections.Select(c => c.Name))}); not recorded", "INFO");
            }
            catch (Exception ex) when (ex is System.Management.ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                log.Write("could not read the connection profile: " + ex.Message, "WARN");
            }

            if (options.ObserveOnly) return 0;

            if (!options.Force && LastSuccessAge(db) is { } age && age < SrumInterval)
            {
                log.Write($"SRUM read {Math.Round(age.TotalMinutes)} min ago; next read is due in {Math.Round((SrumInterval - age).TotalMinutes)} min", "INFO");
                return 0;
            }

            log.Write("=== SRUM collection started ===", "INFO");
            try
            {
                Collect(db, dataDir, options, log, started);
                log.Write($"=== run complete in {started.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s ===", "OK");
                return 0;
            }
            catch (Exception ex)
            {
                log.Write(ex.Message, "ERROR");
                try { UsageDb.RecordFailure(db, ex.Message, started.ElapsedMilliseconds); }
                catch (Microsoft.Data.Sqlite.SqliteException inner) { log.Write("could not record the failure: " + inner.Message, "WARN"); }
                // Scratch stays: it is the evidence.
                log.Write("=== run FAILED ===", "ERROR");
                return 1;
            }
        }
    }

    private static TimeSpan? LastSuccessAge(Microsoft.Data.Sqlite.SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT started_at FROM sync_log WHERE status = 'success' ORDER BY id DESC LIMIT 1";
        return cmd.ExecuteScalar() is string s && View.Format.TryInstant(s, out var t) ? DateTime.UtcNow - t : null;
    }

    private static void Collect(Microsoft.Data.Sqlite.SqliteConnection db, string dataDir, CollectOptions options, RunLog log, Stopwatch started)
    {
        var scratch = AppPaths.ScratchDir;
        PrepareScratch(scratch);
        var snapshot = Path.Combine(scratch, SrumRecovery.DatabaseName);

        // 1. The VSS snapshot, by the elevated task.
        var sw = Stopwatch.StartNew();
        TakeSnapshot(snapshot, log);
        log.Write($"snapshot ok: {new FileInfo(snapshot).Length / 1048576.0:F1} MB in {sw.Elapsed.TotalSeconds:F1}s (elevated task)", "OK");

        // 2. Recovery - unelevated, in scratch. A failure is a warning: a
        //    snapshot that happens to be clean still reads, and the read error
        //    is the more useful place to give up.
        var srumDir = Path.GetDirectoryName(AppPaths.SrumPath)!;
        var rec = SrumRecovery.Recover(scratch, srumDir);
        foreach (var w in rec.Warnings) log.Write(w, "WARN");
        if (rec.Ok && rec.Repaired) log.Write($"replay fell short; hard repair cleaned the snapshot ({rec.LogsCopied} journals copied)", "WARN");
        else if (rec.Ok) log.Write($"soft recovery ok ({rec.LogsCopied} journals replayed, database is clean)", "OK");
        else log.Write($"recovery failed (state '{rec.State}', exit {rec.ExitCode}, {rec.LogsCopied} journals)", "WARN");

        // 3. Read.
        sw.Restart();
        List<UsageRow> rows;
        try { rows = SrumReader.ReadNetworkUsage(snapshot); }
        catch (Exception ex) when (ex.GetType().Namespace == "Microsoft.Isam.Esent.Interop")
        {
            throw new InvalidOperationException($"could not read the snapshot ({ex.GetType().Name}; state '{rec.State}', repaired={rec.Repaired}, {rec.LogsCopied} journals copied, {rec.JournalsMissed} missed): {ex.Message}", ex);
        }
        if (rows.Count == 0) throw new InvalidOperationException("the snapshot held no network rows");
        var oldest = rows.Min(r => r.TimestampUtc)!;
        var newest = rows.Max(r => r.TimestampUtc)!;
        log.Write($"read {rows.Count} rows in {sw.Elapsed.TotalSeconds:F1}s, {oldest} -> {newest}", "OK");

        // 4. Ingest.
        var runId = UsageDb.StartRun(db);
        var result = new SyncResult { RowsRead = rows.Count, SrumOldestUtc = oldest, SrumNewestUtc = newest };
        var logged = false;
        try
        {
            result.RowsInserted = UsageDb.InsertRows(db, rows);
            result.RowsSkipped = rows.Count - result.RowsInserted;
            result.Status = "success";
            log.Write($"inserted {result.RowsInserted}, skipped {result.RowsSkipped} (already present)", "OK");

            // After the rows land: resolution needs the hours this run added.
            foreach (var note in NetworkNames.Resolve(db)) log.Write("network: " + note, "INFO");

            // Finish the log row BEFORE the backup, or every backup carries a
            // run frozen at 'running' - and a restored database then reports
            // the last success as older than it was.
            var doBackup = !options.SkipBackup;
            result.DurationMs = started.ElapsedMilliseconds;
            result.BackupStatus = doBackup ? "pending" : "skipped";
            UsageDb.FinishRun(db, runId, result);
            logged = true;

            UsageDb.Checkpoint(db);
            if (doBackup)
            {
                result.BackupStatus = UsageDb.Backup(db, AppPaths.BackupPath(dataDir));
                log.Write("backup: " + result.BackupStatus, result.BackupStatus == "ok" ? "OK" : "WARN");
                result.DurationMs = started.ElapsedMilliseconds;
                UsageDb.FinishRun(db, runId, result);
            }
        }
        catch (Exception ex)
        {
            result.Status = "failed";
            result.Error = ex.Message;
            throw;
        }
        finally
        {
            if (!logged)
            {
                result.DurationMs = started.ElapsedMilliseconds;
                UsageDb.FinishRun(db, runId, result);
            }
        }

        if (options.KeepScratch) log.Write($"scratch kept at {scratch} (contains real usage data)", "WARN");
        else ClearScratch(scratch);
    }

    /// <summary>
    /// Ask the elevated task for a snapshot and copy it into scratch. The task
    /// writes <c>status.txt</c> last; one older than the request is a previous
    /// run's, never this one's.
    /// </summary>
    private static void TakeSnapshot(string destination, RunLog log)
    {
        var work = AppPaths.SnapshotDir;
        var status = Path.Combine(work, "status.txt");
        var info = ScheduledTasks.Query(ScheduledTasks.SnapshotTask);
        if (!info.Exists)
            throw new InvalidOperationException($"The '{ScheduledTasks.SnapshotTask}' task is not registered, so nothing can take the SRUM snapshot. Run tools\\Install.ps1 once.");

        var requested = DateTime.UtcNow.AddSeconds(-2);
        log.Write($"requesting a snapshot from '{ScheduledTasks.SnapshotTask}'", "INFO");
        ScheduledTasks.Run(ScheduledTasks.SnapshotTask);

        var deadline = DateTime.UtcNow.AddMinutes(10);
        DateTime? idleSince = null;
        string? outcome = null;
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(1000);
            if (File.Exists(status) && File.GetLastWriteTimeUtc(status) >= requested)
            {
                outcome = ReadShared(status).Trim();
                break;
            }
            // A task that ran and stopped without a status never will write one.
            var now = ScheduledTasks.Query(ScheduledTasks.SnapshotTask);
            if (now.LastRunUtc >= requested && !now.Running)
            {
                idleSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - idleSince > TimeSpan.FromSeconds(10))
                    throw new InvalidOperationException($"'{ScheduledTasks.SnapshotTask}' finished (result 0x{now.LastResult:X}) without reporting a status. Re-run tools\\Install.ps1.");
            }
        }
        if (outcome is null) throw new InvalidOperationException($"'{ScheduledTasks.SnapshotTask}' did not report within 10 minutes.");
        if (outcome != "ok")
        {
            var detail = File.Exists(Path.Combine(work, "esentutl.txt")) ? ReadShared(Path.Combine(work, "esentutl.txt")).Trim() : "";
            var tail = string.Join(" ", detail.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(3));
            throw new InvalidOperationException($"the VSS snapshot failed: {tail}");
        }

        var taken = Path.Combine(work, SrumRecovery.DatabaseName);
        if (!File.Exists(taken)) throw new InvalidOperationException($"the snapshot task reported success but {taken} is missing");
        File.Copy(taken, destination, overwrite: true);
    }

    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Empty scratch, refusing anything that is not plainly ours: a link, or a
    /// directory holding files the collector never writes.
    /// </summary>
    private static void PrepareScratch(string scratch)
    {
        ClearScratch(scratch);
        Directory.CreateDirectory(scratch);
        File.WriteAllText(Path.Combine(scratch, ScratchMarker), "Scratch for the Data Usage collector. Safe to delete; it is cleared every run.\r\n");
    }

    private static void ClearScratch(string scratch)
    {
        if (!Directory.Exists(scratch)) return;
        var dir = new DirectoryInfo(scratch);
        if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException($"scratch '{scratch}' is a link; refusing to clear it.");
        if (!File.Exists(Path.Combine(scratch, ScratchMarker)))
        {
            var strangers = dir.EnumerateFileSystemInfos().Where(f => !IsCollectorFile(f.Name)).Select(f => f.Name).Take(3).ToList();
            if (strangers.Count > 0)
                throw new InvalidOperationException($"scratch '{scratch}' holds files the collector did not put there ({string.Join(", ", strangers)}); refusing to clear it.");
        }
        dir.Delete(recursive: true);
    }

    private static bool IsCollectorFile(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, @"^(SRUDB\.dat|SRU.*\.log|sru\.chk|srures\d+\.jrs|.*\.INTEG\.RAW|\.data-usage-scratch)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>The day's log file, plus whoever else wants the lines.</summary>
    private sealed class RunLog(Action<string, string>? sink) : IDisposable
    {
        private StreamWriter? _writer;

        public void Write(string message, string level)
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
            sink?.Invoke(level, message);
            try
            {
                if (_writer is null)
                {
                    Directory.CreateDirectory(AppPaths.LogsDir);
                    var path = Path.Combine(AppPaths.LogsDir, $"collector-{DateTime.Now:yyyy-MM-dd}.log");
                    _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { AutoFlush = true };
                }
                _writer.WriteLine(line);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public void Dispose() => _writer?.Dispose();
    }
}
