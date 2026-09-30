using System.Globalization;

namespace DataUsage.Core.View;

/// <summary>
/// Formatting. Every number drawn from here is rendered with tabular figures,
/// so digits keep their width during count-ups.
/// </summary>
public static class Format
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] Units = ["KB", "MB", "GB", "TB", "PB"];
    private static readonly string[] MonthNames = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    /// <summary>Binary units, matching what Windows' Data usage page reports.</summary>
    public static string Bytes(double bytes, int? decimals = null)
    {
        var abs = Math.Abs(bytes);
        if (abs < 1024) return $"{Math.Round(bytes).ToString(Inv)} B";
        var value = bytes / 1024;
        var i = 0;
        while (Math.Abs(value) >= 1024 && i < Units.Length - 1)
        {
            value /= 1024;
            i++;
        }
        var d = decimals ?? (Math.Abs(value) >= 100 ? 0 : Math.Abs(value) >= 10 ? 1 : 2);
        return $"{value.ToString("F" + d, Inv)} {Units[i]}";
    }

    /// <summary>Number and unit apart, so the unit can be styled smaller.</summary>
    public static (string Value, string Unit) SplitBytes(double bytes)
    {
        var s = Bytes(bytes);
        var idx = s.LastIndexOf(' ');
        return (s[..idx], s[(idx + 1)..]);
    }

    public static string Percent(double n, int decimals = 1) => n.ToString("F" + decimals, Inv) + "%";

    public static string Count(long n) => n.ToString("N0", CultureInfo.GetCultureInfo("en-US"));

    /// <summary>"2026-08-21" to "Aug 21".</summary>
    public static string DayShort(string iso)
    {
        if (!DateOnly.TryParseExact(iso, "yyyy-MM-dd", Inv, DateTimeStyles.None, out var d)) return iso;
        return $"{MonthNames[d.Month - 1]} {d.Day}";
    }

    /// <summary>"2026-08-21" to "Thursday, 21 August 2026".</summary>
    public static string DayLong(string iso)
    {
        if (!DateOnly.TryParseExact(iso, "yyyy-MM-dd", Inv, DateTimeStyles.None, out var d)) return iso;
        return d.ToString("dddd, d MMMM yyyy", CultureInfo.GetCultureInfo("en-GB"));
    }

    /// <summary>"21 Aug 2026, 14:05" in local time.</summary>
    public static string DateTimeLocal(string iso) =>
        TryInstant(iso, out var t) ? t.ToLocalTime().ToString("dd MMM yyyy, HH:mm", CultureInfo.GetCultureInfo("en-GB")) : iso;

    public static bool TryInstant(string iso, out DateTime utc)
    {
        var ok = DateTime.TryParse(iso, Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc);
        return ok;
    }

    public static string Relative(double hours)
    {
        if (hours < 1) return $"{Math.Round(hours * 60).ToString(Inv)} min ago";
        if (hours < 24) return $"{Math.Round(hours).ToString(Inv)}h ago";
        var days = hours / 24;
        return days < 2 ? "yesterday" : $"{Math.Round(days).ToString(Inv)} days ago";
    }

    public static string Duration(long? ms) =>
        ms is null ? "—" : ms < 1000 ? $"{ms} ms" : $"{(ms.Value / 1000.0).ToString("F1", Inv)} s";

    /// <summary>"05:00".</summary>
    public static string Hour(int hour) => $"{hour:00}:00";
}
