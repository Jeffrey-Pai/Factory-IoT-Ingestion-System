namespace FactoryIoT.Domain.Analytics;

/// <summary>
/// A rolled-up, per-machine view of everything the fleet has reported for one machine,
/// used to answer the "give me the whole fleet at a glance" question without pulling raw rows.
/// </summary>
/// <remarks>
/// This is a read-model (an aggregate projection), not a persisted entity: it is produced by a
/// <c>GROUP BY MachineId</c> over the <c>Telemetries</c> table, so it carries only aggregate
/// values (count, extents, averages) rather than any single row. The shape is intentionally flat
/// so it maps directly to a translatable SQL projection (see <c>TelemetryRepository</c>).
/// </remarks>
public sealed record MachineTelemetrySummary(
    string MachineId,
    int SampleCount,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    double MinTemperature,
    double MaxTemperature,
    double AvgTemperature,
    double MinPressure,
    double MaxPressure,
    double AvgPressure);
