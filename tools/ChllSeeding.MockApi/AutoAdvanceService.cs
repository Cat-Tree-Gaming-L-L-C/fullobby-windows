namespace ChllSeeding.MockApi;

/// <summary>When auto-advance is enabled, ticks the mock state on an interval so seeding candidates
/// fill up over time — letting a live seed naturally cross threshold and the client switch/rotate.</summary>
public sealed class AutoAdvanceService(MockState state, ILogger<AutoAdvanceService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, state.AutoAdvanceIntervalSecs)), ct);
            }
            catch (OperationCanceledException) { break; }

            // Only fill servers while a seed is actually in progress, so the list stays seedable
            // until the user clicks Seed (otherwise every server fills before they can start).
            if (state.AutoAdvanceEnabled && state.HasActiveSession)
            {
                state.Tick();
                log.LogDebug("Auto-advance tick");
            }
        }
    }
}
