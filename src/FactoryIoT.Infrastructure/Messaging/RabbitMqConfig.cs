using RabbitMQ.Client;

namespace FactoryIoT.Infrastructure.Messaging;

/// <summary>
/// Configuration for RabbitMQ connection.
/// </summary>
public class RabbitMqConfig
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5672;

    /// <summary>
    /// Broker credentials. Defaults to guest/guest.
    /// NOTE: RabbitMQ only allows the built-in <c>guest</c> user to authenticate over
    /// loopback (localhost) unless <c>loopback_users.guest = false</c> is configured on
    /// the broker (see rabbitmq.conf). When the API/simulator run in separate containers
    /// they connect over the Docker network (non-loopback), so either that broker setting
    /// must be present or a dedicated (non-guest) user must be supplied here.
    /// </summary>
    public string Username { get; set; } = "guest";
    public string Password { get; set; } = "guest";

    /// <summary>
    /// Builds a <see cref="ConnectionFactory"/> with automatic connection/topology
    /// recovery and heartbeats enabled so transient broker blips self-heal instead of
    /// leaving the consumer silently idle.
    /// </summary>
    public ConnectionFactory CreateConnectionFactory() => new()
    {
        HostName = Host,
        Port = Port,
        UserName = Username,
        Password = Password,
        AutomaticRecoveryEnabled = true,
        TopologyRecoveryEnabled = true,
        RequestedHeartbeat = TimeSpan.FromSeconds(30),
    };
}
