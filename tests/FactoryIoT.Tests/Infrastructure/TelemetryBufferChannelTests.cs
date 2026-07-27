using System.Threading.Channels;
using FactoryIoT.Domain.Entities;
using Xunit;

namespace FactoryIoT.Tests.Infrastructure;

/// <summary>
/// Pins the channel behaviour that <see cref="FactoryIoT.Infrastructure.Workers.TelemetryIngestionWorker"/>
/// depends on for its buffer-depth gauge.
///
/// Background: the worker buffers telemetry in an unbounded channel created with
/// <c>SingleReader = true</c>. That option makes .NET hand back a
/// <c>SingleConsumerUnboundedChannel</c>, whose reader does not implement counting —
/// <c>Count</c> throws <see cref="NotSupportedException"/> rather than returning a depth.
/// Reading it once per message threw on every single delivery, the exception escaped the
/// worker's receive callback, and the consumer nacked each message back onto the queue:
/// the backlog grew without bound and nothing reached MSSQL. The worker therefore tracks
/// depth itself with an interlocked counter.
///
/// These tests exist so that stays a deliberate decision. If one of them ever fails, the
/// framework's behaviour has changed and the hand-rolled counter can be revisited — until
/// then, <c>Reader.Count</c> must not come back.
/// </summary>
public sealed class TelemetryBufferChannelTests
{
    /// <summary>Creates the channel exactly as the worker's constructor does.</summary>
    private static Channel<Telemetry> CreateWorkerBuffer() =>
        Channel.CreateUnbounded<Telemetry>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    [Fact]
    public void WorkerBuffer_DoesNotSupportCounting()
    {
        var channel = CreateWorkerBuffer();

        Assert.False(channel.Reader.CanCount);
    }

    [Fact]
    public async Task WorkerBuffer_ReaderCountThrows_EvenWithItemsBuffered()
    {
        var channel = CreateWorkerBuffer();
        await channel.Writer.WriteAsync(new Telemetry { MachineId = "EQP-001", Status = "Running" });

        Assert.Throws<NotSupportedException>(() => channel.Reader.Count);
    }
}
