namespace FactoryIoT.Domain.Entities;

/// <summary>
/// One row per machine holding its running lifetime totals — the materialized answer to
/// "give me the whole fleet at a glance".
/// </summary>
/// <remarks>
/// <para>
/// This table exists because the fleet roster is a <em>running</em> aggregate, and recomputing it
/// from scratch on every dashboard poll is the one query whose cost grows without bound: a
/// <c>GROUP BY MachineId</c> over the raw table reads every row ever ingested, so it degrades from
/// milliseconds on day one to a full scan of hundreds of millions of rows a month later, and drags
/// the entire table through the buffer pool on the way. The rollup job folds each completed minute
/// bucket into these rows instead, turning the roster query into a fifty-row read that costs the
/// same on day 1000 as on day 1.
/// </para>
/// <para>
/// The totals are lifetime figures and are deliberately <em>not</em> affected by retention: once
/// raw rows are purged, <see cref="MaxTemperature"/> still reports the hottest that machine has
/// ever run. Minimum and maximum cannot be un-accumulated anyway, and "hottest ever observed" is
/// the more useful answer for a machine roster than "hottest within whatever window we still keep".
/// </para>
/// <para>
/// Because the fold is an accumulation rather than a recomputation, it is only safe when each
/// bucket is applied exactly once — which the transactional <see cref="RollupCheckpoint"/>
/// guarantees. Manually rewinding a checkpoint therefore requires clearing this table too,
/// otherwise the replayed buckets are counted twice.
/// </para>
/// </remarks>
public sealed class MachineSummary
{
    public string MachineId { get; set; } = string.Empty;

    /// <summary>
    /// Total readings ever ingested for this machine. 64-bit because a single machine reporting
    /// once a second exhausts a 32-bit counter in roughly 68 years — cheap insurance for a value
    /// that only ever increases.
    /// </summary>
    public long SampleCount { get; set; }

    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }

    public double MinTemperature { get; set; }
    public double MaxTemperature { get; set; }

    /// <summary>Running sum, divided by <see cref="SampleCount"/> at read time to yield the average.</summary>
    public double SumTemperature { get; set; }

    public double MinPressure { get; set; }
    public double MaxPressure { get; set; }
    public double SumPressure { get; set; }
}
