using System.Drawing;
using DataUsage.Core.Query;
using DataUsage.Core.View;
using H.NotifyIcon.Core;

namespace DataUsage.App.State;

/// <summary>
/// The notification-area icon: the latest day's traffic in its tooltip, click
/// to open, right-click for the menu. It belongs to the tray process
/// (<see cref="TrayProcess"/>), which outlives the window. Collection never
/// depends on it - the scheduled task collects either way - but the tray is
/// what keeps the numbers and the problem notifications live.
/// </summary>
public sealed class TrayHost : IDisposable
{
    private readonly TrayIconWithContextMenu _tray;
    private readonly Icon _icon;
    private readonly PopupMenuItem _startAtLogin;
    private readonly PopupMenuItem _autoRefresh;
    private readonly PopupMenuItem _notify;

    /// <param name="onUi">Marshals a menu click, which arrives on the icon's thread, onto the UI thread.</param>
    /// <param name="toggleAutoRefresh">Flips the setting and returns its new value; <paramref name="toggleNotify"/> likewise.</param>
    public TrayHost(Action<Action> onUi, UiSettings settings, Action open, Action syncNow, Func<bool> toggleAutoRefresh, Func<bool> toggleNotify, Action exit)
    {
        _icon = new Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"), 16, 16);
        // The id is per exe path. Windows binds a tray icon's id to the exe
        // that first registered it and silently refuses it from any other:
        // once a Debug build had run, the installed app came up with no tray
        // icon at all (HKCU\Control Panel\NotifyIconSettings held the Debug
        // path), and closed to the tray it could not be brought back.
        _tray = new TrayIconWithContextMenu($"DataUsageNative.Tray|{Environment.ProcessPath}")
        {
            Icon = _icon.Handle,
            ToolTip = "Data Usage",
        };

        _startAtLogin = new PopupMenuItem("Start at login", (_, _) => onUi(() =>
        {
            try { StartupRegistration.Set(!StartupRegistration.IsEnabled); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
            _startAtLogin!.Checked = StartupRegistration.IsEnabled;
        }))
        { Checked = StartupRegistration.IsEnabled };

        _autoRefresh = new PopupMenuItem("Refresh when new data lands", (_, _) => onUi(() => _autoRefresh!.Checked = toggleAutoRefresh()))
        { Checked = settings.AutoRefresh };

        _notify = new PopupMenuItem("Notify when collection fails", (_, _) => onUi(() => _notify!.Checked = toggleNotify()))
        { Checked = settings.NotifyProblems };

        _tray.ContextMenu = new PopupMenu
        {
            Items =
            {
                new PopupMenuItem("Open Data Usage", (_, _) => onUi(open)),
                new PopupMenuItem("Sync now", (_, _) => onUi(syncNow)),
                new PopupMenuSeparator(),
                _autoRefresh,
                _notify,
                _startAtLogin,
                new PopupMenuSeparator(),
                new PopupMenuItem("Exit", (_, _) => onUi(exit)),
            },
        };

        _tray.MessageWindow.MouseEventReceived += (_, e) =>
        {
            if (e.MouseEvent is MouseEvent.IconLeftMouseUp or MouseEvent.IconDoubleClick) onUi(open);
        };
        _tray.Create();
        MenuTheme.Allow(_tray.MessageWindow.Handle);
    }

    public void SyncChecks(UiSettings settings)
    {
        _autoRefresh.Checked = settings.AutoRefresh;
        _notify.Checked = settings.NotifyProblems;
        _startAtLogin.Checked = StartupRegistration.IsEnabled;
    }

    /// <summary>"Today: 2.62 GB (↓ 2.40 GB ↑ 220 MB)" - or the latest day held, named.</summary>
    public void ShowStatus((string? Date, Totals Totals) latest, long failedRuns)
    {
        var lines = new List<string> { "Data Usage" };
        if (latest is { Date: { } date, Totals: var t })
        {
            var today = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
            var label = date == today ? "Today" : Format.DayShort(date);
            lines.Add($"{label}: {Format.Bytes(t.Total)}");
            lines.Add($"↓ {Format.Bytes(t.Received)}  ↑ {Format.Bytes(t.Sent)}");
        }
        if (failedRuns > 0) lines.Add($"Last {failedRuns} collection(s) failed");
        // The shell truncates a tray tooltip at 127 characters.
        var text = string.Join("\n", lines);
        _tray.UpdateToolTip(text.Length > 127 ? text[..127] : text);
    }

    public void Notify(string title, string message, bool warning = false) =>
        _tray.ShowNotification(title, message, warning ? NotificationIcon.Warning : NotificationIcon.Info, null, false, false, false, false, null);

    public void Dispose()
    {
        _tray.Dispose();
        _icon.Dispose();
    }
}
