using DataUsage.Core.View;

namespace DataUsage.Core.Query;

/// <summary>
/// What the shared pages ask of a device, whether it is this PC
/// (<see cref="UsageQueries"/>) or a phone (<see cref="PhoneQueries"/>).
/// </summary>
/// <remarks>
/// The two read different tables that must never be summed: a phone's
/// tethering is already in the PC's rows when the PC tethers off it. So each
/// device answers for itself, in the same models, and nothing adds them.
/// </remarks>
public interface IDeviceQueries
{
    string DatabasePath { get; }

    bool DatabaseExists { get; }

    /// <summary>The <c>device</c> column of the rename, colour, hide and merge tables.</summary>
    string PrefsDevice { get; }

    OverviewData Overview(Scope scope);

    ByAppData ByApp(Scope scope);

    AppDetail? AppDetailFor(string key, Scope scope);

    bool AppExists(string key);

    string? MergedInto(string key);

    HashSet<string> AppKeys();

    TimelineData Timeline(int topN = 8);

    List<(string Date, long Total)> HeatmapDays(int? weeks = Heatmap.Weeks);

    Dictionary<string, string> ColorMap();

    Dictionary<string, string> ColorOverrideMap();

    Dictionary<string, (string Name, string Base)> AppNamesByKey();

    (string? Date, Totals Totals) Latest();

    /// <summary>The detail-page gate; a phone's thresholds are lower than a laptop's.</summary>
    bool Earns(long totalBytes, int activeDays);
}
