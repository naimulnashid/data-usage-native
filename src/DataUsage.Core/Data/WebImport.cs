using Microsoft.Data.Sqlite;

namespace DataUsage.Core.Data;

public sealed record ImportResult(long Rows, long RowsNew, long Runs, long Observations, long Names, long Renames);

/// <summary>
/// A one-time copy of the web dashboard's Windows history into this database.
/// </summary>
/// <remarks>
/// <para>Why it exists: SRUM keeps only 30-60 days, so a new collector starts
/// with a month or two. The web dashboard's database holds everything since it
/// began. Copying it once is the only way the older days come across.</para>
/// <para>It is a COPY, not a link: afterwards nothing here reads the other
/// project. The two collectors read the same SRUM, so their rows are
/// byte-identical and the shared dedup key folds the overlap away - running
/// this twice, or after the native collector has run, inserts no duplicates.</para>
/// <para>What comes across: usage rows, the run log (its SRUM windows are what
/// mark a quiet day as known rather than uncollected), network observations
/// and learned names, and the laptop's renames. Nothing Android.</para>
/// </remarks>
public static class WebImport
{
    public static ImportResult Run(SqliteConnection target, string sourcePath)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("web dashboard database not found", sourcePath);

        // Attach a COPY: the source is only ever read by a file copy, so
        // nothing here can write to, lock or journal the other project's file.
        // (Its backup is a single self-contained file, so a copy is complete.)
        var copy = Path.Combine(Path.GetTempPath(), $"DataUsageNative-import-{Guid.NewGuid():N}.db");
        File.Copy(sourcePath, copy);
        UsageDb.Exec(target, "ATTACH DATABASE $src AS web", ("$src", copy));
        try
        {
            long Count(string sql) => (long)(Scalar(target, sql) ?? 0L);

            var rows = Count("SELECT COUNT(*) FROM web.usage_records");
            using var tx = target.BeginTransaction();
            var rowsNew = Exec(target, tx, """
                INSERT OR IGNORE INTO usage_records (
                  timestamp_utc, local_date, local_hour, app_id, is_aggregate, app_identity, app_kind,
                  user_id, sid, interface_luid, interface_type, l2_profile_id, profile_name,
                  bytes_sent, bytes_received, ingested_at)
                SELECT timestamp_utc, local_date, local_hour, app_id, is_aggregate, app_identity, app_kind,
                  user_id, sid, interface_luid, interface_type, l2_profile_id, profile_name,
                  bytes_sent, bytes_received, ingested_at
                FROM web.usage_records ORDER BY id
                """);

            // Runs are matched on their start instant, so a second import adds none.
            var runs = Exec(target, tx, """
                INSERT INTO sync_log (started_at, finished_at, status, rows_read, rows_inserted, rows_skipped,
                  srum_oldest_utc, srum_newest_utc, backup_status, duration_ms, error)
                SELECT started_at, finished_at, status, rows_read, rows_inserted, rows_skipped,
                  srum_oldest_utc, srum_newest_utc, backup_status, duration_ms, error
                FROM web.sync_log w
                WHERE NOT EXISTS (SELECT 1 FROM sync_log s WHERE s.started_at = w.started_at)
                ORDER BY w.id
                """);

            var observations = Exec(target, tx, "INSERT OR IGNORE INTO network_observations SELECT observed_at, name, interface, resolved_to FROM web.network_observations");

            // Votes are counts of resolved observations; the larger count wins
            // rather than adding, since both sides may hold the same votes.
            var names = Exec(target, tx, """
                INSERT INTO network_names (l2_profile_id, name, interface, votes, first_seen, last_seen)
                SELECT l2_profile_id, name, interface, votes, first_seen, last_seen FROM web.network_names WHERE true
                ON CONFLICT(l2_profile_id, name) DO UPDATE SET
                  votes = MAX(network_names.votes, excluded.votes),
                  first_seen = MIN(network_names.first_seen, excluded.first_seen),
                  last_seen = MAX(network_names.last_seen, excluded.last_seen)
                """);

            var renames = Exec(target, tx, "INSERT OR IGNORE INTO app_renames SELECT device, app_key, name, updated_at FROM web.app_renames WHERE device = 'windows'");
            tx.Commit();

            UsageDb.SetMeta(target, "imported_from_web", UsageDb.NowIso());
            return new ImportResult(rows, rowsNew, runs, observations, names, renames);
        }
        finally
        {
            UsageDb.Exec(target, "DETACH DATABASE web");
            SqliteConnection.ClearAllPools();
            try { File.Delete(copy); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static int Exec(SqliteConnection conn, SqliteTransaction tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    /// <summary>
    /// Copy logo files that are not already here. Existing files win: a logo
    /// set in this app is never overwritten by the import.
    /// </summary>
    public static int CopyLogos(string sourceDir, string targetDir)
    {
        if (!Directory.Exists(sourceDir)) return 0;
        Directory.CreateDirectory(targetDir);
        var copied = 0;
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var dest = Path.Combine(targetDir, Path.GetFileName(file));
            if (File.Exists(dest)) continue;
            File.Copy(file, dest);
            copied++;
        }
        return copied;
    }
}
