using DataUsage.Core.Data;
using Microsoft.Data.Sqlite;

namespace DataUsage.Core.Naming;

public enum MergeError
{
    None,
    UnknownApp,
    SameApp,
}

/// <summary>
/// Two choices the user makes about apps on the By App page: hiding one, and
/// merging one into another. Stored beside renames and colours (schema 3),
/// keyed the same way - device <c>'windows'</c> and the FAMILY group key - and
/// column for column the web dashboard's <c>app_hidden</c> and
/// <c>app_merges</c>.
/// </summary>
/// <remarks>
/// <para><b>Hiding is a view choice.</b> A hidden app leaves the By App list,
/// grid and Top 10, and nothing else: totals, charts and every other page
/// still count it, or a hidden app would make the numbers stop adding up.</para>
/// <para><b>Merging is a naming choice</b>, like a rename one level up: the
/// merged family is read as part of its target everywhere, exactly as the
/// built-in families merge Claude Code and Claude Desktop. The target's
/// Merged apps card lists it and separates it again. No bytes move.</para>
/// <para>One level only: a target is never itself merged. Merging into a
/// merged app stores the merge against ITS target, and merging an app others
/// were merged into carries them along, so a lookup is one step and a chain
/// can never loop.</para>
/// </remarks>
public static class AppPrefs
{
    /// <summary>Family key to the key it is merged into.</summary>
    public static Dictionary<string, string> ReadMerges(SqliteConnection db) =>
        ReadPairs(db, "SELECT app_key, into_key FROM app_merges WHERE device = $d");

    /// <summary>Family keys hidden from the By App page.</summary>
    public static HashSet<string> ReadHidden(SqliteConnection db) =>
        [.. ReadPairs(db, "SELECT app_key, app_key FROM app_hidden WHERE device = $d").Keys];

    private static Dictionary<string, string> ReadPairs(SqliteConnection db, string sql)
    {
        var map = new Dictionary<string, string>();
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$d", Renames.Device);
            using var r = cmd.ExecuteReader();
            while (r.Read()) map[r.GetString(0)] = r.GetString(1);
        }
        catch (SqliteException)
        {
            // "no such table" on a database no write path has touched yet.
        }
        return map;
    }

    public static void SetHidden(SqliteConnection db, string key, bool hidden)
    {
        if (hidden)
            UsageDb.Exec(db, "INSERT INTO app_hidden (device, app_key, hidden_at) VALUES ($d, $k, $t) ON CONFLICT (device, app_key) DO NOTHING",
                ("$d", Renames.Device), ("$k", key), ("$t", UsageDb.NowIso()));
        else
            UsageDb.Exec(db, "DELETE FROM app_hidden WHERE device = $d AND app_key = $k", ("$d", Renames.Device), ("$k", key));
    }

    /// <summary>
    /// Merge <paramref name="key"/> into <paramref name="into"/>, or separate it
    /// again when <paramref name="into"/> is null. <paramref name="known"/> is
    /// every family key BEFORE merges.
    /// </summary>
    public static MergeError SetMerge(SqliteConnection db, string key, string? into, IReadOnlySet<string> known)
    {
        if (!known.Contains(key)) return MergeError.UnknownApp;
        if (into is null)
        {
            UsageDb.Exec(db, "DELETE FROM app_merges WHERE device = $d AND app_key = $k", ("$d", Renames.Device), ("$k", key));
            return MergeError.None;
        }
        if (!known.Contains(into)) return MergeError.UnknownApp;
        var target = ReadMerges(db).GetValueOrDefault(into) ?? into;
        if (target == key) return MergeError.SameApp;

        var now = UsageDb.NowIso();
        // BEGIN as SQL rather than BeginTransaction: Exec's commands carry no
        // transaction object, which Microsoft.Data.Sqlite refuses inside one.
        UsageDb.Exec(db, "BEGIN");
        try
        {
            // Whatever was merged into `key` follows it to the new target.
            UsageDb.Exec(db, "UPDATE app_merges SET into_key = $t, updated_at = $n WHERE device = $d AND into_key = $k",
                ("$t", target), ("$n", now), ("$d", Renames.Device), ("$k", key));
            UsageDb.Exec(db, """
                INSERT INTO app_merges (device, app_key, into_key, updated_at) VALUES ($d, $k, $t, $n)
                ON CONFLICT (device, app_key) DO UPDATE SET into_key = excluded.into_key, updated_at = excluded.updated_at
                """, ("$d", Renames.Device), ("$k", key), ("$t", target), ("$n", now));
            // A merged app is not on the list to be hidden from.
            UsageDb.Exec(db, "DELETE FROM app_hidden WHERE device = $d AND app_key = $k", ("$d", Renames.Device), ("$k", key));
            UsageDb.Exec(db, "COMMIT");
        }
        catch
        {
            UsageDb.Exec(db, "ROLLBACK");
            throw;
        }
        return MergeError.None;
    }

    public static string Message(MergeError e) => e switch
    {
        MergeError.UnknownApp => "That app is not in the history.",
        MergeError.SameApp => "An app cannot be merged into itself.",
        _ => "",
    };
}
