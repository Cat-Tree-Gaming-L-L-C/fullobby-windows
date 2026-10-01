using System.Reflection;
using System.Text.Json;
using Fullobby.Core.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fullobby.Core.Update;

/// <summary>An accepted update, downloaded and verified, waiting for the next start to install it.
/// <see cref="Attempts"/> counts installer launches, so an installer that keeps failing is given up
/// on rather than relaunched on every start.</summary>
public sealed record StagedUpdate(string Version, string Sha256, string Signature, string Path, int Attempts = 0);

/// <summary>What the next start should do with the staged update.</summary>
public enum StagedUpdateAction
{
    /// <summary>Nothing is staged.</summary>
    None,
    /// <summary>Run the staged installer.</summary>
    Install,
    /// <summary>Drop it: already installed, given up on, missing, or not trustworthy.</summary>
    Discard,
}

/// <summary>
/// Checks the update feed in the background and holds an accepted update until the next start.
///
/// <para><b>Why this exists.</b> The only update check used to be the Settings button, and this app
/// lives in the tray and starts with Windows, so nobody presses it: clients sat on months-old
/// builds and silently missed whole features. Now the feed is checked shortly after start and every
/// few hours, and a newer version raises <see cref="UpdateAvailable"/> for the app to ask the user.
/// Nothing installs without a yes.</para>
///
/// <para><b>Install on next start, not now.</b> Accepting downloads and verifies the installer
/// straight away (<see cref="StageAsync"/>) but runs it only when the app next starts
/// (<see cref="TryInstallStaged"/>), so an update never cuts into a seed in progress. The start
/// re-checks the signature and the bytes on disk before launching, because the file has sat on disk
/// in between.</para>
/// </summary>
public sealed class AutoUpdateService : IHostedService, IDisposable
{
    /// <summary>Delay before the first check, so it stays out of the way of startup.</summary>
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(2);

    /// <summary>Time between checks. Releases are rare; this is often enough to reach an always-on
    /// tray app the same day.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    /// <summary>Installer launches before a staged update is given up on. A launch that works ends
    /// in a newer running version, which discards the staged update by itself.</summary>
    public const int MaxInstallAttempts = 2;

    private readonly ILogger<AutoUpdateService> _log;
    private readonly UpdaterService _updater;
    private readonly ConfigService _config;
    private readonly SemaphoreSlim _stageGate = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile UpdateInfo? _offered;

    /// <summary>Raised (off the UI thread) the first time this run that a check finds a version that
    /// isn't staged or declined. The app asks the user, then calls <see cref="StageAsync"/> or
    /// <see cref="Decline"/>.</summary>
    public event Action<UpdateInfo>? UpdateAvailable;

    public AutoUpdateService(ILogger<AutoUpdateService> log, UpdaterService updater, ConfigService config)
    {
        _log = log;
        _updater = updater;
        _config = config;
    }

    /// <summary>The running version, as compared against the feed.</summary>
    public static string CurrentVersion =>
        Assembly.GetEntryAssembly() is { } entry ? AppVersion.ForUpdateCheck(entry) : "0.0.0";

    /// <summary>The update most recently put to the user, if any.</summary>
    public UpdateInfo? Offered => _offered;

    /// <summary>The update waiting for the next start, if any.</summary>
    public StagedUpdate? Staged
    {
        get
        {
            var json = _config.GetString(ConfigKeys.StagedUpdate);
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }
            try
            {
                return JsonSerializer.Deserialize<StagedUpdate>(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { /* expected */ }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _stageGate.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var delay = FirstCheckDelay;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            delay = CheckInterval;

            try
            {
                await CheckOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                // Offline or the feed is down: the next tick retries.
                _log.LogInformation(e, "Background update check failed");
            }
        }
    }

    private async Task CheckOnceAsync(CancellationToken ct)
    {
        var info = await _updater.CheckForUpdatesAsync(
            CurrentVersion, _config.GetString("update_channel"), ct).ConfigureAwait(false);
        if (info is null
            || !ShouldOffer(info.Version, Staged?.Version, _config.GetString(ConfigKeys.DeclinedUpdateVersion),
                _offered?.Version))
        {
            return;
        }
        _offered = info;
        UpdateAvailable?.Invoke(info);
    }

    /// <summary>Whether to ask the user about <paramref name="version"/>: not when it is already
    /// staged, was declined, or was already asked about this run. Pure; public for unit coverage.</summary>
    public static bool ShouldOffer(string version, string? staged, string? declined, string? offeredThisRun) =>
        version != staged && version != declined && version != offeredThisRun;

    /// <summary>Remember "Not now" for the offered version, so it isn't asked about again.</summary>
    public void Decline()
    {
        if (_offered is { } info)
        {
            _config.SetString(ConfigKeys.DeclinedUpdateVersion, info.Version);
            _log.LogInformation("Update v{Version} declined", info.Version);
        }
    }

    /// <summary>Download and verify the offered update and keep it for the next start. Throws on a
    /// download or verification failure; nothing is staged then. With nothing offered yet this run (a
    /// prompt answered from Action Center after a restart) the feed is asked again.</summary>
    public async Task StageAsync(CancellationToken ct = default)
    {
        var info = _offered
            ?? await _updater.CheckForUpdatesAsync(CurrentVersion, _config.GetString("update_channel"), ct)
                .ConfigureAwait(false)
            ?? throw new InvalidOperationException("No update is available");
        _offered = info;
        await _stageGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Staged?.Version == info.Version)
            {
                return;
            }
            ClearStaged();
            var path = await _updater.DownloadAndVerifyAsync(info, ct, Branding.StagedUpdatesDir)
                .ConfigureAwait(false);
            // DownloadAndVerifyAsync throws without a checksum and a valid signature, so both are set.
            var staged = new StagedUpdate(info.Version, info.Sha256!, info.Signature!, path);
            _config.SetString(ConfigKeys.StagedUpdate, JsonSerializer.Serialize(staged));
            _config.FlushPendingSaves();
            _log.LogInformation("Update v{Version} staged; it installs at the next start", info.Version);
        }
        finally
        {
            _stageGate.Release();
        }
    }

    /// <summary>
    /// At startup: run the staged installer if there is one worth running. True means the installer
    /// is running and the caller must exit at once so it can replace the exe; it relaunches the app
    /// with <paramref name="relaunchArgs"/> when it finishes. False means carry on starting normally.
    /// </summary>
    public bool TryInstallStaged(string? relaunchArgs)
    {
        var staged = Staged;
        var action = Decide(staged, CurrentVersion, Branding.StagedUpdatesDir, File.Exists);
        if (action == StagedUpdateAction.None)
        {
            return false;
        }
        if (action == StagedUpdateAction.Discard)
        {
            _log.LogInformation("Dropping staged update v{Version}", staged!.Version);
            ClearStaged();
            return false;
        }

        // Count the attempt before launching: if the installer dies before relaunching us, the next
        // start must see it, or a broken installer would run on every start forever.
        _config.SetString(ConfigKeys.StagedUpdate,
            JsonSerializer.Serialize(staged! with { Attempts = staged.Attempts + 1 }));
        _config.FlushPendingSaves();

        try
        {
            List<string> args = ["/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/RELAUNCH=1"];
            if (!string.IsNullOrEmpty(relaunchArgs))
            {
                args.Add($"/RELAUNCHARGS={relaunchArgs}");
            }
            _log.LogInformation("Installing staged update v{Version} (attempt {Attempt})",
                staged.Version, staged.Attempts + 1);
            _updater.LaunchInstaller(staged.Path, staged.Sha256, args);
            return true;
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Staged update v{Version} could not be installed; dropping it", staged.Version);
            ClearStaged();
            return false;
        }
    }

    /// <summary>
    /// The startup decision for a staged update. Install only when it is still newer than the
    /// running version (a successful install makes it equal, which retires it), has attempts left,
    /// sits inside <paramref name="stagingRoot"/> (the path comes from a user-writable config
    /// file), exists, and still carries a valid release signature. The installer bytes are checked
    /// against that signed hash when launched. Pure; public for unit coverage.
    /// </summary>
    public static StagedUpdateAction Decide(
        StagedUpdate? staged, string currentVersion, string stagingRoot, Func<string, bool> fileExists)
    {
        if (staged is null)
        {
            return StagedUpdateAction.None;
        }
        if (!UpdateValidation.IsUpdateAvailable(currentVersion, staged.Version)
            || staged.Attempts >= MaxInstallAttempts
            || !IsUnder(staged.Path, stagingRoot)
            || !fileExists(staged.Path)
            || UpdateSignature.VerifySignature(staged.Version, staged.Sha256, staged.Signature) is not null)
        {
            return StagedUpdateAction.Discard;
        }
        return StagedUpdateAction.Install;
    }

    private static bool IsUnder(string path, string root)
    {
        try
        {
            var full = System.IO.Path.GetFullPath(path);
            var rootFull = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root))
                + System.IO.Path.DirectorySeparatorChar;
            return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Forget the staged update and delete any downloaded installers.</summary>
    private void ClearStaged()
    {
        _config.Remove(ConfigKeys.StagedUpdate);
        _config.FlushPendingSaves();
        try
        {
            if (Directory.Exists(Branding.StagedUpdatesDir))
            {
                Directory.Delete(Branding.StagedUpdatesDir, recursive: true);
            }
        }
        catch (Exception e)
        {
            _log.LogDebug(e, "Couldn't delete staged installers");
        }
    }
}
