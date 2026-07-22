using System.Text;
using System.Text.Json;
using FactoryIoT.Application.Common.Interfaces;
using FactoryIoT.Domain.Entities;
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
    private Func<Telemetry, Task>? _onMessageReceived;

    private RabbitMqTelemetryConsumer(IConnection connection, IChannel channel)
    {
        _connection = connection;
        _channel = channel;
    }

    public static async Task<RabbitMqTelemetryConsumer> CreateAsync(string hostName, CancellationToken cancellationToken = default)
    {
        var factory = new ConnectionFactory { HostName = hostName };
        var connection = await factory.CreateConnectionAsync(cancellationToken);
        var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        
        // Declare queue (durable = true for persistence)
        await channel.QueueDeclareAsync(QueueName, durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);
        
        return new RabbitMqTelemetryConsumer(connection, channel);
    }

    public async Task StartAsync(Func<Telemetry, Task> onMessageReceived, CancellationToken cancellationToken = default)
    {
        _onMessageReceived = onMessageReceived ?? throw new ArgumentNullException(nameof(onMessageReceived));

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += OnMessageReceivedAsync;

        await _channel.BasicConsumeAsync(QueueName, autoAck: false, consumer, cancellationToken);
    }

    private async Task OnMessageReceivedAsync(object sender, BasicDeliverEventArgs eventArgs)
    {
        try
        {
            var body = eventArgs.Body.ToArray();
            var json = Encoding.UTF8.GetString(body);
            var telemetry = JsonSerializer.Deserialize<Telemetry>(json);

            if (telemetry is not null && _onMessageReceived is not null)
            {
                await _onMessageReceived(telemetry);
                await _channel.BasicAckAsync(eventArgs.DeliveryTag, multiple: false);
            }
        }
        catch
        {
            // On error, reject and requeue
            await _channel.BasicNackAsync(eventArgs.DeliveryTag, multiple: false, requeue: true);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _channel.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
