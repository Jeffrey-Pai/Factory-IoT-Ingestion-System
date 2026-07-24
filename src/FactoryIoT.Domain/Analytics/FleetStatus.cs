namespace FactoryIoT.Domain.Analytics;

/// <summary>
/// A fleet-wide health snapshot over a time window: how many machines are reporting, how many
/// readings arrived, and how those readings break down by operating status (Running / Warning / …).
/// This is the single call a monitoring dashboard makes to render its top-line "is the floor OK?".
/// </summary>
public sealed record FleetStatus(
    int MachineCount,
    int TotalReadings,
    IReadOnlyList<StatusBreakdown> Breakdown);

/// <summary>
/// The number of readings observed for one distinct <see cref="Status"/> value within the window.
/// </summary>
public sealed record StatusBreakdown(string Status, int Count);
