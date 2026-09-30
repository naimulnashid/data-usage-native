using System.Text.Json;
using System.Text.Json.Nodes;

namespace DataUsage.Core;

/// <summary>
/// Where everything lives. Two places, on purpose:
/// <list type="bullet">
/// <item><b>The data folder</b> (default <c>D:\PersistentData\data-usage-native</c>)
/// holds what must survive a Windows reset: the database, its backup, logos
/// and settings. It must not be on the system drive - that is the whole point.</item>
/// <item><b>The local folder</b> (<c>%LOCALAPPDATA%\Data Usage Native</c>) holds
/// what is cheap to lose: UI preferences, the pointer to the data folder, logs
/// and scratch. Scratch holds a ~99 MB snapshot mid-run, so it must stay out of
/// the synced data folder: every run would upload a full copy of the raw
/// usage history and then delete it.</item>
/// </list>
/// </summary>
public static class AppPaths
{
    public const string DataDirVariable = "DATAUSAGE_DATA_DIR";

    /// <summary>What the installer offers, and what this machine uses.</summary>
    public const string DefaultDataDir = @"D:\PersistentData\data-usage-native";

    public static string LocalDir { get; } = Environment.GetEnvironmentVariable("DATAUSAGE_LOCAL_DIR") is { Length: > 0 } local
        ? local
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Data Usage Native");

    public static string LocationFile => Path.Combine(LocalDir, "location.json");

    public static string ScratchDir => Path.Combine(LocalDir, "scratch");

    public static string LogsDir => Path.Combine(LocalDir, "logs");

    /// <summary>
    /// The data folder, or null when none has been chosen yet (a fresh install
    /// before its first run, or after a reset). <see cref="DataDirVariable"/>
    /// overrides it, which the demo and screenshot runs rely on.
    /// </summary>
    public static string? DataDir => Environment.GetEnvironmentVariable(DataDirVariable) is { Length: > 0 } env
        ? Path.GetFullPath(env)
        : ReadLocation().DataDir;

    public static string DatabasePath(string dataDir) => Path.Combine(dataDir, "live", "data-usage.db");

    /// <summary>
    /// The restore source. Written with SQLite's backup API, so it is one
    /// internally consistent file - unlike the live database, whose WAL
    /// sidecars a sync client can upload out of step with it.
    /// </summary>
    public static string BackupPath(string dataDir) => Path.Combine(dataDir, "data-usage.db");

    public static string LogosDir(string dataDir) => Path.Combine(dataDir, "logos");

    public static string SettingsPath(string dataDir) => Path.Combine(dataDir, "settings.json");

    /// <summary>The SRUM database this machine writes.</summary>
    public static string SrumPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\sru\SRUDB.dat");

    /// <summary>
    /// Where the elevated snapshot task leaves its copy. Created by the
    /// installer as admin-writable, user-readable, so nothing the user runs can
    /// plant a link in a directory an administrator writes to.
    /// </summary>
    public static string SnapshotDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Data Usage Native", "work");

    public sealed record Location(string? DataDir, bool AllowSystemDrive);

    public static Location ReadLocation()
    {
        try
        {
            if (!File.Exists(LocationFile)) return new(null, false);
            var node = JsonNode.Parse(File.ReadAllText(LocationFile));
            var dir = node?["dataDir"]?.GetValue<string>();
            var allow = node?["allowSystemDrive"]?.GetValue<bool>() ?? false;
            return new(string.IsNullOrWhiteSpace(dir) ? null : dir, allow);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new(null, false);
        }
    }

    public static void WriteLocation(string dataDir, bool allowSystemDrive)
    {
        Directory.CreateDirectory(LocalDir);
        var json = new JsonObject { ["dataDir"] = Path.GetFullPath(dataDir), ["allowSystemDrive"] = allowSystemDrive };
        File.WriteAllText(LocationFile, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>True when <paramref name="path"/> is on the drive Windows is installed on.</summary>
    public static bool IsOnSystemDrive(string path)
    {
        var system = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";
        return string.Equals(Path.GetPathRoot(Path.GetFullPath(path)), system, StringComparison.OrdinalIgnoreCase);
    }
}
