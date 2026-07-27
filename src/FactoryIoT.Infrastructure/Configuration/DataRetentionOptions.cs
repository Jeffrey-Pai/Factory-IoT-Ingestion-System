using FactoryIoT.Domain.Analytics;

namespace FactoryIoT.Infrastructure.Configuration;

/// <summary>
/// How long each storage tier keeps data, and how hard the lifecycle worker is allowed to push
/// while moving data between tiers. Bound from the <c>DataRetention</c> configuration section.
/// </summary>
/// <remarks>
/// The defaults are sized for the shipped workload (50 machines × 1 reading/second) on SQL Server
/// Express, whose 10 GB per-database ceiling is a hard wall rather than a performance cliff. They
/// land the whole database at roughly 2 GB in steady state. See <c>docs/DATA-LIFECYCLE.md</c> for
/// the arithmetic and for how to re-size them when the machine count grows.
/// </remarks>
public sealed class DataRetentionOptions
{
    public const string SectionName = "DataRetention";

    /// <summary>
    /// Master switch. When false the worker does not start at all: nothing is aggregated and
    /// nothing is purged. Useful for reproducing a problem against untouched raw data — but
    /// leaving it off is what lets the database grow without bound.
    /// </summary>
    public bool Enabled { get; set; } = true;

    // ── Retention windows ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// How long full-fidelity <c>Telemetries</c> rows are kept. This is the tier that dominates
    /// storage, and the only one whose cost scales with the sampling rate rather than with the
    /// machine count.
    /// </summary>
    public double RawTelemetryHours { get; set; } = 72;

    /// <summary>
    /// How long <c>SensorReadings</c> rows are kept — shorter than raw telemetry by default, and
    /// deliberately so. Every sensor reading is mechanically derived from a telemetry snapshot by
    /// <see cref="Domain.Entities.SensorReading.FromTelemetry"/>, so while both tables are at full
    /// retention the narrow table costs two-thirds of all rows written and adds no information
    /// that the wide table does not already hold.
    /// </summary>
    public double RawSensorReadingHours { get; set; } = 24;

    /// <summary>How long per-minute buckets are kept — the tier that answers day-and-week questions.</summary>
    public double MinuteRollupHours { get; set; } = 24 * 30;

    /// <summary>How long per-hour buckets are kept — the long-term trend tier. Cheap: ~1,200 rows per day.</summary>
    public double HourRollupHours { get; set; } = 24 * 365 * 2;

    // ── Rollup pacing ───────────────────────────────────────────────────────────────────────

    /// <summary>How often the lifecycle worker wakes up to aggregate and purge.</summary>
    public int RollupIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// How long a bucket must be closed before it is aggregated. This is the safety margin for
    /// data still in flight — queued in RabbitMQ, sitting in the ingestion channel, or waiting on
    /// the batch timer. Aggregating too eagerly bakes an incomplete bucket into the warm tier,
    /// where nothing ever revisits it.
    /// </summary>
    public int RollupLagSeconds { get; set; } = 120;

    /// <summary>
    /// Upper bound on buckets aggregated per pass, per granularity. Caps the work a single pass
    /// can do when the worker restarts after a long outage and has hours of backlog to chew
    /// through, so catching up never turns into one enormous transaction.
    /// </summary>
    public int MaxBucketsPerPass { get; set; } = 240;

    // ── Purge pacing ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Rows deleted per statement. Deleting a day's telemetry in one statement would hold locks
    /// and transaction log for the duration; small batches keep each delete short and let the log
    /// be reclaimed between them.
    /// </summary>
    public int PurgeBatchSize { get; set; } = 5_000;

    /// <summary>
    /// Upper bound on delete batches per table per pass. At the shipped rate a steady-state pass
    /// only needs one or two; the headroom is for catching up after retention is shortened.
    /// </summary>
    public int MaxPurgeBatchesPerPass { get; set; } = 40;

    // ── Read routing ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Windows no longer than this are answered from raw rows (exact, to the second); longer
    /// windows are answered from pre-aggregated buckets. The default comfortably covers the
    /// dashboard's live presets (15 minutes, 1 hour) while sending its 6-hour and 24-hour views —
    /// the ones that would otherwise scan millions of rows on every poll — to the rollup tier.
    /// </summary>
    public int RawQueryWindowMinutes { get; set; } = 180;

    /// <summary>
    /// How far back the fleet roster may reach for readings the rollup job has not folded in yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The roster is only ever as fresh as the last <em>closed</em> minute bucket, which trails the
    /// wall clock by a bucket width plus <see cref="RollupLagSeconds"/> — so on its own it reports
    /// a machine reporting once a second as last seen three minutes ago. The roster query therefore
    /// overlays the raw readings newer than the aggregation frontier, and this is the clamp on how
    /// far back that overlay may look when the frontier is old or unknown.
    /// </para>
    /// <para>
    /// It exists purely to bound the read: with a healthy rollup job the frontier is minutes old
    /// and the clamp never binds. It only takes effect when aggregation has stalled or is switched
    /// off, and then it trades completeness for a bounded query — the roster's totals under-report
    /// the un-aggregated gap, but <c>LastSeen</c> stays truthful and the dashboard keeps working
    /// instead of scanning an ever-growing table on every poll.
    /// </para>
    /// </remarks>
    public int RosterTailMinutes { get; set; } = 15;

    /// <summary>Projects the retention windows into the Domain shape used by the lifecycle report.</summary>
    public RetentionWindows ToRetentionWindows() => new(
        RawTelemetryHours,
        RawSensorReadingHours,
        MinuteRollupHours,
        HourRollupHours);
}
