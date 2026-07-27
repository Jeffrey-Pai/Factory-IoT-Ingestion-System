namespace FactoryIoT.Domain.Entities;

/// <summary>
/// How many readings one machine reported under one operating status during one time bucket.
/// </summary>
/// <remarks>
/// Status is open-ended text (<c>Running</c>, <c>Warning</c>, and whatever a future machine model
/// reports), so it cannot live as fixed columns on <see cref="TelemetryRollup"/> without a schema
/// change per new status. Splitting it into its own narrow table keeps the numeric rollup
/// fixed-width and lets the fleet-status query stay a single indexed
/// <c>GROUP BY Status</c> that returns a handful of rows.
/// </remarks>
public sealed class TelemetryStatusRollup
{
    public RollupGranularity Granularity { get; init; }

    /// <summary>Inclusive UTC start of the bucket; matches <see cref="TelemetryRollup.BucketStart"/>.</summary>
    public DateTimeOffset BucketStart { get; init; }

    public string MachineId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public int Count { get; init; }
}
