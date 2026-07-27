using FactoryIoT.Domain.Entities;
using FactoryIoT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FactoryIoT.Tests.Infrastructure;

/// <summary>
/// Exercises the tier-to-tier fold: raw readings into minute buckets, minute buckets into hour
/// buckets, and both into the running fleet roster.
/// </summary>
/// <remarks>
/// The purge methods are deliberately not covered here. They issue provider-specific
/// <c>DELETE TOP (n)</c> statements that the in-memory provider cannot execute, so verifying them
/// needs a real SQL Server — see the manual check in <c>docs/DATA-LIFECYCLE.md</c>.
/// </remarks>
public sealed class DataLifecycleRepositoryTests
{
    private static readonly DateTimeOffset BucketStart = new(2026, 7, 27, 9, 0, 0, TimeSpan.Zero);

    private static Telemetry Reading(string machineId, double temperature, double pressure, string status, DateTimeOffset timestamp) =>
        new()
        {
            MachineId = machineId,
            Temperature = temperature,
            Pressure = pressure,
            Status = status,
            Timestamp = timestamp,
        };

    [Fact]
    public async Task BuildBucketAsync_Minute_AggregatesRawReadingsIntoOneRowPerMachine()
    {
        using var context = TestDatabase.CreateContext();
        context.Telemetries.AddRange(
            Reading("EQP-001", 10, 2, "Running", BucketStart.AddSeconds(1)),
            Reading("EQP-001", 30, 4, "Warning", BucketStart.AddSeconds(2)),
            Reading("EQP-002", 50, 6, "Running", BucketStart.AddSeconds(3)));
        await context.SaveChangesAsync();
        var repository = new DataLifecycleRepository(context);

        var rowsWritten = await repository.BuildBucketAsync(RollupGranularity.Minute, BucketStart);

        Assert.Equal(2, rowsWritten);

        var eqp1 = await context.TelemetryRollups.SingleAsync(r =>
            r.Granularity == RollupGranularity.Minute && r.MachineId == "EQP-001");
        Assert.Equal(BucketStart, eqp1.BucketStart);
        Assert.Equal(2, eqp1.SampleCount);
        Assert.Equal(10d, eqp1.MinTemperature, 3);
        Assert.Equal(30d, eqp1.MaxTemperature, 3);
        Assert.Equal(40d, eqp1.SumTemperature, 3);
        Assert.Equal(2d, eqp1.MinPressure, 3);
        Assert.Equal(4d, eqp1.MaxPressure, 3);
        Assert.Equal(6d, eqp1.SumPressure, 3);
        Assert.Equal(BucketStart.AddSeconds(1), eqp1.FirstReading);
        Assert.Equal(BucketStart.AddSeconds(2), eqp1.LastReading);

        // Status counts land in their own narrow table so the fleet breakdown stays a single
        // indexed GROUP BY rather than a column-per-status schema.
        var statuses = await context.TelemetryStatusRollups
            .Where(r => r.MachineId == "EQP-001")
            .OrderBy(r => r.Status)
            .ToListAsync();
        Assert.Equal(2, statuses.Count);
        Assert.Equal("Running", statuses[0].Status);
        Assert.Equal(1, statuses[0].Count);
        Assert.Equal("Warning", statuses[1].Status);
        Assert.Equal(1, statuses[1].Count);

        var checkpoint = await repository.GetCheckpointAsync(RollupGranularity.Minute);
        Assert.Equal(BucketStart, checkpoint);
    }

    [Fact]
    public async Task BuildBucketAsync_Minute_UsesHalfOpenBucketBoundaries()
    {
        using var context = TestDatabase.CreateContext();
        context.Telemetries.AddRange(
            Reading("EQP-001", 10, 1, "Running", BucketStart),                  // first tick: inside
            Reading("EQP-001", 20, 1, "Running", BucketStart.AddSeconds(59.9)), // last moment: inside
            Reading("EQP-001", 99, 9, "Running", BucketStart.AddMinutes(1)));   // next bucket: excluded
        await context.SaveChangesAsync();
        var repository = new DataLifecycleRepository(context);

        await repository.BuildBucketAsync(RollupGranularity.Minute, BucketStart);

        var bucket = await context.TelemetryRollups.SingleAsync();
        Assert.Equal(2, bucket.SampleCount);
        Assert.Equal(20d, bucket.MaxTemperature, 3);
    }

    [Fact]
    public async Task BuildBucketAsync_Minute_FloorsAnArbitraryInstantToItsBucket()
    {
        using var context = TestDatabase.CreateContext();
        context.Telemetries.Add(Reading("EQP-001", 10, 1, "Running", BucketStart.AddSeconds(30)));
        await context.SaveChangesAsync();
        var repository = new DataLifecycleRepository(context);

        // Asked for a mid-bucket instant; the bucket that contains it is what gets built.
        await repository.BuildBucketAsync(RollupGranularity.Minute, BucketStart.AddSeconds(42));

        var bucket = await context.TelemetryRollups.SingleAsync();
        Assert.Equal(BucketStart, bucket.BucketStart);
        Assert.Equal(1, bucket.SampleCount);
    }

    [Fact]
    public async Task BuildBucketAsync_Hour_ReAggregatesMinuteBucketsExactly()
    {
        using var context = TestDatabase.CreateContext();

        // Deliberately lopsided: three readings in the first minute, one in the second. Averaging
        // the two per-minute averages would give (10 + 70) / 2 = 40; the true average is
        // 100 / 4 = 25. Storing sums instead of averages is what makes the coarse bucket exact.
        context.Telemetries.AddRange(
            Reading("EQP-001", 10, 1, "Running", BucketStart.AddSeconds(1)),
            Reading("EQP-001", 10, 1, "Running", BucketStart.AddSeconds(2)),
            Reading("EQP-001", 10, 1, "Warning", BucketStart.AddSeconds(3)),
            Reading("EQP-001", 70, 5, "Running", BucketStart.AddMinutes(1).AddSeconds(1)));
        await context.SaveChangesAsync();
        var repository = new DataLifecycleRepository(context);

        await repository.BuildBucketAsync(RollupGranularity.Minute, BucketStart);
        await repository.BuildBucketAsync(RollupGranularity.Minute, BucketStart.AddMinutes(1));
        await repository.BuildBucketAsync(RollupGranularity.Hour, BucketStart);

        var hour = await context.TelemetryRollups
            .SingleAsync(r => r.Granularity == RollupGranularity.Hour);

        Assert.Equal(BucketStart, hour.BucketStart);
        Assert.Equal(4, hour.SampleCount);
        Assert.Equal(100d, hour.SumTemperature, 3);
        Assert.Equal(25d, hour.SumTemperature / hour.SampleCount, 3);
        Assert.Equal(10d, hour.MinTemperature, 3);
        Assert.Equal(70d, hour.MaxTemperature, 3);
        Assert.Equal(BucketStart.AddSeconds(1), hour.FirstReading);
        Assert.Equal(BucketStart.AddMinutes(1).AddSeconds(1), hour.LastReading);

        // Status counts roll up the same way — summed from the finer tier, not recounted from raw.
        var hourStatuses = await context.TelemetryStatusRollups
            .Where(r => r.Granularity == RollupGranularity.Hour)
            .OrderBy(r => r.Status)
            .ToListAsync();
        Assert.Equal(2, hourStatuses.Count);
        Assert.Equal("Running", hourStatuses[0].Status);
        Assert.Equal(3, hourStatuses[0].Count);
        Assert.Equal("Warning", hourStatuses[1].Status);
        Assert.Equal(1, hourStatuses[1].Count);
    }

    [Fact]
    public async Task BuildBucketAsync_Minute_AccumulatesTheRunningMachineRoster()
    {
        using var context = TestDatabase.CreateContext();
        context.Telemetries.AddRange(
            Reading("EQP-001", 10, 2, "Running", BucketStart.AddSeconds(1)),
            Reading("EQP-001", 30, 6, "Running", BucketStart.AddMinutes(1).AddSeconds(1)));
        await context.SaveChangesAsync();
        var repository = new DataLifecycleRepository(context);

        await repository.BuildBucketAsync(RollupGranularity.Minute, BucketStart);
        await repository.BuildBucketAsync(RollupGranularity.Minute, BucketStart.AddMinutes(1));

        var summary = await context.MachineSummaries.SingleAsync();
        Assert.Equal("EQP-001", summary.MachineId);
        Assert.Equal(2L, summary.SampleCount);
        Assert.Equal(40d, summary.SumTemperature, 3);
        Assert.Equal(10d, summary.MinTemperature, 3);
        Assert.Equal(30d, summary.MaxTemperature, 3);
        Assert.Equal(8d, summary.SumPressure, 3);
        Assert.Equal(BucketStart.AddSeconds(1), summary.FirstSeen);
        Assert.Equal(BucketStart.AddMinutes(1).AddSeconds(1), summary.LastSeen);
    }

    [Fact]
    public async Task BuildBucketAsync_RebuildingABucket_RefreshesItWithoutDoubleCountingTheRoster()
    {
        using var context = TestDatabase.CreateContext();
        context.Telemetries.Add(Reading("EQP-001", 10, 2, "Running", BucketStart.AddSeconds(1)));
        await context.SaveChangesAsync();
        var repository = new DataLifecycleRepository(context);

        await repository.BuildBucketAsync(RollupGranularity.Minute, BucketStart);
        await repository.BuildBucketAsync(RollupGranularity.Minute, BucketStart);

        // The bucket's own rows are replaced, not appended...
        var bucket = await context.TelemetryRollups.SingleAsync();
        Assert.Equal(1, bucket.SampleCount);
        Assert.Single(await context.TelemetryStatusRollups.ToListAsync());

        // ...and the roster, which accumulates and cannot subtract, is left alone the second time.
        var summary = await context.MachineSummaries.SingleAsync();
        Assert.Equal(1L, summary.SampleCount);
        Assert.Equal(10d, summary.SumTemperature, 3);
    }

    [Fact]
    public async Task BuildBucketAsync_EmptyBucket_WritesNothingButStillAdvancesTheCheckpoint()
    {
        using var context = TestDatabase.CreateContext();
        var repository = new DataLifecycleRepository(context);

        var rowsWritten = await repository.BuildBucketAsync(RollupGranularity.Minute, BucketStart);

        // A minute in which no machine reported produces no rows. The checkpoint still has to move,
        // otherwise the job would rescan that empty window forever.
        Assert.Equal(0, rowsWritten);
        Assert.Empty(await context.TelemetryRollups.ToListAsync());
        Assert.Equal(BucketStart, await repository.GetCheckpointAsync(RollupGranularity.Minute));
    }

    [Fact]
    public async Task SeedCheckpointAsync_FloorsTheSeedAndWillNotOverwriteAnExistingCheckpoint()
    {
        using var context = TestDatabase.CreateContext();
        var repository = new DataLifecycleRepository(context);

        // The upgrade migration seeds this with a raw reading timestamp, not a bucket boundary.
        await repository.SeedCheckpointAsync(RollupGranularity.Minute, BucketStart.AddSeconds(37));
        Assert.Equal(BucketStart, await repository.GetCheckpointAsync(RollupGranularity.Minute));

        // Seeding runs on every startup, so a second call must be a no-op rather than a rewind.
        await repository.SeedCheckpointAsync(RollupGranularity.Minute, BucketStart.AddHours(-5));
        Assert.Equal(BucketStart, await repository.GetCheckpointAsync(RollupGranularity.Minute));
    }

    [Fact]
    public async Task GetReportAsync_DescribesEachTierWithoutCountingRawRows()
    {
        using var context = TestDatabase.CreateContext();
        context.Telemetries.AddRange(
            Reading("EQP-001", 10, 2, "Running", BucketStart.AddSeconds(1)),
            Reading("EQP-001", 30, 4, "Running", BucketStart.AddSeconds(5)));
        await context.SaveChangesAsync();
        var repository = new DataLifecycleRepository(context);
        await repository.BuildBucketAsync(RollupGranularity.Minute, BucketStart);

        var report = await repository.GetReportAsync(TestDatabase.Retention().Value.ToRetentionWindows());

        Assert.Equal("Telemetries", report.RawTelemetry.Table);
        Assert.Equal(BucketStart.AddSeconds(1), report.RawTelemetry.OldestRetained);
        Assert.Equal(BucketStart.AddSeconds(5), report.RawTelemetry.NewestRetained);
        Assert.Equal(72d, report.RawTelemetry.RetentionHours);

        Assert.Null(report.RawSensorReadings.OldestRetained);

        Assert.Equal("Minute", report.MinuteRollups.Granularity);
        Assert.Equal(1L, report.MinuteRollups.BucketCount);
        Assert.Equal(BucketStart, report.MinuteRollups.OldestBucket);
        Assert.Equal(BucketStart, report.MinuteRollups.LastCompletedBucket);

        Assert.Equal(0L, report.HourRollups.BucketCount);
        Assert.Null(report.HourRollups.LastCompletedBucket);
        Assert.Null(report.HourRollups.LagSeconds);

        Assert.Equal(1, report.TrackedMachines);
    }
}
