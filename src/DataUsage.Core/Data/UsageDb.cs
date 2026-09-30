using System.Globalization;
using DataUsage.Core.Srum;
using Microsoft.Data.Sqlite;

namespace DataUsage.Core.Data;

/// <summary>The outcome of one collection, as <c>sync_log</c> records it.</summary>
public sealed class SyncResult
{
    public string Status { get; set; } = "failed";
    public long RowsRead { get; set; }
    public long RowsInserted { get; set; }
    public long RowsSkipped { get; set; }
    public string? SrumOldestUtc { get; set; }
    public string? SrumNewestUtc { get; set; }
    public string? BackupStatus { get; set; }
    public long DurationMs { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Opening the database, and every write the collector makes.
/// </summary>
public static class UsageDb
{
    /// <summary>
    /// The one rule the project exists to enforce. A database under the system
    /// drive is destroyed by the Windows reset it is meant to survive, and
    /// failing loudly now beats finding out after one.
    /// </summary>
    public static void AssertNotOnSystemDrive(string path)
    {
        if (AppPaths.IsOnSystemDrive(path))
            throw new InvalidOperationException(
                $"Refusing to use a database on the system drive: {Path.GetFullPath(path)}. " +
                "It would be destroyed by a Windows reset, which is the exact failure this app exists to prevent. " +
                "Choose a data folder on another drive.");
    }

    /// <summary>
    /// Open for writing, creating the file and schema as needed.
    /// <paramref name="allowSystemDrive"/> is for tests and the demo, whose
    /// throwaway databases live in TEMP or the repo - never the collector's
    /// own choice.
    /// </summary>
    public static SqliteConnection Open(string path, bool allowSystemDrive = false)
    {
        if (!allowSystemDrive) AssertNotOnSystemDrive(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        conn.Open();
        Exec(conn, "PRAGMA busy_timeout = 10000;");
        Exec(conn, Schema.Sql);
        Exec(conn, "INSERT INTO meta(key, value) VALUES('schema_version', $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value", ("$v", Schema.Version.ToString(CultureInfo.InvariantCulture)));
        return conn;
    }

    /// <summary>
    /// Open for reading, per use. Not pooled: the collector writes on a
    /// schedule, and a long-lived handle across a WAL checkpoint serves stale
    /// numbers. Opening costs well under a millisecond.
    /// </summary>
    public static SqliteConnection OpenRead(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        conn.Open();
        Exec(conn, "PRAGMA busy_timeout = 5000;");
        return conn;
    }

    public static int Exec(SqliteConnection conn, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    public static string NowIso() => SrumFields.IsoUtc(DateTime.UtcNow);

    /// <summary>
    /// Insert every row the dedup key has not seen, in one transaction (without
    /// it ~33k single commits turn a sub-second ingest into minutes). Returns
    /// how many were new.
    /// </summary>
    public static long InsertRows(SqliteConnection conn, IReadOnlyCollection<UsageRow> rows, string? ingestedAt = null)
    {
        var stamp = ingestedAt ?? NowIso();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO usage_records (
              timestamp_utc, local_date, local_hour, app_id, is_aggregate,
              app_identity, app_kind, user_id, sid, interface_luid, interface_type,
              l2_profile_id, profile_name, bytes_sent, bytes_received, ingested_at
            ) VALUES ($t,$d,$h,$a,$g,$i,$k,$u,$s,$l,$y,$p,'',$bs,$br,$at)
            """;
        var p = new[] { "$t", "$d", "$h", "$a", "$g", "$i", "$k", "$u", "$s", "$l", "$y", "$p", "$bs", "$br", "$at" }
            .Select(n => cmd.Parameters.Add(new SqliteParameter { ParameterName = n })).ToArray();
        cmd.Prepare();

        long inserted = 0;
        foreach (var r in rows)
        {
            p[0].Value = r.TimestampUtc;
            p[1].Value = r.LocalDate;
            p[2].Value = r.LocalHour;
            p[3].Value = r.AppId;
            p[4].Value = r.IsAggregate ? 1 : 0;
            p[5].Value = r.AppIdentity;
            p[6].Value = r.AppKind;
            p[7].Value = r.UserId;
            p[8].Value = r.Sid;
            p[9].Value = r.InterfaceLuid;
            p[10].Value = r.InterfaceType;
            p[11].Value = r.L2ProfileId;
            p[12].Value = r.BytesSent;
            p[13].Value = r.BytesReceived;
            p[14].Value = stamp;
            inserted += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return inserted;
    }

    public static long StartRun(SqliteConnection conn, string? startedAt = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO sync_log(started_at, status) VALUES($s, 'running'); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$s", startedAt ?? NowIso());
        return (long)cmd.ExecuteScalar()!;
    }

    public static void FinishRun(SqliteConnection conn, long id, SyncResult r) =>
        Exec(conn, """
            UPDATE sync_log SET
              finished_at = $f, status = $st, rows_read = $rr, rows_inserted = $ri,
              rows_skipped = $rs, srum_oldest_utc = $o, srum_newest_utc = $n,
              backup_status = $b, duration_ms = $d, error = $e
            WHERE id = $id
            """,
            ("$f", NowIso()), ("$st", r.Status), ("$rr", r.RowsRead), ("$ri", r.RowsInserted),
            ("$rs", r.RowsSkipped), ("$o", r.SrumOldestUtc), ("$n", r.SrumNewestUtc),
            ("$b", r.BackupStatus), ("$d", r.DurationMs), ("$e", r.Error), ("$id", id));

    /// <summary>A run that died before it could ingest, so the page shows the breakage.</summary>
    public static void RecordFailure(SqliteConnection conn, string message, long durationMs)
    {
        var id = StartRun(conn);
        FinishRun(conn, id, new SyncResult { Status = "failed", Error = message, DurationMs = durationMs });
    }

    /// <summary>
    /// Fold the WAL into the main file and empty it. The live file sits in a
    /// synced folder; this shrinks the window in which its three files disagree
    /// from constant to momentary. It does not make it a restore source.
    /// </summary>
    public static void Checkpoint(SqliteConnection conn)
    {
        try { Exec(conn, "PRAGMA wal_checkpoint(TRUNCATE);"); }
        catch (SqliteException) { }
    }

    /// <summary>
    /// A consistent copy through SQLite's backup API - never a file copy, which
    /// can be torn mid-write and misses the WAL. Written beside the target and
    /// swapped in, so a crash mid-backup cannot leave the restore source half
    /// written. Returns a status for sync_log rather than throwing: a failed
    /// backup must not discard a successful collection.
    /// </summary>
    public static string Backup(SqliteConnection conn, string backupPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(backupPath))!);
            var tmp = backupPath + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
            using (var dest = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tmp, Pooling = false }.ToString()))
            {
                dest.Open();
                conn.BackupDatabase(dest);
                // The copy inherits WAL mode; a restore source must be one file.
                Exec(dest, "PRAGMA journal_mode = DELETE;");
            }
            File.Move(tmp, backupPath, overwrite: true);
            return "ok";
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            return "failed: " + ex.Message;
        }
    }

    public static string? Meta(SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public static void SetMeta(SqliteConnection conn, string key, string value) =>
        Exec(conn, "INSERT INTO meta(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value", ("$k", key), ("$v", value));
}
