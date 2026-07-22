using FactoryIoT.Domain.Entities;
using FactoryIoT.Domain.Interfaces;

namespace FactoryIoT.Infrastructure.Persistence;

/// <summary>
/// In-memory placeholder implementation of ISensorReadingRepository.
/// Replace with an EF Core / Npgsql implementation targeting PostgreSQL.
/// </summary>
public sealed class InMemorySensorReadingRepository : ISensorReadingRepository
{
    private readonly List<SensorReading> _store = new();

    public Task AddAsync(SensorReading reading, CancellationToken cancellationToken = default)
    {
        _store.Add(reading);
        return Task.CompletedTask;
    }

    public Task AddRangeAsync(IEnumerable<SensorReading> readings, CancellationToken cancellationToken = default)
    {
        _store.AddRange(readings);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SensorReading>> GetByMachineAsync(string machineId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SensorReading> result = _store
            .Where(r => r.MachineId == machineId)
            .ToList();
        return Task.FromResult(result);
    }
}
