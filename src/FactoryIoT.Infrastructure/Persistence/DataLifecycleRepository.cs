using FactoryIoT.Domain.Analytics;
using FactoryIoT.Domain.Entities;
using FactoryIoT.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FactoryIoT.Infrastructure.Persistence;

/// <summary>
/// EF Core implementation of <see cref="IDataLifecycleRepository"/>: folds raw telemetry into
/// pre-aggregated buckets and drops whatever has aged out of its tier.
/// </summary>
/// <remarks>
/// Buckets are built one at a time with an explicit <c>[start, end)</c> range rather than with a
/// <c>GROUP BY DATEADD(minute, ...)</c> over a wide span. That keeps every aggregation query a
/// plain range predicate on the time-leading clustered index — a seek, translatable by any
/// provider, with no date arithmetic for the optimiser to lose track of — and it makes catching up
/// after an outage naturally incremental: each bucket commits on its own, so progress is never
/// lost to one oversized transaction.
/// </remarks>
public sealed class DataLifecycleRepository : IDataLifecycleRepository
{
    private readonly FactoryIoTDbContext _context;

    public DataLifecycleRepository(FactoryIoTDbContext context)
    {
        _context = context;
    }

    public async Task<DateTimeOffset?> GetCheckpointAsync(
        RollupGranularity granularity,
        CancellationToken cancellationToken = default)
    {
        var checkpoint = await _context.RollupCheckpoints
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Granularity == granularity, cancellationToken);

        // Flooring on read is what lets the upgrade migration seed this row with a raw telemetry
        // timestamp: an already-floored value survives the call unchanged.
        return checkpoint is null
            ? null
            : RollupBucket.Floor(checkpoint.LastCompletedBucketStart, granularity);
    }

    public async Task SeedCheckpointAsync(
        RollupGranularity granularity,
        DateTimeOffset lastCompletedBucketStart,
        CancellationToken cancellationToken = default)
    {
        if (await _context.RollupCheckpoints.AnyAsync(c => c.Granularity == granularity, cancellationToken))
        {
            return;
        }

        _context.RollupCheckpoints.Add(new RollupCheckpoint
        {
            Granularity = granularity,
            LastCompletedBucketStart = RollupBucket.Floor(lastCompletedBucketStart, granularity),
        });

        await _context.SaveChangesAsync(cancellationToken);
    }

    public Task<DateTimeOffset?> GetOldestRawTimestampAsync(CancellationToken cancellationToken = default)
        => _context.Telemetries.AsNoTracking().MinAsync(t => (DateTimeOffset?)t.Timestamp, cancellationToken);

    public Task<DateTimeOffset?> GetNewestRawTimestampAsync(CancellationToken cancellationToken = default)
        => _context.Telemetries.AsNoTracking().MaxAsync(t => (DateTimeOffset?)t.Timestamp, cancellationToken);

    public async Task<int> BuildBucketAsync(
        RollupGranularity granularity,
        DateTimeOffset bucketStart,
        CancellationToken cancellationToken = default)
    {
        var start = RollupBucket.Floor(bucketStart, granularity);
        var end = start + RollupBucket.Size(granularity);

        // One transaction spans the aggregate, the roster fold and the checkpoint advance. That is
        // what makes the running machine summaries safe to accumulate rather than recompute: a
        // process that dies part-way rolls back the partial fold together with the checkpoint, so
        // the bucket is rebuilt from scratch next pass instead of being counted one-and-a-bit times.
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        // Read the checkpoint before doing any work. Rebuilding a bucket at or below it — a manual
        // re-run, or a retry that raced another instance — must refresh the bucket's own rows but
        // must NOT fold them into the roster a second time, because those totals accumulate rather
        // than recompute and have no way to subtract a duplicate.
        var checkpoint = await _context.RollupCheckpoints
            .FirstOrDefaultAsync(c => c.Granularity == granularity, cancellationToken);
        var isNewBucket = checkpoint is null
            || RollupBucket.Floor(checkpoint.LastCompletedBucketStart, granularity) < start;

        var aggregates = granularity == RollupGranularity.Minute
            ? await AggregateRawAsync(start, end, cancellationToken)
            : await AggregateMinuteBucketsAsync(start, end, cancellationToken);

        var statusCounts = granularity == RollupGranularity.Minute
            ? await CountRawStatusesAsync(start, end, cancellationToken)
            : await CountMinuteBucketStatusesAsync(start, end, cancellationToken);

        await ClearBucketAsync(granularity, start, cancellationToken);

        foreach (var aggregate in aggregates)
        {
            _context.TelemetryRollups.Add(new TelemetryRollup
            {
                Granularity = granularity,
                MachineId = aggregate.MachineId,
                BucketStart = start,
                SampleCount = aggregate.SampleCount,
                FirstReading = aggregate.FirstReading,
                LastReading = aggregate.LastReading,
                MinTemperature = aggregate.MinTemperature,
                MaxTemperature = aggregate.MaxTemperature,
                SumTemperature = aggregate.SumTemperature,
                MinPressure = aggregate.MinPressure,
                MaxPressure = aggregate.MaxPressure,
                SumPressure = aggregate.SumPressure,
            });
        }

        foreach (var status in statusCounts)
        {
            _context.TelemetryStatusRollups.Add(new TelemetryStatusRollup
            {
                Granularity = granularity,
                BucketStart = start,
                MachineId = status.MachineId,
                Status = status.Status,
                Count = status.Count,
            });
        }

        // Only the finest granularity feeds the roster. Folding hour buckets in as well would
        // count every reading a second time, since an hour bucket is just its minutes restated.
        if (granularity == RollupGranularity.Minute && isNewBucket)
        {
            await FoldIntoMachineSummariesAsync(aggregates, cancellationToken);
        }

        AdvanceCheckpoint(checkpoint, granularity, start);

        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // Catching up after an outage walks hundreds of buckets through this one scoped context.
        // Without detaching, every rollup row written stays in the change tracker for the rest of
        // the pass, so memory climbs and each subsequent SaveChanges has more entities to scan.
        _context.ChangeTracker.Clear();

        return aggregates.Count;
    }

    public Task<int> PurgeRawTelemetryAsync(
        DateTimeOffset cutoff,
        int batchSize,
        int maxBatches,
        CancellationToken cancellationToken = default)
        => PurgeInBatchesAsync(
            batch => _context.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE TOP ({batch}) FROM [Telemetries] WHERE [Timestamp] < {cutoff}",
                cancellationToken),
            batchSize,
            maxBatches);

    public Task<int> PurgeRawSensorReadingsAsync(
        DateTimeOffset cutoff,
        int batchSize,
        int maxBatches,
        CancellationToken cancellationToken = default)
        => PurgeInBatchesAsync(
            batch => _context.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE TOP ({batch}) FROM [SensorReadings] WHERE [Timestamp] < {cutoff}",
                cancellationToken),
            batchSize,
            maxBatches);

    public async Task<int> PurgeRollupsAsync(
        RollupGranularity granularity,
        DateTimeOffset cutoff,
        int batchSize,
        int maxBatches,
        CancellationToken cancellationToken = default)
    {
        var tier = (int)granularity;

        var numericRows = await PurgeInBatchesAsync(
            batch => _context.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE TOP ({batch}) FROM [TelemetryRollups] WHERE [Granularity] = {tier} AND [BucketStart] < {cutoff}",
                cancellationToken),
            batchSize,
            maxBatches);

        var statusRows = await PurgeInBatchesAsync(
            batch => _context.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE TOP ({batch}) FROM [TelemetryStatusRollups] WHERE [Granularity] = {tier} AND [BucketStart] < {cutoff}",
                cancellationToken),
            batchSize,
            maxBatches);

        return numericRows + statusRows;
    }

    public async Task<DataLifecycleReport> GetReportAsync(
        RetentionWindows windows,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        // MIN/MAX on a time-leading clustered index are single seeks to the first and last leaf
        // page. COUNT(*) over the same table would read every page to produce a number nobody
        // acts on, which is precisely the access pattern this design exists to remove.
        var rawTelemetry = new StorageTierState(
            "Telemetries",
            await _context.Telemetries.AsNoTracking().MinAsync(t => (DateTimeOffset?)t.Timestamp, cancellationToken),
            await _context.Telemetries.AsNoTracking().MaxAsync(t => (DateTimeOffset?)t.Timestamp, cancellationToken),
            windows.RawTelemetryHours);

        var rawSensorReadings = new StorageTierState(
            "SensorReadings",
            await _context.SensorReadings.AsNoTracking().MinAsync(r => (DateTimeOffset?)r.Timestamp, cancellationToken),
            await _context.SensorReadings.AsNoTracking().MaxAsync(r => (DateTimeOffset?)r.Timestamp, cancellationToken),
            windows.RawSensorReadingHours);

        return new DataLifecycleReport(
            now,
            rawTelemetry,
            rawSensorReadings,
            await BuildTierStateAsync(RollupGranularity.Minute, windows.MinuteRollupHours, now, cancellationToken),
            await BuildTierStateAsync(RollupGranularity.Hour, windows.HourRollupHours, now, cancellationToken),
            await _context.MachineSummaries.AsNoTracking().CountAsync(cancellationToken));
    }

    // ── Aggregation ─────────────────────────────────────────────────────────────────────────

    private async Task<List<BucketAggregate>> AggregateRawAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        var rows = await _context.Telemetries
            .AsNoTracking()
            .Where(t => t.Timestamp >= start && t.Timestamp < end)
            .GroupBy(t => t.MachineId)
            .Select(g => new
            {
                MachineId = g.Key,
                SampleCount = g.Count(),
                FirstReading = g.Min(t => t.Timestamp),
                LastReading = g.Max(t => t.Timestamp),
                MinTemperature = g.Min(t => t.Temperature),
                MaxTemperature = g.Max(t => t.Temperature),
                SumTemperature = g.Sum(t => t.Temperature),
                MinPressure = g.Min(t => t.Pressure),
                MaxPressure = g.Max(t => t.Pressure),
                SumPressure = g.Sum(t => t.Pressure),
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new BucketAggregate(
                r.MachineId, r.SampleCount, r.FirstReading, r.LastReading,
                r.MinTemperature, r.MaxTemperature, r.SumTemperature,
                r.MinPressure, r.MaxPressure, r.SumPressure))
            .ToList();
    }

    /// <summary>
    /// Builds a coarse bucket from the finer ones it contains. This is exact rather than
    /// approximate because the finer tier stores sums and counts, never averages: sums add,
    /// extents take the extent of extents, and the average is recovered by division at read time.
    /// </summary>
    private async Task<List<BucketAggregate>> AggregateMinuteBucketsAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        var rows = await _context.TelemetryRollups
            .AsNoTracking()
            .Where(r => r.Granularity == RollupGranularity.Minute && r.BucketStart >= start && r.BucketStart < end)
            .GroupBy(r => r.MachineId)
            .Select(g => new
            {
                MachineId = g.Key,
                SampleCount = g.Sum(r => r.SampleCount),
                FirstReading = g.Min(r => r.FirstReading),
                LastReading = g.Max(r => r.LastReading),
                MinTemperature = g.Min(r => r.MinTemperature),
                MaxTemperature = g.Max(r => r.MaxTemperature),
                SumTemperature = g.Sum(r => r.SumTemperature),
                MinPressure = g.Min(r => r.MinPressure),
                MaxPressure = g.Max(r => r.MaxPressure),
                SumPressure = g.Sum(r => r.SumPressure),
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new BucketAggregate(
                r.MachineId, r.SampleCount, r.FirstReading, r.LastReading,
                r.MinTemperature, r.MaxTemperature, r.SumTemperature,
                r.MinPressure, r.MaxPressure, r.SumPressure))
            .ToList();
    }

    private async Task<List<StatusAggregate>> CountRawStatusesAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        var rows = await _context.Telemetries
            .AsNoTracking()
            .Where(t => t.Timestamp >= start && t.Timestamp < end)
            .GroupBy(t => new { t.MachineId, t.Status })
            .Select(g => new { g.Key.MachineId, g.Key.Status, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return rows.Select(r => new StatusAggregate(r.MachineId, r.Status, r.Count)).ToList();
    }

    private async Task<List<StatusAggregate>> CountMinuteBucketStatusesAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        var rows = await _context.TelemetryStatusRollups
            .AsNoTracking()
            .Where(r => r.Granularity == RollupGranularity.Minute && r.BucketStart >= start && r.BucketStart < end)
            .GroupBy(r => new { r.MachineId, r.Status })
            .Select(g => new { g.Key.MachineId, g.Key.Status, Count = g.Sum(r => r.Count) })
            .ToListAsync(cancellationToken);

        return rows.Select(r => new StatusAggregate(r.MachineId, r.Status, r.Count)).ToList();
    }

    // ── Bucket persistence ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Removes any rows already present for this bucket and flushes the deletes immediately.
    /// The checkpoint normally guarantees a bucket is built exactly once, so this usually deletes
    /// nothing — but the flush matters: EF cannot hold a pending delete and a pending insert for
    /// the same primary key at once, so the deletes have to reach the database before the fresh
    /// rows are added.
    /// </summary>
    private async Task ClearBucketAsync(
        RollupGranularity granularity,
        DateTimeOffset bucketStart,
        CancellationToken cancellationToken)
    {
        var staleRollups = await _context.TelemetryRollups
            .Where(r => r.Granularity == granularity && r.BucketStart == bucketStart)
            .ToListAsync(cancellationToken);

        var staleStatuses = await _context.TelemetryStatusRollups
            .Where(r => r.Granularity == granularity && r.BucketStart == bucketStart)
            .ToListAsync(cancellationToken);

        if (staleRollups.Count == 0 && staleStatuses.Count == 0)
        {
            return;
        }

        _context.TelemetryRollups.RemoveRange(staleRollups);
        _context.TelemetryStatusRollups.RemoveRange(staleStatuses);
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task FoldIntoMachineSummariesAsync(
        IReadOnlyList<BucketAggregate> aggregates,
        CancellationToken cancellationToken)
    {
        if (aggregates.Count == 0)
        {
            return;
        }

        var machineIds = aggregates.Select(a => a.MachineId).ToList();

        // Tracked on purpose — these rows are mutated in place and flushed by the caller's
        // SaveChangesAsync inside the bucket transaction.
        var summaries = await _context.MachineSummaries
            .Where(s => machineIds.Contains(s.MachineId))
            .ToListAsync(cancellationToken);

        var byMachine = summaries.ToDictionary(s => s.MachineId, StringComparer.Ordinal);

        foreach (var aggregate in aggregates)
        {
            if (!byMachine.TryGetValue(aggregate.MachineId, out var summary))
            {
                _context.MachineSummaries.Add(new MachineSummary
                {
                    MachineId = aggregate.MachineId,
                    SampleCount = aggregate.SampleCount,
                    FirstSeen = aggregate.FirstReading,
                    LastSeen = aggregate.LastReading,
                    MinTemperature = aggregate.MinTemperature,
                    MaxTemperature = aggregate.MaxTemperature,
                    SumTemperature = aggregate.SumTemperature,
                    MinPressure = aggregate.MinPressure,
                    MaxPressure = aggregate.MaxPressure,
                    SumPressure = aggregate.SumPressure,
                });
                continue;
            }

            summary.SampleCount += aggregate.SampleCount;
            summary.SumTemperature += aggregate.SumTemperature;
            summary.SumPressure += aggregate.SumPressure;
            summary.MinTemperature = Math.Min(summary.MinTemperature, aggregate.MinTemperature);
            summary.MaxTemperature = Math.Max(summary.MaxTemperature, aggregate.MaxTemperature);
            summary.MinPressure = Math.Min(summary.MinPressure, aggregate.MinPressure);
            summary.MaxPressure = Math.Max(summary.MaxPressure, aggregate.MaxPressure);

            if (aggregate.FirstReading < summary.FirstSeen)
            {
                summary.FirstSeen = aggregate.FirstReading;
            }

            if (aggregate.LastReading > summary.LastSeen)
            {
                summary.LastSeen = aggregate.LastReading;
            }
        }
    }

    private void AdvanceCheckpoint(
        RollupCheckpoint? checkpoint,
        RollupGranularity granularity,
        DateTimeOffset bucketStart)
    {
        if (checkpoint is null)
        {
            _context.RollupCheckpoints.Add(new RollupCheckpoint
            {
                Granularity = granularity,
                LastCompletedBucketStart = bucketStart,
            });
            return;
        }

        // Never move a checkpoint backwards: rebuilding an old bucket by hand must not cause every
        // bucket after it to be replayed into the running roster totals.
        if (checkpoint.LastCompletedBucketStart < bucketStart)
        {
            checkpoint.LastCompletedBucketStart = bucketStart;
        }
    }

    // ── Purge ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the supplied delete until it comes back short (nothing left to delete) or the batch
    /// budget is spent. Stopping short of the budget is the normal case; exhausting it just means
    /// the next pass picks up where this one left off.
    /// </summary>
    private static async Task<int> PurgeInBatchesAsync(
        Func<int, Task<int>> deleteBatchAsync,
        int batchSize,
        int maxBatches)
    {
        var effectiveBatchSize = Math.Max(1, batchSize);
        var budget = Math.Max(1, maxBatches);
        var total = 0;

        for (var attempt = 0; attempt < budget; attempt++)
        {
            var deleted = await deleteBatchAsync(effectiveBatchSize);
            total += deleted;

            if (deleted < effectiveBatchSize)
            {
                break;
            }
        }

        return total;
    }

    // ── Reporting ───────────────────────────────────────────────────────────────────────────

    private async Task<RollupTierState> BuildTierStateAsync(
        RollupGranularity granularity,
        double retentionHours,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var buckets = _context.TelemetryRollups.AsNoTracking().Where(r => r.Granularity == granularity);

        var lastCompleted = await GetCheckpointAsync(granularity, cancellationToken);

        // Lag is measured from the end of the last completed bucket, so a job that is perfectly
        // up to date still reports roughly one bucket width plus the configured safety margin.
        var lagSeconds = lastCompleted is null
            ? (double?)null
            : (now - (lastCompleted.Value + RollupBucket.Size(granularity))).TotalSeconds;

        return new RollupTierState(
            granularity.ToString(),
            await buckets.LongCountAsync(cancellationToken),
            await buckets.MinAsync(r => (DateTimeOffset?)r.BucketStart, cancellationToken),
            lastCompleted,
            lagSeconds,
            retentionHours);
    }

    private sealed record BucketAggregate(
        string MachineId,
        int SampleCount,
        DateTimeOffset FirstReading,
        DateTimeOffset LastReading,
        double MinTemperature,
        double MaxTemperature,
        double SumTemperature,
        double MinPressure,
        double MaxPressure,
        double SumPressure);

    private sealed record StatusAggregate(string MachineId, string Status, int Count);
}
