using System.Drawing;
using DataUsage.Core.View;
using H.NotifyIcon.Core;

namespace DataUsage.App.State;

/// <summary>
/// The notification-area icon: the latest day's traffic in its tooltip, click
/// to open, right-click for the menu. Closing the window leaves the app here.
/// Collection never depends on it - the scheduled task collects either way -
/// but the tray is what keeps the numbers and the problem notifications live.
/// </summary>
public sealed class TrayHost : IDisposable
{
    private readonly TrayIconWithContextMenu _tray;
    private readonly Icon _icon;
    private readonly PopupMenuItem _startAtLogin;
    private readonly PopupMenuItem _autoRefresh;
    private readonly PopupMenuItem _notify;

    public TrayHost(AppState state, UiSettings settings, Action open, Action syncNow, Action exit)
    {
        _icon = new Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"), 16, 16);
        _tray = new TrayIconWithContextMenu("DataUsageNative.Tray")
        {
            Icon = _icon.Handle,
            ToolTip = "Data Usage",
        };

        _startAtLogin = new PopupMenuItem("Start at login", (_, _) => state.OnUi(() =>
        {
            try { StartupRegistration.Set(!StartupRegistration.IsEnabled); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
            _startAtLogin!.Checked = StartupRegistration.IsEnabled;
        }))
        { Checked = StartupRegistration.IsEnabled };

        _autoRefresh = new PopupMenuItem("Refresh when new data lands", (_, _) => state.OnUi(() =>
        {
            settings.AutoRefresh = !settings.AutoRefresh;
            settings.Save();
            _autoRefresh!.Checked = settings.AutoRefresh;
        }))
        { Checked = settings.AutoRefresh };

        _notify = new PopupMenuItem("Notify when collection fails", (_, _) => state.OnUi(() =>
        {
            settings.NotifyProblems = !settings.NotifyProblems;
            settings.Save();
            _notify!.Checked = settings.NotifyProblems;
        }))
        { Checked = settings.NotifyProblems };

        _tray.ContextMenu = new PopupMenu
        {
            Items =
            {
                new PopupMenuItem("Open Data Usage", (_, _) => state.OnUi(open)),
                new PopupMenuItem("Sync now", (_, _) => state.OnUi(syncNow)),
                new PopupMenuSeparator(),
                _autoRefresh,
                _notify,
                _startAtLogin,
                new PopupMenuSeparator(),
                new PopupMenuItem("Exit", (_, _) => state.OnUi(exit)),
            },
        };

        _tray.MessageWindow.MouseEventReceived += (_, e) =>
        {
            if (e.MouseEvent is MouseEvent.IconLeftMouseUp or MouseEvent.IconDoubleClick) state.OnUi(open);
        };
        _tray.Create();

        state.Changed += () => UpdateToolTip(state);
    }

    public void SyncChecks(UiSettings settings)
    {
        _autoRefresh.Checked = settings.AutoRefresh;
        _notify.Checked = settings.NotifyProblems;
        _startAtLogin.Checked = StartupRegistration.IsEnabled;
    }

    /// <summary>"Today: 2.62 GB (↓ 2.40 GB ↑ 220 MB)" - or the latest day held, named.</summary>
    private void UpdateToolTip(AppState state)
    {
        var lines = new List<string> { "Data Usage" };
        if (state.Latest is { Date: { } date, Totals: var t })
        {
            var today = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
            var label = date == today ? "Today" : Format.DayShort(date);
            lines.Add($"{label}: {Format.Bytes(t.Total)}");
            lines.Add($"↓ {Format.Bytes(t.Received)}  ↑ {Format.Bytes(t.Sent)}");
        }
        if (state.Sync is { ConsecutiveFailures: > 0 } s) lines.Add($"Last {s.ConsecutiveFailures} collection(s) failed");
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
