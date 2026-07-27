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
/// Read queries pick a storage tier from the width of the window they are asked about. Short
/// windows are answered from raw rows and are exact to the second; longer ones are answered from
/// pre-aggregated buckets, because a dashboard polling a 24-hour window every few seconds must not
/// re-read a day of raw telemetry each time. The cost of a rollup-backed answer is flat — a
/// 30-day window reads the same handful of buckets per machine whether the fleet has been running
/// for a week or a decade.
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
        var rows = await _context.MachineSummaries
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        // Ordered in memory with an ordinal comparer so EQP-001..EQP-050 sort predictably
        // regardless of the database's collation.
        return rows
            .OrderBy(r => r.MachineId, StringComparer.Ordinal)
            .Select(r => new MachineTelemetrySummary(
                r.MachineId, r.SampleCount, r.FirstSeen, r.LastSeen,
                r.MinTemperature, r.MaxTemperature, Mean(r.SumTemperature, r.SampleCount),
                r.MinPressure, r.MaxPressure, Mean(r.SumPressure, r.SampleCount)))
            .ToList();
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
}
