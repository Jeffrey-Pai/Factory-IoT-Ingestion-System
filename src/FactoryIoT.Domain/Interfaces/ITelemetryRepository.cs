using FactoryIoT.Domain.Entities;

namespace FactoryIoT.Domain.Interfaces;

/// <summary>
/// Persistence contract for telemetry data.
/// </summary>
public interface ITelemetryRepository
{
    Task AddRangeAsync(IEnumerable<Telemetry> telemetries, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Telemetry>> GetLatestByMachineAsync(string machineId, int count, CancellationToken cancellationToken = default);
}
