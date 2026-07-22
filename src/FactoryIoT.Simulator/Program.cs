using System.Text;
using System.Text.Json;
using FactoryIoT.Domain.Entities;
using RabbitMQ.Client;

// ---------------------------------------------------------------------------
// Factory IoT Simulator
// Simulates 50 equipment machines (EQP-001 ~ EQP-050), each publishing
// telemetry data every 1 second to RabbitMQ telemetry-queue.
// Set RABBITMQ_HOST env-var (default: localhost) to target the
// RabbitMQ broker defined in docker-compose.yml.
// ---------------------------------------------------------------------------

const int MachineCount = 50;
const int IntervalMs = 1000; // publish every 1 second per machine
const string QueueName = "telemetry-queue";

var rabbitHost = Environment.GetEnvironmentVariable("RABBITMQ_HOST") ?? "localhost";
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine($"[Simulator] Starting {MachineCount} equipment → RabbitMQ @ {rabbitHost}");

// Initialize RabbitMQ connection
var factory = new ConnectionFactory { HostName = rabbitHost };
await using var connection = await factory.CreateConnectionAsync(cts.Token);
await using var channel = await connection.CreateChannelAsync(cancellationToken: cts.Token);

// Declare queue (durable = true for persistence)
await channel.QueueDeclareAsync(QueueName, durable: true, exclusive: false, autoDelete: false, cancellationToken: cts.Token);

Console.WriteLine($"[Simulator] Connected to RabbitMQ, queue: {QueueName}");

var rng = new Random();

// Each machine runs in its own task
var tasks = Enumerable.Range(1, MachineCount).Select(i => Task.Run(async () =>
{
    var machineId = $"EQP-{i:D3}";
    
    while (!cts.Token.IsCancellationRequested)
    {
        try
        {
            var telemetry = new Telemetry
            {
                MachineId = machineId,
                Temperature = Math.Round(rng.NextDouble() * 100 + 20, 2), // 20-120°C
                Pressure = Math.Round(rng.NextDouble() * 10 + 1, 2), // 1-11 bar
                Status = rng.Next(100) < 95 ? "Running" : "Warning", // 95% running, 5% warning
                Timestamp = DateTimeOffset.UtcNow
            };

            var json = JsonSerializer.Serialize(telemetry);
            var body = Encoding.UTF8.GetBytes(json);
            
            await channel.BasicPublishAsync(exchange: string.Empty, routingKey: QueueName, body: body, cancellationToken: cts.Token);

            Console.WriteLine(
                $"[{telemetry.MachineId}] Temp={telemetry.Temperature:F2}°C, Pressure={telemetry.Pressure:F2}bar, Status={telemetry.Status}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{machineId}] Error: {ex.Message}");
        }

        await Task.Delay(IntervalMs, cts.Token).ConfigureAwait(false);
    }
}, cts.Token)).ToArray();

try
{
    await Task.WhenAll(tasks);
}
catch (OperationCanceledException)
{
    Console.WriteLine("[Simulator] Stopped.");
}
