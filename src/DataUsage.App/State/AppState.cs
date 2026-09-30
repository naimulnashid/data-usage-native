using DataUsage.Core;
using DataUsage.Core.Collect;
using DataUsage.Core.Data;
using DataUsage.Core.Naming;
using DataUsage.Core.Query;
using Microsoft.UI.Dispatching;

namespace DataUsage.App.State;

/// <summary>
/// Where the app's data comes from, and when it changes.
/// </summary>
/// <remarks>
/// <para>The pages read only the database; the scheduled collector is what
/// writes it. So "live refresh" is watching the database file and reloading
/// when a run lands - throttled, since the network sample writes every 15
/// minutes and a collection writes several times in a few seconds.</para>
/// <para>Queries run off the UI thread (a full-history query reads ~60k rows),
/// and each page asks for its own data; this class only holds what every page
/// shares: the device settings, the range, the colour map and the logos.</para>
/// </remarks>
public sealed class AppState : IDisposable
{
    private readonly DispatcherQueue _ui;
    private readonly UiSettings _settings;
    private FileSystemWatcher? _watcher;
    private DispatcherQueueTimer? _debounce;
    private string? _signature;

    public AppState(DispatcherQueue ui, UiSettings settings)
    {
        _ui = ui;
        _settings = settings;
        Scope = new Scope(Math.Clamp(settings.ScopeDays, 1, Scope.AllDays));
        Logos = new LogoStore(ui);
        Logos.Changed += () => _ = ReloadAsync();
        Open();
    }

    public string? DataDir { get; private set; }

    public DeviceSettings Device { get; private set; } = new();

    public UsageQueries? Queries { get; private set; }

    public Scope Scope { get; private set; }

    public LogoStore Logos { get; }

    /// <summary>App name to colour, from all-time totals.</summary>
    public IReadOnlyDictionary<string, string> Colors { get; private set; } = new Dictionary<string, string>();

    /// <summary>Name to logo, renames included.</summary>
    public IReadOnlyDictionary<string, AppIcon> Icons { get; private set; } = new Dictionary<string, AppIcon>();

    /// <summary>Health of the collector, as of the last reload.</summary>
    public SyncData? Sync { get; private set; }

    public (string? Date, Totals Totals) Latest { get; private set; }

    /// <summary>Bumped whenever something a page shows may have changed.</summary>
    public int Version { get; private set; }

    public bool HasData => Queries is { DatabaseExists: true };

    /// <summary>Raised on the UI thread after a reload.</summary>
    public event Action? Changed;

    /// <summary>Raised on the UI thread for a problem worth a notification: title, message.</summary>
    public event Action<string, string>? Problem;

    public void OnUi(Action action) => _ui.TryEnqueue(() => action());

    /// <summary>(Re)reads where the data folder is, and watches its database.</summary>
    public void Open()
    {
        DataDir = AppPaths.DataDir;
        _watcher?.Dispose();
        _watcher = null;
        if (DataDir is null)
        {
            Queries = null;
            return;
        }
        Device = DeviceSettings.Load(DataDir);
        Queries = new UsageQueries(AppPaths.DatabasePath(DataDir), Device.Split);
        Logos.SetFolder(AppPaths.LogosDir(DataDir));

        var live = Path.GetDirectoryName(AppPaths.DatabasePath(DataDir))!;
        try
        {
            Directory.CreateDirectory(live);
            _watcher = new FileSystemWatcher(live, "data-usage.db*") { EnableRaisingEvents = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
            _watcher.Changed += (_, _) => Touch();
            _watcher.Created += (_, _) => Touch();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unwatchable: the Refresh command still works.
        }
    }

    public void SetScope(Scope scope)
    {
        if (scope == Scope) return;
        Scope = scope;
        _settings.ScopeDays = scope.Days;
        _settings.Save();
        Version++;
        Changed?.Invoke();
    }

    public void SaveDevice(DeviceSettings device)
    {
        if (DataDir is null) return;
        device.Save(DataDir);
        Open();
        _ = ReloadAsync();
    }

    /// <summary>A write burst is many events; one reload a few seconds after the last.</summary>
    private void Touch()
    {
        if (!_settings.AutoRefresh) return;
        _ui.TryEnqueue(() =>
        {
            if (_debounce is null)
            {
                _debounce = _ui.CreateTimer();
                _debounce.Interval = TimeSpan.FromSeconds(3);
                _debounce.IsRepeating = false;
                _debounce.Tick += (_, _) => _ = ReloadAsync(onlyIfChanged: true);
            }
            _debounce.Stop();
            _debounce.Start();
        });
    }

    private int _reloading;

    /// <summary>
    /// Re-reads the shared data. With <paramref name="onlyIfChanged"/>, a write
    /// that changed nothing a page shows (a WAL checkpoint, a network sample)
    /// does not rebuild the page.
    /// </summary>
    public async Task ReloadAsync(bool onlyIfChanged = false)
    {
        if (Interlocked.Exchange(ref _reloading, 1) == 1) return;
        try
        {
            var queries = Queries;
            if (queries is null || !queries.DatabaseExists)
            {
                Version++;
                Changed?.Invoke();
                return;
            }
            var logos = Logos.Folder;
            var result = await Task.Run(() =>
            {
                var signature = Signature(queries.DatabasePath);
                var names = queries.AppNamesByKey();
                return (Signature: signature,
                    Colors: queries.ColorMap(),
                    Icons: AppIcons.Map(logos, names.Values.Select(v => (v.Name, v.Base))),
                    Sync: queries.Sync(10),
                    Latest: queries.Latest());
            });
            if (onlyIfChanged && result.Signature == _signature) return;
            _signature = result.Signature;
            Colors = result.Colors;
            Icons = result.Icons;
            Sync = result.Sync;
            Latest = result.Latest;
            Version++;
            Changed?.Invoke();
            CheckHealth();
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            // Mid-write, or the drive is gone: the next change tries again.
        }
        finally
        {
            Interlocked.Exchange(ref _reloading, 0);
        }
    }

    /// <summary>What changes when anything a page shows changes.</summary>
    private static string Signature(string db)
    {
        using var conn = UsageDb.OpenRead(db);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT (SELECT COUNT(*) FROM usage_records) || '|' ||
                   (SELECT COALESCE(MAX(id), 0) || ':' || COALESCE(MAX(finished_at), '') FROM sync_log) || '|' ||
                   (SELECT COALESCE(SUM(votes), 0) FROM network_names) || '|' ||
                   (SELECT COUNT(*) || ':' || COALESCE(MAX(updated_at), '') FROM app_renames)
            """;
        return cmd.ExecuteScalar() as string ?? "";
    }

    /// <summary>The Sync button: a collection now, whatever the schedule says.</summary>
    public async Task<int> CollectNowAsync(Action<string> progress)
    {
        var code = await Task.Run(() => Collector.Run(new CollectOptions
        {
            Force = true,
            Log = (_, message) => _ui.TryEnqueue(() => progress(message)),
        }));
        // The ESE engine and the 33k rows it read are garbage now.
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        await ReloadAsync();
        return code;
    }

    /// <summary>
    /// One notification per failed run, and one per stall. The collector runs
    /// hourly; three hours without a success is a laptop that slept - the task
    /// catches up on waking - so a stall is called at six.
    /// </summary>
    private void CheckHealth()
    {
        if (!_settings.NotifyProblems || Sync is not { } sync) return;
        var newest = sync.Runs.FirstOrDefault();
        if (newest is { Status: "failed" } && newest.Id > _settings.NotifiedFailureId)
        {
            _settings.NotifiedFailureId = newest.Id;
            _settings.Save();
            Problem?.Invoke("Data Usage collection failed", Trim(newest.Error ?? "The collector reported a failure. Open Sync Status for details."));
            return;
        }
        if (sync.HoursSinceSuccess is > 6 && sync.LastSuccess?.StartedAt is { } since && since != _settings.NotifiedStallSince)
        {
            _settings.NotifiedStallSince = since;
            _settings.Save();
            Problem?.Invoke("Data Usage has stopped collecting", $"No successful collection in {Math.Round(sync.HoursSinceSuccess.Value)} hours. Open Sync Status to check the scheduled task.");
        }
    }

    /// <summary>Stalls can begin with no write at all, so health is also checked on a clock.</summary>
    public void CheckHealthNow() => _ = ReloadAsync(onlyIfChanged: false);

    private static string Trim(string s) => s.Length > 200 ? s[..197] + "..." : s;

    public void Dispose()
    {
        _watcher?.Dispose();
        Logos.Dispose();
    }
}
