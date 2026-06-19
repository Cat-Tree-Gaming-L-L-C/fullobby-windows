namespace ChllSeeding.Core.Api;

/// <summary>
/// SSE-derived seeding-status cache: written by the SSE loop (Phase 2), read by
/// the seeding monitor loop. Returns the cached value only while fresh, so a
/// stale or missing entry transparently falls back to HTTP polling. Port of the
/// cache in <c>src-rust/src/backend/api_client.rs</c>. DI singleton, thread-safe.
/// </summary>
public sealed class SeedingStatusCache
{
    /// <summary>Cache is considered stale after this long without an SSE update.</summary>
    private const long StaleSecs = 90;

    private readonly object _gate = new();
    private SeedingStatusResponse? _cached;
    private long _updatedAtUnix;

    /// <summary>Store a fresh status (from an SSE event).</summary>
    public void Update(SeedingStatusResponse status)
    {
        lock (_gate)
        {
            _cached = status;
            _updatedAtUnix = UnixNow();
        }
    }

    /// <summary>Mark the cache stale so the next read falls back to HTTP. Called on
    /// SSE reconnect to catch events lost during the disconnect.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _updatedAtUnix = 0;
        }
    }

    /// <summary>The cached status if present and fresh; otherwise null.</summary>
    public SeedingStatusResponse? GetCached()
    {
        lock (_gate)
        {
            if (_updatedAtUnix == 0 || UnixNow() - _updatedAtUnix > StaleSecs)
            {
                return null;
            }
            return _cached;
        }
    }

    private static long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
