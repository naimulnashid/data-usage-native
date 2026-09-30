using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DataUsage.App.Theme;

/// <summary>
/// The colour tokens: the web dashboard's, one for one. True-black OLED theme,
/// one accent (Tech Blue), per-app chart colours the only other saturated thing
/// on screen.
/// </summary>
/// <remarks>
/// <para><b>This is the only place any accent colour exists.</b> Never write an
/// accent hex in a page or a chart: the web dashboard's trend chart once stayed
/// blue on a green page because a chart carried its own copy.</para>
/// <para><b>Red is reserved for genuine anomalies</b> - a failed collector run,
/// a day well above trend - so it reads as signal, not decoration.</para>
/// <para>Contrast is measured, not judged: <c>TextFaint</c> (#7c7c88) clears
/// 4.6:1 on every panel; solid controls use <c>AccentFill</c> (#1570eb, white
/// on it 4.62:1), never the accent with white (3.87:1).</para>
/// </remarks>
public static class Palette
{
    public static Color Hex(string hex, double alpha = 1)
    {
        var h = hex.TrimStart('#');
        return Color.FromArgb((byte)Math.Round(alpha * 255), Convert.ToByte(h[..2], 16), Convert.ToByte(h[2..4], 16), Convert.ToByte(h[4..6], 16));
    }

    public static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)Math.Round(a.A + (b.A - a.A) * t),
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    public static Color WithAlpha(Color c, double a) => Color.FromArgb((byte)Math.Round(a * 255), c.R, c.G, c.B);

    public static readonly Color Bg = Hex("#000000");
    public static readonly Color Surface = Hex("#0a0a0b");
    public static readonly Color SurfaceHover = Hex("#101012");
    public static readonly Color Inset = Hex("#08080a");
    public static readonly Color Border = Hex("#1e1e22");
    public static readonly Color BorderBright = Hex("#2c2c33");
    public static readonly Color Grid = Hex("#1a1a1e");
    public static readonly Color Text = Hex("#f5f5f7");
    public static readonly Color TextMuted = Hex("#a1a1aa");
    public static readonly Color TextFaint = Hex("#7c7c88");
    public static readonly Color TooltipBg = Hex("#0c0c0e");
    public static readonly Color Warn = Hex("#ff4d4f");
    public static readonly Color Good = Hex("#22c55e");
    public static readonly Color RowBorder = Hex("#1e1e22", 0.6);

    public static readonly Color Accent = Hex("#2f80ed");
    public static readonly Color AccentBright = Hex("#4d97ff");

    /// <summary>
    /// Download and upload: two shades of the ONE accent, because they are two
    /// halves of one quantity - a second hue would read as a second series.
    /// Darker is download.
    /// </summary>
    public static readonly Color Down = Mix(Hex("#000000"), Accent, 0.78);
    public static readonly Color Up = Mix(Hex("#ffffff"), AccentBright, 0.72);

    /// <summary>Wired, on "Where it went": the accent's second shade.</summary>
    public static readonly Color Wired = Mix(Hex("#ffffff"), Accent, 0.40);

    /// <summary>A day with no collected data at all. Neutral: it must never read as a quiet day.</summary>
    public static readonly Color HeatNone = Hex("#0b0b0d");

    public static readonly SolidColorBrush BgBrush = new(Bg);
    public static readonly SolidColorBrush SurfaceBrush = new(Surface);
    public static readonly SolidColorBrush SurfaceHoverBrush = new(SurfaceHover);
    public static readonly SolidColorBrush InsetBrush = new(Inset);
    public static readonly SolidColorBrush BorderBrush = new(Border);
    public static readonly SolidColorBrush BorderBrightBrush = new(BorderBright);
    public static readonly SolidColorBrush GridBrush = new(Grid);
    public static readonly SolidColorBrush TextBrush = new(Text);
    public static readonly SolidColorBrush TextMutedBrush = new(TextMuted);
    public static readonly SolidColorBrush TextFaintBrush = new(TextFaint);
    public static readonly SolidColorBrush TooltipBgBrush = new(TooltipBg);
    public static readonly SolidColorBrush WarnBrush = new(Warn);
    public static readonly SolidColorBrush WarnDimBrush = new(Hex("#ff4d4f", 0.14));
    public static readonly SolidColorBrush WarnBorderBrush = new(Hex("#ff4d4f", 0.34));
    public static readonly SolidColorBrush GoodBrush = new(Good);
    public static readonly SolidColorBrush GoodDimBrush = new(Hex("#22c55e", 0.14));
    public static readonly SolidColorBrush RowBorderBrush = new(RowBorder);
    public static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent);
    public static readonly SolidColorBrush HoverWashBrush = new(Hex("#ffffff", 0.04));

    public static readonly SolidColorBrush AccentBrush = new(Accent);
    public static readonly SolidColorBrush AccentBrightBrush = new(AccentBright);
    public static readonly SolidColorBrush AccentDimBrush = new(Hex("#2f80ed", 0.16));
    public static readonly SolidColorBrush AccentBorderBrush = new(Hex("#2f80ed", 0.34));
    public static readonly SolidColorBrush AccentBorderStrongBrush = new(Hex("#2f80ed", 0.46));
    public static readonly SolidColorBrush AccentFillBrush = new(Hex("#1570eb"));
    public static readonly SolidColorBrush OnAccentFillBrush = new(Hex("#ffffff"));
    public static readonly SolidColorBrush DownBrush = new(Down);
    public static readonly SolidColorBrush UpBrush = new(Up);
    public static readonly SolidColorBrush WiredBrush = new(Wired);
    public static readonly SolidColorBrush HeatNoneBrush = new(HeatNone);

    public static readonly Color AccentGlow = Hex("#2f80ed", 0.28);

    /// <summary>The heat ramp in the accent's own hue family. Step 0 is a real but quiet day.</summary>
    public static readonly SolidColorBrush[] Heat =
        new[] { "#131519", "#12325c", "#17488a", "#1f62bd", "#2f80ed", "#6fb0ff" }.Select(h => new SolidColorBrush(Hex(h))).ToArray();

    /// <summary>An app's chart colour, from the colour map.</summary>
    public static Color App(IReadOnlyDictionary<string, string> map, string name) =>
        Hex(Core.Naming.AppColors.Of(map, name));

    public static Color HexOrOther(string hex) => Core.Naming.AppColors.TryParse(hex, out _) ? Hex(hex) : Hex(Core.Naming.AppColors.Other);
}
