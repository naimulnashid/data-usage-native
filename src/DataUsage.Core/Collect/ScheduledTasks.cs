using System.Runtime.InteropServices;

namespace DataUsage.Core.Collect;

/// <summary>What Task Scheduler knows about one task.</summary>
public sealed record TaskInfo(bool Exists, bool Running, DateTime? LastRunUtc, int LastResult, DateTime? NextRunUtc);

/// <summary>
/// The app's scheduled tasks, through Task Scheduler's own COM API rather than
/// by parsing schtasks output.
/// </summary>
/// <remarks>
/// <para><b>The privilege split.</b> Only the VSS snapshot needs
/// Administrator. The snapshot task runs nothing but Windows' own
/// <c>cmd.exe</c> and <c>esentutl.exe</c>, with every argument fixed when an
/// administrator registered it, writing into a directory only administrators
/// can write. So nothing the user can edit - this app, its settings, its
/// scripts - ever runs elevated, and there is no deployed script to protect or
/// redeploy. Starting a registered task needs no elevation, which is how the
/// unelevated collector (and the app's Sync button) ask for a snapshot.</para>
/// </remarks>
public static class ScheduledTasks
{
    public const string CollectorTask = "Data Usage Native Collector";
    public const string SnapshotTask = "Data Usage Native Snapshot";

    private const int TaskStateRunning = 4;

    private static dynamic Folder()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler is not available");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service.GetFolder(@"\");
    }

    private static dynamic? Find(string name)
    {
        try { return Folder().GetTask(name); }
        catch (COMException) { return null; }
        catch (FileNotFoundException) { return null; }
    }

    public static TaskInfo Query(string name)
    {
        var task = Find(name);
        if (task is null) return new TaskInfo(false, false, null, 0, null);
        DateTime? Time(DateTime t) => t.Year < 2000 ? null : t.ToUniversalTime();
        return new TaskInfo(true, (int)task.State == TaskStateRunning, Time((DateTime)task.LastRunTime), (int)task.LastTaskResult, Time((DateTime)task.NextRunTime));
    }

    /// <summary>Start a registered task now. Needs no elevation, even for an elevated task.</summary>
    public static void Run(string name)
    {
        var task = Find(name) ?? throw new InvalidOperationException($"The '{name}' task is not registered. Run tools\\Install.ps1 to register it.");
        task.Run(null);
    }
}
