namespace FactoryIoT.Domain.Entities;

/// <summary>
/// One machine's numeric telemetry pre-aggregated over one time bucket — the "warm" storage tier
/// that lets dashboard queries answer questions about hours, days or months of history without
/// ever touching a raw row.
/// </summary>
/// <remarks>
/// <para>
/// Sums are stored instead of averages on purpose. An average is not re-aggregatable: averaging
/// sixty per-minute averages only equals the true hourly average when every minute contains the
/// same number of samples, which stops being true the moment a machine drops a reading. Keeping
/// <see cref="SumTemperature"/> alongside <see cref="SampleCount"/> makes every coarser bucket an
/// exact sum-of-sums, and the average is recovered at read time by division.
/// </para>
/// <para>
/// Likewise <see cref="FirstReading"/> / <see cref="LastReading"/> are the extents of the readings
/// that actually landed in the bucket, not the bucket boundaries — so a machine that reported for
/// ten seconds of a minute is reported honestly rather than as a full minute of coverage.
/// </para>
/// </remarks>
public sealed class TelemetryRollup
{
    public RollupGranularity Granularity { get; init; }
    public string MachineId { get; init; } = string.Empty;

    /// <summary>Inclusive UTC start of the bucket; the bucket ends at <c>BucketStart + Size(Granularity)</c>.</summary>
    public DateTimeOffset BucketStart { get; init; }

    public int SampleCount { get; init; }
    public DateTimeOffset FirstReading { get; init; }
    public DateTimeOffset LastReading { get; init; }

    public double MinTemperature { get; init; }
    public double MaxTemperature { get; init; }
    public double SumTemperature { get; init; }

    public double MinPressure { get; init; }
    public double MaxPressure { get; init; }
    public double SumPressure { get; init; }
}
