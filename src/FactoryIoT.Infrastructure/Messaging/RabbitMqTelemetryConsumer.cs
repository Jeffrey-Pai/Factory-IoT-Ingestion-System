using System.Text;
using System.Text.Json;
using FactoryIoT.Application.Common.Interfaces;
using FactoryIoT.Domain.Entities;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace FactoryIoT.Infrastructure.Messaging;

/// <summary>
/// Consumes telemetry messages from RabbitMQ queue.
/// </summary>
public sealed class RabbitMqTelemetryConsumer : ITelemetryConsumer
{
    private const string QueueName = "telemetry-queue";

    private readonly IConnection _connection;
    private readonly IChannel _channel;
    private readonly ILogger<RabbitMqTelemetryConsumer> _logger;
    private Func<Telemetry, Task>? _onMessageReceived;
    private long _messageCount = 0;

    private RabbitMqTelemetryConsumer(IConnection connection, IChannel channel, ILogger<RabbitMqTelemetryConsumer> logger)
    {
        _connection = connection;
        _channel = channel;
        _logger = logger;
    }

    public static async Task<RabbitMqTelemetryConsumer> CreateAsync(string hostName, ILogger<RabbitMqTelemetryConsumer> logger, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Creating RabbitMQ connection to {HostName}...", hostName);
        
        var factory = new ConnectionFactory { HostName = hostName };
        var connection = await factory.CreateConnectionAsync(cancellationToken);
        var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        
        logger.LogInformation("Declaring queue '{QueueName}' (durable=true)...", QueueName);
        
        // Declare queue (durable = true for persistence)
        await channel.QueueDeclareAsync(QueueName, durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);
        
        logger.LogInformation("RabbitMQ consumer created successfully for queue '{QueueName}'", QueueName);
        
        return new RabbitMqTelemetryConsumer(connection, channel, logger);
    }

    public async Task StartAsync(Func<Telemetry, Task> onMessageReceived, CancellationToken cancellationToken = default)
    {
        _onMessageReceived = onMessageReceived ?? throw new ArgumentNullException(nameof(onMessageReceived));

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += OnMessageReceivedAsync;

        _logger.LogInformation("Starting to consume messages from queue '{QueueName}' (autoAck=false)...", QueueName);
        
        await _channel.BasicConsumeAsync(QueueName, autoAck: false, consumer, cancellationToken);
        
        _logger.LogInformation("✓ RabbitMQ consumer is now actively listening for messages on '{QueueName}'", QueueName);
    }

    private async Task OnMessageReceivedAsync(object sender, BasicDeliverEventArgs eventArgs)
    {
        var messageNumber = Interlocked.Increment(ref _messageCount);
        
        try
        {
            var body = eventArgs.Body.ToArray();
            var json = Encoding.UTF8.GetString(body);
            
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Message #{MessageNumber} received: {Json}", messageNumber, json);
            }
            
            var telemetry = JsonSerializer.Deserialize<Telemetry>(json);

            if (telemetry is null)
            {
                _logger.LogWarning("Message #{MessageNumber}: Received null or malformed telemetry. JSON: {Json}", messageNumber, json);
                // Acknowledge the message to remove it from the queue (cannot process malformed messages)
                await _channel.BasicAckAsync(eventArgs.DeliveryTag, multiple: false);
                return;
            }

            if (_onMessageReceived is null)
            {
                _logger.LogError("Message #{MessageNumber}: Callback is null! Acknowledging to prevent accumulation.", messageNumber);
                await _channel.BasicAckAsync(eventArgs.DeliveryTag, multiple: false);
                return;
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Message #{MessageNumber}: Processing telemetry from {MachineId}", messageNumber, telemetry.MachineId);
            }

            await _onMessageReceived(telemetry);
            await _channel.BasicAckAsync(eventArgs.DeliveryTag, multiple: false);
            
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Message #{MessageNumber}: Acknowledged successfully", messageNumber);
            }
            
            // Log progress every 100 messages
            if (messageNumber % 100 == 0)
            {
                _logger.LogInformation("✓ Processed {MessageCount} messages from RabbitMQ", messageNumber);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Message #{MessageNumber}: Error processing message. Rejecting and requeueing.", messageNumber);
            // On error, reject and requeue
            await _channel.BasicNackAsync(eventArgs.DeliveryTag, multiple: false, requeue: true);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Stopping RabbitMQ consumer. Total messages processed: {MessageCount}", _messageCount);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _logger.LogInformation("Disposing RabbitMQ consumer and connection");
        await _channel.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
