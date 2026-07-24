using FactoryIoT.Domain.Entities;
using FactoryIoT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FactoryIoT.Tests.Infrastructure;

/// <summary>
/// Exercises the analytics queries on <see cref="TelemetryRepository"/> against the EF Core
/// in-memory provider, so the real LINQ runs without needing a live SQL Server.
/// </summary>
public sealed class TelemetryRepositoryTests
{
    private static FactoryIoTDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<FactoryIoTDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options);

    private static Telemetry Reading(string machineId, double temperature, double pressure, string status, DateTimeOffset timestamp) =>
        new()
        {
            MachineId = machineId,
            Temperature = temperature,
            Pressure = pressure,
            Status = status,
            Timestamp = timestamp,
        };

    [Fact]
    public async Task GetLatestByMachineAsync_ReturnsNewestFirstAndHonoursCount()
    {
        using var context = CreateContext();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            context.Telemetries.Add(Reading("EQP-001", i, i, "Running", now.AddMinutes(-i)));
        }
        await context.SaveChangesAsync();
        var repository = new TelemetryRepository(context);

        var latest = await repository.GetLatestByMachineAsync("EQP-001", 3);

        Assert.Equal(3, latest.Count);
        Assert.Equal(0d, latest[0].Temperature); // i == 0 is the most recent (now - 0)
        Assert.Equal(1d, latest[1].Temperature);
        Assert.Equal(2d, latest[2].Temperature);
    }

    [Fact]
    public async Task GetMachineSummariesAsync_RollsUpPerMachineAndOrdersById()
    {
        using var context = CreateContext();
        var now = DateTimeOffset.UtcNow;
        context.Telemetries.AddRange(
            Reading("EQP-002", 30, 6, "Warning", now.AddMinutes(-3)),
            Reading("EQP-001", 10, 2, "Running", now.AddMinutes(-2)),
            Reading("EQP-001", 20, 4, "Running", now.AddMinutes(-1)));
        await context.SaveChangesAsync();
        var repository = new TelemetryRepository(context);

        var summaries = await repository.GetMachineSummariesAsync();

        Assert.Equal(2, summaries.Count);
        Assert.Equal("EQP-001", summaries[0].MachineId); // ordered by machine id
        Assert.Equal("EQP-002", summaries[1].MachineId);

        var eqp1 = summaries[0];
        Assert.Equal(2, eqp1.SampleCount);
        Assert.Equal(10d, eqp1.MinTemperature, 3);
        Assert.Equal(20d, eqp1.MaxTemperature, 3);
        Assert.Equal(15d, eqp1.AvgTemperature, 3);
        Assert.Equal(2d, eqp1.MinPressure, 3);
        Assert.Equal(4d, eqp1.MaxPressure, 3);
        Assert.Equal(3d, eqp1.AvgPressure, 3);
        Assert.Equal(now.AddMinutes(-2), eqp1.FirstSeen);
        Assert.Equal(now.AddMinutes(-1), eqp1.LastSeen);
    }

    [Fact]
    public async Task GetStatisticsAsync_AggregatesOnlyReadingsInsideWindowForThatMachine()
    {
        using var context = CreateContext();
        var now = DateTimeOffset.UtcNow;
        context.Telemetries.AddRange(
            Reading("EQP-001", 40, 5, "Running", now.AddMinutes(-5)),
            Reading("EQP-001", 60, 7, "Warning", now.AddMinutes(-1)),
            Reading("EQP-001", 999, 999, "Running", now.AddMinutes(-30)), // outside the window
            Reading("EQP-002", 1, 1, "Running", now.AddMinutes(-1)));      // different machine
        await context.SaveChangesAsync();
        var repository = new TelemetryRepository(context);

        var stats = await repository.GetStatisticsAsync("EQP-001", now.AddMinutes(-10));

        Assert.NotNull(stats);
        Assert.Equal("EQP-001", stats!.MachineId);
        Assert.Equal(2, stats.SampleCount);
        Assert.Equal(40d, stats.MinTemperature, 3);
        Assert.Equal(60d, stats.MaxTemperature, 3);
        Assert.Equal(50d, stats.AvgTemperature, 3);
        Assert.Equal(5d, stats.MinPressure, 3);
        Assert.Equal(7d, stats.MaxPressure, 3);
        Assert.Equal(now.AddMinutes(-5), stats.FirstReading);
        Assert.Equal(now.AddMinutes(-1), stats.LastReading);
    }

    [Fact]
    public async Task GetStatisticsAsync_NoReadingsInsideWindow_ReturnsNull()
    {
        using var context = CreateContext();
        var now = DateTimeOffset.UtcNow;
        context.Telemetries.Add(Reading("EQP-001", 40, 5, "Running", now.AddHours(-2)));
        await context.SaveChangesAsync();
        var repository = new TelemetryRepository(context);

        var stats = await repository.GetStatisticsAsync("EQP-001", now.AddMinutes(-10));

        Assert.Null(stats);
    }

    [Fact]
    public async Task GetFleetStatusAsync_CountsMachinesAndBreaksDownByStatusDescending()
    {
        using var context = CreateContext();
        var now = DateTimeOffset.UtcNow;
        context.Telemetries.AddRange(
            Reading("EQP-001", 1, 1, "Running", now.AddMinutes(-1)),
            Reading("EQP-001", 1, 1, "Running", now.AddMinutes(-2)),
            Reading("EQP-002", 1, 1, "Warning", now.AddMinutes(-1)),
            Reading("EQP-003", 1, 1, "Running", now.AddMinutes(-30))); // outside the window
        await context.SaveChangesAsync();
        var repository = new TelemetryRepository(context);

        var status = await repository.GetFleetStatusAsync(now.AddMinutes(-10));

        Assert.Equal(2, status.MachineCount);   // EQP-001 and EQP-002 (EQP-003 is outside the window)
        Assert.Equal(3, status.TotalReadings);
        Assert.Equal(2, status.Breakdown.Count);
        Assert.Equal("Running", status.Breakdown[0].Status); // most frequent first
        Assert.Equal(2, status.Breakdown[0].Count);
        Assert.Equal("Warning", status.Breakdown[1].Status);
        Assert.Equal(1, status.Breakdown[1].Count);
    }
}
