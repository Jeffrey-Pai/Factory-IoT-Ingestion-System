using FactoryIoT.Domain.Analytics;
using FactoryIoT.Domain.Entities;

namespace FactoryIoT.Domain.Interfaces;

/// <summary>
/// Persistence contract for moving telemetry between storage tiers: folding raw readings into
/// pre-aggregated buckets, and dropping whatever has aged past its tier's retention window.
/// </summary>
/// <remarks>
/// The methods take primitives rather than a configuration object so the Domain layer stays free
/// of any dependency on how retention happens to be configured — the caller (the lifecycle worker)
/// owns that translation.
/// </remarks>
public interface IDataLifecycleRepository
{
    /// <summary>
    /// The start of the last bucket fully aggregated at this granularity, or <c>null</c> when the
    /// job has never run against this database.
    /// </summary>
    Task<DateTimeOffset?> GetCheckpointAsync(RollupGranularity granularity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a starting point for a granularity that has no checkpoint yet. Does nothing when a
    /// checkpoint already exists, so it is safe to call on every startup.
    /// </summary>
    Task SeedCheckpointAsync(RollupGranularity granularity, DateTimeOffset lastCompletedBucketStart, CancellationToken cancellationToken = default);

    /// <summary>Oldest raw telemetry timestamp still retained, or <c>null</c> when the table is empty.</summary>
    Task<DateTimeOffset?> GetOldestRawTimestampAsync(CancellationToken cancellationToken = default);

    /// <summary>Newest raw telemetry timestamp, or <c>null</c> when the table is empty.</summary>
    Task<DateTimeOffset?> GetNewestRawTimestampAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Aggregates one closed bucket and advances the checkpoint in a single transaction, so the
    /// bucket's contribution to the running machine summaries is applied exactly once. Minute
    /// buckets are built from raw telemetry; hour buckets are built by re-aggregating minute
    /// buckets. Returns the number of per-machine rows written.
    /// </summary>
    Task<int> BuildBucketAsync(RollupGranularity granularity, DateTimeOffset bucketStart, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes raw telemetry older than <paramref name="cutoff"/> in batches of
    /// <paramref name="batchSize"/>, stopping after <paramref name="maxBatches"/> batches so a
    /// single pass can never hold a long transaction or flood the log. Returns rows deleted.
    /// </summary>
    Task<int> PurgeRawTelemetryAsync(DateTimeOffset cutoff, int batchSize, int maxBatches, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="PurgeRawTelemetryAsync"/>
    Task<int> PurgeRawSensorReadingsAsync(DateTimeOffset cutoff, int batchSize, int maxBatches, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes rollup buckets of one granularity whose <c>BucketStart</c> is older than
    /// <paramref name="cutoff"/>, in the same bounded-batch fashion. Returns rows deleted across
    /// both the numeric and status rollup tables.
    /// </summary>
    Task<int> PurgeRollupsAsync(RollupGranularity granularity, DateTimeOffset cutoff, int batchSize, int maxBatches, CancellationToken cancellationToken = default);

    /// <summary>Builds the operational snapshot behind <c>GET /api/v1/data-lifecycle</c>.</summary>
    Task<DataLifecycleReport> GetReportAsync(RetentionWindows windows, CancellationToken cancellationToken = default);
}
