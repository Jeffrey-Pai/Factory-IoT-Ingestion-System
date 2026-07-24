using FactoryIoT.Domain.Entities;
using Xunit;

namespace FactoryIoT.Tests.Domain;

/// <summary>
/// Pure-domain tests for <see cref="SensorReading.FromTelemetry"/> — the decomposition that puts
/// every wide <see cref="Telemetry"/> snapshot into the normalized SensorReadings table.
/// </summary>
public sealed class SensorReadingTests
{
    [Fact]
    public void FromTelemetry_EmitsOneTemperatureAndOnePressureReading()
    {
        var telemetry = new Telemetry
        {
            MachineId = "EQP-001",
            Temperature = 42.5,
            Pressure = 3.14,
            Status = "Running",
            Timestamp = DateTimeOffset.UtcNow,
        };

        var readings = SensorReading.FromTelemetry(telemetry);

        Assert.Equal(2, readings.Count);

        var temperature = Assert.Single(readings, r => r.SensorType == "Temperature");
        Assert.Equal(42.5, temperature.Value);
        Assert.Equal("°C", temperature.Unit);

        var pressure = Assert.Single(readings, r => r.SensorType == "Pressure");
        Assert.Equal(3.14, pressure.Value);
        Assert.Equal("bar", pressure.Unit);
    }

    [Fact]
    public void FromTelemetry_PreservesMachineIdAndTimestampOnEveryReading()
    {
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(-7);
        var telemetry = new Telemetry
        {
            MachineId = "EQP-042",
            Temperature = 10,
            Pressure = 2,
            Status = "Warning",
            Timestamp = timestamp,
        };

        var readings = SensorReading.FromTelemetry(telemetry);

        Assert.All(readings, r =>
        {
            Assert.Equal("EQP-042", r.MachineId);
            Assert.Equal(timestamp, r.Timestamp);
        });
    }

    [Fact]
    public void FromTelemetry_NullTelemetry_Throws()
    {
        // Block-bodied lambda so this binds unambiguously to Assert.Throws<T>(Action).
        Assert.Throws<ArgumentNullException>(() => { SensorReading.FromTelemetry(null!); });
    }
}
