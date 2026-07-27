namespace FactoryIoT.Domain.Analytics;

/// <summary>
/// An operational snapshot of the storage tiers: what each tier currently holds, how far behind
/// the rollup job is, and how long each tier is configured to keep data.
/// </summary>
/// <remarks>
/// Deliberately free of <c>COUNT(*)</c> over the raw tables. Counting rows in the hot tier is the
/// very anti-pattern this design removes — it reads every page just to produce a number nobody
/// acts on. The cheap questions ("what is the oldest row we still hold?", "is the rollup job
/// keeping up?") are answered instead, and each is a single index seek.
/// </remarks>
public sealed record DataLifecycleReport(
    DateTimeOffset GeneratedAt,
    StorageTierState RawTelemetry,
    StorageTierState RawSensorReadings,
    RollupTierState MinuteRollups,
    RollupTierState HourRollups,
    int TrackedMachines);

/// <summary>
/// The retention window of each tier, expressed in hours. Passed into the report so the snapshot
/// can show configured-vs-actual side by side without the Domain layer knowing anything about how
/// retention is configured.
/// </summary>
public sealed record RetentionWindows(
    double RawTelemetryHours,
    double RawSensorReadingHours,
    double MinuteRollupHours,
    double HourRollupHours);

/// <summary>
/// The state of one raw (hot) tier: the extent of the data still retained, and the retention
/// window that governs it.
/// </summary>
public sealed record StorageTierState(
    string Table,
    DateTimeOffset? OldestRetained,
    DateTimeOffset? NewestRetained,
    double RetentionHours);

/// <summary>
/// The state of one pre-aggregated tier, including how far behind real time the rollup job is.
/// A steadily growing <see cref="LagSeconds"/> is the signal that aggregation can no longer keep
/// up with ingestion.
/// </summary>
public sealed record RollupTierState(
    string Granularity,
    long BucketCount,
    DateTimeOffset? OldestBucket,
    DateTimeOffset? LastCompletedBucket,
    double? LagSeconds,
    double RetentionHours);
