using System.Globalization;

namespace DataUsage.Core.View;

/// <summary>An inclusive run of local dates, <c>yyyy-MM-dd</c>.</summary>
public readonly record struct DayRange(string First, string Last);

/// <summary>
/// One day of a filled daily series: all three figures null on a day nothing
/// collected, 0 on a day known to be quiet.
/// </summary>
/// <remarks>
/// Anything counted "per active day" - the spike threshold, an app's days,
/// first/last, the detail-page gate - comes from the rows BEFORE filling.
/// Counting filled entries would call every day active and hand every app a
/// detail page.
/// </remarks>
public sealed record DailyPoint(string Date, long? Sent, long? Received, long? Total)
{
    public static DailyPoint Quiet(string date) => new(date, 0, 0, 0);

    public static DailyPoint Unknown(string date) => new(date, null, null, null);

    public static DailyPoint Of(string date, long sent, long received) => new(date, sent, received, sent + received);
}

/// <summary>One hour of the day, summed over a range.</summary>
public sealed record HourPoint(int Hour, long Sent, long Received)
{
    public long Total => Sent + Received;
}

/// <summary>
/// Calendar days, and which of them the history actually covers.
/// </summary>
/// <remarks>
/// A day with no rows means one of two very different things. Inside collected
/// history it is a real zero: the laptop was off, asleep or idle. Outside it
/// nothing is known - before collection began, or across a stretch lost before
/// anything collected it (a collector down for longer than SRUM's retention:
/// the reset case). Drawing that as zero would invent quiet days, so a series
/// is filled with rows as they are, known days as 0 and unknown days as null,
/// and charts draw a null as a break.
/// </remarks>
public static class Days
{
    public static DateOnly Parse(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Add(string date, int n) => Iso(Parse(date).AddDays(n));

    public static string LaterOf(string a, string b) => string.CompareOrdinal(a, b) > 0 ? a : b;

    /// <summary>Every date from <paramref name="from"/> to <paramref name="to"/>, both included.</summary>
    public static IEnumerable<string> Each(string from, string to)
    {
        if (from.Length == 0 || string.CompareOrdinal(from, to) > 0) yield break;
        for (var d = Parse(from); d <= Parse(to); d = d.AddDays(1)) yield return Iso(d);
    }

    public static bool IsKnown(string date, IReadOnlyList<DayRange> known)
    {
        foreach (var r in known)
            if (string.CompareOrdinal(date, r.First) >= 0 && string.CompareOrdinal(date, r.Last) <= 0) return true;
        return false;
    }

    /// <summary>
    /// One entry per day from <paramref name="from"/> to <paramref name="to"/>.
    /// A day with a row keeps it whatever <paramref name="known"/> says - a row
    /// is proof enough; otherwise known days get <paramref name="zero"/> and the
    /// rest <paramref name="unknown"/>.
    /// </summary>
    public static List<T> Fill<T>(IEnumerable<T> rows, Func<T, string> dateOf, string from, string to,
        IReadOnlyList<DayRange> known, Func<string, T> zero, Func<string, T> unknown)
    {
        var byDate = new Dictionary<string, T>();
        foreach (var r in rows) byDate[dateOf(r)] = r;
        return Each(from, to).Select(d => byDate.TryGetValue(d, out var r) ? r : IsKnown(d, known) ? zero(d) : unknown(d)).ToList();
    }

    public static List<DailyPoint> Fill(IEnumerable<DailyPoint> rows, string from, string to, IReadOnlyList<DayRange> known) =>
        Fill(rows, r => r.Date, from, to, known, DailyPoint.Quiet, DailyPoint.Unknown);

    /// <summary>
    /// The local days a set of collection windows covers, merged. A day counts
    /// only when a window covers ALL of it: a window's first and last days are
    /// partial, and an empty partial day is not proof the whole day was quiet.
    /// </summary>
    public static List<DayRange> Covered(IEnumerable<(DateTime Oldest, DateTime Newest)> windows, Func<DateTime, string> localDateOf)
    {
        var spans = windows.Where(w => w.Oldest <= w.Newest).OrderBy(w => w.Oldest).ToList();
        var merged = new List<(DateTime A, DateTime B)>();
        foreach (var s in spans)
        {
            if (merged.Count > 0 && s.Oldest <= merged[^1].B)
                merged[^1] = (merged[^1].A, s.Newest > merged[^1].B ? s.Newest : merged[^1].B);
            else merged.Add((s.Oldest, s.Newest));
        }

        var result = new List<DayRange>();
        foreach (var (a, b) in merged)
        {
            var dayOfA = localDateOf(a);
            // `a` starts a whole day only when the instant before it is another day.
            var first = localDateOf(a.AddMilliseconds(-1)) == dayOfA ? Add(dayOfA, 1) : dayOfA;
            // Whatever time `b` falls at, its own day is not covered to the end.
            var last = Add(localDateOf(b), -1);
            if (string.CompareOrdinal(first, last) <= 0) result.Add(new DayRange(first, last));
        }
        return result;
    }

    /// <summary>
    /// Every hour of the day in order, the hours that moved nothing as zero.
    /// Hour of day is a category axis too: an hour without rows used to vanish
    /// and its neighbours closed up, so 05 sat beside 08.
    /// </summary>
    public static List<HourPoint> FillHours(IEnumerable<HourPoint> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0) return [];
        var byHour = list.ToDictionary(r => r.Hour);
        return Enumerable.Range(0, 24).Select(h => byHour.TryGetValue(h, out var r) ? r : new HourPoint(h, 0, 0)).ToList();
    }

    /// <summary>"87 days, 84 with traffic". Unknown days are counted in neither.</summary>
    public static string SpanLabel(IEnumerable<DailyPoint> daily)
    {
        var list = daily.ToList();
        var known = list.Count(d => d.Total is not null);
        var active = list.Count(d => d.Total > 0);
        var days = $"{known} day{(known == 1 ? "" : "s")}";
        return active == known ? days : $"{days}, {active} with traffic";
    }
}
