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
    /// reported, ordered by machine id — the fleet roster with health metrics attached. The
    /// figures are lifetime totals and are not narrowed by retention.
    /// </summary>
    /// <remarks>
    /// Unlike the windowed queries below, this one is fresh to the second: readings that have
    /// arrived since the last aggregation pass are included, so <c>LastSeen</c> is the machine's
    /// genuine most recent reading rather than the aggregation job's high-water mark. Callers may
    /// treat it as a liveness signal.
    /// </remarks>
    Task<IReadOnlyList<MachineTelemetrySummary>> GetMachineSummariesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns aggregate statistics for a single machine over readings at or after
    /// <paramref name="from"/>, or <c>null</c> when the machine reported nothing in that window.
    /// </summary>
    /// <remarks>
    /// Short windows are answered from raw readings and are exact to the second. Wider ones are
    /// answered from pre-aggregated buckets, so the result is exact up to the last <em>closed</em>
    /// bucket — the newest minute or two of data is not yet included. Callers that need
    /// to-the-second freshness should ask for a window inside the raw threshold.
    /// </remarks>
    Task<TelemetryStatistics?> GetStatisticsAsync(string machineId, DateTimeOffset from, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a fleet-wide health snapshot (reporting machines, reading volume, status breakdown)
    /// over readings at or after <paramref name="from"/>. Tier selection and its freshness
    /// trade-off match <see cref="GetStatisticsAsync"/>.
    /// </summary>
    Task<FleetStatus> GetFleetStatusAsync(DateTimeOffset from, CancellationToken cancellationToken = default);
}
