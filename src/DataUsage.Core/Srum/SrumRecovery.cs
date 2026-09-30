using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DataUsage.Core.Srum;

public sealed record RecoveryResult(bool Ok, string State, bool Repaired, int ExitCode, int LogsCopied, int JournalsMissed, IReadOnlyList<string> Warnings, IReadOnlyList<string> Output);

/// <summary>
/// Makes a VSS copy of SRUDB.dat attachable. Not optional: a VSS copy of a
/// live ESE database is always in Dirty Shutdown state, and ESE refuses to
/// attach one at all - no rows, not "most of the rows".
/// </summary>
/// <remarks>
/// <para><b>The rolled-over journals copy fine unelevated; the CURRENT one,
/// SRU.log, is often held exclusively by the SRUM service</b>, longest right
/// after a snapshot. Missing it is fatal - it is the log recovery most needs -
/// so it is opened with ReadWrite|Delete sharing and retried.</para>
/// <para><b>esentutl /r exiting 0 does NOT mean the database is clean.</b>
/// With SRU.chk absent, replay runs the contiguous generations it has; a
/// missing tail leaves the database dirty and still exits 0. The state is read
/// back from the header with /mh, never inferred from the exit code.</para>
/// <para><b>When replay falls short, repair (/p) is the fallback</b> - safe
/// because it only ever touches the throwaway copy, and it cost no rows when
/// measured. But repair resets the database's log position, so the copied
/// stream beside it must be deleted, or the next attach tries recovery of its
/// own and throws "Current log file missing".</para>
/// <para><b>The copy must be named SRUDB.dat</b>: the log stream records the
/// database by name, and /d only redirects the directory.</para>
/// <para><b>Not copied:</b> SRU.chk (it can name a generation Windows has
/// deleted; without it recovery starts at the oldest log present, which exists
/// by construction), SRUtmp.log and SRUres*.jrs (ESE scratch and reserve).</para>
/// </remarks>
public static partial class SrumRecovery
{
    public const string DatabaseName = "SRUDB.dat";

    [GeneratedRegex(@"^SRU[0-9A-F]*\.log$", RegexOptions.IgnoreCase)]
    private static partial Regex JournalName();

    [GeneratedRegex(@"^(SRU.*\.log|sru\.chk|srures\d+\.jrs)$", RegexOptions.IgnoreCase)]
    private static partial Regex StreamFile();

    [GeneratedRegex(@"^\s*State:\s*(.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex StateLine();

    private static string Esentutl => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "esentutl.exe");

    /// <summary>
    /// Copy one journal, tolerating the writer that holds it. The risk accepted
    /// is a torn read of the generation being written; ESE rejects such a log,
    /// which lands where a missing log does: dirty, then repair.
    /// </summary>
    private static void CopyJournal(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = File.Create(destination);
        input.CopyTo(output);
    }

    public static (int ExitCode, string Output) RunEsentutl(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo(Esentutl)
        {
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start esentutl.exe");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(TimeSpan.FromMinutes(5)))
        {
            try { p.Kill(); } catch (InvalidOperationException) { }
            return (-1, "esentutl timed out");
        }
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    /// <summary>The header's <c>State:</c> value, e.g. "Clean Shutdown".</summary>
    public static string ShutdownState(string database)
    {
        if (!File.Exists(database)) return "missing";
        // /mh writes nothing; run it beside the file so nothing lands elsewhere.
        var (_, output) = RunEsentutl(Path.GetDirectoryName(database)!, "/mh", database);
        var m = StateLine().Match(output);
        return m.Success ? m.Groups[1].Value : "unknown";
    }

    /// <summary>
    /// Copy the journals into <paramref name="workDir"/> (which holds the
    /// snapshot as SRUDB.dat), replay them, and repair if replay falls short.
    /// Every path esentutl sees is scoped to <paramref name="workDir"/>; the
    /// live SRUM directory is only ever read.
    /// </summary>
    public static RecoveryResult Recover(string workDir, string srumDir, int copyAttempts = 5, int copyDelayMs = 1000)
    {
        var warnings = new List<string>();
        var copied = 0;
        var lastError = new Dictionary<string, string>();

        // Copied AFTER the snapshot, so they can only be newer than it - the
        // direction soft recovery resolves.
        var pending = Directory.EnumerateFiles(srumDir, "SRU*.log")
            .Where(f => JournalName().IsMatch(Path.GetFileName(f)))
            .ToList();

        for (var attempt = 1; attempt <= copyAttempts && pending.Count > 0; attempt++)
        {
            if (attempt > 1) Thread.Sleep(copyDelayMs);
            var locked = new List<string>();
            foreach (var log in pending)
            {
                try
                {
                    CopyJournal(log, Path.Combine(workDir, Path.GetFileName(log)));
                    copied++;
                }
                catch (IOException ex)
                {
                    lastError[log] = ex.Message;
                    locked.Add(log);
                }
                catch (UnauthorizedAccessException ex)
                {
                    lastError[log] = ex.Message;
                    locked.Add(log);
                }
            }
            pending = locked;
        }
        foreach (var log in pending)
            warnings.Add($"could not copy {Path.GetFileName(log)} after {copyAttempts} attempts: {lastError[log]}");

        // /r sru replays the 'sru' stream; /i tolerates the attachment mismatch
        // every snapshot has; /l /s /d scope logs, checkpoint and database to workDir.
        var (code, output) = RunEsentutl(workDir, "/r", "sru", "/i", "/l", workDir, "/s", workDir, "/d", workDir);
        var outputs = new List<string> { output };

        var db = Path.Combine(workDir, DatabaseName);
        var state = ShutdownState(db);
        var repaired = false;

        if (state != "Clean Shutdown" && File.Exists(db))
        {
            warnings.Add($"replay left the database '{state}' (exit {code}); attempting hard repair");
            // Repair writes SRUDB.INTEG.RAW into its WORKING directory, and
            // appends forever - so it runs in workDir, never the repo.
            var (repairCode, repairOut) = RunEsentutl(workDir, "/p", db, "/o");
            outputs.Add($"--- esentutl /p (exit {repairCode}) ---");
            outputs.Add(repairOut);
            repaired = true;

            // Leave the repaired database ALONE, or the attach fails with a
            // different error that looks like the same one.
            foreach (var f in Directory.EnumerateFiles(workDir).Where(f => StreamFile().IsMatch(Path.GetFileName(f))).ToList())
            {
                try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            outputs.Add("--- log stream removed so the repaired database attaches alone ---");
            state = ShutdownState(db);
        }

        return new RecoveryResult(state == "Clean Shutdown", state, repaired, code, copied, pending.Count, warnings, outputs);
    }
}
