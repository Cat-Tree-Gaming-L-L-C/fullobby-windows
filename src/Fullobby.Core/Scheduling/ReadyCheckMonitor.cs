using Fullobby.Core.Api;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fullobby.Core.Scheduling;

/// <summary>
/// Watches for ready checks waiting on this user and raises <see cref="ChecksDue"/> once per check,
/// so the app can put a desktop toast in front of them.
///
/// <para><b>Why this exists.</b> A ready check blocks its server from becoming a seeding candidate
/// until an operator confirms, and the only thing that ever <i>told</i> an operator was the bot's
/// Discord DM at T-30min. That needs a Discord install, the bot in the right guild, and the
/// recipient's DMs open — miss any of those and the check is still answerable, but nobody knows to
/// answer it, and the server quietly loses its seed priority for the day. This app is the one piece
/// of software an operator is already running, which makes it the notification channel the platform
/// actually owns.</para>
///
/// <para>Polls <c>GET /api/seeding/ready-checks</c>, which lists only checks this caller may answer
/// and are still open — so an empty response genuinely means nothing is waiting, and a plain player
/// always gets one. Answering is not done from here: the toast brings the app forward and the
/// operator confirms on the Admin tab's hub.</para>
/// </summary>
public sealed class ReadyCheckMonitor : IHostedService, IDisposable
{
    /// <summary>A check is opened 30 minutes before its window, so two minutes is prompt enough to
    /// leave plenty of room to act while costing one small request per interval.</summary>
    public const int PollIntervalSecs = 120;

    private readonly ILogger<ReadyCheckMonitor> _log;
    private readonly SeedingApiClient _api;
    private readonly AuthSession _session;

    /// <summary>Checks already announced, so a check pending for its full half-hour produces one
    /// toast rather than fifteen. Ready-check ids are unique per server per seeding day and never
    /// reopen once settled, so this only ever needs to grow within a run and be pruned to what is
    /// still outstanding.</summary>
    private readonly HashSet<long> _announced = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>Raised (off the UI thread) with the checks seen for the first time this run,
    /// soonest window first. Never raised with an empty list.</summary>
    public event Action<IReadOnlyList<ReadyCheckNotice>>? ChecksDue;

    public ReadyCheckMonitor(ILogger<ReadyCheckMonitor> log, SeedingApiClient api, AuthSession session)
    {
        _log = log;
        _api = api;
        _session = session;
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

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(PollIntervalSecs), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await PollOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                // Never let a transient API failure kill the watchdog: the next tick retries.
                _log.LogDebug(e, "Ready-check poll failed");
            }
        }
    }

    /// <summary>One poll. Internal for tests.</summary>
    internal async Task PollOnceAsync(CancellationToken ct)
    {
        if (!_session.IsAuthenticated)
        {
            return; // guest/signed-out: no grants, so nothing could be waiting
        }

        var response = await _api.GetMyReadyChecksAsync(ct).ConfigureAwait(false);
        var fresh = SelectFresh(_announced, response.Checks);
        if (fresh.Count == 0)
        {
            return;
        }

        _log.LogInformation(
            "{Count} ready check(s) awaiting this operator: {Servers}",
            fresh.Count,
            string.Join(", ", fresh.Select(c => c.ServerName)));
        ChecksDue?.Invoke(fresh);
    }

    /// <summary>The checks in <paramref name="checks"/> not yet announced, in the order given;
    /// <paramref name="announced"/> is pruned to what is still outstanding and then extended with
    /// the fresh ids.
    ///
    /// <para>Pure and separate because this is the rule that decides between one toast and fifteen:
    /// a check sits open for its full half-hour and is returned by every poll in that time.</para>
    /// </summary>
    public static List<ReadyCheckNotice> SelectFresh(
        HashSet<long> announced, IReadOnlyList<ReadyCheckNotice> checks)
    {
        // Drop ids that have settled since the last poll, so the set tracks what is outstanding
        // rather than growing for the lifetime of the process.
        announced.IntersectWith(checks.Select(c => c.ReadyCheckId));
        return checks.Where(c => announced.Add(c.ReadyCheckId)).ToList();
    }

    public void Dispose() => _cts?.Dispose();
}
