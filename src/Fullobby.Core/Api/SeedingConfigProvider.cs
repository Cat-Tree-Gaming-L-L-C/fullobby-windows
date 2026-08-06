namespace Fullobby.Core.Api;

/// <summary>
/// Holds the latest admin-editable <see cref="SeedingConfig"/>, swapped atomically. Starts at the
/// baked-in defaults (which match the server's) so the client still works before the first
/// successful fetch and whenever the API is unreachable. Updated from each directive and from the
/// bootstrapper's periodic config fetch; read by the engine and the peripheral services
/// (heartbeat, SSE, poll, status cache, missed-autoseed). DI singleton, thread-safe.
/// </summary>
public sealed class SeedingConfigProvider
{
    // Reference assignment is atomic; SeedingConfig is treated as immutable once published.
    private volatile SeedingConfig _current = new();

    /// <summary>The current config (never null).</summary>
    public SeedingConfig Current => _current;

    /// <summary>Publish a newly fetched config.</summary>
    public void Update(SeedingConfig config) => _current = config;
}
