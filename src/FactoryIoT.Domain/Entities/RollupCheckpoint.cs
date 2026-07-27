namespace FactoryIoT.Domain.Entities;

/// <summary>
/// How far the rollup job has got at one granularity: the start of the last bucket it finished.
/// </summary>
/// <remarks>
/// <para>
/// The checkpoint is written in the same transaction as the buckets it describes, which is what
/// makes the job crash-safe. A process that dies mid-bucket rolls back the partial aggregate and
/// the checkpoint together, so the bucket is simply rebuilt on the next pass — never skipped, and
/// never counted twice into the running <see cref="MachineSummary"/> totals.
/// </para>
/// <para>
/// A checkpoint could in principle be derived from <c>MAX(BucketStart)</c> in the rollup table, but
/// that breaks down for a bucket in which no machine reported anything: no row is written, the
/// derived watermark never advances, and the job re-scans the same empty window forever. An
/// explicit two-row table avoids that dead end.
/// </para>
/// </remarks>
public sealed class RollupCheckpoint
{
    public RollupGranularity Granularity { get; init; }

    /// <summary>
    /// UTC start of the last bucket that was completely aggregated. The value is floored to the
    /// granularity before use, so a checkpoint seeded from a raw telemetry timestamp (as the
    /// migration does when upgrading a database that already holds data) behaves identically to
    /// one the job wrote itself.
    /// </summary>
    public DateTimeOffset LastCompletedBucketStart { get; set; }
}
