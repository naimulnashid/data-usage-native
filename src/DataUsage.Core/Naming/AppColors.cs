using System.Globalization;

namespace DataUsage.Core.Naming;

/// <summary>
/// Per-app chart colours: brand first, a generated palette second.
/// </summary>
/// <remarks>
/// The cost of brand-first is real and was chosen knowingly: Edge
/// <c>#0078D7</c>, VS Code <c>#007ACC</c>, Windows Update <c>#0067B8</c> and
/// qBittorrent <c>#2F6790</c> are four blues in the top eight. The logos beside
/// every legend entry carry the identification the colour no longer does
/// alone. Don't "fix" it by reverting to arbitrary colours.
/// </remarks>
public static class AppColors
{
    /// <summary>Brand colours by DISPLAY NAME, which is unique per family.</summary>
    private static readonly Dictionary<string, string> Brand = new()
    {
        ["qBittorrent"] = "#2F6790",
        ["Google Drive"] = "#FFBA00",
        ["Microsoft Edge"] = "#0078D7",
        ["Claude"] = "#D97757",
        ["System and Windows Update"] = "#0067B8",
        ["VS Code"] = "#007ACC",
        ["ChatGPT"] = "#10A37F",
        ["Node.js"] = "#68A063",
        ["Brave"] = "#FB542B",
        // Ollama's mark is black or off-white; on true black only the off-white reads.
        ["Ollama"] = "#DCDCDC",
        ["Telegram"] = "#24A1DE",
        ["Android Studio"] = "#3DDC84",
        ["Java"] = "#EA2D2E",
        ["YouTube"] = "#FF0000",
        ["Facebook"] = "#1877F2",
        ["Instagram"] = "#E4405F",
        ["Threads"] = "#D8D8DE",
        ["Messenger"] = "#A334FA",
        ["Spotify"] = "#1DB954",
        ["Netflix"] = "#E50914",
        ["Reddit"] = "#FF4500",
        ["X"] = "#D8D8DE",
        ["Pinterest"] = "#E60023",
        ["Gmail"] = "#EA4335",
        ["Google"] = "#4285F4",
        ["Proton VPN"] = "#6D4AFF",
        ["Android Emulator"] = "#A4C639",
        ["Antigravity"] = "#8AB4F8",
        ["ASUS"] = "#00539B",
        ["Bitwarden"] = "#175DDC",
        ["Chrome"] = "#4285F4",
        ["Codex"] = "#10A37F",
        ["Command Palette"] = "#8E8CD8",
        ["Connected Devices"] = "#5C7CFA",
        ["Cursor"] = "#E4E4E4",
        ["curl"] = "#7EB543",
        ["Discord"] = "#5865F2",
        ["Firefox"] = "#FF7139",
        ["Git"] = "#F1502F",
        ["GitHub"] = "#C9D1D9",
        ["GitKraken"] = "#179287",
        ["Google Updater"] = "#34A853",
        ["Kimi"] = "#7B61FF",
        ["Microsoft Copilot"] = "#8661C5",
        ["Microsoft News"] = "#C43E1C",
        ["Microsoft Office"] = "#D83B01",
        ["Microsoft Store"] = "#2D7D9A",
        ["Microsoft Teams"] = "#6264A7",
        ["Microsoft To Do"] = "#3D6DB5",
        ["MSN Weather"] = "#4FA3D1",
        ["NVIDIA"] = "#76B900",
        ["OBS Studio"] = "#D0D0D0",
        ["Obsidian"] = "#7C3AED",
        ["OneDrive"] = "#0364B8",
        ["OpenCode"] = "#E8E5DE",
        ["Outlook"] = "#0F6CBD",
        ["Perplexity"] = "#20808D",
        ["Phone Link"] = "#4A90D9",
        ["Microsoft Photos"] = "#C7539C",
        ["PowerShell"] = "#5391FE",
        ["PowerToys"] = "#C2185B",
        ["Python"] = "#FFD43B",
        ["Rust"] = "#DEA584",
        ["uv"] = "#DE5FE9",
        ["Visual Studio"] = "#5C2D91",
        ["WhatsApp"] = "#25D366",
        ["Windows Defender"] = "#4CC2FF",
        ["Windows Error Reporting"] = "#8A8A94",
        ["Windows Notifications"] = "#7A9CC6",
        ["Windows Search"] = "#3FA9F5",
        ["Windows Widgets"] = "#5AA7E0",
        ["Wispr Flow"] = "#F5A623",
        ["Xbox"] = "#107C10",
        ["ZCode"] = "#E5B94E",
        ["Zoom"] = "#2D8CFF",
        ["Zotero"] = "#CC2936",
    };

    /// <summary>
    /// For apps with no brand: vivid, mutually distinct, adjacent entries
    /// contrasting (adjacent ranks sit side by side in a stacked band). The
    /// accent blue is absent - it is reserved for UI chrome.
    /// </summary>
    private static readonly string[] Palette =
    [
        "#ec4899", "#22d3ee", "#f59e0b", "#a855f7", "#10b981", "#f97316",
        "#818cf8", "#84cc16", "#fb7185", "#14b8a6", "#eab308", "#d946ef",
    ];

    /// <summary>
    /// "Everything else" on the split card: a pale neutral, not the accent -
    /// qBittorrent's deep teal blue and the mid-blue accent were the same hue,
    /// on the one chart whose job is to separate two quantities.
    /// </summary>
    public const string EverythingElse = "#C9C9D3";

    public const string Other = "#4b4b55";
    public const string Unattributed = "#3f3f48";

    /// <summary>
    /// By rank, brands first. Ranking by all-time bytes (not a name hash, which
    /// collided) is what keeps an app's colour stable across ranges and pages.
    /// </summary>
    public static Dictionary<string, string> Assign(IEnumerable<string> namesByRank)
    {
        var map = new Dictionary<string, string>();
        var i = 0;
        foreach (var name in namesByRank)
        {
            if (name == "Other") map[name] = Other;
            else if (name == AppNames.UnknownName) map[name] = Unattributed;
            else if (Brand.TryGetValue(name, out var brand)) map[name] = brand;
            else map[name] = Palette[i++ % Palette.Length];
        }
        return map;
    }

    public static string Of(IReadOnlyDictionary<string, string> map, string name) =>
        name == "Other" ? Other
        : name == AppNames.UnknownName ? Unattributed
        : map.GetValueOrDefault(name) ?? Brand.GetValueOrDefault(name) ?? Other;

    // ---- Upload tint --------------------------------------------------------

    /// <summary>
    /// The upload half of a stacked app bar: same hue, visibly different shade.
    /// The direction cannot be fixed: near-white brands (Ollama, Cursor,
    /// OpenCode, X) lightened by the same amount move 2.7-4.6 dE - invisible.
    /// So a colour above CIE L* 76 is darkened and everything else lightened;
    /// worst separation over every colour here is 10.1 dE. WCAG contrast was
    /// tried first and is the wrong metric: it flips saturated reds for nothing.
    /// </summary>
    public static string UploadTint(string hex)
    {
        if (!TryParse(hex, out var rgb)) return hex;
        return Lightness(rgb) > 76 ? Blend(rgb, 0.62, 0) : Blend(rgb, 0.72, 255);
    }

    public static bool TryParse(string hex, out (int R, int G, int B) rgb)
    {
        rgb = default;
        if (hex.Length != 7 || hex[0] != '#' || !int.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n)) return false;
        rgb = ((n >> 16) & 255, (n >> 8) & 255, n & 255);
        return true;
    }

    private static double Channel(int c)
    {
        var s = c / 255.0;
        return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    /// <summary>CIE L*, 0-100.</summary>
    public static double Lightness((int R, int G, int B) c)
    {
        var y = 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        return y > 0.008856 ? 116 * Math.Cbrt(y) - 16 : 903.3 * y;
    }

    private static string Blend((int R, int G, int B) c, double keep, int toward)
    {
        int One(int v) => (int)Math.Round(v * keep + toward * (1 - keep), MidpointRounding.AwayFromZero);
        return $"#{One(c.R):x2}{One(c.G):x2}{One(c.B):x2}";
    }

    // ---- Heat map --------------------------------------------------------------

    /// <summary>
    /// Heat map step 0-5 for a day against the period's peak. Uneven on purpose:
    /// usage is dominated by occasional torrent days an order of magnitude
    /// above the rest, so linear buckets would paint almost every day the
    /// palest step. Weighting the low end spreads ordinary days over three.
    /// </summary>
    public static int HeatStep(long value, long max)
    {
        if (value <= 0 || max <= 0) return 0;
        var ratio = (double)value / max;
        return ratio <= 0.05 ? 1 : ratio <= 0.15 ? 2 : ratio <= 0.35 ? 3 : ratio <= 0.7 ? 4 : 5;
    }
}
