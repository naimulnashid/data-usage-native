using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace DataUsage.App;

/// <summary>
/// A custom entry point, for two reasons.
/// <list type="bullet">
/// <item><b>The collector.</b> The scheduled task runs this same exe with
/// <c>--collect</c> every 15 minutes. That path returns before WinUI starts, so
/// it opens no window - the exe is a WinExe, so there is no console to flash
/// either, and no wrapper script is needed.</item>
/// <item><b>A single instance.</b> The app lives in the tray and can start at
/// login, so a second launch must bring the running copy forward.</item>
/// </list>
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--collect", StringComparer.OrdinalIgnoreCase))
        {
            return Core.Collect.Collector.Run(new Core.Collect.CollectOptions
            {
                Force = args.Contains("--force", StringComparer.OrdinalIgnoreCase),
                ObserveOnly = args.Contains("--observe-only", StringComparer.OrdinalIgnoreCase),
            });
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        var instance = AppInstance.FindOrRegisterForKey(InstanceKey());
        if (!instance.IsCurrent)
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            instance.RedirectActivationToAsync(activation).AsTask().Wait();
            return 0;
        }

        instance.Activated += (_, _) => App.OnRedirected();

        Application.Start(callback =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    /// <summary>
    /// One instance per data folder, not per machine. A copy pointed elsewhere
    /// by DATAUSAGE_DATA_DIR - the demo, a screenshot run - starts beside the
    /// one in the tray instead of handing its launch to it.
    /// </summary>
    private static string InstanceKey()
    {
        if (Environment.GetEnvironmentVariable(Core.AppPaths.DataDirVariable) is not { Length: > 0 } dir) return "DataUsageNative.Main";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dir).ToUpperInvariant()));
        return "DataUsageNative." + Convert.ToHexString(hash, 0, 8);
    }
}
