using System.Management;
using Microsoft.Data.Sqlite;

namespace DataUsage.Core.Data;

/// <summary>A network the machine was connected to at one moment.</summary>
public sealed record Connection(string Name, string Interface);

/// <summary>
/// Naming networks. SRUM stores only <c>L2ProfileId</c>; its ProfileName is
/// empty in every row, and passing SrumECmd the SOFTWARE hive did not fill it.
/// So names are OBSERVED: record what the machine is connected to, and when,
/// and pair it with a profile later.
/// </summary>
/// <remarks>
/// <para><b>Attribution must be deferred.</b> SRUM writes hourly and the
/// snapshot misses the newest uncommitted hour, so its newest row is routinely
/// more than an hour old. Pairing "the network I am on now" with "the profile
/// owning the newest row" named a profile after a network it had never
/// carried - on real data, 2026-08-21.</para>
/// <para>So an observation resolves only once the hour it falls in is present
/// in <c>usage_records</c>, and only if exactly ONE profile has rows in that
/// hour. An hour spanning a network change carries two, and there is no
/// honest way to pick: it is marked <c>ambiguous</c>.</para>
/// <para><b>Sampling cadence is the whole game</b>: an observation only
/// resolves if its hour is clean. The collector samples every 15 minutes.</para>
/// </remarks>
public static class NetworkNames
{
    /// <summary>
    /// The connected networks, from the WMI class Get-NetConnectionProfile
    /// reads: no elevation needed, and it covers Ethernet as well as Wi-Fi.
    /// </summary>
    public static List<Connection> Current()
    {
        var list = new List<Connection>();
        using var searcher = new ManagementObjectSearcher(@"root\StandardCimv2", "SELECT Name, InterfaceAlias FROM MSFT_NetConnectionProfile");
        foreach (var obj in searcher.Get())
        {
            using (obj)
            {
                var name = obj["Name"] as string;
                if (!string.IsNullOrWhiteSpace(name)) list.Add(new Connection(name.Trim(), (obj["InterfaceAlias"] as string ?? "").Trim()));
            }
        }
        return list;
    }

    /// <summary>
    /// Record one observation. Two connections at once (Wi-Fi plus a dock)
    /// make the pairing ambiguous, and recording a guess is worse than
    /// recording nothing - so only a single connection is kept.
    /// </summary>
    public static bool Record(SqliteConnection conn, IReadOnlyList<Connection> connections, string observedAtIso)
    {
        if (connections.Count != 1) return false;
        var c = connections[0];
        return UsageDb.Exec(conn, "INSERT OR IGNORE INTO network_observations (observed_at, name, interface) VALUES ($o, $n, $i)",
            ("$o", observedAtIso), ("$n", c.Name), ("$i", c.Interface)) > 0;
    }

    /// <summary>Resolve every pending observation whose hour SRUM has now written.</summary>
    public static List<string> Resolve(SqliteConnection conn)
    {
        var pending = new List<(string At, string Name, string Iface)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT observed_at, name, interface FROM network_observations WHERE resolved_to IS NULL";
            using var r = cmd.ExecuteReader();
            while (r.Read()) pending.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
        }

        var notes = new List<string>();
        foreach (var obs in pending)
        {
            // SRUM rows are hourly: compare on the UTC hour the observation fell in.
            var hour = obs.At.Length >= 13 ? obs.At[..13] : obs.At;
            var profiles = new List<string>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT DISTINCT l2_profile_id FROM usage_records WHERE substr(timestamp_utc, 1, 13) = $h AND l2_profile_id NOT IN ('', '0')";
                cmd.Parameters.AddWithValue("$h", hour);
                using var r = cmd.ExecuteReader();
                while (r.Read()) profiles.Add(r.GetString(0));
            }

            // Hour not written yet: it resolves on a later run.
            if (profiles.Count == 0) continue;

            if (profiles.Count > 1)
            {
                UsageDb.Exec(conn, "UPDATE network_observations SET resolved_to = 'ambiguous' WHERE observed_at = $o", ("$o", obs.At));
                notes.Add($"{obs.Name}: hour {hour}Z carried {profiles.Count} profiles, not attributed");
                continue;
            }

            var now = UsageDb.NowIso();
            UsageDb.Exec(conn, """
                INSERT INTO network_names (l2_profile_id, name, interface, votes, first_seen, last_seen)
                VALUES ($p, $n, $i, 1, $now, $now)
                ON CONFLICT(l2_profile_id, name) DO UPDATE SET
                  votes = network_names.votes + 1,
                  last_seen = excluded.last_seen
                """, ("$p", profiles[0]), ("$n", obs.Name), ("$i", obs.Iface), ("$now", now));
            UsageDb.Exec(conn, "UPDATE network_observations SET resolved_to = $p WHERE observed_at = $o", ("$p", profiles[0]), ("$o", obs.At));
            notes.Add($"{obs.Name} -> profile {profiles[0]}");
        }
        return notes;
    }
}
