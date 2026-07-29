using System.Diagnostics;
using System.Threading.Channels;
using FactoryIoT.Application.Common.Interfaces;
using FactoryIoT.Domain.Entities;
using FactoryIoT.Domain.Interfaces;
using FactoryIoT.Infrastructure.Messaging;
using FactoryIoT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Prometheus;

namespace FactoryIoT.Infrastructure.Workers;

/// <summary>
/// Background worker that consumes telemetry from RabbitMQ, buffers using Channels,
/// and bulk inserts to MSSQL. Tracks metrics with prometheus-net.
/// </summary>
public sealed class TelemetryIngestionWorker : BackgroundService
{
    private const int BatchSize = 100;

    // Ceiling on messages buffered in memory between RabbitMQ and the database. Roughly six
    // minutes of headroom at the shipped rate — enough to ride out a slow batch or a brief
    // database stall without anything reaching the writer's retry path.
    private const int ChannelCapacity = 20_000;

    private static readonly TimeSpan BatchInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PipelineRestartDelay = TimeSpan.FromSeconds(5);

    private static readonly Counter TelemetriesWrittenCounter = Metrics.CreateCounter(
        "telemetry_written_total",
        "Total number of telemetry records written to database");

    private static readonly Counter TelemetriesConsumedCounter = Metrics.CreateCounter(
        "telemetry_consumed_total",
        "Total number of telemetry messages consumed from RabbitMQ");

    private static readonly Counter SensorReadingsWrittenCounter = Metrics.CreateCounter(
        "sensor_readings_written_total",
        "Total number of normalized per-sensor readings written to database");

    private static readonly Counter TelemetriesFailedCounter = Metrics.CreateCounter(
        "telemetry_failed_total",
        "Total number of telemetry records that failed to save");

    private static readonly Histogram BatchProcessingHistogram = Metrics.CreateHistogram(
        "telemetry_batch_processing_seconds",
        "Time taken to process and insert a batch of telemetry records");

    // ── Worker liveness gauges ──────────────────────────────────────────────────
    // The counters above only ever say how much work *did* happen. When the worker
    // stalls they simply stop moving, which is indistinguishable from "the factory
    // went quiet" until you correlate against the queue depth. These gauges give
    // Prometheus a direct answer instead, and are what the Grafana alert rules in
    // grafana/provisioning/alerting key off (see docs/MONITORING.md).

    private static readonly Gauge WorkerHealthyGauge = Metrics.CreateGauge(
        "telemetry_worker_healthy",
        "1 when the worker is connected to RabbitMQ and its batch processor is running, 0 otherwise");

    // Supersedes the earlier telemetry_channel_depth gauge, which measured this same
    // quantity under a name the provisioned dashboards and alert rules don't read.
    private static readonly Gauge WorkerBufferDepthGauge = Metrics.CreateGauge(
        "telemetry_worker_buffer_depth",
        "Telemetry messages taken off RabbitMQ but still waiting in the in-process channel buffer");

    private static readonly Gauge LastMessageTimestampGauge = Metrics.CreateGauge(
        "telemetry_worker_last_message_timestamp_seconds",
        "Unix timestamp of the last telemetry message received from RabbitMQ (0 when none received yet)");

    private static readonly Gauge LastFlushTimestampGauge = Metrics.CreateGauge(
        "telemetry_worker_last_flush_timestamp_seconds",
        "Unix timestamp of the last batch successfully written to the database (0 when none written yet)");

    private readonly RabbitMqConfig _rabbitConfig;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TelemetryIngestionWorker> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Channel<Telemetry> _channel;
    private ITelemetryConsumer? _consumer;

    // Depth of the in-process buffer, counted by hand rather than read off the channel.
    //
    // The buffer is bounded (see the constructor), and a bounded channel's reader does
    // support Count — so this is a deliberate choice, not a workaround for the current
    // channel type. It is written this way because reading Count is only safe for *some*
    // channel configurations, and picking the wrong one has already cost an outage: when
    // the buffer was created with Channel.CreateUnbounded + SingleReader = true, .NET
    // handed back a SingleConsumerUnboundedChannel whose reader reports CanCount == false
    // and throws NotSupportedException from Count. Reading it once per message took
    // ingestion down — the throw escaped OnTelemetryReceivedAsync, the consumer nacked
    // every message back onto the queue, and the backlog grew forever.
    //
    // An interlocked counter costs one op per message, cannot throw, and stays correct if
    // the channel's bounding or reader options are ever changed again. It also moves at
    // the moment depth actually changes rather than once per batch-loop iteration.
    private long _bufferDepth;

    // Health status tracking
    private bool _isConnected;
    private bool _isProcessing;
    private DateTimeOffset? _lastMessageReceived;
    private DateTimeOffset? _lastBatchFlushed;

    // Written through properties rather than the raw fields so telemetry_worker_healthy
    // can never drift out of sync with what /health/worker reports: every state change
    // goes through one place that republishes the gauge.
    private bool IsConnected
    {
        get => _isConnected;
        set
        {
            _isConnected = value;
            WorkerHealthyGauge.Set(IsHealthy ? 1 : 0);
        }
    }

    private bool IsProcessing
    {
        get => _isProcessing;
        set
        {
            _isProcessing = value;
            WorkerHealthyGauge.Set(IsHealthy ? 1 : 0);
        }
    }

    public bool IsHealthy => IsConnected && IsProcessing;
    public DateTimeOffset? LastMessageReceived => _lastMessageReceived;
    public DateTimeOffset? LastBatchFlushed => _lastBatchFlushed;

    private static double UnixSecondsNow() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;

    public TelemetryIngestionWorker(
        RabbitMqConfig rabbitConfig,
        IServiceScopeFactory scopeFactory,
        ILogger<TelemetryIngestionWorker> logger,
        ILoggerFactory loggerFactory)
    {
        _rabbitConfig = rabbitConfig;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _loggerFactory = loggerFactory;
        // Bounded, and waiting when full, so that a database that cannot keep up becomes
        // backpressure rather than heap growth. The wait blocks the consumer callback before it
        // acknowledges, so unacknowledged messages accumulate against the prefetch window, the
        // broker stops pushing, and the backlog waits in RabbitMQ — which is durable — instead of
        // in a process that will be OOM-killed along with everything it was holding.
        _channel = Channel.CreateBounded<Telemetry>(new BoundedChannelOptions(ChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Telemetry Ingestion Worker starting...");
        _logger.LogInformation("RabbitMQ endpoint: {RabbitMqHost}:{RabbitMqPort} (user '{RabbitMqUser}')",
            _rabbitConfig.Host, _rabbitConfig.Port, _rabbitConfig.Username);

        // Verify database connection before starting (best-effort; informational only).
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<FactoryIoT.Infrastructure.Persistence.FactoryIoTDbContext>();
            await dbContext.Database.CanConnectAsync(stoppingToken);
            _logger.LogInformation("Database connection verified successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to database. Worker will still start but writes may fail.");
        }

        // Outer resilience loop. Previously, any exception that escaped the connect/consume/
        // batch-process pipeline (or an error inside the batch loop that only logged and
        // returned) ended ExecuteAsync for good: the host kept running, but ingestion was
        // dead and /health/worker stayed 503 forever until someone restarted the container.
        // Now the whole pipeline is torn down and reconnected instead of dying permanently.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Connect to RabbitMQ, retrying indefinitely so a slow/unreachable broker
                // never permanently kills ingestion (or, previously, the whole API process).
                _consumer = await ConnectToRabbitMqWithRetryAsync(stoppingToken);
                IsConnected = true;

                // Start consuming from RabbitMQ
                _logger.LogInformation("Starting RabbitMQ consumer...");
                await _consumer.StartAsync(OnTelemetryReceivedAsync, stoppingToken);
                _logger.LogInformation("RabbitMQ consumer started successfully");

                // Start batch processor. This only returns on cancellation or on an
                // unrecoverable error (which it now rethrows instead of swallowing).
                IsProcessing = true;
                _logger.LogInformation("Starting batch processor...");
                await ProcessBatchesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Telemetry Ingestion Worker is stopping.");
                break;
            }
            catch (Exception ex)
            {
                IsConnected = false;
                IsProcessing = false;
                _logger.LogCritical(ex,
                    "Telemetry Ingestion Worker pipeline failed unexpectedly. Ingestion paused; " +
                    "/health/worker will report unhealthy until it reconnects in {DelaySeconds}s.",
                    PipelineRestartDelay.TotalSeconds);

                if (_consumer is not null)
                {
                    try
                    {
                        await _consumer.DisposeAsync();
                    }
                    catch (Exception disposeEx)
                    {
                        _logger.LogWarning(disposeEx, "Error disposing stale RabbitMQ consumer during restart.");
                    }
                    _consumer = null;
                }

                try
                {
                    await Task.Delay(PipelineRestartDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task<RabbitMqTelemetryConsumer> ConnectToRabbitMqWithRetryAsync(CancellationToken stoppingToken)
    {
        var consumerLogger = _loggerFactory.CreateLogger<RabbitMqTelemetryConsumer>();

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                _logger.LogInformation("Attempting to connect to RabbitMQ (attempt {Attempt})...", attempt);
                var consumer = await RabbitMqTelemetryConsumer.CreateAsync(_rabbitConfig, consumerLogger, stoppingToken);
                _logger.LogInformation("Successfully connected to RabbitMQ at {Host}", _rabbitConfig.Host);
                return consumer;
            }
            catch (Exception ex)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(attempt * 2, 30));
                _logger.LogWarning(ex, "Failed to connect to RabbitMQ (attempt {Attempt}). Retrying in {Delay}s...", attempt, delay.TotalSeconds);
                await Task.Delay(delay, stoppingToken);
            }
        }
    }

    private async Task OnTelemetryReceivedAsync(Telemetry telemetry)
    {
        TelemetriesConsumedCounter.Inc();
        _lastMessageReceived = DateTimeOffset.UtcNow;
        LastMessageTimestampGauge.Set(UnixSecondsNow());

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Received telemetry from {MachineId}: Temp={Temperature}, Pressure={Pressure}, Status={Status}", 
                telemetry.MachineId, telemetry.Temperature, telemetry.Pressure, telemetry.Status);
        }
        
        await _channel.Writer.WriteAsync(telemetry);

        // Everything below this line runs after the message has been handed off, and the
        // consumer acks as soon as this method returns. Anything that throws here would
        // be nacked and redelivered even though the telemetry is already buffered, so
        // this tail must stay non-throwing — bookkeeping only, no I/O.
        //
        // Buffer depth separates "RabbitMQ is backed up because nothing is consuming"
        // from "we are consuming fine but the database can't keep up" — in the second
        // case the queue drains into this in-process buffer instead.
        WorkerBufferDepthGauge.Set(Interlocked.Increment(ref _bufferDepth));
    }

    private async Task ProcessBatchesAsync(CancellationToken stoppingToken)
    {
        var batch = new List<Telemetry>(BatchSize);
        using var batchTimer = new PeriodicTimer(BatchInterval);

        _logger.LogInformation("Batch processor running. Batch size: {BatchSize}, Interval: {BatchInterval}s",
            BatchSize, BatchInterval.TotalSeconds);

        // The pending read is owned by this invocation. When the method exits early the
        // whole pipeline is torn down and restarted, and a read left dangling would still
        // be registered on the channel — giving the next ProcessBatchesAsync a second
        // concurrent reader on a channel built with SingleReader = true. Cancelling this
        // source in the finally retires it instead.
        using var readerCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        // Keep a single outstanding read/timer task at a time. PeriodicTimer.WaitForNextTickAsync
        // (and a SingleReader Channel's ReadAsync) must not be invoked again while a previous call
        // is still pending, or it throws/corrupts state - so each is only re-issued after it completes.
        var readTask = _channel.Reader.ReadAsync(readerCts.Token).AsTask();
        var timerTask = batchTimer.WaitForNextTickAsync(stoppingToken).AsTask();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var completedTask = await Task.WhenAny(readTask, timerTask);

                if (completedTask == readTask)
                {
                    batch.Add(await readTask);
                    Interlocked.Decrement(ref _bufferDepth);
                    readTask = _channel.Reader.ReadAsync(readerCts.Token).AsTask();

                    // Accumulate more items if available (up to batch size)
                    while (batch.Count < BatchSize && _channel.Reader.TryRead(out var item))
                    {
                        batch.Add(item);
                        Interlocked.Decrement(ref _bufferDepth);
                    }

                    WorkerBufferDepthGauge.Set(Interlocked.Read(ref _bufferDepth));

                    // If batch is full, flush immediately
                    if (batch.Count >= BatchSize)
                    {
                        _logger.LogInformation("Batch full ({Count} items), flushing to database...", batch.Count);
                        await FlushBatchAsync(batch, stoppingToken);
                        batch.Clear();
                    }
                }
                else
                {
                    await timerTask;
                    timerTask = batchTimer.WaitForNextTickAsync(stoppingToken).AsTask();

                    // Timer elapsed, flush if we have any data
                    if (batch.Count > 0)
                    {
                        _logger.LogInformation("Batch timer elapsed, flushing {Count} items to database...", batch.Count);
                        await FlushBatchAsync(batch, stoppingToken);
                        batch.Clear();
                    }
                }
            }

            // Final flush on shutdown
            if (batch.Count > 0)
            {
                _logger.LogInformation("Shutdown - flushing final batch of {Count} items", batch.Count);
                await FlushBatchAsync(batch, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in batch processing loop. Propagating so the pipeline restarts.");
            IsProcessing = false;
            throw;
        }
        finally
        {
            // Retire the outstanding read (see readerCts above). If it happens to have
            // already produced an item we drop it along with the rest of this batch —
            // losing one message beats leaving a second reader attached to the channel.
            readerCts.Cancel();
            try
            {
                await readTask;

                // The read had pulled an item off the channel, so drop it from the depth
                // count as well; everything still buffered stays counted for the pipeline
                // that restarts after us.
                Interlocked.Decrement(ref _bufferDepth);
            }
            catch
            {
                // Expected: the read was cancelled, or it faulted for the same reason we
                // are unwinding. Nothing here should mask the original failure.
            }

            WorkerBufferDepthGauge.Set(Interlocked.Read(ref _bufferDepth));
        }
    }

    private async Task FlushBatchAsync(List<Telemetry> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0) return;

        // Fan each wide Telemetry snapshot out into its normalized per-sensor readings so the
        // SensorReadings table carries the same data in a query-by-sensor-type shape.
        var sensorReadings = batch.SelectMany(SensorReading.FromTelemetry).ToList();

        const int maxRetries = 3;
        var stopwatch = Stopwatch.StartNew();

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<FactoryIoTDbContext>();
                var telemetryRepository = scope.ServiceProvider.GetRequiredService<ITelemetryRepository>();
                var sensorReadingRepository = scope.ServiceProvider.GetRequiredService<ISensorReadingRepository>();

                _logger.LogDebug(
                    "Saving batch of {TelemetryCount} telemetry records and {SensorReadingCount} sensor readings to database (attempt {Attempt})...",
                    batch.Count, sensorReadings.Count, attempt);

                // Both repositories resolve the same scoped DbContext, so a single transaction
                // makes the two writes atomic: either both tables get the batch or neither does.
                // That keeps them reconciled and makes a retry after failure safe (a rolled-back
                // attempt commits nothing, so re-inserting the same primary keys can't collide).
                await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
                await telemetryRepository.AddRangeAsync(batch, cancellationToken);
                await sensorReadingRepository.AddRangeAsync(sensorReadings, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "✗ Failed to save batch of {Count} telemetry records (attempt {Attempt}/{MaxRetries})",
                    batch.Count, attempt, maxRetries);

                if (attempt >= maxRetries)
                {
                    stopwatch.Stop();
                    TelemetriesFailedCounter.Inc(batch.Count);
                    _logger.LogCritical("✗✗✗ CRITICAL: Failed to save batch after {MaxRetries} attempts. DATA LOSS for {Count} records! ✗✗✗",
                        maxRetries, batch.Count);
                    return;
                }

                // Exponential backoff before retry
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
                continue;
            }

            // ── Committed ────────────────────────────────────────────────────────────
            // Deliberately outside the try: once CommitAsync returns, the rows are durable
            // and this batch must never go round the retry loop again. Bookkeeping that
            // threw in here used to send an already-committed batch back through the
            // insert, which collided on the primary keys and reported DATA LOSS for
            // records that were sitting safely in the database.
            stopwatch.Stop();

            TelemetriesWrittenCounter.Inc(batch.Count);
            SensorReadingsWrittenCounter.Inc(sensorReadings.Count);
            BatchProcessingHistogram.Observe(stopwatch.Elapsed.TotalSeconds);
            _lastBatchFlushed = DateTimeOffset.UtcNow;
            LastFlushTimestampGauge.Set(UnixSecondsNow());

            _logger.LogInformation("✓ Successfully saved {Count} telemetry records and {SensorReadingCount} sensor readings to MSSQL in {ElapsedMs}ms",
                batch.Count, sensorReadings.Count, stopwatch.ElapsedMilliseconds);
            return; // Success, exit retry loop
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Telemetry Ingestion Worker...");
        IsProcessing = false;
        IsConnected = false;
        
        if (_consumer != null)
        {
            await _consumer.StopAsync(cancellationToken);
        }
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        if (_consumer != null)
        {
            _consumer.DisposeAsync().AsTask().Wait();
        }
        base.Dispose();
    }
}
