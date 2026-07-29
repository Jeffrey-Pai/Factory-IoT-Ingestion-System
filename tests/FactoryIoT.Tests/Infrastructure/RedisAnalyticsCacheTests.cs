using FactoryIoT.Domain.Analytics;
using FactoryIoT.Infrastructure.Caching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FactoryIoT.Tests.Infrastructure;

/// <summary>
/// Tests for the distributed-cache implementation of the analytics cache, standing a fake
/// <see cref="IDistributedCache"/> in for Redis. These pin the two guarantees the read path leans
/// on: it fails open when the store misbehaves, and it caches negatives (the 404 case) rather than
/// recomputing them on every poll.
/// </summary>
public sealed class RedisAnalyticsCacheTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Computes_OnMiss_ThenServesFromCache()
    {
        var sut = new RedisAnalyticsCache(new InMemoryDistributedCache(), NullLogger<RedisAnalyticsCache>.Instance);
        var calls = 0;
        Task<TelemetryStatistics?> Factory(CancellationToken _) { calls++; return Task.FromResult<TelemetryStatistics?>(Sample("EQP-001")); }

        var first = await sut.GetOrCreateAsync("stats", "EQP-001:60", Ttl, Factory);
        var second = await sut.GetOrCreateAsync("stats", "EQP-001:60", Ttl, Factory);

        Assert.Equal(1, calls); // second call served from the store
        Assert.Equal("EQP-001", first!.MachineId);
        Assert.Equal("EQP-001", second!.MachineId);
    }

    [Fact]
    public async Task CachesNegativeResults_SoAQuietMachineIsNotRecomputedEveryPoll()
    {
        var sut = new RedisAnalyticsCache(new InMemoryDistributedCache(), NullLogger<RedisAnalyticsCache>.Instance);
        var calls = 0;
        Task<TelemetryStatistics?> NullFactory(CancellationToken _) { calls++; return Task.FromResult<TelemetryStatistics?>(null); }

        var first = await sut.GetOrCreateAsync("stats", "EQP-404:60", Ttl, NullFactory);
        var second = await sut.GetOrCreateAsync("stats", "EQP-404:60", Ttl, NullFactory);

        // The stored `null` is a cached negative, distinct from a miss — so the factory runs once.
        Assert.Null(first);
        Assert.Null(second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FailsOpen_WhenTheStoreThrows()
    {
        var sut = new RedisAnalyticsCache(new ThrowingDistributedCache(), NullLogger<RedisAnalyticsCache>.Instance);
        var calls = 0;
        Task<TelemetryStatistics?> Factory(CancellationToken _) { calls++; return Task.FromResult<TelemetryStatistics?>(Sample("EQP-007")); }

        // A store that errors on every access must not surface as an exception: the read is served
        // from the factory (the database, in production) instead.
        var result = await sut.GetOrCreateAsync("stats", "EQP-007:60", Ttl, Factory);

        Assert.Equal("EQP-007", result!.MachineId);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NonPositiveTtl_BypassesTheStoreEntirely()
    {
        // A throwing store proves the bypass never touches it: if it did, this would throw.
        var sut = new RedisAnalyticsCache(new ThrowingDistributedCache(), NullLogger<RedisAnalyticsCache>.Instance);
        var calls = 0;
        Task<TelemetryStatistics?> Factory(CancellationToken _) { calls++; return Task.FromResult<TelemetryStatistics?>(Sample("EQP-001")); }

        await sut.GetOrCreateAsync("latest", "EQP-001:10", TimeSpan.Zero, Factory);
        await sut.GetOrCreateAsync("latest", "EQP-001:10", TimeSpan.Zero, Factory);

        Assert.Equal(2, calls); // bypassed both times, no caching
    }

    private static TelemetryStatistics Sample(string machineId) => new(
        machineId, SampleCount: 42,
        FirstReading: DateTimeOffset.UnixEpoch, LastReading: DateTimeOffset.UnixEpoch.AddMinutes(1),
        MinTemperature: 20, MaxTemperature: 80, AvgTemperature: 50,
        MinPressure: 1, MaxPressure: 9, AvgPressure: 5);

    /// <summary>A dictionary-backed <see cref="IDistributedCache"/>; expiry is irrelevant here.</summary>
    private sealed class InMemoryDistributedCache : IDistributedCache
    {
        private readonly Dictionary<string, byte[]> _store = new(StringComparer.Ordinal);

        public byte[]? Get(string key) => _store.TryGetValue(key, out var v) ? v : null;
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => _store[key] = value;
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }
        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key) => _store.Remove(key);
        public Task RemoveAsync(string key, CancellationToken token = default) { Remove(key); return Task.CompletedTask; }
    }

    /// <summary>An <see cref="IDistributedCache"/> that fails every access — a stand-in for a dead Redis.</summary>
    private sealed class ThrowingDistributedCache : IDistributedCache
    {
        private static InvalidOperationException Boom() => new("cache is down");

        public byte[]? Get(string key) => throw Boom();
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => throw Boom();
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw Boom();
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => throw Boom();
        public void Refresh(string key) => throw Boom();
        public Task RefreshAsync(string key, CancellationToken token = default) => throw Boom();
        public void Remove(string key) => throw Boom();
        public Task RemoveAsync(string key, CancellationToken token = default) => throw Boom();
    }
}
