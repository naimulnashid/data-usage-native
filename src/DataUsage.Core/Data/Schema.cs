namespace DataUsage.Core.Data;

/// <summary>
/// The database schema. <c>usage_records</c>, <c>sync_log</c>, the two network
/// tables and <c>app_renames</c> have exactly the web dashboard's columns, so
/// its history imports row for row and the dedup key recognises its rows.
/// </summary>
public static class Schema
{
    public const int Version = 2;

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
        """;
}
