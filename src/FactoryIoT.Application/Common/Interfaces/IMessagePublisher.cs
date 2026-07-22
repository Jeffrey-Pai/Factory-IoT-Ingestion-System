using FactoryIoT.Domain.Entities;

namespace FactoryIoT.Application.Common.Interfaces;

/// <summary>
/// Message-queue publisher contract (defined in Application; implemented in Infrastructure).
/// </summary>
public interface IMessagePublisher
{
    Task PublishAsync(SensorReading reading, CancellationToken cancellationToken = default);
}
