using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DataUsage.Core.Srum;

/// <summary>
/// How raw SRUM values become the stored fields. Each rule reproduces what
/// SrumECmd printed, verified row for row against its CSV of the same snapshot.
/// </summary>
public static partial class SrumFields
{
    /// <summary>
    /// The aggregate AppId. NOT an application: SRUM writes one row per hour
    /// with AppId 1 whose bytes equal the sum of every named app in that hour.
    /// Summing all rows double-counts everything.
    /// </summary>
    public const long AggregateAppId = 1;

    /// <summary>An AppX package full name: Name_Version_Arch__PublisherId.</summary>
    [GeneratedRegex(@"^[^\\/]+_\d+(?:\.\d+)*_(?:x64|x86|arm|arm64|neutral)__[a-z0-9]+$", RegexOptions.IgnoreCase)]
    private static partial Regex AppxFullName();

    /// <summary>path | appx | service | aggregate | unknown.</summary>
    public static string ClassifyApp(long appId, string identity)
    {
        if (appId == AggregateAppId) return "aggregate";
        var s = identity.Trim();
        if (s.Length == 0) return "unknown";
        if (s.Contains('\\') || s.Contains('/')) return "path";
        if (AppxFullName().IsMatch(s)) return "appx";
        // Everything left is a bare token: DoSvc, BITS and friends.
        return "service";
    }

    /// <summary>
    /// SRUM stores UTC. The row keeps it as ISO-8601 with milliseconds, which is
    /// also how the web dashboard's rows are written - so an imported row and a
    /// freshly read one collide in the dedup key, as they must.
    /// </summary>
    public static string IsoUtc(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Local-time day and hour. Grouping raw UTC into days would shift every
    /// daily total by the UTC offset, filing early-morning traffic under the
    /// previous day.
    /// </summary>
    public static (string Date, int Hour) LocalBuckets(DateTime utc, TimeZoneInfo? zone = null)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone ?? TimeZoneInfo.Local);
        return (local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), local.Hour);
    }

    /// <summary>
    /// An IdMap blob: UTF-16LE text for apps and services, a binary SID for
    /// users. Trailing NULs are the C string terminator, not part of the name.
    /// </summary>
    public static string DecodeText(byte[]? blob) =>
        blob is null ? "" : Encoding.Unicode.GetString(blob).TrimEnd('\0');

    /// <summary>A binary SID as "S-1-5-21-...". Empty for anything malformed.</summary>
    public static string DecodeSid(byte[]? blob)
    {
        if (blob is null || blob.Length < 8) return "";
        int count = blob[1];
        if (blob.Length < 8 + 4 * count) return "";
        ulong authority = 0;
        for (var i = 2; i < 8; i++) authority = (authority << 8) | blob[i];
        var sb = new StringBuilder("S-").Append(blob[0]).Append('-').Append(authority);
        for (var i = 0; i < count; i++) sb.Append('-').Append(BitConverter.ToUInt32(blob, 8 + 4 * i));
        return sb.ToString();
    }

    /// <summary>
    /// The interface type, from the top 16 bits of the LUID (its IANA ifType),
    /// named as SrumECmd names it. An unlisted type is its number, which is
    /// what SrumECmd printed for LUID 0 ("0").
    /// </summary>
    public static string InterfaceType(long luid)
    {
        var type = (int)((ulong)luid >> 48);
        return IfTypes.TryGetValue(type, out var name) ? name : type.ToString(CultureInfo.InvariantCulture);
    }

    // ipifcons.h names. Only the kind matters downstream (Wi-Fi, wired,
    // mobile, other), so an entry missing here costs a label, not a total.
    private static readonly Dictionary<int, string> IfTypes = new()
    {
        [1] = "IF_TYPE_OTHER",
        [6] = "IF_TYPE_ETHERNET_CSMACD",
        [9] = "IF_TYPE_ISO88025_TOKENRING",
        [23] = "IF_TYPE_PPP",
        [24] = "IF_TYPE_SOFTWARE_LOOPBACK",
        [37] = "IF_TYPE_ATM",
        [53] = "IF_TYPE_PROP_VIRTUAL",
        [71] = "IF_TYPE_IEEE80211",
        [131] = "IF_TYPE_TUNNEL",
        [144] = "IF_TYPE_IEEE1394",
        [237] = "IF_TYPE_IEEE80216_WMAN",
        [243] = "IF_TYPE_WWANPP",
        [244] = "IF_TYPE_WWANPP2",
    };
}
