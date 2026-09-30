using System.Text.RegularExpressions;
using DataUsage.Core.Data;
using Microsoft.Data.Sqlite;

namespace DataUsage.Core.Naming;

public enum ColorError
{
    None,
    UnknownApp,
    BadColor,
}

/// <summary>
/// Chart colours the user chose, overriding the assigned ones. Stored in the
/// database beside renames, keyed the same way - device <c>'windows'</c> and
/// the FAMILY's group key - for the same reason: it is part of the history,
/// backed up with it, and it survives a reset.
/// </summary>
/// <remarks>
/// <see cref="Queries.UsageQueries.ColorMap"/> applies an override LAST: after
/// brand and palette assignment and after a rename has carried its original's
/// colour across, under whatever name the app shows. Two apps may share a
/// colour; unlike a name, a colour keys nothing. The table and its keys match
/// the web dashboard's <c>app_colors</c>.
/// </remarks>
public static partial class ColorOverrides
{
    [GeneratedRegex("^#?([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")]
    private static partial Regex HexPattern();

    /// <summary>
    /// <c>#rgb</c> or <c>#rrggbb</c>, any case, with or without the <c>#</c>,
    /// to lowercase <c>#rrggbb</c>; null for anything else.
    /// </summary>
    public static string? Clean(string? raw)
    {
        if (raw is null) return null;
        var m = HexPattern().Match(raw.Trim());
        if (!m.Success) return null;
        var hex = m.Groups[1].Value.ToLowerInvariant();
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => $"{c}{c}"));
        return "#" + hex;
    }

    /// <summary>group key to #rrggbb. Empty when there are none, or no table yet.</summary>
    public static Dictionary<string, string> Read(SqliteConnection db)
    {
        var map = new Dictionary<string, string>();
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT app_key, color FROM app_colors WHERE device = $d";
            cmd.Parameters.AddWithValue("$d", Renames.Device);
            using var r = cmd.ExecuteReader();
            // Re-validated on the way out: a hand-edited row must not reach a brush.
            while (r.Read())
                if (Clean(r.GetString(1)) is { } c) map[r.GetString(0)] = c;
        }
        catch (SqliteException)
        {
            // "no such table" on a database no write path has touched yet.
        }
        return map;
    }

    /// <summary>
    /// Validate and store, or clear, one colour. An empty string clears it and
    /// the app goes back to its brand or palette colour.
    /// </summary>
    public static (ColorError Error, string? Color) Save(SqliteConnection db, string key, string? requested, IReadOnlySet<string> known)
    {
        if (!known.Contains(key)) return (ColorError.UnknownApp, null);
        var reset = requested is not null && requested.Trim().Length == 0;
        var color = reset ? null : Clean(requested);
        if (!reset && color is null) return (ColorError.BadColor, null);

        if (color is null)
            UsageDb.Exec(db, "DELETE FROM app_colors WHERE device = $d AND app_key = $k", ("$d", Renames.Device), ("$k", key));
        else
            UsageDb.Exec(db, """
                INSERT INTO app_colors (device, app_key, color, updated_at) VALUES ($d, $k, $c, $t)
                ON CONFLICT (device, app_key) DO UPDATE SET color = excluded.color, updated_at = excluded.updated_at
                """, ("$d", Renames.Device), ("$k", key), ("$c", color), ("$t", UsageDb.NowIso()));
        return (ColorError.None, color);
    }

    public static string Message(ColorError e) => e switch
    {
        ColorError.UnknownApp => "That app is not in the history.",
        ColorError.BadColor => "A colour is a hex code like #2f80ed.",
        _ => "",
    };
}
