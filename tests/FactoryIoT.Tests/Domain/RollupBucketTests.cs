using FactoryIoT.Domain.Entities;
using Xunit;

namespace FactoryIoT.Tests.Domain;

/// <summary>
/// Bucket arithmetic underpins both aggregation and retention: a bucket boundary that drifts by a
/// tick either double-counts readings or silently drops them.
/// </summary>
public sealed class RollupBucketTests
{
    [Fact]
    public void Floor_TruncatesToTheStartOfTheContainingBucket()
    {
        var instant = new DateTimeOffset(2026, 7, 27, 13, 42, 37, 512, TimeSpan.Zero);

        Assert.Equal(
            new DateTimeOffset(2026, 7, 27, 13, 42, 0, TimeSpan.Zero),
            RollupBucket.Floor(instant, RollupGranularity.Minute));

        Assert.Equal(
            new DateTimeOffset(2026, 7, 27, 13, 0, 0, TimeSpan.Zero),
            RollupBucket.Floor(instant, RollupGranularity.Hour));
    }

    [Fact]
    public void Floor_IsIdempotent()
    {
        // The upgrade migration seeds the checkpoint with a raw telemetry timestamp and relies on
        // the reader to floor it. Flooring an already-floored value must not shift it.
        var instant = new DateTimeOffset(2026, 7, 27, 13, 42, 37, TimeSpan.Zero);

        foreach (var granularity in new[] { RollupGranularity.Minute, RollupGranularity.Hour })
        {
            var once = RollupBucket.Floor(instant, granularity);
            Assert.Equal(once, RollupBucket.Floor(once, granularity));
        }
    }

    [Fact]
    public void Floor_NormalisesToUtcRatherThanTruncatingLocalTime()
    {
        // 13:42:37+08:00 is 05:42:37Z — the bucket is chosen by the instant, not by the wall-clock
        // reading of whatever offset the producer happened to send.
        var instant = new DateTimeOffset(2026, 7, 27, 13, 42, 37, TimeSpan.FromHours(8));

        var bucket = RollupBucket.Floor(instant, RollupGranularity.Hour);

        Assert.Equal(new DateTimeOffset(2026, 7, 27, 5, 0, 0, TimeSpan.Zero), bucket);
        Assert.Equal(TimeSpan.Zero, bucket.Offset);
    }

    [Fact]
    public void Floor_LeavesAnExactBoundaryUntouched()
    {
        var boundary = new DateTimeOffset(2026, 7, 27, 13, 0, 0, TimeSpan.Zero);

        Assert.Equal(boundary, RollupBucket.Floor(boundary, RollupGranularity.Minute));
        Assert.Equal(boundary, RollupBucket.Floor(boundary, RollupGranularity.Hour));
    }

    [Fact]
    public void Next_ReturnsTheFollowingBucketStart()
    {
        var instant = new DateTimeOffset(2026, 7, 27, 13, 42, 37, TimeSpan.Zero);

        Assert.Equal(
            new DateTimeOffset(2026, 7, 27, 13, 43, 0, TimeSpan.Zero),
            RollupBucket.Next(instant, RollupGranularity.Minute));

        Assert.Equal(
            new DateTimeOffset(2026, 7, 27, 14, 0, 0, TimeSpan.Zero),
            RollupBucket.Next(instant, RollupGranularity.Hour));
    }

    [Fact]
    public void Size_MatchesTheGranularity()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), RollupBucket.Size(RollupGranularity.Minute));
        Assert.Equal(TimeSpan.FromHours(1), RollupBucket.Size(RollupGranularity.Hour));
    }

    [Fact]
    public void Size_RejectsAnUnknownGranularity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RollupBucket.Size((RollupGranularity)99));
    }
}
