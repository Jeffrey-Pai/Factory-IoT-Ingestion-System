namespace FactoryIoT.Domain.Analytics;

/// <summary>
/// A rolled-up, per-machine view of everything the fleet has reported for one machine,
/// used to answer the "give me the whole fleet at a glance" question without pulling raw rows.
/// </summary>
/// <remarks>
/// This is a read-model (an aggregate projection), not a persisted entity. It is served from the
/// <c>MachineSummaries</c> tier — one running row per machine, folded forward by the rollup job —
/// so the query stays a fifty-row read instead of the unbounded <c>GROUP BY MachineId</c> over the
/// whole raw table it used to be. The figures are therefore lifetime totals that survive
/// retention: <c>MaxTemperature</c> is the hottest the machine has ever run, not the hottest
/// within whatever raw history is still on disk.
/// </remarks>
public sealed record MachineTelemetrySummary(
    string MachineId,
    long SampleCount,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    double MinTemperature,
    double MaxTemperature,
    double AvgTemperature,
    double MinPressure,
    double MaxPressure,
    double AvgPressure);
