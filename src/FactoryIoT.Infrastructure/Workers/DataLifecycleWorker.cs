using FactoryIoT.Domain.Entities;
using FactoryIoT.Domain.Interfaces;
using FactoryIoT.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Prometheus;

namespace FactoryIoT.Infrastructure.Workers;

/// <summary>
/// Background worker that keeps the database a fixed size instead of an ever-growing one: it folds
/// closed time buckets into the pre-aggregated tiers, and deletes whatever has aged past its
/// tier's retention window.
/// </summary>
/// <remarks>
/// <para>
/// Ingestion and lifecycle are separate workers on purpose. Aggregating and purging are bulk
/// operations measured in seconds; the ingestion path is a latency-sensitive loop that must never
/// be blocked behind them. Keeping them apart also means a failure in one cannot stall the other —
/// a database busy enough to fail a purge still accepts telemetry.
/// </para>
/// <para>
/// Every pass is ordered aggregate-then-purge, and the purge cutoffs are clamped to how far
/// aggregation actually got. That ordering is the whole safety story: raw rows are only ever
/// deleted after their bucket exists, and minute buckets only after the hour bucket that
/// supersedes them exists. If aggregation stalls, purging stalls with it and the database grows —
/// which is recoverable — rather than deleting data that was never summarised, which is not.
/// </para>
/// </remarks>
public sealed class DataLifecycleWorker : BackgroundService
{
    private static readonly Counter BucketsBuiltCounter = Metrics.CreateCounter(
        "telemetry_rollup_buckets_total",
        "Total number of pre-aggregated time buckets built, by granularity",
        "granularity");

    private static readonly Counter RollupRowsCounter = Metrics.CreateCounter(
        "telemetry_rollup_rows_total",
        "Total number of per-machine rollup rows written, by granularity",
        "granularity");

    private static readonly Counter RowsPurgedCounter = Metrics.CreateCounter(
        "telemetry_rows_purged_total",
        "Total number of rows deleted by retention enforcement, by storage tier",
        "tier");

    private static readonly Gauge RollupLagGauge = Metrics.CreateGauge(
        "telemetry_rollup_lag_seconds",
        "Seconds between now and the end of the most recently aggregated bucket, by granularity",
        "granularity");

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DataRetentionOptions _options;
    private readonly ILogger<DataLifecycleWorker> _logger;

    private DateTimeOffset? _lastPassCompleted;
    private bool _lastPassSucceeded = true;

    public DataLifecycleWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<DataRetentionOptions> options,
        ILogger<DataLifecycleWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>When the last pass finished, successfully or not. Null until the first pass runs.</summary>
    public DateTimeOffset? LastPassCompleted => _lastPassCompleted;

    /// <summary>Whether the most recent pass completed without throwing.</summary>
    public bool LastPassSucceeded => _lastPassSucceeded;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogWarning(
                "Data lifecycle worker is DISABLED (DataRetention:Enabled=false). Nothing will be " +
                "aggregated or purged, and the database will grow without bound.");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(5, _options.RollupIntervalSeconds));

        _logger.LogInformation(
            "Data lifecycle worker starting. Interval: {IntervalSeconds}s, lag: {LagSeconds}s. " +
            "Retention — raw telemetry {RawTelemetryHours}h, sensor readings {RawSensorHours}h, " +
            "minute buckets {MinuteHours}h, hour buckets {HourHours}h.",
            interval.TotalSeconds, _options.RollupLagSeconds,
            _options.RawTelemetryHours, _options.RawSensorReadingHours,
            _options.MinuteRollupHours, _options.HourRollupHours);

        using var timer = new PeriodicTimer(interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
                await RunPassAsync(stoppingToken);
                _lastPassSucceeded = true;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Data lifecycle worker is stopping.");
                break;
            }
            catch (Exception ex)
            {
                // A failed pass is not a failed worker. Aggregation resumes from its checkpoint and
                // purging is idempotent, so the next tick simply retries whatever did not finish.
                _lastPassSucceeded = false;
                _logger.LogError(ex, "Data lifecycle pass failed. Retrying on the next tick.");
            }
            finally
            {
                _lastPassCompleted = DateTimeOffset.UtcNow;
            }
        }
    }

    private async Task RunPassAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IDataLifecycleRepository>();

        var now = DateTimeOffset.UtcNow;
        await EnsureCheckpointsAsync(repository, now, cancellationToken);

        // Minute buckets come from raw telemetry, and are only considered closed once the lag
        // window has passed — long enough for anything still queued in RabbitMQ or sitting in the
        // ingestion channel to have landed. A bucket aggregated too early is silently incomplete
        // forever, because nothing ever revisits it.
        var minuteCeiling = now - TimeSpan.FromSeconds(Math.Max(0, _options.RollupLagSeconds));
        var minuteFrontier = await RollUpAsync(
            repository, RollupGranularity.Minute, now, minuteCeiling, cancellationToken);

        // Hour buckets come from minute buckets, so their ceiling is however far minute
        // aggregation has actually reached — never the wall clock.
        var hourFrontier = minuteFrontier is null
            ? null
            : await RollUpAsync(
                repository, RollupGranularity.Hour, now, minuteFrontier.Value, cancellationToken);

        // A null frontier means aggregation could not establish how far it has got. Purging is
        // skipped entirely rather than guessed at: an unknown frontier must never be read as
        // "everything is aggregated", because that is exactly the reading that would delete raw
        // rows nothing has summarised.
        if (minuteFrontier is null || hourFrontier is null)
        {
            _logger.LogWarning("Aggregation frontier unknown this pass; skipping retention purge.");
            return;
        }

        await PurgeAsync(repository, now, minuteFrontier.Value, hourFrontier.Value, cancellationToken);
    }

    /// <summary>
    /// Gives each granularity a starting point the first time this database is seen. On a fresh
    /// database that is the oldest raw reading (so nothing already ingested is skipped); on an
    /// empty one it is the current bucket. Upgrades arrive with checkpoints already seeded by the
    /// migration, and seeding is a no-op when a checkpoint exists.
    /// </summary>
    private async Task EnsureCheckpointsAsync(
        IDataLifecycleRepository repository,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var minuteCheckpoint = await repository.GetCheckpointAsync(RollupGranularity.Minute, cancellationToken);
        var hourCheckpoint = await repository.GetCheckpointAsync(RollupGranularity.Hour, cancellationToken);

        if (minuteCheckpoint is not null && hourCheckpoint is not null)
        {
            return;
        }

        var basis = await repository.GetOldestRawTimestampAsync(cancellationToken) ?? now;

        _logger.LogInformation(
            "No rollup checkpoint found; seeding aggregation to start at {Basis:o}.", basis);

        // Seed one bucket *before* the basis so the bucket containing it is the first one built.
        foreach (var granularity in new[] { RollupGranularity.Minute, RollupGranularity.Hour })
        {
            var seed = RollupBucket.Floor(basis, granularity) - RollupBucket.Size(granularity);
            await repository.SeedCheckpointAsync(granularity, seed, cancellationToken);
        }
    }

    /// <summary>
    /// Builds every closed bucket between the checkpoint and <paramref name="ceiling"/>, up to the
    /// per-pass budget, and returns the exclusive end of the aggregated range — or <c>null</c> when
    /// there is no checkpoint to work from, meaning the frontier is unknown.
    /// </summary>
    private async Task<DateTimeOffset?> RollUpAsync(
        IDataLifecycleRepository repository,
        RollupGranularity granularity,
        DateTimeOffset now,
        DateTimeOffset ceiling,
        CancellationToken cancellationToken)
    {
        var size = RollupBucket.Size(granularity);
        var label = granularity.ToString();

        var checkpoint = await repository.GetCheckpointAsync(granularity, cancellationToken);
        if (checkpoint is null)
        {
            // EnsureCheckpointsAsync runs first, so this only happens if the seed lost a race with
            // another instance. The next pass will find the checkpoint the winner wrote.
            _logger.LogWarning("No {Granularity} checkpoint available this pass; skipping.", label);
            return null;
        }

        // A bucket is buildable once it has fully elapsed relative to the ceiling.
        var lastBuildable = RollupBucket.Floor(ceiling, granularity) - size;
        var budget = Math.Max(1, _options.MaxBucketsPerPass);

        var bucket = checkpoint.Value + size;
        var bucketsBuilt = 0;
        var rowsWritten = 0;

        while (bucket <= lastBuildable && bucketsBuilt < budget && !cancellationToken.IsCancellationRequested)
        {
            rowsWritten += await repository.BuildBucketAsync(granularity, bucket, cancellationToken);
            bucketsBuilt++;
            bucket += size;
        }

        if (bucketsBuilt > 0)
        {
            BucketsBuiltCounter.WithLabels(label).Inc(bucketsBuilt);
            RollupRowsCounter.WithLabels(label).Inc(rowsWritten);

            _logger.LogInformation(
                "Built {BucketCount} {Granularity} bucket(s) ({RowCount} rows), through {Through:o}.",
                bucketsBuilt, label, rowsWritten, bucket - size);
        }

        if (bucket <= lastBuildable)
        {
            _logger.LogWarning(
                "{Granularity} aggregation hit its per-pass budget of {Budget} buckets and is still " +
                "behind (next bucket {Next:o}, buildable through {LastBuildable:o}). It will keep " +
                "catching up on subsequent passes.",
                label, budget, bucket, lastBuildable);
        }

        RollupLagGauge.WithLabels(label).Set((now - bucket).TotalSeconds);

        // Everything before `bucket` is aggregated; `bucket` is the next one to build.
        return bucket;
    }

    private async Task PurgeAsync(
        IDataLifecycleRepository repository,
        DateTimeOffset now,
        DateTimeOffset minuteFrontier,
        DateTimeOffset hourFrontier,
        CancellationToken cancellationToken)
    {
        var batchSize = _options.PurgeBatchSize;
        var maxBatches = _options.MaxPurgeBatchesPerPass;

        // Raw telemetry is the source for minute buckets, so it can never be deleted past where
        // minute aggregation has reached — otherwise a stalled rollup job would quietly destroy
        // readings that were never summarised.
        var rawTelemetryCutoff = Earlier(
            now - Hours(_options.RawTelemetryHours),
            minuteFrontier);

        // Sensor readings feed no tier — they are a re-shaping of telemetry that has already been
        // aggregated from the wide table — so their cutoff needs no such guard.
        var sensorReadingCutoff = now - Hours(_options.RawSensorReadingHours);

        // Minute buckets are the source for hour buckets, and are clamped the same way.
        var minuteRollupCutoff = Earlier(
            now - Hours(_options.MinuteRollupHours),
            hourFrontier);

        var hourRollupCutoff = now - Hours(_options.HourRollupHours);

        var purgedTelemetry = await repository.PurgeRawTelemetryAsync(
            rawTelemetryCutoff, batchSize, maxBatches, cancellationToken);
        var purgedSensorReadings = await repository.PurgeRawSensorReadingsAsync(
            sensorReadingCutoff, batchSize, maxBatches, cancellationToken);
        var purgedMinuteBuckets = await repository.PurgeRollupsAsync(
            RollupGranularity.Minute, minuteRollupCutoff, batchSize, maxBatches, cancellationToken);
        var purgedHourBuckets = await repository.PurgeRollupsAsync(
            RollupGranularity.Hour, hourRollupCutoff, batchSize, maxBatches, cancellationToken);

        Record("telemetries", purgedTelemetry);
        Record("sensor_readings", purgedSensorReadings);
        Record("minute_rollups", purgedMinuteBuckets);
        Record("hour_rollups", purgedHourBuckets);

        var total = purgedTelemetry + purgedSensorReadings + purgedMinuteBuckets + purgedHourBuckets;
        if (total > 0)
        {
            _logger.LogInformation(
                "Retention purge removed {Total} row(s): {Telemetry} telemetry (older than {TelemetryCutoff:o}), " +
                "{SensorReadings} sensor readings, {MinuteBuckets} minute buckets, {HourBuckets} hour buckets.",
                total, purgedTelemetry, rawTelemetryCutoff, purgedSensorReadings,
                purgedMinuteBuckets, purgedHourBuckets);
        }

        static void Record(string tier, int rows)
        {
            if (rows > 0)
            {
                RowsPurgedCounter.WithLabels(tier).Inc(rows);
            }
        }
    }

    private static TimeSpan Hours(double hours) => TimeSpan.FromHours(Math.Max(0, hours));

    private static DateTimeOffset Earlier(DateTimeOffset left, DateTimeOffset right)
        => left <= right ? left : right;
}
