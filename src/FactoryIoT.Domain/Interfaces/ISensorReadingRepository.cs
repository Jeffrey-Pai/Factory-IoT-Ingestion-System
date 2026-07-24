using FactoryIoT.Domain.Entities;

namespace FactoryIoT.Domain.Interfaces;

/// <summary>
/// Persistence contract for normalized sensor readings (defined in Domain; implemented in Infrastructure).
/// </summary>
public interface ISensorReadingRepository
{
    Task AddRangeAsync(IEnumerable<SensorReading> readings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the most recent readings for a machine, newest first, optionally filtered to a
    /// single <paramref name="sensorType"/> (case-insensitive; null/blank returns all types).
    /// </summary>
    Task<IReadOnlyList<SensorReading>> GetLatestAsync(
        string machineId,
        string? sensorType,
        int count,
        CancellationToken cancellationToken = default);
}
