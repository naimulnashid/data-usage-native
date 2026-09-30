namespace DataUsage.Core.Naming;

/// <summary>A logo file, and whether it needs a light plate behind it.</summary>
public sealed record AppIcon(string Path, bool Plate);

/// <summary>
/// App logos: files dropped into the data folder's <c>logos\</c>, matched to
/// display names by file name, ignoring case. <c>Bitwarden.svg</c> finds
/// "Bitwarden" and <c>Nvidia.png</c> finds "NVIDIA" with no code change. An app
/// with no file shows its colour swatch - the normal case, not a gap.
/// </summary>
/// <remarks>
/// <para><see cref="Aliases"/> exists only for names that CANNOT match a file:
/// members ("Claude Code" to the Claude mark), shared marks (two dozen Windows
/// components to <c>Windows.svg</c>), and files named differently from the app.</para>
/// <para><b>Which logos need a light plate cannot be guessed, only
/// measured</b>, and the MARK is measured, not the file: a sparse glyph by its
/// mean luma, a full-bleed tile by whether it carries a light glyph of its own.
/// ASUS (pure black), OpenCode (#4B4646) and Cursor (mean 69) are plated;
/// Ollama looks as if it should be and must not be (its file is the off-white
/// variant).</para>
/// </remarks>
public static class AppIcons
{
    public static readonly HashSet<string> Renderable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".svg", ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".ico",
    };

    public static readonly HashSet<string> PlateStems = new(StringComparer.OrdinalIgnoreCase) { "asus", "opencode", "cursor" };

    /// <summary>Display name to file stem.</summary>
    public static readonly Dictionary<string, string> Aliases = new()
    {
        ["ChatGPT"] = "ChatGPT Green",
        [".NET"] = "dotnet",
        ["Microsoft Office"] = "Office",
        ["Microsoft Teams"] = "Teams",
        ["Google Updater"] = "Google",
        ["Android Emulator"] = "Android Studio",

        // Windows components share one mark
        ["System and Windows Update"] = "Windows",
        ["Windows Notifications"] = "Windows",
        ["Windows Search"] = "Windows",
        ["Windows Widgets"] = "Windows",
        ["Windows Explorer"] = "Windows",
        ["Windows Error Reporting"] = "Windows",
        ["Windows Telemetry"] = "Windows",
        ["Windows Task Host"] = "Windows",
        ["Connected Devices"] = "Windows",
        ["Settings"] = "Windows",
        ["Start Menu"] = "Windows",
        ["Shell Experience Host"] = "Windows",
        ["Content Delivery Manager"] = "Windows",
        ["Cloud Experience Host"] = "Windows",
        ["Work or School Account"] = "Windows",
        ["Microsoft Account Sign-in"] = "Windows",
        ["Cryptographic Services"] = "Windows",
        ["Connected User Experiences"] = "Windows",
        ["App Installer"] = "Microsoft Store",
        ["Microsoft Store Install Service"] = "Microsoft Store",
        ["Windows SmartScreen"] = "Windows Defender",

        // Members, so the Merged apps card is illustrated too
        ["Claude Code"] = "Claude",
        ["Claude Desktop"] = "Claude",
        ["Claude Installer"] = "Claude",
        ["Codex"] = "ChatGPT Green",
        ["Codex Sandbox"] = "ChatGPT Green",
        ["Ollama Desktop"] = "Ollama",
        ["Edge WebView2"] = "Microsoft Edge",
        ["Edge Updater"] = "Microsoft Edge",
        ["Brave Updater"] = "Brave",
        ["Bitwarden Installer"] = "Bitwarden",
        ["Android Emulator (QEMU)"] = "Android Studio",
        ["Antigravity IDE"] = "Antigravity",
        ["Antigravity Language Server"] = "Antigravity",
        ["GitHub CLI"] = "GitHub",
        ["GitHub Desktop"] = "GitHub",
        ["GitHub Desktop Updater"] = "GitHub",
        ["GitHub Copilot"] = "GitHub",
        ["Windows PowerShell"] = "Powershell",
        ["PowerShell 7"] = "Powershell",
        ["NVIDIA App"] = "Nvidia",
        ["NVIDIA Container"] = "Nvidia",
        ["NVIDIA Overlay"] = "Nvidia",
        ["NVIDIA Display Container"] = "Nvidia",
        ["NVIDIA DLSS Update"] = "Nvidia",
        ["NVIDIA Installer"] = "Nvidia",
        ["OneDrive Sync"] = "OneDrive",
        ["OneDrive Launcher"] = "OneDrive",
        ["OneDrive Updater"] = "OneDrive",
        ["OneDrive Co-authoring"] = "OneDrive",
        ["Word"] = "Office",
        ["Excel"] = "Office",
        ["PowerPoint"] = "Office",
        ["OneNote"] = "Office",
        ["Office Helper"] = "Office",
        ["Office Hub"] = "Office",
        ["Office Actions"] = "Office",
        ["Office Notifications"] = "Office",
        ["Office Click-to-Run"] = "Office",
        ["ASUS Software Manager"] = "ASUS",
        ["ASUS Software Manager Agent"] = "ASUS",
        ["ASUS Update"] = "ASUS",
        ["ASUS Verify"] = "ASUS",
        ["ASUS PC Assistant"] = "ASUS",
        ["Armoury Crate"] = "ASUS",
        ["ASUS App Service"] = "ASUS",
        ["ROG Live Service"] = "ASUS",
        ["Delivery Optimisation"] = "Windows",
        ["Background Transfer (BITS)"] = "Windows",
        ["Windows Update"] = "Windows",
        ["Update Orchestrator"] = "Windows",
        ["Update Medic"] = "Windows",
        ["Windows Update Orchestrator"] = "Windows",
        ["Windows Server-Initiated Healing"] = "Windows",
        ["Microsoft Defender Antivirus"] = "Windows Defender",
        ["Microsoft Defender Core"] = "Windows Defender",
        ["Windows Push Notifications"] = "Windows",
        ["Windows Push Notifications (user)"] = "Windows",
        ["Connected Devices Platform"] = "Windows",
        ["Connected Devices Platform (user)"] = "Windows",
        ["Gaming Services"] = "Xbox",
        ["Xbox Game Bar"] = "Xbox",
        ["Xbox Live Auth"] = "Xbox",
    };

    /// <summary>
    /// Every name that resolves to a logo, keyed case-insensitively. File stems
    /// first (any file is reachable by its own name), then aliases (a curated
    /// pointer overrides a coincidental stem), then renames: a renamed app with
    /// no file of its own keeps its original's logo, and a file named for the
    /// new name still wins.
    /// </summary>
    public static Dictionary<string, AppIcon> Map(string? folder, IEnumerable<(string Name, string Base)>? renamed = null)
    {
        var map = new Dictionary<string, AppIcon>(StringComparer.OrdinalIgnoreCase);
        if (folder is null || !Directory.Exists(folder)) return map;

        var byStem = new Dictionary<string, AppIcon>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            if (!Renderable.Contains(Path.GetExtension(file))) continue;
            var stem = Path.GetFileNameWithoutExtension(file);
            byStem[stem] = new AppIcon(file, PlateStems.Contains(stem));
        }

        foreach (var (stem, icon) in byStem) map[stem] = icon;
        foreach (var (name, stem) in Aliases)
            if (byStem.TryGetValue(stem, out var icon)) map[name] = icon;
        foreach (var (name, @base) in renamed ?? [])
            if (!string.Equals(name, @base, StringComparison.OrdinalIgnoreCase) && !map.ContainsKey(name) && map.TryGetValue(@base, out var from))
                map[name] = from;
        return map;
    }

    /// <summary>A file name for a logo set from the app: the display name, made safe.</summary>
    public static string FileStemFor(string displayName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = displayName.Select(c => invalid.Contains(c) ? ' ' : c).ToArray();
        return new string(chars).Trim().TrimEnd('.');
    }
}
