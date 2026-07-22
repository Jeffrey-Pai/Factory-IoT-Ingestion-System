using FactoryIoT.Domain.Entities;
using FactoryIoT.Simulator.Models;

// ---------------------------------------------------------------------------
// Factory IoT Simulator
// Simulates 50+ machines, each publishing sensor readings at a configurable
// interval. Set RABBITMQ_HOST env-var (default: localhost) to target the
// RabbitMQ broker defined in docker-compose.yml.
// ---------------------------------------------------------------------------

const int MachineCount = 50;
const int IntervalMs = 500; // publish every 500 ms per machine

var rabbitHost = Environment.GetEnvironmentVariable("RABBITMQ_HOST") ?? "localhost";
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine($"[Simulator] Starting {MachineCount} machines → RabbitMQ @ {rabbitHost}");

var machines = Enumerable.Range(1, MachineCount)
    .Select(i => new MachineDescriptor(
        MachineId: $"MACHINE-{i:D3}",
        SensorTypes: ["temperature", "vibration", "pressure"]))
    .ToArray();

var rng = new Random();

// Each machine runs in its own task
var tasks = machines.Select(machine => Task.Run(async () =>
{
    while (!cts.Token.IsCancellationRequested)
    {
        foreach (var sensor in machine.SensorTypes)
        {
            var reading = new SensorReading
            {
                MachineId  = machine.MachineId,
                SensorType = sensor,
                Value      = Math.Round(rng.NextDouble() * 100, 2),
                Unit       = sensor switch
                {
                    "temperature" => "°C",
                    "vibration"   => "mm/s",
                    "pressure"    => "bar",
                    _             => "unit"
                },
                Timestamp = DateTimeOffset.UtcNow
            };

            // In a full implementation, inject IMessagePublisher and call PublishAsync.
            Console.WriteLine(
                $"[{reading.MachineId}] {reading.SensorType}={reading.Value}{reading.Unit} @ {reading.Timestamp:HH:mm:ss.fff}");
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
