namespace FactoryIoT.Domain.Entities;

/// <summary>
/// The time resolution of a pre-aggregated telemetry bucket.
/// </summary>
/// <remarks>
/// The numeric values are persisted (they form part of the rollup tables' primary key), so they
/// must never be renumbered. Coarser granularities are derived from finer ones — an
/// <see cref="Hour"/> bucket is built by re-aggregating the 60 <see cref="Minute"/> buckets it
/// contains, never by re-scanning raw telemetry.
/// </remarks>
public enum RollupGranularity
{
    /// <summary>One bucket per machine per minute. Built directly from raw telemetry.</summary>
    Minute = 1,

    /// <summary>One bucket per machine per hour. Built by re-aggregating minute buckets.</summary>
    Hour = 2,
}
