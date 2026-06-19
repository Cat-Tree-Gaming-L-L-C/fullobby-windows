using System.Diagnostics;
using System.Text.RegularExpressions;
using ChllSeeding.Core.Games;
using Microsoft.Extensions.Logging;

namespace ChllSeeding.Core.Native;

/// <summary>
/// Launches games through the Steam client (<c>-applaunch &lt;appid&gt; -dev +connect &lt;ip&gt;</c>),
/// cold-starting Steam first if needed. Port of the launch half of
/// <c>src-rust/src/backend/steam.rs</c>. Efficiency-mode application and config
/// backup/restore are orchestrated by the seeding engine, not here.
/// </summary>
public sealed partial class SteamLauncher
{
    private static readonly TimeSpan SteamStartupPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SteamStartupTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan SteamPostStartupDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LaunchWaitTimeout = TimeSpan.FromSeconds(30);

    [GeneratedRegex(@"^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})(:\d{1,5})?$")]
    private static partial Regex IpRegex();

    private readonly ILogger<SteamLauncher> _log;
    private readonly ProcessMonitor _processMonitor;

    public SteamLauncher(ILogger<SteamLauncher> log, ProcessMonitor processMonitor)
    {
        _log = log;
        _processMonitor = processMonitor;
    }

    /// <summary>Ensure the Steam client is running before issuing <c>-applaunch</c>: starts it if
    /// needed and polls until detected (then a short IPC-warmup delay).</summary>
    public async Task EnsureSteamRunningAsync(CancellationToken ct = default)
    {
        if (_processMonitor.IsSteamRunningFresh())
        {
            _log.LogInformation("Steam is already running");
            return;
        }

        _log.LogInformation("Steam is not running — starting Steam client");
        var steamPath = SteamPaths.GetExecutablePath();

        using var child = Process.Start(new ProcessStartInfo(steamPath) { UseShellExecute = false });
        if (child is null)
        {
            throw new IOException("Failed to start Steam");
        }

        var start = Stopwatch.StartNew();
        while (true)
        {
            await Task.Delay(SteamStartupPollInterval, ct).ConfigureAwait(false);
            var elapsed = start.Elapsed;

            if (child.HasExited && child.ExitCode != 0)
            {
                throw new IOException($"Steam launcher exited prematurely with code {child.ExitCode}");
            }

            if (_processMonitor.IsSteamRunningFresh())
            {
                _log.LogInformation("Steam client detected after {Secs}s — waiting {Delay}s for IPC initialization",
                    (int)elapsed.TotalSeconds, (int)SteamPostStartupDelay.TotalSeconds);
                await Task.Delay(SteamPostStartupDelay, ct).ConfigureAwait(false);
                _log.LogInformation("Steam is ready");
                return;
            }

            if (elapsed >= SteamStartupTimeout)
            {
                throw new TimeoutException($"Steam did not start within {(int)SteamStartupTimeout.TotalSeconds}s");
            }
        }
    }

    /// <summary>Launch a game and connect to a server. Ensures Steam is running, validates the IP,
    /// then runs <c>steam.exe -applaunch &lt;appid&gt; -dev +connect &lt;ip&gt;</c> and waits up to 30s
    /// for the launcher shim to exit.</summary>
    public async Task OpenGameAsync(GameDefinition game, string serverIp, CancellationToken ct = default)
    {
        await EnsureSteamRunningAsync(ct).ConfigureAwait(false);

        if (ValidateServerIp(serverIp) is { } error)
        {
            throw new ArgumentException($"Invalid server IP: {error}", nameof(serverIp));
        }

        _log.LogInformation("Opening {Game}", game.DisplayName);
        var steamPath = SteamPaths.GetExecutablePath();

        var psi = new ProcessStartInfo(steamPath) { UseShellExecute = false };
        psi.ArgumentList.Add("-applaunch");
        psi.ArgumentList.Add(game.SteamAppId);
        psi.ArgumentList.Add("-dev");
        psi.ArgumentList.Add("+connect");
        psi.ArgumentList.Add(serverIp);

        using var child = Process.Start(psi);
        if (child is null)
        {
            throw new IOException("Failed to start Steam");
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(LaunchWaitTimeout);
            await child.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            _log.LogInformation("Steam launcher process exited normally");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.LogInformation("Steam launcher process did not exit within 30s, continuing anyway");
        }
    }

    /// <summary>Validate a server IP (and optional :port) to prevent command injection. Returns
    /// <c>null</c> when valid, otherwise an error message. Public + static for unit coverage.</summary>
    public static string? ValidateServerIp(string ip)
    {
        if (ip.Length > 21)
        {
            return "IP address too long";
        }
        if (ip.Contains('\0'))
        {
            return "IP contains invalid characters";
        }

        var match = IpRegex().Match(ip);
        if (!match.Success)
        {
            return "Invalid IP format";
        }

        for (var i = 1; i <= 4; i++)
        {
            if (!int.TryParse(match.Groups[i].Value, out var octet) || octet > 255)
            {
                return $"IP octet {match.Groups[i].Value} out of range";
            }
        }

        if (match.Groups[5].Success)
        {
            var portStr = match.Groups[5].Value[1..]; // strip leading ':'
            if (!int.TryParse(portStr, out var port) || port == 0 || port > 65535)
            {
                return $"Port {portStr} out of range";
            }
        }

        return null;
    }
}
