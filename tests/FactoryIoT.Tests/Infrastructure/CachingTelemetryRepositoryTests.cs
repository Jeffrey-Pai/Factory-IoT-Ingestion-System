using FactoryIoT.Application.Common.Interfaces;
using FactoryIoT.Domain.Analytics;
using FactoryIoT.Domain.Entities;
using FactoryIoT.Domain.Interfaces;
using FactoryIoT.Infrastructure.Configuration;
using FactoryIoT.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using Xunit;

namespace FactoryIoT.Tests.Infrastructure;

/// <summary>
/// Behavioural tests for the cache-aside decorator: reads collapse onto the inner repository once
/// per key, distinct parameters are distinct keys, the write path is untouched, and a zero TTL
/// opts a family out. The cache itself is a faithful in-memory stand-in so the decorator's key and
/// pass-through logic can be asserted without a Redis or a clock.
/// </summary>
public sealed class CachingTelemetryRepositoryTests
{
    [Fact]
    public async Task Roster_IsServedFromCacheOnTheSecondCall()
    {
        var (sut, inner, _) = Build();

        await sut.GetMachineSummariesAsync();
        await sut.GetMachineSummariesAsync();

        // Two polls, one database read: the whole point of the roster cache.
        Assert.Equal(1, inner.RosterCalls);
    }

    [Fact]
    public async Task Latest_IsKeyedByMachineAndCount()
    {
        var (sut, inner, _) = Build();

        await sut.GetLatestByMachineAsync("EQP-001", 10);
        await sut.GetLatestByMachineAsync("EQP-001", 10); // same key → hit
        await sut.GetLatestByMachineAsync("EQP-001", 20); // different count → miss
        await sut.GetLatestByMachineAsync("EQP-002", 10); // different machine → miss

        Assert.Equal(3, inner.LatestCalls);
    }

    [Fact]
    public async Task Stats_ShareAKeyForTheSameWindowButNotAcrossWindows()
    {
        var (sut, inner, _) = Build();
        var lastHour = DateTimeOffset.UtcNow.AddMinutes(-60);
        var lastQuarter = DateTimeOffset.UtcNow.AddMinutes(-15);

        await sut.GetStatisticsAsync("EQP-001", lastHour);
        await sut.GetStatisticsAsync("EQP-001", lastHour);    // same window width → hit
        await sut.GetStatisticsAsync("EQP-001", lastQuarter); // different window → miss

        Assert.Equal(2, inner.StatsCalls);
    }

    [Fact]
    public async Task FleetStatus_ShareAKeyForTheSameWindow()
    {
        var (sut, inner, _) = Build();
        var lastHour = DateTimeOffset.UtcNow.AddMinutes(-60);

        await sut.GetFleetStatusAsync(lastHour);
        await sut.GetFleetStatusAsync(lastHour);

        Assert.Equal(1, inner.FleetCalls);
    }

    [Fact]
    public async Task Writes_PassStraightThroughAndAreNotCached()
    {
        var (sut, inner, cache) = Build();

        await sut.AddRangeAsync(new[] { new Telemetry { MachineId = "EQP-001" } });

        Assert.Equal(1, inner.WriteCalls);
        Assert.Equal(0, cache.Stores); // the write never touched the cache
    }

    [Fact]
    public async Task ZeroTtl_TurnsOffCachingForThatFamilyOnly()
    {
        // Latest opted out, roster left on.
        var (sut, inner, _) = Build(o =>
        {
            o.LatestTtlSeconds = 0;
            o.RosterTtlSeconds = 2;
        });

        await sut.GetLatestByMachineAsync("EQP-001", 10);
        await sut.GetLatestByMachineAsync("EQP-001", 10);
        await sut.GetMachineSummariesAsync();
        await sut.GetMachineSummariesAsync();

        Assert.Equal(2, inner.LatestCalls); // bypassed → every call reaches the source
        Assert.Equal(1, inner.RosterCalls); // still cached
    }

    private static (CachingTelemetryRepository Sut, CountingRepository Inner, FakeAnalyticsCache Cache) Build(
        Action<AnalyticsCacheOptions>? configure = null)
    {
        var options = new AnalyticsCacheOptions();
        configure?.Invoke(options);
        var inner = new CountingRepository();
        var cache = new FakeAnalyticsCache();
        var sut = new CachingTelemetryRepository(inner, cache, Options.Create(options));
        return (sut, inner, cache);
    }

    /// <summary>
    /// A faithful get-or-create cache: a positive TTL caches forever for the test's lifetime (so a
    /// second call is a guaranteed hit), a non-positive TTL bypasses. Time is irrelevant here — what
    /// is under test is the decorator's keying, not expiry.
    /// </summary>
    private sealed class FakeAnalyticsCache : IAnalyticsCache
    {
        private readonly Dictionary<string, object?> _store = new(StringComparer.Ordinal);
        public int Stores { get; private set; }

        public async Task<T> GetOrCreateAsync<T>(
            string region, string key, TimeSpan ttl,
            Func<CancellationToken, Task<T>> factory, CancellationToken cancellationToken = default)
        {
            if (ttl <= TimeSpan.Zero)
            {
                return await factory(cancellationToken);
            }

            var full = $"{region}:{key}";
            if (_store.TryGetValue(full, out var hit))
            {
                return (T)hit!;
            }

            var value = await factory(cancellationToken);
            _store[full] = value;
            Stores++;
            return value;
        }
    }

    /// <summary>Inner repository that counts calls per method and returns empty results.</summary>
    private sealed class CountingRepository : ITelemetryRepository
    {
        public int RosterCalls;
        public int LatestCalls;
        public int StatsCalls;
        public int FleetCalls;
        public int WriteCalls;

        public Task AddRangeAsync(IEnumerable<Telemetry> telemetries, CancellationToken cancellationToken = default)
        {
            WriteCalls++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Telemetry>> GetLatestByMachineAsync(string machineId, int count, CancellationToken cancellationToken = default)
        {
            LatestCalls++;
            return Task.FromResult<IReadOnlyList<Telemetry>>(new List<Telemetry>());
        }

        public Task<IReadOnlyList<MachineTelemetrySummary>> GetMachineSummariesAsync(CancellationToken cancellationToken = default)
        {
            RosterCalls++;
            return Task.FromResult<IReadOnlyList<MachineTelemetrySummary>>(new List<MachineTelemetrySummary>());
        }

        public Task<TelemetryStatistics?> GetStatisticsAsync(string machineId, DateTimeOffset from, CancellationToken cancellationToken = default)
        {
            StatsCalls++;
            return Task.FromResult<TelemetryStatistics?>(null);
        }

        public Task<FleetStatus> GetFleetStatusAsync(DateTimeOffset from, CancellationToken cancellationToken = default)
        {
            FleetCalls++;
            return Task.FromResult(new FleetStatus(0, 0, new List<StatusBreakdown>()));
        }
    }
}
