using FactoryIoT.Domain.Entities;
using FactoryIoT.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FactoryIoT.Infrastructure.Persistence;

/// <summary>
/// EF Core implementation of <see cref="ISensorReadingRepository"/> targeting the
/// <c>SensorReadings</c> table.
/// </summary>
public sealed class SensorReadingRepository : ISensorReadingRepository
{
    private readonly FactoryIoTDbContext _context;

    public SensorReadingRepository(FactoryIoTDbContext context)
    {
        _context = context;
    }

    public async Task AddRangeAsync(IEnumerable<SensorReading> readings, CancellationToken cancellationToken = default)
    {
        await _context.SensorReadings.AddRangeAsync(readings, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SensorReading>> GetLatestAsync(
        string machineId,
        string? sensorType,
        int count,
        CancellationToken cancellationToken = default)
    {
        var query = _context.SensorReadings
            .AsNoTracking()
            .Where(r => r.MachineId == machineId);

        if (!string.IsNullOrWhiteSpace(sensorType))
        {
            query = query.Where(r => r.SensorType == sensorType);
        }

        return await query
            .OrderByDescending(r => r.Timestamp)
            .Take(count)
            .ToListAsync(cancellationToken);
    }
}
