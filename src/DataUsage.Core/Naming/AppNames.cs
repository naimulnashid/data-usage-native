using System.Text.RegularExpressions;

namespace DataUsage.Core.Naming;

/// <summary>What a raw SRUM identity resolves to.</summary>
/// <param name="GroupKey">Group on this: rows sharing it are one real product (the FAMILY).</param>
/// <param name="DisplayName">What to show for the family.</param>
/// <param name="Kind">path | appx | service | aggregate | unknown.</param>
/// <param name="MemberKey">One per distinct program inside the family; equal to GroupKey when nothing merged.</param>
/// <param name="MemberName">What to show for that one program.</param>
public sealed record ResolvedApp(string GroupKey, string DisplayName, string Kind, string MemberKey, string MemberName);

/// <summary>
/// Raw SRUM identities to the names a person recognises, in two stages that
/// must not be conflated:
/// <list type="number">
/// <item><b>Identity to member.</b> One raw string becomes one program: an exe
/// basename, a version-stripped AppX package, a version-stripped service. This
/// is what collapses <c>...\129.0.1.0\googledrivefs.exe</c> and
/// <c>...\128.0.0.0\googledrivefs.exe</c> into one thing.</item>
/// <item><b>Member to family.</b> Several programs that are one product to a
/// person: the Claude Code CLI and the Claude desktop app; every NVIDIA
/// background process. Everything the app groups, charts and links on is the
/// family; only the detail page's "Merged apps" card shows members, so a merge
/// is never silent.</item>
/// </list>
/// Both change the NUMBERS - that is the point: a product split across several
/// identity strings otherwise shows up several times, each looking smaller
/// than the truth. Resolved in code, never stored, so every rule here can be
/// corrected without re-collecting anything.
/// </summary>
/// <remarks>
/// Rules to keep: a family's display name must be unique (colours and logos
/// are keyed by it); a path-vendor rule applies even when the basename is
/// known (else an app and its installer become two groups with one name);
/// generic basenames are keyed by directory (<c>setup.exe</c> is Visual
/// Studio's in one path and NVIDIA's in another).
/// </remarks>
public static partial class AppNames
{
    public const string AggregateKey = "__aggregate__";
    public const string UnknownKey = "__unknown__";
    public const string UnknownName = "Unattributed";

    // ---- Stage 1: names for individual members ------------------------------

    /// <summary>
    /// Proper names for executables, by lowercase basename. SRUM lowercases
    /// most paths and Windows reads real casing from file metadata, which is
    /// not available - so without this the list says <c>googledrivefs.exe</c>.
    /// Falls back to the raw basename.
    /// </summary>
    private static readonly Dictionary<string, string> KnownExeNames = new()
    {
        // Browsers
        ["msedge.exe"] = "Microsoft Edge",
        ["msedgewebview2.exe"] = "Edge WebView2",
        ["microsoftedgeupdate.exe"] = "Edge Updater",
        ["brave.exe"] = "Brave",
        ["braveupdate.exe"] = "Brave Updater",
        ["chrome.exe"] = "Chrome",
        ["firefox.exe"] = "Firefox",

        // AI tools
        ["claude.exe"] = "Claude Code",
        ["claude setup.exe"] = "Claude Installer",
        ["codex.exe"] = "Codex",
        ["node_repl.exe"] = "Codex Sandbox",
        ["ollama.exe"] = "Ollama",
        ["ollama app.exe"] = "Ollama Desktop",
        ["cursor.exe"] = "Cursor",
        ["opencode.exe"] = "OpenCode",
        ["zcode.exe"] = "ZCode",
        ["antigravity ide.exe"] = "Antigravity IDE",
        ["antigravity.exe"] = "Antigravity",
        ["language_server_windows_x64.exe"] = "Antigravity Language Server",
        ["language_server.exe"] = "Antigravity Language Server",
        ["perplexity ai.exe"] = "Perplexity",
        ["kimi.exe"] = "Kimi",
        ["minimax code.exe"] = "MiniMax Code",
        ["mscopilot.exe"] = "Microsoft Copilot",
        ["wispr flow.exe"] = "Wispr Flow",

        // Dev tooling
        ["code.exe"] = "VS Code",
        ["node.exe"] = "Node.js",
        ["python.exe"] = "Python",
        ["pythonw.exe"] = "Python",
        ["java.exe"] = "Java",
        ["javaw.exe"] = "Java",
        ["studio64.exe"] = "Android Studio",
        ["netsimd.exe"] = "Android Emulator",
        ["qemu-system-x86_64.exe"] = "Android Emulator (QEMU)",
        ["qemu-system-x86_64-headless.exe"] = "Android Emulator (QEMU)",
        ["git-remote-https.exe"] = "Git",
        ["gh.exe"] = "GitHub CLI",
        ["github.exe"] = "GitHub Copilot",
        ["githubdesktop.exe"] = "GitHub Desktop",
        ["gk.exe"] = "GitKraken",
        ["dotnet.exe"] = ".NET",
        ["cargo.exe"] = "Cargo",
        ["rustup-init.exe"] = "Rustup",
        ["uv.exe"] = "uv",
        ["curl.exe"] = "curl",
        ["wget.exe"] = "Wget",
        ["powershell.exe"] = "Windows PowerShell",
        ["vs_setup_bootstrapper.exe"] = "Visual Studio Installer",
        ["vctip.exe"] = "Visual C++ Telemetry",

        // Everyday apps
        ["qbittorrent.exe"] = "qBittorrent",
        ["googledrivefs.exe"] = "Google Drive",
        ["telegram.exe"] = "Telegram",
        ["discord.exe"] = "Discord",
        ["zoom.exe"] = "Zoom",
        ["zotero.exe"] = "Zotero",
        ["obsidian.exe"] = "Obsidian",
        ["obs64.exe"] = "OBS Studio",
        ["bitwarden.exe"] = "Bitwarden",
        ["powertoys.exe"] = "PowerToys",
        ["music-vault.exe"] = "Music Vault",
        ["vlc.exe"] = "VLC",
        ["insta360 studio.exe"] = "Insta360 Studio",

        // Microsoft Office / OneDrive
        ["winword.exe"] = "Word",
        ["excel.exe"] = "Excel",
        ["powerpnt.exe"] = "PowerPoint",
        ["onenote.exe"] = "OneNote",
        ["sdxhelper.exe"] = "Office Helper",
        ["officec2rclient.exe"] = "Office Click-to-Run",
        ["officeclicktorun.exe"] = "Office Click-to-Run",
        ["onedrive.exe"] = "OneDrive",
        ["onedrivelauncher.exe"] = "OneDrive Launcher",
        ["onedrivestandaloneupdater.exe"] = "OneDrive Updater",
        ["onedrive.sync.service.exe"] = "OneDrive Sync",
        ["filecoauth.exe"] = "OneDrive Co-authoring",

        // NVIDIA
        ["nvcontainer.exe"] = "NVIDIA Container",
        ["nvidia app.exe"] = "NVIDIA App",
        ["nvidia overlay.exe"] = "NVIDIA Overlay",
        ["nvdisplay.container.exe"] = "NVIDIA Display Container",
        ["nvngx_update.exe"] = "NVIDIA DLSS Update",

        // ASUS
        ["asussoftwaremanager.exe"] = "ASUS Software Manager",
        ["asussoftwaremanageragent.exe"] = "ASUS Software Manager Agent",
        ["asusupdate.exe"] = "ASUS Update",
        ["asusverifyjwt.exe"] = "ASUS Verify",

        // Windows components
        ["explorer.exe"] = "Windows Explorer",
        ["searchapp.exe"] = "Windows Search",
        ["svchost.exe"] = "Service Host",
        ["taskhostw.exe"] = "Windows Task Host",
        ["wermgr.exe"] = "Windows Error Reporting",
        ["werfault.exe"] = "Windows Error Reporting",
        ["compattelrunner.exe"] = "Windows Telemetry",
        ["smartscreen.exe"] = "Windows SmartScreen",
        ["mousocoreworker.exe"] = "Windows Update Orchestrator",
        ["sihclient.exe"] = "Windows Server-Initiated Healing",
        ["apphostregistrationverifier.exe"] = "App Host Verifier",
        ["updater.exe"] = "Google Updater",
    };

    /// <summary>
    /// AppX names, by package family name with the version stripped. Two are
    /// counter-intuitive and were verified against Settings: <c>OpenAI.Codex_*</c>
    /// is what Windows labels <b>ChatGPT</b>, and the Claude desktop app's
    /// package is plain <c>Claude_…</c>.
    /// </summary>
    private static readonly Dictionary<string, string> AppxKnownNames = new()
    {
        ["openai.codex"] = "ChatGPT",
        ["openai.chatgpt"] = "ChatGPT",
        ["claude"] = "Claude Desktop",
        ["anthropic.claude"] = "Claude Desktop",

        ["microsoft.desktopappinstaller"] = "App Installer",
        ["microsoft.windowsstore"] = "Microsoft Store",
        ["microsoft.outlookforwindows"] = "Outlook",
        ["msteams"] = "Microsoft Teams",
        ["microsoft.todos"] = "Microsoft To Do",
        ["microsoft.powershell"] = "PowerShell 7",
        ["microsoft.windows.photos"] = "Microsoft Photos",
        ["microsoft.yourphone"] = "Phone Link",
        ["microsoft.bingnews"] = "Microsoft News",
        ["microsoft.bingweather"] = "MSN Weather",
        ["microsoft.windowsfeedbackhub"] = "Feedback Hub",
        ["microsoft.commandpalette"] = "Command Palette",
        ["microsoft.lockapp"] = "Lock Screen",
        ["microsoft.startexperiencesapp"] = "Start Experiences",
        ["microsoft.microsoftofficehub"] = "Office Hub",
        ["microsoft.officepushnotificationutility"] = "Office Notifications",
        ["microsoft.office.actionsserver"] = "Office Actions",
        ["microsoft.gamingservices"] = "Gaming Services",
        ["microsoft.gamingapp"] = "Xbox",
        ["microsoft.xboxgamingoverlay"] = "Xbox Game Bar",
        ["microsoftcorporationii.quickassist"] = "Quick Assist",
        ["microsoftcorporationii.microsoftfamily"] = "Microsoft Family Safety",
        ["microsoftwindows.client.webexperience"] = "Windows Widgets",
        ["microsoftwindows.client.cbs"] = "Windows Search",
        ["microsoftwindows.client.oobe"] = "Windows Setup",
        ["microsoftwindows.crossdevice"] = "Cross Device",
        ["dolbylaboratories.dolbyaccess"] = "Dolby Access",
        ["5319275a.whatsappdesktop"] = "WhatsApp",
        ["b9eced6f.asuspcassistant"] = "ASUS PC Assistant",
        ["b9eced6f.armourycrate"] = "Armoury Crate",
    };

    /// <summary>Friendly names for services, by normalised service name.</summary>
    private static readonly Dictionary<string, string> ServiceKnownNames = new()
    {
        ["googleupdaterservice"] = "Google Updater",
        ["dosvc"] = "Delivery Optimisation",
        ["bits"] = "Background Transfer (BITS)",
        ["wuauserv"] = "Windows Update",
        ["usosvc"] = "Update Orchestrator",
        ["waasmedicsvc"] = "Update Medic",
        ["installservice"] = "Microsoft Store Install Service",
        ["diagtrack"] = "Connected User Experiences",
        ["wlidsvc"] = "Microsoft Account Sign-in",
        ["mdcoresvc"] = "Microsoft Defender Core",
        ["windefend"] = "Microsoft Defender Antivirus",
        ["cryptsvc"] = "Cryptographic Services",
        ["dnscache"] = "DNS Client",
        ["dhcp"] = "DHCP Client",
        ["spooler"] = "Print Spooler",
        ["ssdpsrv"] = "SSDP Discovery",
        ["netprofm"] = "Network List Service",
        ["lfsvc"] = "Geolocation Service",
        ["wisvc"] = "Windows Insider Service",
        ["licensemanager"] = "Windows License Manager",
        ["appxsvc"] = "AppX Deployment Service",
        ["xblauthmanager"] = "Xbox Live Auth",
        ["wpnservice"] = "Windows Push Notifications",
        ["wpnuserservice"] = "Windows Push Notifications (user)",
        ["cdpsvc"] = "Connected Devices Platform",
        ["cdpusersvc"] = "Connected Devices Platform (user)",
        ["system"] = "System",
        ["rog live service"] = "ROG Live Service",
        ["asusappservice"] = "ASUS App Service",
        ["windows.immersivecontrolpanel"] = "Settings",
        ["microsoft.windows.contentdeliverymanager"] = "Content Delivery Manager",
        ["microsoft.windows.cloudexperiencehost"] = "Cloud Experience Host",
        ["microsoft.windows.shellexperiencehost"] = "Shell Experience Host",
        ["microsoft.windows.startmenuexperiencehost"] = "Start Menu",
        ["microsoft.aad.brokerplugin"] = "Work or School Account",
    };

    // ---- Stage 2: families ---------------------------------------------------

    /// <summary>Member key to family id. Wins over a path-vendor rule.</summary>
    private static readonly Dictionary<string, string> FamilyOf = new()
    {
        // Anthropic
        ["claude.exe"] = "claude",
        ["appx:claude"] = "claude",
        ["appx:anthropic.claude"] = "claude",
        ["claude setup.exe"] = "claude",

        // OpenAI
        ["appx:openai.codex"] = "chatgpt",
        ["appx:openai.chatgpt"] = "chatgpt",
        ["codex.exe"] = "chatgpt",
        ["node_repl.exe"] = "chatgpt",

        // Ollama
        ["ollama.exe"] = "ollama",
        ["ollama app.exe"] = "ollama",

        // Browsers
        ["msedge.exe"] = "edge",
        ["msedgewebview2.exe"] = "edge",
        ["microsoftedgeupdate.exe"] = "edge",
        ["brave.exe"] = "brave",
        ["braveupdate.exe"] = "brave",

        // Android
        ["studio64.exe"] = "android-studio",
        ["netsimd.exe"] = "android-emulator",
        ["qemu-system-x86_64.exe"] = "android-emulator",
        ["qemu-system-x86_64-headless.exe"] = "android-emulator",

        // Editors that ship several binaries
        ["antigravity ide.exe"] = "antigravity",
        ["antigravity.exe"] = "antigravity",
        ["language_server_windows_x64.exe"] = "antigravity",
        ["language_server.exe"] = "antigravity",

        // NVIDIA
        ["nvcontainer.exe"] = "nvidia",
        ["nvidia app.exe"] = "nvidia",
        ["nvidia overlay.exe"] = "nvidia",
        ["nvdisplay.container.exe"] = "nvidia",
        ["nvngx_update.exe"] = "nvidia",

        // OneDrive
        ["onedrive.exe"] = "onedrive",
        ["onedrivelauncher.exe"] = "onedrive",
        ["onedrivestandaloneupdater.exe"] = "onedrive",
        ["onedrive.sync.service.exe"] = "onedrive",
        ["filecoauth.exe"] = "onedrive",

        // Office
        ["winword.exe"] = "office",
        ["excel.exe"] = "office",
        ["powerpnt.exe"] = "office",
        ["onenote.exe"] = "office",
        ["sdxhelper.exe"] = "office",
        ["officec2rclient.exe"] = "office",
        ["officeclicktorun.exe"] = "office",
        ["appx:microsoft.microsoftofficehub"] = "office",
        ["appx:microsoft.officepushnotificationutility"] = "office",
        ["appx:microsoft.office.actionsserver"] = "office",

        // ASUS
        ["asussoftwaremanager.exe"] = "asus",
        ["asussoftwaremanageragent.exe"] = "asus",
        ["asusupdate.exe"] = "asus",
        ["asusverifyjwt.exe"] = "asus",
        ["appx:b9eced6f.asuspcassistant"] = "asus",
        ["appx:b9eced6f.armourycrate"] = "asus",
        ["svc:asusappservice"] = "asus",
        ["svc:rog live service"] = "asus",

        // GitHub
        ["gh.exe"] = "github",
        ["github.exe"] = "github",
        ["githubdesktop.exe"] = "github",

        // Apps whose installer is a separate binary
        ["bitwarden.exe"] = "bitwarden",
        ["wispr flow.exe"] = "wispr-flow",

        // Languages / runtimes
        ["python.exe"] = "python",
        ["pythonw.exe"] = "python",
        ["java.exe"] = "java",
        ["javaw.exe"] = "java",
        ["cargo.exe"] = "rust",
        ["rustup-init.exe"] = "rust",

        // Google
        ["svc:googleupdaterservice"] = "google-updater",
        ["updater.exe"] = "google-updater",

        // Windows groupings. "System and Windows Update" is Windows' own label,
        // verified against Settings: DoSvc + BITS together match its figure.
        ["svc:dosvc"] = "windows-update",
        ["svc:bits"] = "windows-update",
        ["svc:wuauserv"] = "windows-update",
        ["svc:usosvc"] = "windows-update",
        ["svc:waasmedicsvc"] = "windows-update",
        ["svc:deliveryoptimization"] = "windows-update",
        ["mousocoreworker.exe"] = "windows-update",
        ["sihclient.exe"] = "windows-update",

        ["svc:windefend"] = "windows-defender",
        ["svc:mdcoresvc"] = "windows-defender",
        ["smartscreen.exe"] = "windows-defender",

        ["svc:wpnservice"] = "windows-notifications",
        ["svc:wpnuserservice"] = "windows-notifications",

        ["svc:cdpsvc"] = "connected-devices",
        ["svc:cdpusersvc"] = "connected-devices",
        ["appx:microsoftwindows.crossdevice"] = "connected-devices",
        ["appx:microsoft.yourphone"] = "connected-devices",

        ["wermgr.exe"] = "windows-error-reporting",
        ["werfault.exe"] = "windows-error-reporting",

        ["appx:microsoft.gamingservices"] = "xbox",
        ["appx:microsoft.gamingapp"] = "xbox",
        ["appx:microsoft.xboxgamingoverlay"] = "xbox",
        ["svc:xblauthmanager"] = "xbox",

        ["appx:msteams"] = "teams",
        ["appx:microsoft.outlookforwindows"] = "outlook",

        // Both are "PowerShell" to a person, and apart they were two groups
        // with an identical label - which the colour and logo maps key on.
        ["powershell.exe"] = "powershell",
        ["appx:microsoft.powershell"] = "powershell",

        ["vs_setup_bootstrapper.exe"] = "visual-studio",
        ["vctip.exe"] = "visual-studio",
    };

    private static readonly Dictionary<string, string> FamilyNames = new()
    {
        ["claude"] = "Claude",
        ["chatgpt"] = "ChatGPT",
        ["ollama"] = "Ollama",
        ["edge"] = "Microsoft Edge",
        ["brave"] = "Brave",
        ["android-studio"] = "Android Studio",
        ["android-emulator"] = "Android Emulator",
        ["antigravity"] = "Antigravity",
        ["nvidia"] = "NVIDIA",
        ["onedrive"] = "OneDrive",
        ["office"] = "Microsoft Office",
        ["asus"] = "ASUS",
        ["github"] = "GitHub",
        ["python"] = "Python",
        ["java"] = "Java",
        ["rust"] = "Rust",
        ["google-updater"] = "Google Updater",
        ["windows-update"] = "System and Windows Update",
        ["windows-defender"] = "Windows Defender",
        ["windows-notifications"] = "Windows Notifications",
        ["connected-devices"] = "Connected Devices",
        ["windows-error-reporting"] = "Windows Error Reporting",
        ["xbox"] = "Xbox",
        ["teams"] = "Microsoft Teams",
        ["outlook"] = "Outlook",
        ["visual-studio"] = "Visual Studio",
        ["powershell"] = "PowerShell",
        ["wispr-flow"] = "Wispr Flow",
        ["bitwarden"] = "Bitwarden",
    };

    // ---- Path shape ------------------------------------------------------------

    /// <summary>Basenames that name a task, not a program: keyed by directory instead.</summary>
    private static readonly HashSet<string> GenericBasenames =
    [
        "setup.exe", "install.exe", "installer.exe", "uninstall.exe",
        "update.exe", "main.exe", "app.exe", "launcher.exe", "helper.exe",
        "service.exe", "start.exe",
    ];

    /// <summary>
    /// Vendors identified by the whole NT path, first match wins. Case-sensitive
    /// as the original was: SRUM lowercases the paths these are written for.
    /// </summary>
    private static readonly (Regex Test, string Family, string Name)[] PathVendors =
    [
        (new(@"\\microsoft visual studio\\"), "visual-studio", "Visual Studio Installer"),
        (new(@"\\nvidia\b"), "nvidia", "NVIDIA Installer"),
        (new(@"\\wisprflow\\"), "wispr-flow", "Wispr Flow Updater"),
        (new(@"\\githubdesktop\\"), "github", "GitHub Desktop Updater"),
        (new(@"\\googleupdater\\"), "google-updater", "Google Updater"),
        (new(@"\\bravesoftware\\"), "brave", "Brave Updater"),
        (new(@"\\bitwarden-installer"), "bitwarden", "Bitwarden Installer"),
        (new(@"\\openai\\codex\\"), "chatgpt", "Codex"),
    ];

    private static string[] Segments(string p) => p.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

    private static string Basename(string p)
    {
        var parts = Segments(p);
        return parts.Length > 0 ? parts[^1] : p;
    }

    private static string ParentDir(string p)
    {
        var parts = Segments(p);
        return parts.Length > 1 ? parts[^2] : "";
    }

    /// <summary><c>OpenAI.Codex_26.814.5517.0_x64__2p2nqsd0c76g0</c> to <c>openai.codex</c>.</summary>
    private static string AppxPackageName(string identity)
    {
        var underscore = identity.IndexOf('_');
        return (underscore > 0 ? identity[..underscore] : identity).ToLowerInvariant();
    }

    // A version (a dot is required, so Tcpip6 and Dhcp4 survive), a
    // per-logon-session hex tail (WpnUserService_ca529), and an AppX package
    // tail each fragment one service into many rows.
    [GeneratedRegex(@"\d+\.\d[\d.]*$")]
    private static partial Regex ServiceVersionSuffix();

    [GeneratedRegex(@"^([a-z][a-z0-9.]*?)_[0-9a-f]{4,}$")]
    private static partial Regex ServiceSessionSuffix();

    [GeneratedRegex(@"^(.+?)_\d[\d.]*_[a-z0-9]*_[a-z0-9]*_[a-z0-9]+$")]
    private static partial Regex ServicePackageSuffix();

    internal static string NormaliseService(string raw)
    {
        var s = ServiceVersionSuffix().Replace(raw, "", 1);
        var pkg = ServicePackageSuffix().Match(s);
        if (pkg.Success) s = pkg.Groups[1].Value;
        var session = ServiceSessionSuffix().Match(s.ToLowerInvariant());
        if (session.Success) s = session.Groups[1].Value;
        return s;
    }

    // ---- Resolution --------------------------------------------------------------

    private readonly record struct Member(string Key, string Name, string? Family = null);

    private static Member ResolveMember(string raw, string kind)
    {
        if (kind == "path")
        {
            var base_ = Basename(raw).ToLowerInvariant();

            if (GenericBasenames.Contains(base_))
            {
                var generic = PathVendors.FirstOrDefault(v => v.Test.IsMatch(raw));
                if (generic.Test is not null) return new($"{generic.Family}:{base_}", generic.Name, generic.Family);
                // No vendor known: key on the directory so two unrelated
                // installers do not merge into one row called "setup.exe".
                var dir = ParentDir(raw).ToLowerInvariant();
                return new(dir.Length > 0 ? $"{dir}\\{base_}" : base_, Basename(raw));
            }

            var vendor = PathVendors.FirstOrDefault(v => v.Test.IsMatch(raw));
            return new(base_, KnownExeNames.GetValueOrDefault(base_) ?? Basename(raw), vendor.Test is null ? null : vendor.Family);
        }

        if (kind == "appx")
        {
            var pkg = AppxPackageName(raw);
            return new($"appx:{pkg}", AppxKnownNames.GetValueOrDefault(pkg) ?? pkg.Split('.')[^1]);
        }

        var stripped = NormaliseService(raw);
        var key = stripped.ToLowerInvariant();
        return new($"svc:{key}", ServiceKnownNames.GetValueOrDefault(key) ?? stripped);
    }

    public static ResolvedApp Resolve(string identity, string kind)
    {
        var raw = identity.Trim();

        if (kind == "aggregate")
        {
            const string name = "All traffic (interface total)";
            return new(AggregateKey, name, kind, AggregateKey, name);
        }

        if (raw.Length == 0 || kind == "unknown") return new(UnknownKey, UnknownName, kind, UnknownKey, UnknownName);

        var member = ResolveMember(raw, kind);
        var family = FamilyOf.GetValueOrDefault(member.Key) ?? member.Family;
        return family is not null
            ? new(family, FamilyNames.GetValueOrDefault(family) ?? member.Name, kind, member.Key, member.Name)
            : new(member.Key, member.Name, kind, member.Key, member.Name);
    }
}
