using System.Diagnostics;
using System.Threading.Channels;
using FactoryIoT.Application.Common.Interfaces;
using FactoryIoT.Domain.Entities;
using FactoryIoT.Domain.Interfaces;
using FactoryIoT.Infrastructure.Messaging;
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
    private static readonly TimeSpan BatchInterval = TimeSpan.FromSeconds(2);

    private static readonly Counter TelemetriesWrittenCounter = Metrics.CreateCounter(
        "telemetry_written_total",
        "Total number of telemetry records written to database");

    private static readonly Counter TelemetriesConsumedCounter = Metrics.CreateCounter(
        "telemetry_consumed_total",
        "Total number of telemetry messages consumed from RabbitMQ");

    private static readonly Counter TelemetriesFailedCounter = Metrics.CreateCounter(
        "telemetry_failed_total",
        "Total number of telemetry records that failed to save");

    private static readonly Histogram BatchProcessingHistogram = Metrics.CreateHistogram(
        "telemetry_batch_processing_seconds",
        "Time taken to process and insert a batch of telemetry records");

    private readonly RabbitMqConfig _rabbitConfig;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TelemetryIngestionWorker> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Channel<Telemetry> _channel;
    private ITelemetryConsumer? _consumer;
    
    // Health status tracking
    private bool _isConnected;
    private bool _isProcessing;
    private DateTimeOffset? _lastMessageReceived;
    private DateTimeOffset? _lastBatchFlushed;
    
    public bool IsHealthy => _isConnected && _isProcessing;
    public DateTimeOffset? LastMessageReceived => _lastMessageReceived;
    public DateTimeOffset? LastBatchFlushed => _lastBatchFlushed;

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
        _channel = Channel.CreateUnbounded<Telemetry>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Telemetry Ingestion Worker starting...");
        _logger.LogInformation("RabbitMQ Host: {RabbitMqHost}", _rabbitConfig.Host);

        // Verify database connection before starting
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

        // Create RabbitMQ consumer with retry logic
        const int maxRetries = 10;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                _logger.LogInformation("Attempting to connect to RabbitMQ (attempt {Attempt}/{MaxRetries})...", attempt, maxRetries);
                var consumerLogger = _loggerFactory.CreateLogger<RabbitMqTelemetryConsumer>();
                _consumer = await RabbitMqTelemetryConsumer.CreateAsync(_rabbitConfig.Host, consumerLogger, stoppingToken);
                _isConnected = true;
                _logger.LogInformation("Successfully connected to RabbitMQ at {Host}", _rabbitConfig.Host);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to connect to RabbitMQ (attempt {Attempt}/{MaxRetries})", attempt, maxRetries);
                if (attempt >= maxRetries)
                {
                    _logger.LogError("Failed to connect to RabbitMQ after {MaxRetries} attempts. Worker will exit.", maxRetries);
                    throw;
                }
                // Wait before retrying (exponential backoff)
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(attempt * 2, 30)), stoppingToken);
            }
        }

        // Start consuming from RabbitMQ
        _logger.LogInformation("Starting RabbitMQ consumer...");
        await _consumer!.StartAsync(OnTelemetryReceivedAsync, stoppingToken);
        _logger.LogInformation("RabbitMQ consumer started successfully");

        // Start batch processor
        _isProcessing = true;
        _logger.LogInformation("Starting batch processor...");
        await ProcessBatchesAsync(stoppingToken);
    }

    private async Task OnTelemetryReceivedAsync(Telemetry telemetry)
    {
        TelemetriesConsumedCounter.Inc();
        _lastMessageReceived = DateTimeOffset.UtcNow;
        
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Received telemetry from {MachineId}: Temp={Temperature}, Pressure={Pressure}, Status={Status}", 
                telemetry.MachineId, telemetry.Temperature, telemetry.Pressure, telemetry.Status);
        }
        
        await _channel.Writer.WriteAsync(telemetry);
    }

    private async Task ProcessBatchesAsync(CancellationToken stoppingToken)
    {
        var batch = new List<Telemetry>(BatchSize);
        using var batchTimer = new PeriodicTimer(BatchInterval);

        _logger.LogInformation("Batch processor running. Batch size: {BatchSize}, Interval: {BatchInterval}s",
            BatchSize, BatchInterval.TotalSeconds);

        // Keep a single outstanding read/timer task at a time. PeriodicTimer.WaitForNextTickAsync
        // (and a SingleReader Channel's ReadAsync) must not be invoked again while a previous call
        // is still pending, or it throws/corrupts state - so each is only re-issued after it completes.
        var readTask = _channel.Reader.ReadAsync(stoppingToken).AsTask();
        var timerTask = batchTimer.WaitForNextTickAsync(stoppingToken).AsTask();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var completedTask = await Task.WhenAny(readTask, timerTask);

                if (completedTask == readTask)
                {
                    batch.Add(await readTask);
                    readTask = _channel.Reader.ReadAsync(stoppingToken).AsTask();

                    // Accumulate more items if available (up to batch size)
                    while (batch.Count < BatchSize && _channel.Reader.TryRead(out var item))
                    {
                        batch.Add(item);
                    }

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
            _logger.LogInformation("Telemetry Ingestion Worker is stopping.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in batch processing loop.");
            _isProcessing = false;
        }
    }

    private async Task FlushBatchAsync(List<Telemetry> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0) return;

        const int maxRetries = 3;
        var stopwatch = Stopwatch.StartNew();
        
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<ITelemetryRepository>();
                
                _logger.LogDebug("Saving batch of {Count} telemetry records to database (attempt {Attempt})...", batch.Count, attempt);
                await repository.AddRangeAsync(batch, cancellationToken);
                stopwatch.Stop();

                TelemetriesWrittenCounter.Inc(batch.Count);
                BatchProcessingHistogram.Observe(stopwatch.Elapsed.TotalSeconds);
                _lastBatchFlushed = DateTimeOffset.UtcNow;

                _logger.LogInformation("✓ Successfully saved {Count} telemetry records to MSSQL in {ElapsedMs}ms", 
                    batch.Count, stopwatch.ElapsedMilliseconds);
                return; // Success, exit retry loop
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
                    break;
                }
                
                // Exponential backoff before retry
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Telemetry Ingestion Worker...");
        _isProcessing = false;
        _isConnected = false;
        
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
