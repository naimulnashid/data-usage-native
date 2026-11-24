using System.Globalization;
using System.Text.Json;
using DataUsage.Core.Data;
using DataUsage.Core.Srum;
using Microsoft.Data.Sqlite;

namespace DataUsage.Core.Android;

public sealed record PhoneApp(long Uid, string Package, string Label, bool IsSystem);

/// <param name="Start">Bucket start, epoch milliseconds UTC.</param>
/// <param name="UtcOffsetMinutes">The phone's offset at THIS bucket, sent only where it differs from the upload's (phone app 1.5.2+).</param>
public sealed record PhoneBucket(long Uid, long Start, string Network, bool Metered, bool Roaming, long Rx, long Tx, int? UtcOffsetMinutes);

public sealed record PhonePayload(
    string DeviceId,
    string? Label,
    string? Brand,
    string? Model,
    string? Release,
    int? Sdk,
    string? AppVersion,
    int UtcOffsetMinutes,
    IReadOnlyList<PhoneApp> Apps,
    IReadOnlyList<PhoneBucket> Buckets);

public sealed record PhoneIngestOutcome(long Written, long Updated, int Apps, string? Oldest, string? Newest);

/// <summary>
/// Writing one upload from a phone, a port of the web dashboard's
/// <c>android-ingest.ts</c>: the same checks, the same upsert, the same log row.
/// </summary>
/// <remarks>
/// <para><b>A NetworkStats bucket is a running total, not an immutable
/// delta.</b> SRUM never touches a row again, so identical bytes mean a
/// duplicate and the bytes are in its dedup key. A phone's bucket accumulates
/// across its 2-hour window: read at 10:30 it is partial, read at 11:30 it is
/// larger under the same key. <c>INSERT OR IGNORE</c> would keep the partial
/// figure forever and bytes in the key would store both and double-count, so
/// this upserts with <c>MAX()</c>, which also stops a stale partial arriving
/// late from shrinking a bucket.</para>
/// <para>The upload is checked in full before anything is written. Whoever
/// sealed it holds the ingest token, but a token on a phone is the kind of
/// secret that ends up somewhere unexpected, and a buggy phone build should be
/// turned away rather than stored.</para>
/// </remarks>
public static class PhoneIngest
{
    private static readonly HashSet<string> Networks = ["wifi", "mobile"];

    /// <summary>
    /// How far ahead of this clock a bucket may start: a phone's clock can run
    /// fast. What it rules out is a timestamp no skew explains, which would
    /// anchor every chart's "latest day" in the future.
    /// </summary>
    private static readonly long MaxFutureMs = (long)TimeSpan.FromDays(2).TotalMilliseconds;

    /// <summary>JavaScript's Number.MAX_SAFE_INTEGER: the phone's JSON is read as JS numbers on the web side.</summary>
    private const long MaxSafe = 9_007_199_254_740_991;

    public sealed class InvalidPayloadException(string message) : Exception(message);

    /// <summary>Throws <see cref="InvalidPayloadException"/> with a readable reason rather than return something half-valid.</summary>
    public static PhonePayload Validate(JsonElement p, DateTimeOffset? now = null)
    {
        var nowMs = (now ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
        if (p.ValueKind != JsonValueKind.Object) throw Bad("body must be an object");

        var deviceId = Str(p, "deviceId");
        if (deviceId is null || deviceId.Length < 8 || deviceId.Length > 128) throw Bad("deviceId must be a string of 8-128 characters");
        var offset = Int(p, "utcOffsetMinutes");
        if (offset is null || !IsOffset(offset.Value)) throw Bad("utcOffsetMinutes out of range");
        var label = OptionalText(p, "label", 128);
        var brand = OptionalText(p, "brand", 128);
        var model = OptionalText(p, "model", 128);
        var release = OptionalText(p, "release", 32);
        var appVersion = OptionalText(p, "appVersion", 64);
        int? sdk = null;
        if (Has(p, "sdk"))
        {
            var s = Int(p, "sdk");
            if (s is null || s < 0 || s > 1000) throw Bad("sdk must be an integer API level");
            sdk = (int)s.Value;
        }

        if (!p.TryGetProperty("apps", out var apps) || apps.ValueKind != JsonValueKind.Array
            || !p.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array)
            throw Bad("apps and buckets must be arrays");
        // Three months of 2-hour buckets across ~130 uids and two networks is
        // on the order of 100k rows; these guard against a runaway client.
        if (buckets.GetArrayLength() > 400_000) throw Bad("too many buckets in one upload");
        if (apps.GetArrayLength() > 20_000) throw Bad("too many apps in one upload");

        var appList = new List<PhoneApp>(apps.GetArrayLength());
        foreach (var a in apps.EnumerateArray())
        {
            if (a.ValueKind != JsonValueKind.Object) throw Bad("each app must be an object");
            var uid = Int(a, "uid") ?? throw Bad("app.uid must be an integer");
            var package = Str(a, "package");
            if (package is null || package.Length == 0 || package.Length > 256) throw Bad("app.package must be a string of 1-256 characters");
            var appLabel = OptionalText(a, "label", 256, "app.label");
            var isSystem = a.TryGetProperty("isSystem", out var sys) && sys.ValueKind == JsonValueKind.True;
            appList.Add(new PhoneApp(uid, package, appLabel ?? "", isSystem));
        }

        var bucketList = new List<PhoneBucket>(buckets.GetArrayLength());
        foreach (var b in buckets.EnumerateArray())
        {
            if (b.ValueKind != JsonValueKind.Object) throw Bad("each bucket must be an object");
            var uid = Int(b, "uid") ?? throw Bad("bucket.uid must be an integer");
            var start = Int(b, "start");
            if (start is null || start < 1_000_000_000_000) throw Bad("bucket.start must be epoch ms");
            if (start > nowMs + MaxFutureMs) throw Bad("bucket.start is in the future");
            var network = Str(b, "network");
            if (network is null || !Networks.Contains(network)) throw Bad("bucket.network must be one of wifi, mobile");
            var rx = Int(b, "rx");
            var tx = Int(b, "tx");
            if (rx is null || tx is null || rx < 0 || tx < 0 || rx > MaxSafe || tx > MaxSafe)
                throw Bad("bucket.rx and bucket.tx must be non-negative integers");
            int? bucketOffset = null;
            if (Has(b, "utcOffsetMinutes"))
            {
                var o = Int(b, "utcOffsetMinutes");
                if (o is null || !IsOffset(o.Value)) throw Bad("bucket.utcOffsetMinutes out of range");
                bucketOffset = (int)o.Value;
            }
            bucketList.Add(new PhoneBucket(uid, start.Value, network, Bool(b, "metered"), Bool(b, "roaming"), rx.Value, tx.Value, bucketOffset));
        }

        return new PhonePayload(deviceId, label, brand, model, release, sdk, appVersion, (int)offset.Value, appList, bucketList);
    }

    private static InvalidPayloadException Bad(string message) => new(message);

    /// <summary>A UTC offset in whole minutes; real ones run from -12:00 to +14:00.</summary>
    private static bool IsOffset(long v) => Math.Abs(v) <= 15 * 60;

    /// <summary>Present and not null: an explicit null is treated as absent, as the web side does.</summary>
    private static bool Has(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Int(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return null;
        if (v.TryGetInt64(out var i)) return i;
        // JS's Number.isInteger accepts 3.0; a whole double is an integer here too.
        return v.TryGetDouble(out var d) && Math.Abs(d) <= MaxSafe && d == Math.Floor(d) ? (long)d : null;
    }

    private static bool Bool(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>Every string the phone sends is rendered somewhere; none has cause to be long.</summary>
    private static string? OptionalText(JsonElement o, string name, int max, string? field = null)
    {
        if (!Has(o, name)) return null;
        var v = o.GetProperty(name);
        if (v.ValueKind != JsonValueKind.String || v.GetString()!.Length > max)
            throw Bad($"{field ?? name} must be a string of at most {max} characters");
        return v.GetString();
    }

    /// <summary>
    /// Epoch ms to the PHONE's local date and hour. Arithmetic, never this
    /// machine's time zone: the phone may not be in it.
    /// </summary>
    public static (string Date, int Hour) LocalParts(long epochMs, int offsetMinutes)
    {
        var shifted = DateTimeOffset.FromUnixTimeMilliseconds(epochMs + offsetMinutes * 60_000L).UtcDateTime;
        return (shifted.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), shifted.Hour);
    }

    /// <summary>The bucket's key column: what JavaScript's toISOString writes, so imported rows collide with new ones.</summary>
    public static string IsoOf(long epochMs) => SrumFields.IsoUtc(DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime);

    /// <summary>Write one upload in one transaction.</summary>
    public static PhoneIngestOutcome Ingest(SqliteConnection db, PhonePayload payload)
    {
        var now = UsageDb.NowIso();
        long Count() => (long)(Scalar(db, "SELECT COUNT(*) FROM android_usage_records WHERE device_id = $d", ("$d", payload.DeviceId)) ?? 0L);
        var before = Count();
        long touched = 0;
        string? oldest = null, newest = null;

        using (var tx = db.BeginTransaction())
        {
            using (var device = Command(db, tx, """
                INSERT INTO android_devices (device_id, label, brand, model, android_release, sdk, first_seen, last_seen)
                VALUES ($id, $label, $brand, $model, $release, $sdk, $now, $now)
                ON CONFLICT(device_id) DO UPDATE SET
                  label = excluded.label, brand = excluded.brand, model = excluded.model,
                  android_release = excluded.android_release, sdk = excluded.sdk,
                  last_seen = excluded.last_seen
                """))
            {
                device.Parameters.AddWithValue("$id", payload.DeviceId);
                device.Parameters.AddWithValue("$label", payload.Label ?? $"{payload.Brand ?? ""} {payload.Model ?? ""}".Trim());
                device.Parameters.AddWithValue("$brand", payload.Brand ?? "");
                device.Parameters.AddWithValue("$model", payload.Model ?? "");
                device.Parameters.AddWithValue("$release", payload.Release ?? "");
                device.Parameters.AddWithValue("$sdk", payload.Sdk ?? 0);
                device.Parameters.AddWithValue("$now", now);
                device.ExecuteNonQuery();
            }

            using (var app = Command(db, tx, """
                INSERT INTO android_apps (device_id, uid, package, label, is_system, user_profile, first_seen, last_seen)
                VALUES ($id, $uid, $package, $label, $system, $profile, $now, $now)
                ON CONFLICT(device_id, uid, package) DO UPDATE SET
                  label = excluded.label, is_system = excluded.is_system, last_seen = excluded.last_seen
                """))
            {
                var pUid = Param(app, "$uid");
                var pPackage = Param(app, "$package");
                var pLabel = Param(app, "$label");
                var pSystem = Param(app, "$system");
                var pProfile = Param(app, "$profile");
                app.Parameters.AddWithValue("$id", payload.DeviceId);
                app.Parameters.AddWithValue("$now", now);
                foreach (var a in payload.Apps)
                {
                    pUid.Value = a.Uid;
                    pPackage.Value = a.Package;
                    pLabel.Value = a.Label;
                    pSystem.Value = a.IsSystem ? 1 : 0;
                    // Math.floor, as the web side does: a negative special uid lands in profile -1.
                    pProfile.Value = (long)Math.Floor(a.Uid / 100_000.0);
                    app.ExecuteNonQuery();
                }
            }

            // MAX(), not assignment, and only when larger: an upload carrying
            // an older partial reading must not shrink a complete bucket.
            using (var bucket = Command(db, tx, """
                INSERT INTO android_usage_records
                  (device_id, uid, bucket_start_utc, local_date, local_hour, network, metered, roaming, rx_bytes, tx_bytes, ingested_at)
                VALUES ($id, $uid, $start, $date, $hour, $network, $metered, $roaming, $rx, $tx, $now)
                ON CONFLICT(device_id, uid, bucket_start_utc, network, metered, roaming) DO UPDATE SET
                  rx_bytes    = MAX(rx_bytes, excluded.rx_bytes),
                  tx_bytes    = MAX(tx_bytes, excluded.tx_bytes),
                  ingested_at = excluded.ingested_at
                WHERE excluded.rx_bytes > rx_bytes OR excluded.tx_bytes > tx_bytes
                """))
            {
                var pUid = Param(bucket, "$uid");
                var pStart = Param(bucket, "$start");
                var pDate = Param(bucket, "$date");
                var pHour = Param(bucket, "$hour");
                var pNetwork = Param(bucket, "$network");
                var pMetered = Param(bucket, "$metered");
                var pRoaming = Param(bucket, "$roaming");
                var pRx = Param(bucket, "$rx");
                var pTx = Param(bucket, "$tx");
                bucket.Parameters.AddWithValue("$id", payload.DeviceId);
                bucket.Parameters.AddWithValue("$now", now);
                foreach (var b in payload.Buckets)
                {
                    var iso = IsoOf(b.Start);
                    var (date, hour) = LocalParts(b.Start, b.UtcOffsetMinutes ?? payload.UtcOffsetMinutes);
                    pUid.Value = b.Uid;
                    pStart.Value = iso;
                    pDate.Value = date;
                    pHour.Value = hour;
                    pNetwork.Value = b.Network;
                    pMetered.Value = b.Metered ? 1 : 0;
                    pRoaming.Value = b.Roaming ? 1 : 0;
                    pRx.Value = b.Rx;
                    pTx.Value = b.Tx;
                    if (bucket.ExecuteNonQuery() > 0) touched++;
                    if (oldest is null || string.CompareOrdinal(iso, oldest) < 0) oldest = iso;
                    if (newest is null || string.CompareOrdinal(iso, newest) > 0) newest = iso;
                }
            }
            tx.Commit();
        }

        // ExecuteNonQuery cannot tell an insert from an update, so the split
        // comes from the row count. It is only ever reported, never acted on.
        var written = Count() - before;
        return new PhoneIngestOutcome(written, Math.Max(0, touched - written), payload.Apps.Count, oldest, newest);
    }

    /// <summary>One row of the phone's upload history.</summary>
    /// <param name="receivedAt">When the PHONE sent it (the inbox's receive time); now when null.</param>
    public static void LogSync(SqliteConnection db, string deviceId, string status, PhoneIngestOutcome? outcome = null,
        int bucketsSent = 0, string? appVersion = null, string? error = null, string? receivedAt = null, string? landedAt = null)
    {
        UsageDb.Exec(db, """
            INSERT INTO android_sync_log
              (device_id, received_at, status, buckets_sent, rows_written, rows_updated, apps_sent,
               oldest_bucket, newest_bucket, app_version, error, landed_at)
            VALUES ($d, $r, $s, $b, $w, $u, $a, $o, $n, $v, $e, $l)
            """,
            ("$d", deviceId), ("$r", receivedAt ?? UsageDb.NowIso()), ("$s", status), ("$b", bucketsSent),
            ("$w", outcome?.Written ?? 0), ("$u", outcome?.Updated ?? 0), ("$a", outcome?.Apps ?? 0),
            ("$o", outcome?.Oldest), ("$n", outcome?.Newest), ("$v", appVersion), ("$e", error), ("$l", landedAt));
    }

    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction tx, string sql)
    {
        var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    private static SqliteParameter Param(SqliteCommand cmd, string name) => cmd.Parameters.Add(new SqliteParameter(name, null));

    private static object? Scalar(SqliteConnection db, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd.ExecuteScalar();
    }
}
