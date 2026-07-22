namespace FactoryIoT.Domain.Entities;

/// <summary>
/// Represents a single telemetry reading from a factory sensor / machine.
/// </summary>
public sealed class SensorReading
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string MachineId { get; init; } = string.Empty;
    public string SensorType { get; init; } = string.Empty;
    public double Value { get; init; }
    public string Unit { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
