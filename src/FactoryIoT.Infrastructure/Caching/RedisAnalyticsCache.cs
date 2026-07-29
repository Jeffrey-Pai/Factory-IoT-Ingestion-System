using System.Text.Json;
using FactoryIoT.Application.Common.Interfaces;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Prometheus;

namespace FactoryIoT.Infrastructure.Caching;

/// <summary>
/// <see cref="IAnalyticsCache"/> backed by a distributed cache (Redis in the shipped stack, via
/// <see cref="IDistributedCache"/>). Values are stored as UTF-8 JSON.
/// </summary>
/// <remarks>
/// <para>
/// This class exists to satisfy the two guarantees the port promises, and both are load-bearing:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <b>It fails open.</b> Every access to the store is wrapped so that an unreachable or erroring
/// Redis degrades a read to "recompute from the database", never to an exception. The read path
/// treats the cache as pure upside — a Redis outage makes the dashboard slower and hits SQL Server
/// harder, which is exactly what would happen if the cache had never been added. Cancellation is
/// the one exception that is allowed to propagate: an aborted request is not a cache fault.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>It caches negatives.</b> A key that is absent is a miss; a key whose stored bytes decode to
/// <c>null</c> is a cached negative and is returned as such. That distinction is what lets the 404
/// case (a machine that reported nothing in the window) be served from cache for its TTL instead of
/// re-running the aggregate on every poll for a quiet machine.
/// </description>
/// </item>
/// </list>
/// <para>
/// The <c>factoryiot:</c> key prefix is applied by the <see cref="IDistributedCache"/> registration
/// (StackExchange.Redis's <c>InstanceName</c>), so the keys built here are just
/// <c>{region}:{key}</c>; in Redis they appear as <c>factoryiot:{region}:{key}</c>.
/// </para>
/// </remarks>
public sealed class RedisAnalyticsCache : IAnalyticsCache
{
    // region: the coarse family (roster/fleet/stats/latest). outcome: hit | miss | error.
    // Twelve series total — a hit ratio in Grafana is
    //   sum(rate(analytics_cache_requests_total{outcome="hit"}[5m]))
    //     / sum(rate(analytics_cache_requests_total[5m]))
    private static readonly Counter CacheRequests = Metrics.CreateCounter(
        "analytics_cache_requests_total",
        "Analytics read-cache lookups, by region and outcome (hit/miss/error).",
        "region", "outcome");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDistributedCache _cache;
    private readonly ILogger<RedisAnalyticsCache> _logger;

    public RedisAnalyticsCache(IDistributedCache cache, ILogger<RedisAnalyticsCache> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task<T> GetOrCreateAsync<T>(
        string region,
        string key,
        TimeSpan ttl,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken = default)
    {
        // A non-positive TTL means this region is switched off from configuration: go straight to
        // the source, touch neither the store nor the metric. This is how a single family (e.g. the
        // live "latest" endpoint) is left uncached while the rest stay on.
        if (ttl <= TimeSpan.Zero)
        {
            return await factory(cancellationToken);
        }

        var fullKey = $"{region}:{key}";

        // ── Read side ───────────────────────────────────────────────────────────────────────────
        byte[]? bytes;
        try
        {
            bytes = await _cache.GetAsync(fullKey, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The store is unreachable. Don't attempt a write-back against the same dead store —
            // just recompute and return. This is the fail-open path in its purest form.
            CacheRequests.WithLabels(region, "error").Inc();
            LogStoreFault(ex, "read", region);
            return await factory(cancellationToken);
        }

        if (bytes is not null)
        {
            try
            {
                // A stored `null` payload decodes to default(T): a cached negative, returned as-is.
                var cached = JsonSerializer.Deserialize<T>(bytes, JsonOptions);
                CacheRequests.WithLabels(region, "hit").Inc();
                return cached!;
            }
            catch (JsonException ex)
            {
                // A poisoned or shape-incompatible entry (e.g. a read model changed shape across a
                // deploy). Treat it as absent, recompute below, and overwrite it.
                CacheRequests.WithLabels(region, "error").Inc();
                _logger.LogWarning(ex,
                    "Discarding an undecodable analytics cache entry for {Region}; recomputing.", region);
            }
        }
        else
        {
            CacheRequests.WithLabels(region, "miss").Inc();
        }

        // ── Compute + write-back ─────────────────────────────────────────────────────────────────
        var value = await factory(cancellationToken);

        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
            await _cache.SetAsync(
                fullKey,
                payload,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Best-effort write-back: the caller already holds its answer. A store that can't take
            // the value just means the next caller recomputes too — never a reason to fail this one.
            LogStoreFault(ex, "write", region);
        }

        return value;
    }

    /// <summary>
    /// Logs a store fault at Warning, but at most occasionally: a Redis outage would otherwise emit
    /// one line per request (hundreds a second under the dashboard + k6), drowning the log. The
    /// metric <c>analytics_cache_requests_total{outcome="error"}</c> is the complete signal; the log
    /// is only there to carry the exception detail when someone goes looking.
    /// </summary>
    private void LogStoreFault(Exception ex, string operation, string region)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(ex, "Analytics cache {Operation} failed for {Region}; serving from source.", operation, region);
            return;
        }

        // Coarse throttle without extra state: log the full detail roughly once a minute, keyed on
        // the wall-clock minute. Enough to notice and diagnose, not enough to flood.
        if (DateTimeOffset.UtcNow.Second == 0)
        {
            _logger.LogWarning(ex, "Analytics cache {Operation} failing for {Region}; serving from source.", operation, region);
        }
    }
}
