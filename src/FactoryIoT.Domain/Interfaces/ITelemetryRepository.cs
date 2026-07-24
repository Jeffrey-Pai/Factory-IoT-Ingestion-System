using FactoryIoT.Domain.Analytics;
using FactoryIoT.Domain.Entities;

namespace FactoryIoT.Domain.Interfaces;

/// <summary>
/// Persistence contract for telemetry data.
/// </summary>
public interface ITelemetryRepository
{
    Task AddRangeAsync(IEnumerable<Telemetry> telemetries, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Telemetry>> GetLatestByMachineAsync(string machineId, int count, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one rolled-up <see cref="MachineTelemetrySummary"/> per machine that has ever
    /// reported, ordered by machine id — the fleet roster with health metrics attached.
    /// </summary>
    Task<IReadOnlyList<MachineTelemetrySummary>> GetMachineSummariesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns aggregate statistics for a single machine over readings at or after
    /// <paramref name="from"/>, or <c>null</c> when the machine reported nothing in that window.
    /// </summary>
    Task<TelemetryStatistics?> GetStatisticsAsync(string machineId, DateTimeOffset from, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a fleet-wide health snapshot (reporting machines, reading volume, status breakdown)
    /// over readings at or after <paramref name="from"/>.
    /// </summary>
    Task<FleetStatus> GetFleetStatusAsync(DateTimeOffset from, CancellationToken cancellationToken = default);
}
