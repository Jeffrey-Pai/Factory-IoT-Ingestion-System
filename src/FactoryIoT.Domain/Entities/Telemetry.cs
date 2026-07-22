namespace FactoryIoT.Domain.Entities;

/// <summary>
/// Represents telemetry data from factory equipment.
/// </summary>
public sealed class Telemetry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string MachineId { get; init; } = string.Empty;
    public double Temperature { get; init; }
    public double Pressure { get; init; }
    public string Status { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
