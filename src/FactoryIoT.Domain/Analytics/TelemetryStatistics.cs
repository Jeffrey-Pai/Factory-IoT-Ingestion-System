namespace FactoryIoT.Domain.Analytics;

/// <summary>
/// Aggregate statistics for a single machine over a bounded time window — the classic IoT
/// analytics question "how has EQP-001 behaved in the last hour?" answered in one round-trip.
/// </summary>
/// <remarks>
/// Like <see cref="MachineTelemetrySummary"/> this is a read-model produced by a grouped
/// aggregate query, not a persisted entity. <see cref="FirstReading"/> / <see cref="LastReading"/>
/// are the extents of the data that actually fell inside the requested window (which can be
/// narrower than the window itself when a machine reports intermittently).
/// </remarks>
public sealed record TelemetryStatistics(
    string MachineId,
    int SampleCount,
    DateTimeOffset FirstReading,
    DateTimeOffset LastReading,
    double MinTemperature,
    double MaxTemperature,
    double AvgTemperature,
    double MinPressure,
    double MaxPressure,
    double AvgPressure);
