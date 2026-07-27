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

    /// <summary>
    /// Marks minute aggregation as complete through <paramref name="lastCompletedBucketStart"/>.
    /// Readings at or after the following bucket boundary are the roster's live tail.
    /// </summary>
    private static void Checkpoint(FactoryIoTDbContext context, DateTimeOffset lastCompletedBucketStart) =>
        context.RollupCheckpoints.Add(new RollupCheckpoint
        {
            Granularity = RollupGranularity.Minute,
            LastCompletedBucketStart = lastCompletedBucketStart,
        });

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

        // Raw rows already inside the aggregated range. They are represented in the roster totals
        // above, so re-reading them here would count the same readings twice.
        Checkpoint(context, now);
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

    // ── Roster tier: the live tail ──────────────────────────────────────────────────────────
    //
    // The roster only advances when a minute bucket closes, so on its own it reports a machine
    // reporting once a second as last seen minutes ago — which is what put a grey "stale" dot
    // against every healthy machine on the dashboard. These cover the overlay that fixes it, and
    // the boundary that keeps the overlay from counting anything the roster already holds.

    [Fact]
    public async Task GetMachineSummariesAsync_OverlaysReadingsNewerThanTheAggregationFrontier()
    {
        using var context = TestDatabase.CreateContext();
        var now = DateTimeOffset.UtcNow;
        var checkpoint = RollupBucket.Floor(now.AddMinutes(-3), RollupGranularity.Minute);

        context.MachineSummaries.Add(new MachineSummary
        {
            MachineId = "EQP-001",
            SampleCount = 2,
            FirstSeen = checkpoint,
            LastSeen = checkpoint.AddSeconds(30),
            MinTemperature = 10, MaxTemperature = 20, SumTemperature = 30,
            MinPressure = 2, MaxPressure = 4, SumPressure = 6,
        });
        Checkpoint(context, checkpoint);

        // Two readings that landed after the last closed bucket — exactly the window the roster
        // cannot know about. The newest is seconds old, which is what "live" has to reflect.
        var frontier = RollupBucket.Next(checkpoint, RollupGranularity.Minute);
        context.Telemetries.AddRange(
            Reading("EQP-001", 40, 8, "Running", frontier.AddSeconds(10)),
            Reading("EQP-001", 60, 10, "Running", now.AddSeconds(-2)));
        await context.SaveChangesAsync();
        var repository = CreateRepository(context);

        var summary = Assert.Single(await repository.GetMachineSummariesAsync());

        Assert.Equal(now.AddSeconds(-2), summary.LastSeen); // the real latest reading, not the frontier
        Assert.Equal(checkpoint, summary.FirstSeen);        // lifetime start is unchanged
        Assert.Equal(4L, summary.SampleCount);              // 2 aggregated + 2 still raw
        Assert.Equal(10d, summary.MinTemperature, 3);       // extent of both ranges
        Assert.Equal(60d, summary.MaxTemperature, 3);
        Assert.Equal(32.5d, summary.AvgTemperature, 3);     // (30 + 40 + 60) / 4
        Assert.Equal(2d, summary.MinPressure, 3);
        Assert.Equal(10d, summary.MaxPressure, 3);
        Assert.Equal(6d, summary.AvgPressure, 3);           // (6 + 8 + 10) / 4
    }

    [Fact]
    public async Task GetMachineSummariesAsync_ExcludesReadingsTheRosterAlreadyHolds()
    {
        using var context = TestDatabase.CreateContext();
        var now = DateTimeOffset.UtcNow;
        var checkpoint = RollupBucket.Floor(now.AddMinutes(-3), RollupGranularity.Minute);

        context.MachineSummaries.Add(new MachineSummary
        {
            MachineId = "EQP-001",
            SampleCount = 1,
            FirstSeen = checkpoint,
            LastSeen = checkpoint,
            MinTemperature = 50, MaxTemperature = 50, SumTemperature = 50,
            MinPressure = 5, MaxPressure = 5, SumPressure = 5,
        });
        Checkpoint(context, checkpoint);

        // Raw rows still on disk inside the aggregated range. Retention keeps raw telemetry for
        // days after its bucket closes, so these are the normal case — and counting them again
        // would inflate a running total that has no way to subtract the duplicate.
        context.Telemetries.AddRange(
            Reading("EQP-001", 50, 5, "Running", checkpoint),
            Reading("EQP-001", 50, 5, "Running", checkpoint.AddSeconds(59)));
        await context.SaveChangesAsync();
        var repository = CreateRepository(context);

        var summary = Assert.Single(await repository.GetMachineSummariesAsync());

        Assert.Equal(1L, summary.SampleCount);
        Assert.Equal(checkpoint, summary.LastSeen);
    }

    [Fact]
    public async Task GetMachineSummariesAsync_SurfacesAMachineWhoseFirstBucketHasNotClosedYet()
    {
        using var context = TestDatabase.CreateContext();
        var now = DateTimeOffset.UtcNow;
        Checkpoint(context, RollupBucket.Floor(now.AddMinutes(-3), RollupGranularity.Minute));

        // No roster row at all: this machine started reporting less than a bucket ago. It should
        // appear immediately rather than after the rollup job next runs.
        context.Telemetries.AddRange(
            Reading("EQP-050", 70, 7, "Running", now.AddSeconds(-20)),
            Reading("EQP-050", 90, 9, "Warning", now.AddSeconds(-5)));
        await context.SaveChangesAsync();
        var repository = CreateRepository(context);

        var summary = Assert.Single(await repository.GetMachineSummariesAsync());

        Assert.Equal("EQP-050", summary.MachineId);
        Assert.Equal(2L, summary.SampleCount);
        Assert.Equal(now.AddSeconds(-20), summary.FirstSeen);
        Assert.Equal(now.AddSeconds(-5), summary.LastSeen);
        Assert.Equal(80d, summary.AvgTemperature, 3);
    }

    [Fact]
    public async Task GetMachineSummariesAsync_WithoutACheckpoint_StillReportsLiveMachines()
    {
        using var context = TestDatabase.CreateContext();
        var now = DateTimeOffset.UtcNow;

        // No checkpoint and no roster rows: retention switched off, or a database that has not
        // completed its first pass. The fleet page has to keep working in that configuration.
        context.Telemetries.Add(Reading("EQP-001", 55, 5, "Running", now.AddSeconds(-3)));
        await context.SaveChangesAsync();
        var repository = CreateRepository(context);

        var summary = Assert.Single(await repository.GetMachineSummariesAsync());

        Assert.Equal("EQP-001", summary.MachineId);
        Assert.Equal(1L, summary.SampleCount);
        Assert.Equal(now.AddSeconds(-3), summary.LastSeen);
    }

    [Fact]
    public async Task GetMachineSummariesAsync_ClampsTheTailWhenAggregationHasStalled()
    {
        using var context = TestDatabase.CreateContext();
        var now = DateTimeOffset.UtcNow;

        // Aggregation has not advanced in two hours. Trusting the frontier would widen the overlay
        // without limit — the unbounded scan the roster tier exists to prevent — so the clamp wins
        // and the answer is knowingly incomplete rather than knowingly expensive.
        Checkpoint(context, RollupBucket.Floor(now.AddHours(-2), RollupGranularity.Minute));
        context.Telemetries.AddRange(
            Reading("EQP-001", 100, 10, "Running", now.AddMinutes(-30)), // outside the clamp
            Reading("EQP-001", 20, 2, "Running", now.AddMinutes(-5)));   // inside it
        await context.SaveChangesAsync();
        var repository = CreateRepository(context, o => o.RosterTailMinutes = 15);

        var summary = Assert.Single(await repository.GetMachineSummariesAsync());

        Assert.Equal(1L, summary.SampleCount);
        Assert.Equal(now.AddMinutes(-5), summary.LastSeen);
        Assert.Equal(20d, summary.MaxTemperature, 3); // the older reading was not read at all
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
