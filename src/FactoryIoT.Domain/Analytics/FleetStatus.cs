namespace FactoryIoT.Domain.Analytics;

/// <summary>
/// A fleet-wide health snapshot over a time window: how many machines are reporting, how many
/// readings arrived, and how those readings break down by operating status (Running / Warning / …).
/// This is the single call a monitoring dashboard makes to render its top-line "is the floor OK?".
/// </summary>
public sealed record FleetStatus(
    int MachineCount,
    long TotalReadings,
    IReadOnlyList<StatusBreakdown> Breakdown);

/// <summary>
/// The number of readings observed for one distinct <see cref="Status"/> value within the window.
/// </summary>
/// <remarks>
/// 64-bit because the window can span the whole retained history: fifty machines reporting once a
/// second overflow a 32-bit count after roughly sixteen months, and the sum is taken over
/// pre-aggregated buckets whose per-bucket counts are already in the thousands.
/// </remarks>
public sealed record StatusBreakdown(string Status, long Count);
