using FactoryIoT.Domain.Analytics;
using FactoryIoT.Domain.Entities;
using FactoryIoT.Domain.Interfaces;
using FactoryIoT.Infrastructure.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FactoryIoT.Infrastructure.Persistence;

/// <summary>
/// EF Core implementation of telemetry repository.
/// </summary>
/// <remarks>
/// <para>
/// Read queries pick a storage tier from the width of the window they are asked about. Short
/// windows are answered from raw rows and are exact to the second; longer ones are answered from
/// pre-aggregated buckets, because a dashboard polling a 24-hour window every few seconds must not
/// re-read a day of raw telemetry each time. The cost of a rollup-backed answer is flat — a
/// 30-day window reads the same handful of buckets per machine whether the fleet has been running
/// for a week or a decade.
/// </para>
/// <para>
/// The fleet roster is the exception, and reads from two tiers at once. Pre-aggregation buys its
/// flat cost by only ever being as current as the last <em>closed</em> bucket, which is fine for a
/// question about the last 24 hours and wrong for "is this machine reporting right now" — the
/// question the roster is actually asked. So it reads the maintained roster for the long history
/// and overlays the raw readings newer than the aggregation frontier for the recent minutes. The
/// overlay's width is the job's own lag, not the age of the database, so the query stays bounded.
/// </para>
/// </remarks>
public sealed class TelemetryRepository : ITelemetryRepository
{
    private readonly FactoryIoTDbContext _context;
    private readonly DataRetentionOptions _options;

    public TelemetryRepository(FactoryIoTDbContext context, IOptions<DataRetentionOptions> options)
    {
        _context = context;
        _options = options.Value;
    }

    public async Task AddRangeAsync(IEnumerable<Telemetry> telemetries, CancellationToken cancellationToken = default)
    {
        await _context.Telemetries.AddRangeAsync(telemetries, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Telemetry>> GetLatestByMachineAsync(string machineId, int count, CancellationToken cancellationToken = default)
    {
        // Always raw: "the last N readings" is a question only full-fidelity rows can answer, and
        // the covering (MachineId, Timestamp DESC) index makes it a seek plus N rows regardless of
        // how much history the table holds.
        return await _context.Telemetries
            .AsNoTracking()
            .Where(t => t.MachineId == machineId)
            .OrderByDescending(t => t.Timestamp)
            .Take(count)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MachineTelemetrySummary>> GetMachineSummariesAsync(CancellationToken cancellationToken = default)
    {
        // One row per machine, maintained forward by the lifecycle worker. This used to be a
        // GROUP BY over the whole Telemetries table — the single query in the system whose cost
        // grew without bound, since it read every row ever ingested on every dashboard poll.
        // Reading the maintained roster instead costs the same on day 1000 as on day 1.
        var roster = await _context.MachineSummaries
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        // The roster alone answers "how has this machine behaved", but it cannot answer "is it
        // reporting right now": it only advances when a minute bucket closes, which by design
        // trails the wall clock by a bucket width plus the rollup safety lag. Overlaying the raw
        // readings newer than that frontier is what makes LastSeen mean *now* rather than "as of
        // the last aggregation pass" — the difference between a live dot and a permanently grey one
        // for a machine reporting every second.
        //
        // The frontier is read *after* the roster on purpose. A pass that commits between the two
        // reads then leaves its bucket in neither, so one poll under-counts by a single bucket and
        // the next is exact. Reading the frontier first would produce the opposite skew — a bucket
        // present in both — and double-counting a running total is not self-correcting.
        var tail = await GetUnaggregatedTailAsync(cancellationToken);

        var merged = roster.ToDictionary(r => r.MachineId, RosterRow.From, StringComparer.Ordinal);

        foreach (var row in tail)
        {
            merged[row.MachineId] = merged.TryGetValue(row.MachineId, out var folded)
                ? folded.Merge(row)
                // A machine whose first bucket has not closed yet has no roster row at all. Taking
                // the tail as-is is what lets it appear on the dashboard as it starts reporting,
                // rather than three minutes later.
                : row;
        }

        // Ordered in memory with an ordinal comparer so EQP-001..EQP-050 sort predictably
        // regardless of the database's collation.
        return merged.Values
            .OrderBy(r => r.MachineId, StringComparer.Ordinal)
            .Select(r => r.ToSummary())
            .ToList();
    }

    /// <summary>
    /// Aggregates the raw readings that have landed since the last minute bucket closed — the slice
    /// of history the maintained roster cannot know about yet.
    /// </summary>
    /// <remarks>
    /// Bounded by construction: the window is the rollup job's own lag, a few minutes wide, and
    /// clamped by <see cref="DataRetentionOptions.RosterTailMinutes"/> when that job is behind or
    /// switched off. Because <c>Telemetries</c> is clustered on <c>(Timestamp, Id)</c> the query is
    /// a range seek over the newest pages of the index, so its cost tracks the width of the window
    /// rather than the size of the table — which is the property the roster tier exists to protect.
    /// </remarks>
    private async Task<List<RosterRow>> GetUnaggregatedTailAsync(CancellationToken cancellationToken)
    {
        var from = await GetTailStartAsync(cancellationToken);

        var rows = await _context.Telemetries
            .AsNoTracking()
            .Where(t => t.Timestamp >= from)
            .GroupBy(t => t.MachineId)
            .Select(g => new
            {
                MachineId = g.Key,
                SampleCount = g.Count(),
                FirstSeen = g.Min(t => t.Timestamp),
                LastSeen = g.Max(t => t.Timestamp),
                MinTemperature = g.Min(t => t.Temperature),
                MaxTemperature = g.Max(t => t.Temperature),
                SumTemperature = g.Sum(t => t.Temperature),
                MinPressure = g.Min(t => t.Pressure),
                MaxPressure = g.Max(t => t.Pressure),
                SumPressure = g.Sum(t => t.Pressure),
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new RosterRow(
                r.MachineId, r.SampleCount, r.FirstSeen, r.LastSeen,
                r.MinTemperature, r.MaxTemperature, r.SumTemperature,
                r.MinPressure, r.MaxPressure, r.SumPressure))
            .ToList();
    }

    /// <summary>
    /// Where the live tail begins: the exclusive end of the aggregated range, clamped so a stalled
    /// or disabled lifecycle worker can never turn the roster query into a full table scan.
    /// </summary>
    private async Task<DateTimeOffset> GetTailStartAsync(CancellationToken cancellationToken)
    {
        var clamp = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(Math.Max(1, _options.RosterTailMinutes));

        var checkpoint = await _context.RollupCheckpoints
            .AsNoTracking()
            .Where(c => c.Granularity == RollupGranularity.Minute)
            .Select(c => (DateTimeOffset?)c.LastCompletedBucketStart)
            .FirstOrDefaultAsync(cancellationToken);

        // No checkpoint means nothing has ever been folded into the roster — a fresh database, or
        // one running with retention switched off. The clamp window is then the whole answer, which
        // is why the fleet page still shows live machines in that configuration instead of nothing.
        if (checkpoint is null)
        {
            return clamp;
        }

        // Everything strictly before the next bucket boundary is already in the roster, so that
        // boundary is exactly where the tail may start without counting a reading twice.
        var frontier = RollupBucket.Next(checkpoint.Value, RollupGranularity.Minute);
        return frontier > clamp ? frontier : clamp;
    }

    public Task<TelemetryStatistics?> GetStatisticsAsync(string machineId, DateTimeOffset from, CancellationToken cancellationToken = default)
    {
        var tier = SelectTier(from);
        return tier is null
            ? GetStatisticsFromRawAsync(machineId, from, cancellationToken)
            : GetStatisticsFromRollupsAsync(machineId, from, tier.Value, cancellationToken);
    }

    public Task<FleetStatus> GetFleetStatusAsync(DateTimeOffset from, CancellationToken cancellationToken = default)
    {
        var tier = SelectTier(from);
        return tier is null
            ? GetFleetStatusFromRawAsync(from, cancellationToken)
            : GetFleetStatusFromRollupsAsync(from, tier.Value, cancellationToken);
    }

    /// <summary>
    /// Picks the tier for a window starting at <paramref name="from"/>: <c>null</c> for raw rows,
    /// otherwise the coarsest granularity that still has the window in retention.
    /// </summary>
    private RollupGranularity? SelectTier(DateTimeOffset from)
    {
        var windowMinutes = (DateTimeOffset.UtcNow - from).TotalMinutes;

        if (windowMinutes <= _options.RawQueryWindowMinutes)
        {
            return null;
        }

        return windowMinutes <= _options.MinuteRollupHours * 60
            ? RollupGranularity.Minute
            : RollupGranularity.Hour;
    }

    // ── Raw tier ────────────────────────────────────────────────────────────────────────────

    private async Task<TelemetryStatistics?> GetStatisticsFromRawAsync(string machineId, DateTimeOffset from, CancellationToken cancellationToken)
    {
        // Grouping the windowed rows means an empty window produces no group, so FirstOrDefault
        // returns null — the natural "this machine reported nothing in that window" signal —
        // instead of a bare aggregate over zero rows (which would throw for Min/Max/Average).
        var row = await _context.Telemetries
            .AsNoTracking()
            .Where(t => t.MachineId == machineId && t.Timestamp >= from)
            .GroupBy(t => t.MachineId)
            .Select(g => new
            {
                SampleCount = g.Count(),
                FirstReading = g.Min(t => t.Timestamp),
                LastReading = g.Max(t => t.Timestamp),
                MinTemperature = g.Min(t => t.Temperature),
                MaxTemperature = g.Max(t => t.Temperature),
                AvgTemperature = g.Average(t => t.Temperature),
                MinPressure = g.Min(t => t.Pressure),
                MaxPressure = g.Max(t => t.Pressure),
                AvgPressure = g.Average(t => t.Pressure),
            })
            .FirstOrDefaultAsync(cancellationToken);

        return row is null
            ? null
            : new TelemetryStatistics(
                machineId, row.SampleCount, row.FirstReading, row.LastReading,
                row.MinTemperature, row.MaxTemperature, row.AvgTemperature,
                row.MinPressure, row.MaxPressure, row.AvgPressure);
    }

    private async Task<FleetStatus> GetFleetStatusFromRawAsync(DateTimeOffset from, CancellationToken cancellationToken)
    {
        var windowed = _context.Telemetries
            .AsNoTracking()
            .Where(t => t.Timestamp >= from);

        var machineCount = await windowed
            .Select(t => t.MachineId)
            .Distinct()
            .CountAsync(cancellationToken);

        var counts = await windowed
            .GroupBy(t => t.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return BuildFleetStatus(machineCount, counts.Select(c => new StatusBreakdown(c.Status, c.Count)));
    }

    // ── Pre-aggregated tiers ────────────────────────────────────────────────────────────────

    private async Task<TelemetryStatistics?> GetStatisticsFromRollupsAsync(
        string machineId,
        DateTimeOffset from,
        RollupGranularity granularity,
        CancellationToken cancellationToken)
    {
        var row = await _context.TelemetryRollups
            .AsNoTracking()
            .Where(r => r.Granularity == granularity && r.MachineId == machineId && r.BucketStart >= from)
            .GroupBy(r => r.MachineId)
            .Select(g => new
            {
                // Widened to 64-bit before summing: SQL Server's SUM over an int column returns an
                // int and raises an arithmetic overflow rather than promoting, and these per-bucket
                // counts run into the thousands over a multi-month window.
                SampleCount = g.Sum(r => (long)r.SampleCount),
                FirstReading = g.Min(r => r.FirstReading),
                LastReading = g.Max(r => r.LastReading),
                MinTemperature = g.Min(r => r.MinTemperature),
                MaxTemperature = g.Max(r => r.MaxTemperature),
                SumTemperature = g.Sum(r => r.SumTemperature),
                MinPressure = g.Min(r => r.MinPressure),
                MaxPressure = g.Max(r => r.MaxPressure),
                SumPressure = g.Sum(r => r.SumPressure),
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null || row.SampleCount == 0)
        {
            return null;
        }

        return new TelemetryStatistics(
            machineId, row.SampleCount, row.FirstReading, row.LastReading,
            row.MinTemperature, row.MaxTemperature, Mean(row.SumTemperature, row.SampleCount),
            row.MinPressure, row.MaxPressure, Mean(row.SumPressure, row.SampleCount));
    }

    private async Task<FleetStatus> GetFleetStatusFromRollupsAsync(
        DateTimeOffset from,
        RollupGranularity granularity,
        CancellationToken cancellationToken)
    {
        var machineCount = await _context.TelemetryRollups
            .AsNoTracking()
            .Where(r => r.Granularity == granularity && r.BucketStart >= from)
            .Select(r => r.MachineId)
            .Distinct()
            .CountAsync(cancellationToken);

        var counts = await _context.TelemetryStatusRollups
            .AsNoTracking()
            .Where(r => r.Granularity == granularity && r.BucketStart >= from)
            .GroupBy(r => r.Status)
            .Select(g => new { Status = g.Key, Count = g.Sum(r => (long)r.Count) })
            .ToListAsync(cancellationToken);

        return BuildFleetStatus(machineCount, counts.Select(c => new StatusBreakdown(c.Status, c.Count)));
    }

    // ── Shared shaping ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Orders the status breakdown most-frequent-first in memory. The result set is a handful of
    /// rows either way, so sorting here keeps the SQL projection to the always-translatable
    /// "group key + aggregate" shape.
    /// </summary>
    private static FleetStatus BuildFleetStatus(int machineCount, IEnumerable<StatusBreakdown> breakdown)
    {
        var ordered = breakdown
            .OrderByDescending(b => b.Count)
            .ToList();

        return new FleetStatus(machineCount, ordered.Sum(b => b.Count), ordered);
    }

    private static double Mean(double sum, long count) => count == 0 ? 0 : sum / count;

    /// <summary>
    /// One machine's running totals while the roster tier and the live tail are being combined.
    /// Carries sums rather than averages for the same reason the rollup tables do: sums compose,
    /// averages do not — averaging two averages is only correct when both cover the same number of
    /// samples, which the roster (days) and the tail (minutes) never do.
    /// </summary>
    private sealed record RosterRow(
        string MachineId,
        long SampleCount,
        DateTimeOffset FirstSeen,
        DateTimeOffset LastSeen,
        double MinTemperature,
        double MaxTemperature,
        double SumTemperature,
        double MinPressure,
        double MaxPressure,
        double SumPressure)
    {
        public static RosterRow From(MachineSummary summary) => new(
            summary.MachineId, summary.SampleCount, summary.FirstSeen, summary.LastSeen,
            summary.MinTemperature, summary.MaxTemperature, summary.SumTemperature,
            summary.MinPressure, summary.MaxPressure, summary.SumPressure);

        /// <summary>
        /// Folds a later, non-overlapping range into this one. The frontier is what guarantees the
        /// two never overlap, so counts and sums simply add and the extents take the wider of the
        /// two — no reading is seen twice.
        /// </summary>
        public RosterRow Merge(RosterRow later)
        {
            // A zero-sample row has observed nothing, so its min/max/first/last are placeholders
            // rather than measurements: folding them in would drag the extents toward zero and
            // report a first-seen of year one.
            if (SampleCount == 0)
            {
                return later with { MachineId = MachineId };
            }

            if (later.SampleCount == 0)
            {
                return this;
            }

            return new RosterRow(
                MachineId,
                SampleCount + later.SampleCount,
                FirstSeen <= later.FirstSeen ? FirstSeen : later.FirstSeen,
                LastSeen >= later.LastSeen ? LastSeen : later.LastSeen,
                Math.Min(MinTemperature, later.MinTemperature),
                Math.Max(MaxTemperature, later.MaxTemperature),
                SumTemperature + later.SumTemperature,
                Math.Min(MinPressure, later.MinPressure),
                Math.Max(MaxPressure, later.MaxPressure),
                SumPressure + later.SumPressure);
        }

        public MachineTelemetrySummary ToSummary() => new(
            MachineId, SampleCount, FirstSeen, LastSeen,
            MinTemperature, MaxTemperature, Mean(SumTemperature, SampleCount),
            MinPressure, MaxPressure, Mean(SumPressure, SampleCount));
    }
}
