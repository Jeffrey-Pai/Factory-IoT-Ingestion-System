using FactoryIoT.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FactoryIoT.Infrastructure.Persistence;

/// <summary>
/// EF Core DbContext for Factory IoT system.
/// </summary>
public sealed class FactoryIoTDbContext : DbContext
{
    public FactoryIoTDbContext(DbContextOptions<FactoryIoTDbContext> options)
        : base(options)
    {
    }

    public DbSet<Telemetry> Telemetries => Set<Telemetry>();
    public DbSet<SensorReading> SensorReadings => Set<SensorReading>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Telemetry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.MachineId).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Timestamp).IsRequired();
            entity.HasIndex(e => new { e.MachineId, e.Timestamp });
        });

        modelBuilder.Entity<SensorReading>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.MachineId).IsRequired().HasMaxLength(50);
            entity.Property(e => e.SensorType).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Unit).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Timestamp).IsRequired();
            entity.HasIndex(e => new { e.MachineId, e.Timestamp });
        });
    }
}
