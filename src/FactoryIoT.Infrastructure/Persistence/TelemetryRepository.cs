using FactoryIoT.Domain.Analytics;
using FactoryIoT.Domain.Entities;
using FactoryIoT.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FactoryIoT.Infrastructure.Persistence;

/// <summary>
/// EF Core implementation of telemetry repository.
/// </summary>
public sealed class TelemetryRepository : ITelemetryRepository
{
    private readonly FactoryIoTDbContext _context;

    public TelemetryRepository(FactoryIoTDbContext context)
    {
        _context = context;
    }

    public async Task AddRangeAsync(IEnumerable<Telemetry> telemetries, CancellationToken cancellationToken = default)
    {
        await _context.Telemetries.AddRangeAsync(telemetries, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Telemetry>> GetLatestByMachineAsync(string machineId, int count, CancellationToken cancellationToken = default)
    {
        return await _context.Telemetries
            .AsNoTracking()
            .Where(t => t.MachineId == machineId)
            .OrderByDescending(t => t.Timestamp)
            .Take(count)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MachineTelemetrySummary>> GetMachineSummariesAsync(CancellationToken cancellationToken = default)
    {
        // One GROUP BY MachineId over the whole table yields the fleet roster with health metrics.
        // We project to an anonymous type (the canonical translatable shape for grouped aggregates)
        // and materialize the domain record in memory, then order EQP-001..EQP-050 with an ordinal
        // comparer so paging/display is stable regardless of collation.
        var rows = await _context.Telemetries
            .AsNoTracking()
            .GroupBy(t => t.MachineId)
            .Select(g => new
            {
                MachineId = g.Key,
                SampleCount = g.Count(),
                FirstSeen = g.Min(t => t.Timestamp),
                LastSeen = g.Max(t => t.Timestamp),
                MinTemperature = g.Min(t => t.Temperature),
                MaxTemperature = g.Max(t => t.Temperature),
                AvgTemperature = g.Average(t => t.Temperature),
                MinPressure = g.Min(t => t.Pressure),
                MaxPressure = g.Max(t => t.Pressure),
                AvgPressure = g.Average(t => t.Pressure),
            })
            .ToListAsync(cancellationToken);

        return rows
            .OrderBy(r => r.MachineId, StringComparer.Ordinal)
            .Select(r => new MachineTelemetrySummary(
                r.MachineId, r.SampleCount, r.FirstSeen, r.LastSeen,
                r.MinTemperature, r.MaxTemperature, r.AvgTemperature,
                r.MinPressure, r.MaxPressure, r.AvgPressure))
            .ToList();
    }

    public async Task<TelemetryStatistics?> GetStatisticsAsync(string machineId, DateTimeOffset from, CancellationToken cancellationToken = default)
    {
        // Grouping the windowed rows means an empty window produces no group, so FirstOrDefault
        // returns null — the natural "this machine reported nothing in that window" signal — instead
        // of a bare aggregate over zero rows (which would throw for Min/Max/Average).
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

    public async Task<FleetStatus> GetFleetStatusAsync(DateTimeOffset from, CancellationToken cancellationToken = default)
    {
        var windowed = _context.Telemetries
            .AsNoTracking()
            .Where(t => t.Timestamp >= from);

        // How many distinct machines checked in during the window.
        var machineCount = await windowed
            .Select(t => t.MachineId)
            .Distinct()
            .CountAsync(cancellationToken);

        // Reading volume grouped by operating status (Running / Warning / …). The result set is a
        // handful of rows, so the descending sort is done in memory to keep the SQL projection to
        // the always-translatable "group key + count" shape.
        var counts = await windowed
            .GroupBy(t => t.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var breakdown = counts
            .OrderByDescending(c => c.Count)
            .Select(c => new StatusBreakdown(c.Status, c.Count))
            .ToList();

        var totalReadings = breakdown.Sum(b => b.Count);

        return new FleetStatus(machineCount, totalReadings, breakdown);
    }
}
