using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Fullobby.Core.Scheduling;

/// <summary>
/// Thin wrapper over <c>schtasks.exe</c> for creating/deleting/querying the auto-seed daily tasks.
/// Builds a Task XML document and registers it via <c>schtasks /create /xml</c> (the XML lets us
/// set WakeToRun + StartWhenAvailable, which the plain command line can't). The XML builder and
/// next-run parser are pure + unit-tested.
/// </summary>
public sealed class ScheduledTaskService
{
    private readonly ILogger<ScheduledTaskService> _log;

    /// <summary>The console OEM code page schtasks writes its (localized) output in. Decoding stdout
    /// as UTF-8 mangles non-ASCII text on non-English Windows (e.g. the "Next Run Time" value), so we
    /// decode with the actual OEM code page. Resolved once; falls back to UTF-8 if unavailable.</summary>
    private static readonly Encoding SchtasksOutputEncoding = ResolveOemEncoding();

    static ScheduledTaskService()
    {
        // OEM code pages (437, 850, 932, …) aren't built into .NET — register the provider so
        // Encoding.GetEncoding can resolve them.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static Encoding ResolveOemEncoding()
    {
        try
        {
            return Encoding.GetEncoding((int)GetOEMCP());
        }
        catch (Exception)
        {
            return Encoding.UTF8;
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    public ScheduledTaskService(ILogger<ScheduledTaskService> log) => _log = log;

    /// <summary>
    /// Create (or update) a daily task that runs the current exe with <paramref name="args"/> at the
    /// given local time. <paramref name="localTimeHms"/> is "HH:MM"/"HH:MM:SS" local; the StartBoundary
    /// uses today's local date. Returns true on success.
    /// </summary>
    public async Task<bool> CreateDailyTaskAsync(string taskName, string localTimeHms, string args, CancellationToken ct = default)
    {
        var normalized = AutoSeedTime.NormalizeHms(localTimeHms);
        var exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine current executable path");
        var workingDir = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory;
        var startBoundary = $"{DateTime.Now:yyyy-MM-dd}T{normalized}";

        var xml = BuildTaskXml(exe, workingDir, args, startBoundary);

        // This file decides what a daily, WakeToRun task executes, and a separate process reads it
        // back — so it must not sit at a guessable path in the shared %TEMP% root, where another
        // process running as this user could swap the <Command> between our write and schtasks'
        // read. Write it into a fresh randomly-named subdirectory (same approach the updater takes
        // for the installer) and remove the whole directory afterwards.
        var tempDir = Path.Combine(Path.GetTempPath(), $"fullobby-task-{Guid.NewGuid():N}");
        var tempPath = Path.Combine(tempDir, "task.xml");
        try
        {
            Directory.CreateDirectory(tempDir);
            // schtasks wants UTF-16 XML (matching the <?xml ... encoding="UTF-16"?> declaration).
            await File.WriteAllTextAsync(tempPath, xml, Encoding.Unicode, ct).ConfigureAwait(false);

            var (exit, _, stderr) = await RunSchtasksAsync(
                ["/create", "/tn", taskName, "/xml", tempPath, "/f"], ct).ConfigureAwait(false);
            if (exit != 0)
            {
                _log.LogError("schtasks /create for {Task} failed ({Exit}): {Err}", taskName, exit, stderr);
                return false;
            }
            _log.LogInformation("Created scheduled task {Task} at {Time} ({Args})", taskName, normalized, args);
            return true;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { /* best effort */ }
            catch (UnauthorizedAccessException) { /* best effort */ }
        }
    }

    /// <summary>Delete a task by name. Returns true when the delete succeeds (task existed).</summary>
    public async Task<bool> DeleteTaskAsync(string taskName, CancellationToken ct = default)
    {
        var (exit, _, _) = await RunSchtasksAsync(["/delete", "/tn", taskName, "/f"], ct).ConfigureAwait(false);
        if (exit == 0)
        {
            _log.LogInformation("Deleted scheduled task {Task}", taskName);
            return true;
        }
        return false;
    }

    /// <summary>True when the named task exists.</summary>
    public async Task<bool> IsInstalledAsync(string taskName, CancellationToken ct = default)
    {
        var (exit, _, _) = await RunSchtasksAsync(["/query", "/tn", taskName], ct).ConfigureAwait(false);
        return exit == 0;
    }

    /// <summary>The task's next run time as schtasks reports it, or null when absent/disabled/"N/A".</summary>
    public async Task<string?> GetNextRunTimeAsync(string taskName, CancellationToken ct = default)
    {
        var (exit, stdout, _) = await RunSchtasksAsync(
            ["/query", "/tn", taskName, "/fo", "LIST", "/v"], ct).ConfigureAwait(false);
        return exit == 0 ? ParseNextRunTime(stdout) : null;
    }

    /// <summary>The full verbose schtasks listing for a task (for the "View schedule" dialog), capped
    /// at 2000 chars. Null when the task is absent.</summary>
    public async Task<string?> QueryVerboseAsync(string taskName, CancellationToken ct = default)
    {
        var (exit, stdout, _) = await RunSchtasksAsync(
            ["/query", "/tn", taskName, "/fo", "LIST", "/v"], ct).ConfigureAwait(false);
        if (exit != 0)
        {
            return null;
        }
        return stdout.Length > 2000 ? stdout[..2000] : stdout;
    }

    // ── Pure helpers (public + static for unit coverage) ───────────────────────

    /// <summary>Build the Task Scheduler v1.2 XML for a daily, wake-to-run, non-elevated task.</summary>
    public static string BuildTaskXml(string command, string workingDir, string args, string startBoundary)
    {
        var cmd = SecurityElement.Escape(command);
        var wd = SecurityElement.Escape(workingDir);
        var a = SecurityElement.Escape(args);
        var sb = SecurityElement.Escape(startBoundary);

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>{Branding.ProductName} daily auto-seed</Description>
                <Author>{Branding.ProductName}</Author>
              </RegistrationInfo>
              <Triggers>
                <CalendarTrigger>
                  <StartBoundary>{sb}</StartBoundary>
                  <Enabled>true</Enabled>
                  <ScheduleByDay>
                    <DaysInterval>1</DaysInterval>
                  </ScheduleByDay>
                </CalendarTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>true</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{cmd}</Command>
                  <Arguments>{a}</Arguments>
                  <WorkingDirectory>{wd}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    /// <summary>
    /// Extract the "Next Run Time:" value from <c>schtasks /fo LIST /v</c> output. Returns null when the
    /// line is missing, "N/A", or empty. Port of <c>parse_next_run_time</c>.
    /// </summary>
    public static string? ParseNextRunTime(string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("Next Run Time:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var value = line["Next Run Time:".Length..].Trim();
            if (value.Length == 0 || value.Equals("N/A", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return value;
        }
        return null;
    }

    private async Task<(int Exit, string Stdout, string Stderr)> RunSchtasksAsync(
        IReadOnlyList<string> args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = SchtasksOutputEncoding,
                StandardErrorEncoding = SchtasksOutputEncoding,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                _log.LogWarning("Failed to start schtasks.exe");
                return (-1, "", "");
            }

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            return (proc.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "schtasks invocation failed: {Args}", string.Join(' ', args));
            return (-1, "", e.Message);
        }
    }
}
