namespace DataUsage.Core.Data;

/// <summary>
/// The database schema. <c>usage_records</c>, <c>sync_log</c>, the two network
/// tables, <c>app_renames</c> and the phones' <c>android_*</c> tables (version
/// 4) have exactly the web dashboard's columns, so its history imports row for
/// row and the dedup keys recognise its rows.
/// </summary>
public static class Schema
{
    public const int Version = 4;

    public const string Sql = """
        PRAGMA journal_mode = WAL;

        CREATE TABLE IF NOT EXISTS usage_records (
          id                INTEGER PRIMARY KEY AUTOINCREMENT,
          -- UTC is the source of truth; never bucket days off it directly.
          timestamp_utc     TEXT    NOT NULL,
          -- Machine-local day and hour, computed at ingest. Without them every
          -- daily total shifts by the UTC offset.
          local_date        TEXT    NOT NULL,
          local_hour        INTEGER NOT NULL,
          -- app_id 1 is NOT an app: it is the per-interface aggregate, equal to
          -- the sum of every named app in its hour. is_aggregate makes summing
          -- everything require ignoring a column, not merely forgetting a filter.
          app_id            INTEGER NOT NULL,
          is_aggregate      INTEGER NOT NULL DEFAULT 0,
          -- An NT path, an AppX package full name OR a service name - not an
          -- exe path. Display names are resolved in code, never stored.
          app_identity      TEXT    NOT NULL DEFAULT '',
          app_kind          TEXT    NOT NULL,   -- path | appx | service | aggregate | unknown
          user_id           TEXT    NOT NULL DEFAULT '',
          sid               TEXT    NOT NULL DEFAULT '',
          -- TEXT: real LUIDs exceed 2^53.
          interface_luid    TEXT    NOT NULL DEFAULT '',
          interface_type    TEXT    NOT NULL DEFAULT '',
          -- Which network profile. Windows' own Data usage page is scoped by it.
          l2_profile_id     TEXT    NOT NULL DEFAULT '',
          -- Empty in every SRUM row ever seen; kept for the imported history's shape.
          profile_name      TEXT    NOT NULL DEFAULT '',
          bytes_sent        INTEGER NOT NULL,
          bytes_received    INTEGER NOT NULL,
          ingested_at       TEXT    NOT NULL
        );

        -- Settled against 18,476 real rows: without l2_profile_id 91 collisions,
        -- without the byte values 7 - genuinely distinct measurements SRUM
        -- writes off-cadence at sleep and shutdown. Idempotency still holds: a
        -- re-read produces byte-identical rows, which INSERT OR IGNORE skips.
        -- Never SRUM's AutoIncId: Windows renumbers when it rebuilds the file.
        CREATE UNIQUE INDEX IF NOT EXISTS idx_usage_dedup
          ON usage_records(timestamp_utc, app_id, user_id, interface_luid,
                           l2_profile_id, bytes_sent, bytes_received);

        CREATE INDEX IF NOT EXISTS idx_usage_local_date ON usage_records(local_date);
        CREATE INDEX IF NOT EXISTS idx_usage_identity   ON usage_records(app_identity);
        CREATE INDEX IF NOT EXISTS idx_usage_aggregate  ON usage_records(is_aggregate, local_date);
        CREATE INDEX IF NOT EXISTS idx_usage_profile    ON usage_records(l2_profile_id);

        -- One row per collection. The history's survival depends on the task
        -- actually running, so a broken one must show, not be found after a reset.
        CREATE TABLE IF NOT EXISTS sync_log (
          id              INTEGER PRIMARY KEY AUTOINCREMENT,
          started_at      TEXT    NOT NULL,
          finished_at     TEXT,
          status          TEXT    NOT NULL,   -- running | success | failed
          rows_read       INTEGER NOT NULL DEFAULT 0,
          rows_inserted   INTEGER NOT NULL DEFAULT 0,
          rows_skipped    INTEGER NOT NULL DEFAULT 0,
          srum_oldest_utc TEXT,
          srum_newest_utc TEXT,
          backup_status   TEXT,
          duration_ms     INTEGER,
          error           TEXT
        );

        CREATE INDEX IF NOT EXISTS idx_sync_started ON sync_log(started_at DESC);

        -- What the machine was connected to, and when. Resolved to a profile id
        -- only once SRUM has written that hour, and only if exactly one profile
        -- has rows in it; 'ambiguous' otherwise. See NetworkNames.
        CREATE TABLE IF NOT EXISTS network_observations (
          observed_at TEXT PRIMARY KEY,
          name        TEXT NOT NULL,
          interface   TEXT NOT NULL DEFAULT '',
          resolved_to TEXT
        );

        CREATE INDEX IF NOT EXISTS idx_obs_unresolved ON network_observations(resolved_to);

        -- One row per (profile, name) with a vote count: one L2ProfileId can
        -- carry several SSIDs (a router's 2.4 and 5 GHz networks), and
        -- overwriting made the label flip-flop.
        CREATE TABLE IF NOT EXISTS network_names (
          l2_profile_id TEXT NOT NULL,
          name          TEXT NOT NULL,
          interface     TEXT NOT NULL DEFAULT '',
          votes         INTEGER NOT NULL DEFAULT 0,
          first_seen    TEXT NOT NULL,
          last_seen     TEXT NOT NULL,
          PRIMARY KEY (l2_profile_id, name)
        );

        CREATE TABLE IF NOT EXISTS meta (
          key   TEXT PRIMARY KEY,
          value TEXT NOT NULL
        );

        -- The user's own display names. In the database rather than a settings
        -- file because a rename is part of the history: backed up, and it
        -- survives the reset. app_key is the FAMILY group key. The device
        -- column is always 'windows'; it matches the web dashboard's table.
        CREATE TABLE IF NOT EXISTS app_renames (
          device     TEXT NOT NULL,
          app_key    TEXT NOT NULL,
          name       TEXT NOT NULL,
          updated_at TEXT NOT NULL,
          PRIMARY KEY (device, app_key)
        );

        -- Chart colours chosen in the app (version 2, 2026-09-30), keyed like
        -- app_renames, as lowercase #rrggbb. Only the user's overrides; every
        -- other colour is still assigned in code. The web dashboard's table,
        -- column for column. See Naming/ColorOverrides.cs.
        CREATE TABLE IF NOT EXISTS app_colors (
          device     TEXT NOT NULL,
          app_key    TEXT NOT NULL,
          color      TEXT NOT NULL,
          updated_at TEXT NOT NULL,
          PRIMARY KEY (device, app_key)
        );

        -- Apps hidden from the By App page, and apps merged into another
        -- (version 3, 2026-10-29), keyed like app_renames. The web
        -- dashboard's tables, column for column. See Naming/AppPrefs.cs.
        CREATE TABLE IF NOT EXISTS app_hidden (
          device     TEXT NOT NULL,
          app_key    TEXT NOT NULL,
          hidden_at  TEXT NOT NULL,
          PRIMARY KEY (device, app_key)
        );

        CREATE TABLE IF NOT EXISTS app_merges (
          device     TEXT NOT NULL,
          app_key    TEXT NOT NULL,
          into_key   TEXT NOT NULL,
          updated_at TEXT NOT NULL,
          PRIMARY KEY (device, app_key)
        );
        -- ===================== PHONES (version 4) =====================
        -- Separate tables, never UNION-ed with usage_records: the actor is a
        -- Linux uid, not an exe; buckets are 2 hours; the network axis is
        -- wifi/mobile; and a row is a RUNNING TOTAL for its bucket, not an
        -- immutable delta. See Android/PhoneIngest.cs.

        -- device_id is a random UUID the phone app made, not a hardware id.
        CREATE TABLE IF NOT EXISTS android_devices (
          device_id       TEXT PRIMARY KEY,
          label           TEXT    NOT NULL DEFAULT '',
          brand           TEXT    NOT NULL DEFAULT '',
          model           TEXT    NOT NULL DEFAULT '',
          android_release TEXT    NOT NULL DEFAULT '',
          sdk             INTEGER NOT NULL DEFAULT 0,
          first_seen      TEXT    NOT NULL,
          last_seen       TEXT    NOT NULL
        );

        -- uid -> package, as the PHONE reports it: only its PackageManager
        -- knows the labels and which packages share a uid. user_profile is
        -- uid / 100000, so a cloned app (profile 999) stays apart.
        CREATE TABLE IF NOT EXISTS android_apps (
          device_id    TEXT    NOT NULL,
          uid          INTEGER NOT NULL,
          package      TEXT    NOT NULL,
          label        TEXT    NOT NULL DEFAULT '',
          is_system    INTEGER NOT NULL DEFAULT 0,
          user_profile INTEGER NOT NULL DEFAULT 0,
          first_seen   TEXT    NOT NULL,
          last_seen    TEXT    NOT NULL,
          PRIMARY KEY (device_id, uid, package)
        );

        CREATE INDEX IF NOT EXISTS idx_android_apps_uid ON android_apps(device_id, uid);

        -- No byte values in the key, the opposite of usage_records: a bucket
        -- read mid-window is partial and read again later is larger, so the
        -- ingest upserts with MAX() instead of storing both.
        CREATE TABLE IF NOT EXISTS android_usage_records (
          device_id        TEXT    NOT NULL,
          uid              INTEGER NOT NULL,
          bucket_start_utc TEXT    NOT NULL,
          local_date       TEXT    NOT NULL,
          local_hour       INTEGER NOT NULL,
          network          TEXT    NOT NULL,   -- wifi | mobile
          metered          INTEGER NOT NULL DEFAULT 0,
          roaming          INTEGER NOT NULL DEFAULT 0,
          rx_bytes         INTEGER NOT NULL,
          tx_bytes         INTEGER NOT NULL,
          ingested_at      TEXT    NOT NULL,
          PRIMARY KEY (device_id, uid, bucket_start_utc, network, metered, roaming)
        );

        CREATE INDEX IF NOT EXISTS idx_android_usage_date_cover
          ON android_usage_records(device_id, local_date, uid, network, local_hour, rx_bytes, tx_bytes);
        CREATE INDEX IF NOT EXISTS idx_android_usage_uid_cover
          ON android_usage_records(device_id, uid, local_date, network, local_hour, rx_bytes, tx_bytes);

        -- One row per upload. received_at is when the PHONE sent it; landed_at
        -- when it reached this database, for one drained from the cloud inbox
        -- (NULL for a direct upload, which only the web dashboard took).
        CREATE TABLE IF NOT EXISTS android_sync_log (
          id            INTEGER PRIMARY KEY AUTOINCREMENT,
          device_id     TEXT    NOT NULL,
          received_at   TEXT    NOT NULL,
          status        TEXT    NOT NULL,   -- success | rejected
          buckets_sent  INTEGER NOT NULL DEFAULT 0,
          rows_written  INTEGER NOT NULL DEFAULT 0,
          rows_updated  INTEGER NOT NULL DEFAULT 0,
          apps_sent     INTEGER NOT NULL DEFAULT 0,
          oldest_bucket TEXT,
          newest_bucket TEXT,
          app_version   TEXT,
          error         TEXT,
          landed_at     TEXT
        );

        CREATE INDEX IF NOT EXISTS idx_android_sync_received ON android_sync_log(received_at DESC);

        -- Per-app traffic per Wi-Fi network, captured over USB by the web
        -- project's script. Imported so the history is whole; nothing here
        -- shows it (the web dashboard hides it too).
        CREATE TABLE IF NOT EXISTS android_ssid_usage (
          device_id        TEXT    NOT NULL,
          uid              INTEGER NOT NULL,
          bucket_start_utc TEXT    NOT NULL,
          local_date       TEXT    NOT NULL,
          local_hour       INTEGER NOT NULL,
          ssid             TEXT    NOT NULL,
          metered          INTEGER NOT NULL DEFAULT 0,
          rx_bytes         INTEGER NOT NULL,
          tx_bytes         INTEGER NOT NULL,
          ingested_at      TEXT    NOT NULL,
          PRIMARY KEY (device_id, uid, bucket_start_utc, ssid, metered)
        );
        """;
}
