using FactoryIoT.Domain.Entities;

namespace FactoryIoT.Domain.Interfaces;

/// <summary>
/// Persistence contract for sensor readings (defined in Domain; implemented in Infrastructure).
/// </summary>
public interface ISensorReadingRepository
{
    Task AddAsync(SensorReading reading, CancellationToken cancellationToken = default);
    Task AddRangeAsync(IEnumerable<SensorReading> readings, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SensorReading>> GetByMachineAsync(string machineId, CancellationToken cancellationToken = default);
}
