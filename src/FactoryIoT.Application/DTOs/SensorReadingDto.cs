namespace FactoryIoT.Application.DTOs;

/// <summary>Inbound DTO for a sensor reading payload.</summary>
public sealed record SensorReadingDto(
    string MachineId,
    string SensorType,
    double Value,
    string Unit,
    DateTimeOffset Timestamp);
