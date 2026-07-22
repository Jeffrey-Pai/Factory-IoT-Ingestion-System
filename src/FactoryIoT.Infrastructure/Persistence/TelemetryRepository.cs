using FactoryIoT.Domain.Entities;
using FactoryIoT.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FactoryIoT.Infrastructure.Persistence;

/// <summary>
/// EF Core implementation of telemetry repository.
/// </summary>
public sealed class TelemetryRepository : ITelemetryRepository
{
    private readonly FactoryIoTDbContext _context;

    public TelemetryRepository(FactoryIoTDbContext context)
    {
        _context = context;
    }

    public async Task AddRangeAsync(IEnumerable<Telemetry> telemetries, CancellationToken cancellationToken = default)
    {
        await _context.Telemetries.AddRangeAsync(telemetries, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Telemetry>> GetLatestByMachineAsync(string machineId, int count, CancellationToken cancellationToken = default)
    {
        return await _context.Telemetries
            .Where(t => t.MachineId == machineId)
            .OrderByDescending(t => t.Timestamp)
            .Take(count)
            .ToListAsync(cancellationToken);
    }
}
