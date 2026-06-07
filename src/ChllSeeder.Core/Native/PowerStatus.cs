using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ChllSeeder.Core.Native;

/// <summary>
/// Detects Windows power configuration that can defeat scheduled wake-from-sleep
/// for auto-seeding. Port of <c>src-rust/src/backend/power.rs</c> — shells out to
/// <c>powercfg.exe</c> and parses its text output.
/// </summary>
public sealed class PowerStatus
{
    private readonly ILogger<PowerStatus> _log;

    public PowerStatus(ILogger<PowerStatus> log) => _log = log;

    /// <summary>Detect Modern Standby (S0 Low Power Idle) via <c>powercfg /a</c>.
    /// Returns <c>false</c> (safe default) on failure.</summary>
    public bool IsModernStandby()
    {
        if (RunPowercfg("/a") is { } output)
        {
            var result = ParseModernStandbyOutput(output);
            _log.LogInformation("IsModernStandby: {Result} (S0 Low Power Idle present in powercfg /a)", result);
            return result;
        }
        return false;
    }

    /// <summary>Check whether wake timers are enabled in the active power plan via
    /// <c>powercfg /q SCHEME_CURRENT SUB_SLEEP RTCWAKE</c>. Returns <c>true</c>
    /// (safe default — assume enabled) on failure.</summary>
    public bool AreWakeTimersEnabled()
    {
        if (RunPowercfg("/q", "SCHEME_CURRENT", "SUB_SLEEP", "RTCWAKE") is { } output)
        {
            var enabled = ParseWakeTimersOutput(output);
            _log.LogInformation("AreWakeTimersEnabled: {Enabled}", enabled);
            return enabled;
        }
        return true; // safe default: assume enabled
    }

    /// <summary>Compose user-facing warning text about power configuration issues.
    /// Returns <c>null</c> when no warnings are needed.</summary>
    public string? CheckPowerWarnings() =>
        ComposePowerWarnings(IsModernStandby(), AreWakeTimersEnabled());

    /// <summary>Run powercfg with the given arguments and return stdout, or null on failure.</summary>
    private string? RunPowercfg(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("powercfg")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                _log.LogWarning("Failed to start powercfg");
                return null;
            }
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                _log.LogWarning("powercfg exited with status {Code}: {Err}", proc.ExitCode, stderr);
                return null;
            }
            return stdout;
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Failed to run powercfg");
            return null;
        }
    }

    // ── Pure parsers (public for unit coverage; port of the private fns in power.rs) ──

    /// <summary>True if <c>powercfg /a</c> reports an S0 Low Power Idle sleep state.</summary>
    public static bool ParseModernStandbyOutput(string output) =>
        output.Contains("S0 Low Power Idle", StringComparison.Ordinal);

    /// <summary>True if the RTCWAKE query shows the AC setting index enabled (0x00000001).</summary>
    public static bool ParseWakeTimersOutput(string output)
    {
        foreach (var rawLine in output.Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (trimmed.StartsWith("Current AC Power Setting Index:", StringComparison.Ordinal)
                && trimmed.Contains("0x00000001", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Compose warning text from the two power flags, or null when all is well.</summary>
    public static string? ComposePowerWarnings(bool modernStandby, bool wakeTimersEnabled)
    {
        var warnings = new List<string>();

        if (!wakeTimersEnabled)
        {
            warnings.Add(
                "WARNING: 'Allow wake timers' is DISABLED in your current power plan. " +
                "The scheduled task will NOT be able to wake your PC from sleep. " +
                "Enable it in: Power Options > Change plan settings > Change advanced power settings > Sleep > Allow wake timers.");
        }

        if (modernStandby)
        {
            warnings.Add(
                "NOTE: Your PC uses Modern Standby (S0 Low Power Idle). " +
                "Task Scheduler's wake-from-sleep may be unreliable on Modern Standby systems. " +
                "As a backup, this app will automatically detect and fire missed autoseeds " +
                "when it is running.");
        }

        return warnings.Count == 0 ? null : "\n\n" + string.Join("\n\n", warnings);
    }
}
