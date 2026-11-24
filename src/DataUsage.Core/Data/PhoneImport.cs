using Microsoft.Data.Sqlite;

namespace DataUsage.Core.Data;

/// <param name="Totals">Per phone: bytes in the source and here afterwards, tethering included. They must match.</param>
public sealed record PhoneImportResult(
    long Devices, long Rows, long RowsTouched, long Uploads, long Apps, long Prefs,
    IReadOnlyList<(string DeviceId, string Label, long Source, long Here)> Totals);

/// <summary>
/// A one-time copy of the web dashboard's PHONE history into this database,
/// the companion of <see cref="WebImport"/> (which copies only the laptop).
/// </summary>
/// <remarks>
/// <para>Android keeps ~90 days, and the web dashboard has held every upload
/// since each phone was added, so this is the only way the older months
/// come across. It is a COPY; afterwards nothing here reads the other
/// project.</para>
/// <para>Safe to run again, or after this app has drained the inbox itself:
/// buckets are upserted with the same <c>MAX()</c> the ingest uses, upload
/// log rows are matched on (phone, sent at, status), and the phones' renames,
/// colours, hides and merges are only added, never overwritten.</para>
/// </remarks>
public static class PhoneImport
{
    public static PhoneImportResult Run(SqliteConnection target, string sourcePath)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("web dashboard database not found", sourcePath);
        var copy = Path.Combine(Path.GetTempPath(), $"DataUsageNative-phones-{Guid.NewGuid():N}.db");
        File.Copy(sourcePath, copy);
        UsageDb.Exec(target, "ATTACH DATABASE $src AS web", ("$src", copy));
        try
        {
            if (Scalar(target, "SELECT COUNT(*) FROM web.sqlite_master WHERE type = 'table' AND name = 'android_usage_records'") is not long n || n == 0)
                throw new InvalidOperationException("the source database holds no phone history");
            var hasLanded = Scalar(target, "SELECT COUNT(*) FROM pragma_table_info('android_sync_log', 'web') WHERE name = 'landed_at'") is long l && l > 0;
            var hasSsid = Scalar(target, "SELECT COUNT(*) FROM web.sqlite_master WHERE type = 'table' AND name = 'android_ssid_usage'") is long s && s > 0;

            // Read before the transaction: a command without one is refused inside it.
            var rows = (long)(Scalar(target, "SELECT COUNT(*) FROM web.android_usage_records") ?? 0L);
            var prefTables = new[]
            {
                ("app_renames", "device, app_key, name, updated_at"),
                ("app_colors", "device, app_key, color, updated_at"),
                ("app_hidden", "device, app_key, hidden_at"),
                ("app_merges", "device, app_key, into_key, updated_at"),
            }.Where(t => Scalar(target, $"SELECT COUNT(*) FROM web.sqlite_master WHERE type = 'table' AND name = '{t.Item1}'") is long c && c > 0).ToList();

            long devices, touched, uploads, apps, prefs = 0;
            using (var tx = target.BeginTransaction())
            {
                devices = Exec(target, tx, """
                    INSERT INTO android_devices (device_id, label, brand, model, android_release, sdk, first_seen, last_seen)
                    SELECT device_id, label, brand, model, android_release, sdk, first_seen, last_seen FROM web.android_devices WHERE true
                    ON CONFLICT(device_id) DO UPDATE SET
                      label = CASE WHEN excluded.last_seen > android_devices.last_seen THEN excluded.label ELSE android_devices.label END,
                      brand = CASE WHEN excluded.last_seen > android_devices.last_seen THEN excluded.brand ELSE android_devices.brand END,
                      model = CASE WHEN excluded.last_seen > android_devices.last_seen THEN excluded.model ELSE android_devices.model END,
                      android_release = CASE WHEN excluded.last_seen > android_devices.last_seen THEN excluded.android_release ELSE android_devices.android_release END,
                      sdk = CASE WHEN excluded.last_seen > android_devices.last_seen THEN excluded.sdk ELSE android_devices.sdk END,
                      first_seen = MIN(android_devices.first_seen, excluded.first_seen),
                      last_seen = MAX(android_devices.last_seen, excluded.last_seen)
                    """);

                apps = Exec(target, tx, """
                    INSERT INTO android_apps (device_id, uid, package, label, is_system, user_profile, first_seen, last_seen)
                    SELECT device_id, uid, package, label, is_system, user_profile, first_seen, last_seen FROM web.android_apps WHERE true
                    ON CONFLICT(device_id, uid, package) DO UPDATE SET
                      label = CASE WHEN excluded.last_seen > android_apps.last_seen THEN excluded.label ELSE android_apps.label END,
                      is_system = CASE WHEN excluded.last_seen > android_apps.last_seen THEN excluded.is_system ELSE android_apps.is_system END,
                      first_seen = MIN(android_apps.first_seen, excluded.first_seen),
                      last_seen = MAX(android_apps.last_seen, excluded.last_seen)
                    """);

                // The ingest's own rule: a bucket only ever grows.
                touched = Exec(target, tx, """
                    INSERT INTO android_usage_records
                      (device_id, uid, bucket_start_utc, local_date, local_hour, network, metered, roaming, rx_bytes, tx_bytes, ingested_at)
                    SELECT device_id, uid, bucket_start_utc, local_date, local_hour, network, metered, roaming, rx_bytes, tx_bytes, ingested_at
                    FROM web.android_usage_records WHERE true
                    ON CONFLICT(device_id, uid, bucket_start_utc, network, metered, roaming) DO UPDATE SET
                      rx_bytes = MAX(rx_bytes, excluded.rx_bytes),
                      tx_bytes = MAX(tx_bytes, excluded.tx_bytes),
                      ingested_at = excluded.ingested_at
                    WHERE excluded.rx_bytes > rx_bytes OR excluded.tx_bytes > tx_bytes
                    """);

                // In the order the phones sent them, so ids here read in time order.
                uploads = Exec(target, tx, $"""
                    INSERT INTO android_sync_log (device_id, received_at, status, buckets_sent, rows_written, rows_updated, apps_sent,
                      oldest_bucket, newest_bucket, app_version, error, landed_at)
                    SELECT device_id, received_at, status, buckets_sent, rows_written, rows_updated, apps_sent,
                      oldest_bucket, newest_bucket, app_version, error, {(hasLanded ? "landed_at" : "NULL")}
                    FROM web.android_sync_log w
                    WHERE NOT EXISTS (SELECT 1 FROM android_sync_log s
                                      WHERE s.device_id = w.device_id AND s.received_at = w.received_at AND s.status = w.status)
                    ORDER BY w.received_at, w.id
                    """);

                if (hasSsid)
                    Exec(target, tx, """
                        INSERT INTO android_ssid_usage
                          (device_id, uid, bucket_start_utc, local_date, local_hour, ssid, metered, rx_bytes, tx_bytes, ingested_at)
                        SELECT device_id, uid, bucket_start_utc, local_date, local_hour, ssid, metered, rx_bytes, tx_bytes, ingested_at
                        FROM web.android_ssid_usage WHERE true
                        ON CONFLICT(device_id, uid, bucket_start_utc, ssid, metered) DO UPDATE SET
                          rx_bytes = MAX(rx_bytes, excluded.rx_bytes),
                          tx_bytes = MAX(tx_bytes, excluded.tx_bytes)
                        WHERE excluded.rx_bytes > rx_bytes OR excluded.tx_bytes > tx_bytes
                        """);

                // The phones' rows only: the laptop's came with WebImport, and
                // a choice made here since then must not be overwritten.
                foreach (var (table, cols) in prefTables)
                    prefs += Exec(target, tx, $"INSERT OR IGNORE INTO {table} ({cols}) SELECT {cols} FROM web.{table} WHERE device <> 'windows'");
                tx.Commit();
            }

            var totals = new List<(string, string, long, long)>();
            using (var cmd = target.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT d.device_id, d.label,
                      (SELECT COALESCE(SUM(rx_bytes + tx_bytes), 0) FROM web.android_usage_records w WHERE w.device_id = d.device_id),
                      (SELECT COALESCE(SUM(rx_bytes + tx_bytes), 0) FROM main.android_usage_records h WHERE h.device_id = d.device_id)
                    FROM web.android_devices d ORDER BY d.label
                    """;
                using var r = cmd.ExecuteReader();
                while (r.Read()) totals.Add((r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3)));
            }

            UsageDb.SetMeta(target, "imported_phones_from_web", UsageDb.NowIso());
            return new PhoneImportResult(devices, rows, touched, uploads, apps, prefs, totals);
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
}
