using FactoryIoT.Domain.Entities;
using FactoryIoT.Infrastructure.Configuration;
using FactoryIoT.Infrastructure.Persistence;
using Xunit;

namespace FactoryIoT.Tests.Infrastructure;

/// <summary>
/// Exercises the analytics queries on <see cref="TelemetryRepository"/> against the EF Core
/// in-memory provider, so the real LINQ runs without needing a live SQL Server.
/// </summary>
/// <remarks>
/// These cover both halves of the read path: the short-window queries that still read raw rows,
/// and the long-window queries that must be answered from pre-aggregated buckets instead. The
/// routing tests deliberately seed raw rows that would give a *different* answer, so a query that
/// quietly fell back to scanning them would fail rather than pass by coincidence.
/// </remarks>
public sealed class TelemetryRepositoryTests
{
    private static TelemetryRepository CreateRepository(
        FactoryIoTDbContext context,
        Action<DataRetentionOptions>? configure = null)
        => new(context, TestDatabase.Retention(configure));

    private static Telemetry Reading(string machineId, double temperature, double pressure, string status, DateTimeOffset timestamp) =>
        new()
        {
            MachineId = machineId,
            Temperature = temperature,
            Pressure = pressure,
            Status = status,
            Timestamp = timestamp,
        };

    private static TelemetryRollup Bucket(
        string machineId,
        DateTimeOffset bucketStart,
        int sampleCount,
        double sumTemperature,
        double minTemperature,
        double maxTemperature,
        RollupGranularity granularity = RollupGranularity.Minute) =>
        new()
        {
            Granularity = granularity,
            MachineId = machineId,
            BucketStart = bucketStart,
            SampleCount = sampleCount,
            FirstReading = bucketStart,
            LastReading = bucketStart.AddSeconds(59),
            MinTemperature = minTemperature,
            MaxTemperature = maxTemperature,
            SumTemperature = sumTemperature,
            MinPressure = 1,
            MaxPressure = 9,
            SumPressure = sampleCount * 5d,
        };

    // ── Raw tier ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetLatestByMachineAsync_ReturnsNewestFirstAndHonoursCount()
    {
        using var context = TestDatabase.CreateContext();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            context.Telemetries.Add(Reading("EQP-001", i, i, "Running", now.AddMinutes(-i)));
        }
        await context.SaveChangesAsync();
        var repository = CreateRepository(context);

        var latest = await repository.GetLatestByMachineAsync("EQP-001", 3);

        Assert.Equal(3, latest.Count);
        Assert.Equal(0d, latest[0].Temperature); // i == 0 is the most recent (now - 0)
        Assert.Equal(1d, latest[1].Temperature);
        Assert.Equal(2d, latest[2].Temperature);
    }

    [Fact]
    public async Task GetStatisticsAsync_AggregatesOnlyReadingsInsideWindowForThatMachine()
    {
        using var context = TestDatabase.CreateContext();
        var now = DateTimeOffset.UtcNow;
        context.Telemetries.AddRange(
            Reading("EQP-001", 40, 5, "Running", now.AddMinutes(-5)),
            Reading("EQP-001", 60, 7, "Warning", now.AddMinutes(-1)),
            Reading("EQP-001", 999, 999, "Running", now.AddMinutes(-30)), // outside the window
            Reading("EQP-002", 1, 1, "Running", now.AddMinutes(-1)));      // different machine
        await context.SaveChangesAsync();
        var repository = CreateRepository(context);

        var stats = await repository.GetStatisticsAsync("EQP-001", now.AddMinutes(-10));

        Assert.NotNull(stats);
        Assert.Equal("EQP-001", stats!.MachineId);
        Assert.Equal(2L, stats.SampleCount);
        Assert.Equal(40d, stats.MinTemperature, 3);
        Assert.Equal(60d, stats.MaxTemperature, 3);
        Assert.Equal(50d, stats.AvgTemperature, 3);
        Assert.Equal(5d, stats.MinPressure, 3);
        Assert.Equal(7d, stats.MaxPressure, 3);
        Assert.Equal(now.AddMinutes(-5), stats.FirstReading);
        Assert.Equal(now.AddMinutes(-1), stats.LastReading);
    }

    [Fact]
    public async Task GetStatisticsAsync_NoReadingsInsideWindow_ReturnsNull()
    {
        using var context = TestDatabase.CreateContext();
        var now = DateTimeOffset.UtcNow;
        context.Telemetries.Add(Reading("EQP-001", 40, 5, "Running", now.AddHours(-2)));
        await context.SaveChangesAsync();
        var repository = CreateRepository(context);

        var stats = await repository.GetStatisticsAsync("EQP-001", now.AddMinutes(-10));

        Assert.Null(stats);
    }

    [Fact]
    public async Task GetFleetStatusAsync_CountsMachinesAndBreaksDownByStatusDescending()
    {
        using var context = TestDatabase.CreateContext();
        var now = DateTimeOffset.UtcNow;
        context.Telemetries.AddRange(
            Reading("EQP-001", 1, 1, "Running", now.AddMinutes(-1)),
            Reading("EQP-001", 1, 1, "Running", now.AddMinutes(-2)),
            Reading("EQP-002", 1, 1, "Warning", now.AddMinutes(-1)),
            Reading("EQP-003", 1, 1, "Running", now.AddMinutes(-30))); // outside the window
        await context.SaveChangesAsync();
        var repository = CreateRepository(context);

        var status = await repository.GetFleetStatusAsync(now.AddMinutes(-10));

        Assert.Equal(2, status.MachineCount);   // EQP-001 and EQP-002 (EQP-003 is outside the window)
        Assert.Equal(3L, status.TotalReadings);
        Assert.Equal(2, status.Breakdown.Count);
        Assert.Equal("Running", status.Breakdown[0].Status); // most frequent first
        Assert.Equal(2L, status.Breakdown[0].Count);
        Assert.Equal("Warning", status.Breakdown[1].Status);
        Assert.Equal(1L, status.Breakdown[1].Count);
    }

    // ── Roster tier ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetMachineSummariesAsync_ReadsTheMaintainedRosterAndOrdersById()
    {
        using var context = TestDatabase.CreateContext();
        var now = DateTimeOffset.UtcNow;
        context.MachineSummaries.AddRange(
            new MachineSummary
            {
                MachineId = "EQP-002",
                SampleCount = 1,
                FirstSeen = now.AddMinutes(-3),
                LastSeen = now.AddMinutes(-3),
                MinTemperature = 30, MaxTemperature = 30, SumTemperature = 30,
                MinPressure = 6, MaxPressure = 6, SumPressure = 6,
            },
            new MachineSummary
            {
                MachineId = "EQP-001",
                SampleCount = 2,
                FirstSeen = now.AddMinutes(-2),
                LastSeen = now.AddMinutes(-1),
                MinTemperature = 10, MaxTemperature = 20, SumTemperature = 30,
                MinPressure = 2, MaxPressure = 4, SumPressure = 6,
            });

        // Raw rows the roster does not derive from: the roster is maintained forward by the
        // lifecycle worker, never recomputed here, so these must not affect the answer.
        context.Telemetries.Add(Reading("EQP-009", 500, 500, "Running", now));
        await context.SaveChangesAsync();
        var repository = CreateRepository(context);

        var summaries = await repository.GetMachineSummariesAsync();

        Assert.Equal(2, summaries.Count);
        Assert.Equal("EQP-001", summaries[0].MachineId); // ordered by machine id
        Assert.Equal("EQP-002", summaries[1].MachineId);

        var eqp1 = summaries[0];
        Assert.Equal(2L, eqp1.SampleCount);
        Assert.Equal(10d, eqp1.MinTemperature, 3);
        Assert.Equal(20d, eqp1.MaxTemperature, 3);
        Assert.Equal(15d, eqp1.AvgTemperature, 3); // recovered as sum / count
        Assert.Equal(2d, eqp1.MinPressure, 3);
        Assert.Equal(4d, eqp1.MaxPressure, 3);
        Assert.Equal(3d, eqp1.AvgPressure, 3);
        Assert.Equal(now.AddMinutes(-2), eqp1.FirstSeen);
        Assert.Equal(now.AddMinutes(-1), eqp1.LastSeen);
    }

    [Fact]
    public async Task GetMachineSummariesAsync_ZeroSampleCount_ReportsZeroRatherThanDividingByZero()
    {
        using var context = TestDatabase.CreateContext();
        context.MachineSummaries.Add(new MachineSummary { MachineId = "EQP-001" });
        await context.SaveChangesAsync();
        var repository = CreateRepository(context);

        var summaries = await repository.GetMachineSummariesAsync();

        Assert.Equal(0d, summaries[0].AvgTemperature);
        Assert.Equal(0d, summaries[0].AvgPressure);
    }

    // ── Tier routing ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetStatisticsAsync_WindowWiderThanTheRawThreshold_ReadsMinuteBuckets()
    {
        using var context = TestDatabase.CreateContext();
        var now = DateTimeOffset.UtcNow;
        var bucket = RollupBucket.Floor(now.AddHours(-2), RollupGranularity.Minute);

        context.TelemetryRollups.AddRange(
            Bucket("EQP-001", bucket, sampleCount: 3, sumTemperature: 30, minTemperature: 5, maxTemperature: 20),
            Bucket("EQP-001", bucket.AddMinutes(1), sampleCount: 1, sumTemperature: 70, minTemperature: 70, maxTemperature: 70),
            Bucket("EQP-002", bucket, sampleCount: 9, sumTemperature: 900, minTemperature: 100, maxTemperature: 100));

        // A raw row that would skew every figure if the query scanned it instead.
        context.Telemetries.Add(Reading("EQP-001", 999, 999, "Running", now.AddHours(-2)));
        await context.SaveChangesAsync();
        var repository = CreateRepository(context, o => o.RawQueryWindowMinutes = 180);

        var stats = await repository.GetStatisticsAsync("EQP-001", now.AddHours(-4));

        Assert.NotNull(stats);
        Assert.Equal(4L, stats!.SampleCount);
        Assert.Equal(5d, stats.MinTemperature, 3);
        Assert.Equal(70d, stats.MaxTemperature, 3);
        // 100 / 4 — not the (10 + 70) / 2 that averaging the buckets' averages would give.
        Assert.Equal(25d, stats.AvgTemperature, 3);
    }

    [Fact]
    public async Task GetStatisticsAsync_WindowBeyondMinuteRetention_FallsBackToHourBuckets()
    {
        using var context = TestDatabase.CreateContext();
        var now = DateTimeOffset.UtcNow;
        var hour = RollupBucket.Floor(now.AddHours(-30), RollupGranularity.Hour);

        context.TelemetryRollups.AddRange(
            Bucket("EQP-001", hour, sampleCount: 2, sumTemperature: 60, minTemperature: 20, maxTemperature: 40,
                granularity: RollupGranularity.Hour),
            // Same machine, same window, but the finer tier — must be ignored once the window is
            // wider than how long minute buckets are kept.
            Bucket("EQP-001", RollupBucket.Floor(now.AddHours(-30), RollupGranularity.Minute),
                sampleCount: 500, sumTemperature: 500, minTemperature: 1, maxTemperature: 1));
        await context.SaveChangesAsync();
        var repository = CreateRepository(context, o =>
        {
            o.RawQueryWindowMinutes = 180;
            o.MinuteRollupHours = 24;
        });

        var stats = await repository.GetStatisticsAsync("EQP-001", now.AddHours(-48));

        Assert.NotNull(stats);
        Assert.Equal(2L, stats!.SampleCount);
        Assert.Equal(30d, stats.AvgTemperature, 3);
    }

    [Fact]
    public async Task GetFleetStatusAsync_WindowWiderThanTheRawThreshold_ReadsStatusBuckets()
    {
        using var context = TestDatabase.CreateContext();
        var now = DateTimeOffset.UtcNow;
        var bucket = RollupBucket.Floor(now.AddHours(-2), RollupGranularity.Minute);

        context.TelemetryRollups.AddRange(
            Bucket("EQP-001", bucket, sampleCount: 60, sumTemperature: 600, minTemperature: 10, maxTemperature: 10),
            Bucket("EQP-002", bucket, sampleCount: 40, sumTemperature: 400, minTemperature: 10, maxTemperature: 10));

        context.TelemetryStatusRollups.AddRange(
            new TelemetryStatusRollup { Granularity = RollupGranularity.Minute, BucketStart = bucket, MachineId = "EQP-001", Status = "Running", Count = 58 },
            new TelemetryStatusRollup { Granularity = RollupGranularity.Minute, BucketStart = bucket, MachineId = "EQP-001", Status = "Warning", Count = 2 },
            new TelemetryStatusRollup { Granularity = RollupGranularity.Minute, BucketStart = bucket, MachineId = "EQP-002", Status = "Running", Count = 40 });

        context.Telemetries.Add(Reading("EQP-009", 1, 1, "Faulted", now.AddHours(-2)));
        await context.SaveChangesAsync();
        var repository = CreateRepository(context, o => o.RawQueryWindowMinutes = 180);

        var status = await repository.GetFleetStatusAsync(now.AddHours(-4));

        Assert.Equal(2, status.MachineCount);
        Assert.Equal(100L, status.TotalReadings);
        Assert.Equal(2, status.Breakdown.Count);
        Assert.Equal("Running", status.Breakdown[0].Status); // most frequent first
        Assert.Equal(98L, status.Breakdown[0].Count);
        Assert.Equal("Warning", status.Breakdown[1].Status);
        Assert.Equal(2L, status.Breakdown[1].Count);
    }

    [Fact]
    public async Task GetStatisticsAsync_LongWindowWithNoBuckets_ReturnsNull()
    {
        using var context = TestDatabase.CreateContext();
        var now = DateTimeOffset.UtcNow;
        context.Telemetries.Add(Reading("EQP-001", 40, 5, "Running", now.AddHours(-2)));
        await context.SaveChangesAsync();
        var repository = CreateRepository(context, o => o.RawQueryWindowMinutes = 180);

        // Raw rows exist in the window, but the tier that serves it has nothing yet — the honest
        // answer is "no data", the same 404 the endpoint returns for a silent machine.
        var stats = await repository.GetStatisticsAsync("EQP-001", now.AddHours(-4));

        Assert.Null(stats);
    }
}
