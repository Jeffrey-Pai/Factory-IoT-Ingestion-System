using System.Text;
using System.Text.Json;
using FactoryIoT.Application.Common.Interfaces;
using FactoryIoT.Domain.Entities;
using RabbitMQ.Client;

namespace FactoryIoT.Infrastructure.Messaging;

/// <summary>
/// Publishes sensor readings to RabbitMQ.
/// Exchange: <c>iot.readings</c> (fanout)
/// </summary>
public sealed class RabbitMqPublisher : IMessagePublisher, IAsyncDisposable
{
    private const string ExchangeName = "iot.readings";

    private readonly IConnection _connection;
    private readonly IChannel _channel;

    private RabbitMqPublisher(IConnection connection, IChannel channel)
    {
        _connection = connection;
        _channel = channel;
    }

    public static async Task<RabbitMqPublisher> CreateAsync(RabbitMqConfig config, CancellationToken cancellationToken = default)
    {
        var factory = config.CreateConnectionFactory();
        var connection = await factory.CreateConnectionAsync(cancellationToken);
        var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(ExchangeName, ExchangeType.Fanout, durable: true, cancellationToken: cancellationToken);
        return new RabbitMqPublisher(connection, channel);
    }

    public async Task PublishAsync(SensorReading reading, CancellationToken cancellationToken = default)
    {
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reading));
        await _channel.BasicPublishAsync(ExchangeName, routingKey: string.Empty, body: body, cancellationToken: cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _channel.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
