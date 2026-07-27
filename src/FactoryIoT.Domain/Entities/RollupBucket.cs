namespace FactoryIoT.Domain.Entities;

/// <summary>
/// Bucket arithmetic for the pre-aggregation tiers: how wide a bucket is, and which bucket a
/// given instant belongs to.
/// </summary>
/// <remarks>
/// Buckets are always anchored to UTC. Flooring in .NET (rather than in T-SQL) keeps the
/// aggregation queries free of date arithmetic: the rollup job asks for one closed bucket at a
/// time with a plain <c>Timestamp &gt;= start AND Timestamp &lt; end</c> range, which is a seek on
/// the time-leading clustered index no matter which database provider is underneath.
/// </remarks>
public static class RollupBucket
{
    /// <summary>The width of a single bucket at the given granularity.</summary>
    public static TimeSpan Size(RollupGranularity granularity) => granularity switch
    {
        RollupGranularity.Minute => TimeSpan.FromMinutes(1),
        RollupGranularity.Hour => TimeSpan.FromHours(1),
        _ => throw new ArgumentOutOfRangeException(
            nameof(granularity), granularity, "Unsupported rollup granularity."),
    };

    /// <summary>
    /// Returns the UTC start of the bucket that <paramref name="instant"/> falls into.
    /// Flooring an already-floored value is a no-op, which is what lets the rollup job treat a
    /// checkpoint seeded from a raw timestamp exactly like one it wrote itself.
    /// </summary>
    public static DateTimeOffset Floor(DateTimeOffset instant, RollupGranularity granularity)
    {
        var utcTicks = instant.ToUniversalTime().Ticks;
        var sizeTicks = Size(granularity).Ticks;
        return new DateTimeOffset(utcTicks - (utcTicks % sizeTicks), TimeSpan.Zero);
    }

    /// <summary>
    /// Returns the start of the next bucket after the one containing <paramref name="instant"/>.
    /// </summary>
    public static DateTimeOffset Next(DateTimeOffset instant, RollupGranularity granularity)
        => Floor(instant, granularity) + Size(granularity);
}
