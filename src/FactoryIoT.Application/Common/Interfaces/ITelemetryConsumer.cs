using FactoryIoT.Domain.Entities;

namespace FactoryIoT.Application.Common.Interfaces;

/// <summary>
/// Message-queue consumer contract for telemetry data.
/// </summary>
public interface ITelemetryConsumer : IAsyncDisposable
{
    Task StartAsync(Func<Telemetry, Task> onMessageReceived, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
