using System.Threading.Channels;
using FactoryIoT.Domain.Entities;
using Xunit;

namespace FactoryIoT.Tests.Infrastructure;

/// <summary>
/// Pins the channel behaviour behind <see cref="FactoryIoT.Infrastructure.Workers.TelemetryIngestionWorker"/>'s
/// buffer-depth gauge.
///
/// Background: the worker once buffered telemetry in an unbounded channel created with
/// <c>SingleReader = true</c>. That option makes .NET hand back a
/// <c>SingleConsumerUnboundedChannel</c>, whose reader does not implement counting —
/// <c>Count</c> throws <see cref="NotSupportedException"/> rather than returning a depth.
/// Reading it once per message threw on every single delivery, the exception escaped the
/// worker's receive callback, and the consumer nacked each message back onto the queue:
/// the backlog grew without bound and nothing reached MSSQL.
///
/// The worker's buffer is now bounded, and a bounded channel's reader *does* support
/// counting — so the trap is not currently armed. It stays pinned here because the two
/// configurations differ only by which factory method the constructor calls, and the
/// worker deliberately tracks depth with an interlocked counter so its gauge survives
/// that choice being changed again.
/// </summary>
public sealed class TelemetryBufferChannelTests
{
    private const int ChannelCapacity = 20_000;

    /// <summary>Creates the buffer the worker's constructor builds today.</summary>
    private static Channel<Telemetry> CreateWorkerBuffer() =>
        Channel.CreateBounded<Telemetry>(new BoundedChannelOptions(ChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

    /// <summary>Creates the buffer as it was configured when ingestion stalled.</summary>
    private static Channel<Telemetry> CreateUnboundedSingleReaderBuffer() =>
        Channel.CreateUnbounded<Telemetry>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    [Fact]
    public void UnboundedSingleReaderBuffer_DoesNotSupportCounting()
    {
        var channel = CreateUnboundedSingleReaderBuffer();

        Assert.False(channel.Reader.CanCount);
    }

    [Fact]
    public async Task UnboundedSingleReaderBuffer_ReaderCountThrows_EvenWithItemsBuffered()
    {
        var channel = CreateUnboundedSingleReaderBuffer();
        await channel.Writer.WriteAsync(new Telemetry { MachineId = "EQP-001", Status = "Running" });

        Assert.Throws<NotSupportedException>(() => channel.Reader.Count);
    }

    [Fact]
    public async Task WorkerBuffer_SupportsCounting()
    {
        var channel = CreateWorkerBuffer();
        await channel.Writer.WriteAsync(new Telemetry { MachineId = "EQP-001", Status = "Running" });

        Assert.True(channel.Reader.CanCount);
        Assert.Equal(1, channel.Reader.Count);
    }
}
