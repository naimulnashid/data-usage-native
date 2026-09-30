using DataUsage.Core.View;

namespace DataUsage.Core.Query;

public readonly record struct Totals(long Sent, long Received)
{
    public long Total => Sent + Received;
    public static readonly Totals Empty = new(0, 0);
}

/// <summary>The range the page shows, counted back from the newest day held.</summary>
public readonly record struct Scope(int Days)
{
    /// <summary>
    /// "All": a century rather than a sentinel, so every query keeps the same
    /// <c>local_date &gt;= ?</c> comparison and there is no special case to forget.
    /// </summary>
    public const int AllDays = 36500;

    public static readonly int[] Ranges = [7, 30, 90, AllDays];

    public static Scope All => new(AllDays);

    public bool IsAll => Days >= AllDays;

    public string Label => IsAll ? "All" : $"{Days}d";
}

public sealed record Coverage(string First, string Last, int Days);

public sealed record Peak(string Date, long Total);

public sealed record OverviewData(
    Totals Today,
    Totals Week,
    Totals Month,
    Totals All,
    Coverage? Coverage,
    Peak? Peak,
    IReadOnlyList<DailyPoint> Daily,
    string? SplitApp,
    long SplitFocus,
    long SplitOther,
    string? LatestDate,
    double MeanDaily);

public sealed class AppRow
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    /// <summary>The name without the user's rename.</summary>
    public required string BaseName { get; init; }
    public required string Kind { get; init; }
    public long Sent { get; set; }
    public long Received { get; set; }
    public long Total => Sent + Received;
    public double Share { get; set; }
    public long Rows { get; set; }
    public int Days { get; set; }
    public bool Detailed { get; set; }
}

public sealed record ByAppData(IReadOnlyList<AppRow> Apps, long NamedTotal, long Unattributed, long HeadlineTotal);

public sealed record AppNetwork(string Id, string Label, long Bytes);

public sealed record AppIdentity(string Identity, string Kind, long Bytes);

public sealed record AppMember(string Key, string Name, long Bytes, int Days);

public sealed record AppDetail(
    string Key,
    string Name,
    string BaseName,
    string Kind,
    Totals Totals,
    double Share,
    int Days,
    long Rows,
    string? First,
    string? Last,
    Peak? Peak,
    IReadOnlyList<DailyPoint> Daily,
    IReadOnlyList<HourPoint> Hourly,
    IReadOnlyList<AppNetwork> Networks,
    IReadOnlyList<AppIdentity> Identities,
    IReadOnlyList<AppMember> Members);

public sealed record TimelineData(
    /// <summary>Per day, bytes per series in <see cref="Series"/> order; null on a day never collected.</summary>
    IReadOnlyList<(string Date, long?[] Values)> Points,
    IReadOnlyList<string> Series,
    IReadOnlyList<HourPoint> Hourly);

public sealed record SyncRun(
    long Id,
    string StartedAt,
    string? FinishedAt,
    string Status,
    long RowsRead,
    long RowsInserted,
    long RowsSkipped,
    string? SrumOldestUtc,
    string? SrumNewestUtc,
    string? BackupStatus,
    long? DurationMs,
    string? Error);

public sealed record SyncData(
    IReadOnlyList<SyncRun> Runs,
    long TotalRuns,
    SyncRun? LastSuccess,
    double? HoursSinceSuccess,
    long TotalRows,
    Coverage? Coverage,
    long ConsecutiveFailures);

public enum LinkKind
{
    Wifi,
    Wired,
    Mobile,
    Other,
}

public sealed record NetworkRow(string Id, string Label, bool Named, IReadOnlyList<string> Aliases, long Total);

public sealed record NetworkBreakdown(long Total, IReadOnlyList<(LinkKind Kind, long Total)> ByKind, IReadOnlyList<NetworkRow> Networks);

public sealed record ProfileOption(string Id, string Label, bool Named, IReadOnlyList<string> Aliases, long Bytes, string InterfaceType);
