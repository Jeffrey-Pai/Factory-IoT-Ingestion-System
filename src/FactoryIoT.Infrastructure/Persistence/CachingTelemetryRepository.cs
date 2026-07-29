using FactoryIoT.Application.Common.Interfaces;
using FactoryIoT.Domain.Analytics;
using FactoryIoT.Domain.Entities;
using FactoryIoT.Domain.Interfaces;
using FactoryIoT.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace FactoryIoT.Infrastructure.Persistence;

/// <summary>
/// A cache-aside decorator over <see cref="ITelemetryRepository"/>. It intercepts the read methods,
/// serving each from <see cref="IAnalyticsCache"/> for a short TTL, and passes the write method
/// straight through untouched. The endpoints, the ingestion worker and everything else keep
/// depending on the plain <see cref="ITelemetryRepository"/> and never learn a cache exists.
/// </summary>
/// <remarks>
/// <para>
/// Why decorate rather than cache inside the endpoints: it keeps the caching in one place, keeps
/// <c>Program.cs</c> free of cache plumbing, and — because this type is only wired in when a
/// distributed cache is actually configured — leaves the no-cache path (a local <c>dotnet run</c>)
/// running the bare repository with zero overhead.
/// </para>
/// <para>
/// <b>Why writes are never cached and never invalidate.</b> <see cref="AddRangeAsync"/> is the
/// ingestion hot path; wrapping it would add a dependency and a failure mode to the one path that
/// must stay fast, for no benefit. Freshness on the read side is bounded by the TTL instead of by
/// invalidation, which suits data that is already eventually consistent — a new batch becomes
/// visible within a second or two of a bucket's TTL lapsing, the same order of magnitude the roster
/// and rollup tiers are already stale by. There is nothing to invalidate and nothing to get wrong.
/// </para>
/// </remarks>
public sealed class CachingTelemetryRepository : ITelemetryRepository
{
    // Coarse metric labels / key namespaces — one per cached endpoint family. Never embed a machine
    // id or timestamp here; those go in the key, not the region.
    private const string RosterRegion = "roster";
    private const string FleetRegion = "fleet";
    private const string StatsRegion = "stats";
    private const string LatestRegion = "latest";

    private readonly ITelemetryRepository _inner;
    private readonly IAnalyticsCache _cache;
    private readonly AnalyticsCacheOptions _options;

    public CachingTelemetryRepository(
        ITelemetryRepository inner,
        IAnalyticsCache cache,
        IOptions<AnalyticsCacheOptions> options)
    {
        _inner = inner;
        _cache = cache;
        _options = options.Value;
    }

    /// <summary>Write path: straight through, uncached (see the type's remarks).</summary>
    public Task AddRangeAsync(IEnumerable<Telemetry> telemetries, CancellationToken cancellationToken = default)
        => _inner.AddRangeAsync(telemetries, cancellationToken);

    public Task<IReadOnlyList<Telemetry>> GetLatestByMachineAsync(string machineId, int count, CancellationToken cancellationToken = default)
        => _cache.GetOrCreateAsync(
            LatestRegion,
            $"{machineId}:{count}",
            TimeSpan.FromSeconds(_options.LatestTtlSeconds),
            ct => _inner.GetLatestByMachineAsync(machineId, count, ct),
            cancellationToken);

    public Task<IReadOnlyList<MachineTelemetrySummary>> GetMachineSummariesAsync(CancellationToken cancellationToken = default)
        => _cache.GetOrCreateAsync(
            RosterRegion,
            // The roster is fleet-wide and parameterless — a single key for the whole answer.
            "all",
            TimeSpan.FromSeconds(_options.RosterTtlSeconds),
            ct => _inner.GetMachineSummariesAsync(ct),
            cancellationToken);

    public Task<TelemetryStatistics?> GetStatisticsAsync(string machineId, DateTimeOffset from, CancellationToken cancellationToken = default)
        => _cache.GetOrCreateAsync<TelemetryStatistics?>(
            StatsRegion,
            $"{machineId}:{WindowMinutes(from)}",
            TimeSpan.FromSeconds(_options.StatsTtlSeconds),
            ct => _inner.GetStatisticsAsync(machineId, from, ct),
            cancellationToken);

    public Task<FleetStatus> GetFleetStatusAsync(DateTimeOffset from, CancellationToken cancellationToken = default)
        => _cache.GetOrCreateAsync(
            FleetRegion,
            WindowMinutes(from).ToString(),
            TimeSpan.FromSeconds(_options.FleetStatusTtlSeconds),
            ct => _inner.GetFleetStatusAsync(from, ct),
            cancellationToken);

    /// <summary>
    /// Collapses a moving <paramref name="from"/> instant back into the stable window width the
    /// caller asked for, in whole minutes — the actual cache key for the windowed reads.
    /// </summary>
    /// <remarks>
    /// The windowed endpoints ask their question as "the last N minutes" and hand the repository
    /// <c>UtcNow - N minutes</c>. Keyed on that raw instant the cache would never hit: it advances
    /// every tick, so every poll would be a unique key and a fresh database round-trip. Recovering N
    /// — <c>UtcNow - from</c>, which is <c>N</c> plus the microseconds since the endpoint computed it,
    /// rounded to the nearest minute — gives every caller asking for "the last hour" the same key,
    /// whenever they ask. The value they share was computed for whichever caller missed first, so it
    /// can be up to one TTL stale: seconds against a window measured in minutes, and well inside the
    /// staleness this pre-aggregated path already carries.
    ///
    /// This assumes the caller's <c>from</c> is <c>now - whole minutes</c>, which the analytics
    /// endpoints guarantee (they validate an integer <c>windowMinutes</c>). It is the only code path
    /// that reaches this decorator; the worker writes, and tests exercise the bare repository.
    /// </remarks>
    private static long WindowMinutes(DateTimeOffset from)
    {
        var minutes = (DateTimeOffset.UtcNow - from).TotalMinutes;
        return (long)Math.Max(0, Math.Round(minutes, MidpointRounding.AwayFromZero));
    }
}
