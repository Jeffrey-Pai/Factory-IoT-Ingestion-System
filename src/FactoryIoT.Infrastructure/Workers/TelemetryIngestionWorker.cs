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
/// and bulk inserts to PostgreSQL. Tracks metrics with prometheus-net.
/// </summary>
public sealed class TelemetryIngestionWorker : BackgroundService
{
    private const int BatchSize = 100;
    private static readonly TimeSpan BatchInterval = TimeSpan.FromSeconds(2);

    private static readonly Counter TelemetriesWrittenCounter = Metrics.CreateCounter(
        "telemetry_written_total",
        "Total number of telemetry records written to database");

    private static readonly Histogram BatchProcessingHistogram = Metrics.CreateHistogram(
        "telemetry_batch_processing_seconds",
        "Time taken to process and insert a batch of telemetry records");

    private readonly RabbitMqConfig _rabbitConfig;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TelemetryIngestionWorker> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Channel<Telemetry> _channel;
    private ITelemetryConsumer? _consumer;

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

        // Create RabbitMQ consumer with retry logic
        const int maxRetries = 10;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                _logger.LogInformation("Attempting to connect to RabbitMQ (attempt {Attempt}/{MaxRetries})...", attempt, maxRetries);
                var consumerLogger = _loggerFactory.CreateLogger<RabbitMqTelemetryConsumer>();
                _consumer = await RabbitMqTelemetryConsumer.CreateAsync(_rabbitConfig.Host, consumerLogger, stoppingToken);
                _logger.LogInformation("Successfully connected to RabbitMQ.");
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
        await _consumer!.StartAsync(OnTelemetryReceivedAsync, stoppingToken);

        // Start batch processor
        await ProcessBatchesAsync(stoppingToken);
    }

    private async Task OnTelemetryReceivedAsync(Telemetry telemetry)
    {
        await _channel.Writer.WriteAsync(telemetry);
    }

    private async Task ProcessBatchesAsync(CancellationToken stoppingToken)
    {
        var batch = new List<Telemetry>(BatchSize);
        var batchTimer = new PeriodicTimer(BatchInterval);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Try to read from channel with timeout
                var readTask = _channel.Reader.ReadAsync(stoppingToken).AsTask();
                var timerTask = batchTimer.WaitForNextTickAsync(stoppingToken).AsTask();

                var completedTask = await Task.WhenAny(readTask, timerTask);

                if (completedTask == readTask && readTask.IsCompletedSuccessfully)
                {
                    batch.Add(readTask.Result);

                    // Accumulate more items if available (up to batch size)
                    while (batch.Count < BatchSize && _channel.Reader.TryRead(out var item))
                    {
                        batch.Add(item);
                    }

                    // If batch is full, flush immediately
                    if (batch.Count >= BatchSize)
                    {
                        await FlushBatchAsync(batch, stoppingToken);
                        batch.Clear();
                    }
                }
                else if (completedTask == timerTask)
                {
                    // Timer elapsed, flush if we have any data
                    if (batch.Count > 0)
                    {
                        await FlushBatchAsync(batch, stoppingToken);
                        batch.Clear();
                    }
                }
            }

            // Final flush on shutdown
            if (batch.Count > 0)
            {
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
        }
    }

    private async Task FlushBatchAsync(List<Telemetry> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0) return;

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<ITelemetryRepository>();
            
            await repository.AddRangeAsync(batch, cancellationToken);
            stopwatch.Stop();

            TelemetriesWrittenCounter.Inc(batch.Count);
            BatchProcessingHistogram.Observe(stopwatch.Elapsed.TotalSeconds);

            _logger.LogInformation("Flushed {Count} telemetry records in {ElapsedMs}ms", 
                batch.Count, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Failed to flush batch of {Count} telemetry records", batch.Count);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Telemetry Ingestion Worker...");
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
