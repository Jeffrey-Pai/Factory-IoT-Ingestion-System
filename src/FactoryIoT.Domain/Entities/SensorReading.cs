namespace FactoryIoT.Domain.Entities;

/// <summary>
/// Represents a single, normalized telemetry reading from one factory sensor.
/// </summary>
/// <remarks>
/// Where <see cref="Telemetry"/> is a wide snapshot (one row carrying every metric a
/// machine reports at an instant), a <see cref="SensorReading"/> is the narrow / normalized
/// form: one row per individual sensor measurement. This shape answers per-sensor questions
/// the wide table cannot — e.g. "give me the last N Pressure readings for EQP-001" — without
/// a schema change when new sensor types are added.
/// </remarks>
public sealed class SensorReading
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string MachineId { get; init; } = string.Empty;
    public string SensorType { get; init; } = string.Empty;
    public double Value { get; init; }
    public string Unit { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Decomposes a wide <see cref="Telemetry"/> snapshot into its constituent per-sensor
    /// readings, preserving the source machine and timestamp so the two representations
    /// reconcile. The textual <see cref="Telemetry.Status"/> field is intentionally not
    /// emitted here because a <see cref="SensorReading"/> models a numeric measurement.
    /// </summary>
    public static IReadOnlyList<SensorReading> FromTelemetry(Telemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);

        return new[]
        {
            new SensorReading
            {
                MachineId = telemetry.MachineId,
                SensorType = "Temperature",
                Value = telemetry.Temperature,
                Unit = "°C",
                Timestamp = telemetry.Timestamp,
            },
            new SensorReading
            {
                MachineId = telemetry.MachineId,
                SensorType = "Pressure",
                Value = telemetry.Pressure,
                Unit = "bar",
                Timestamp = telemetry.Timestamp,
            },
        };
    }
}
