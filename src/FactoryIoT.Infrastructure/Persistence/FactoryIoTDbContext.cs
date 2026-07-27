using FactoryIoT.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FactoryIoT.Infrastructure.Persistence;

/// <summary>
/// EF Core DbContext for Factory IoT system.
/// </summary>
/// <remarks>
/// The schema is organised into storage tiers rather than one ever-growing table set — see
/// <c>docs/DATA-LIFECYCLE.md</c>. Hot tables (<c>Telemetries</c>, <c>SensorReadings</c>) hold
/// full-fidelity readings for a short window; rollup tables hold pre-aggregated buckets for the
/// long tail; <c>MachineSummaries</c> holds the running fleet roster. The lifecycle worker moves
/// data between them.
/// </remarks>
public sealed class FactoryIoTDbContext : DbContext
{
    public FactoryIoTDbContext(DbContextOptions<FactoryIoTDbContext> options)
        : base(options)
    {
    }

    public DbSet<Telemetry> Telemetries => Set<Telemetry>();
    public DbSet<SensorReading> SensorReadings => Set<SensorReading>();
    public DbSet<TelemetryRollup> TelemetryRollups => Set<TelemetryRollup>();
    public DbSet<TelemetryStatusRollup> TelemetryStatusRollups => Set<TelemetryStatusRollup>();
    public DbSet<RollupCheckpoint> RollupCheckpoints => Set<RollupCheckpoint>();
    public DbSet<MachineSummary> MachineSummaries => Set<MachineSummary>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ── Hot tier: raw, full-fidelity readings ───────────────────────────────────────────
        //
        // The key is (Timestamp, Id), not Id alone, and that choice is the difference between a
        // table that ingests happily forever and one that degrades week over week. In SQL Server
        // the primary key is clustered by default, meaning it dictates physical row order, so a
        // random GUID scatters every insert to a random page: pages split, density collapses
        // toward 70%, fragmentation climbs, and the whole table has to stay resident in the buffer
        // pool because writes touch it everywhere at once. Leading with Timestamp turns ingestion
        // into an append at the end of the index, which is the cheapest thing a B-tree can do —
        // and it makes the two highest-frequency background queries (aggregate one closed minute;
        // delete everything older than the cutoff) contiguous range operations instead of scans.
        // Id stays in the key only to guarantee uniqueness when two readings share a timestamp.
        modelBuilder.Entity<Telemetry>(entity =>
        {
            entity.HasKey(e => new { e.Timestamp, e.Id });
            entity.Property(e => e.MachineId).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50);

            // The per-machine "latest N" query is the one access path the clustered order cannot
            // serve, so it gets a covering index: seek to the machine, walk newest-first, and read
            // the measurements straight from the leaf without ever touching the base table.
            entity.HasIndex(e => new { e.MachineId, e.Timestamp })
                .IsDescending(false, true)
                .IncludeProperties(e => new { e.Temperature, e.Pressure, e.Status });
        });

        modelBuilder.Entity<SensorReading>(entity =>
        {
            entity.HasKey(e => new { e.Timestamp, e.Id });
            entity.Property(e => e.MachineId).IsRequired().HasMaxLength(50);
            entity.Property(e => e.SensorType).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Unit).IsRequired().HasMaxLength(20);

            // SensorType is an included column rather than a key column on purpose. Leading with
            // (MachineId, Timestamp) serves the unfiltered "latest N readings" query as a pure
            // ordered seek; adding SensorType to the key would serve the filtered variant instead
            // but force a sort on the unfiltered one. With a handful of sensor types the filtered
            // query only over-reads by that same small factor, which is the cheaper trade.
            entity.HasIndex(e => new { e.MachineId, e.Timestamp })
                .IsDescending(false, true)
                .IncludeProperties(e => new { e.SensorType, e.Value, e.Unit });
        });

        // ── Warm / cold tier: pre-aggregated buckets ────────────────────────────────────────
        modelBuilder.Entity<TelemetryRollup>(entity =>
        {
            // Keyed machine-first because the per-machine window query ("how has EQP-001 behaved
            // over the last 30 days?") then becomes a single contiguous seek. Fleet-wide queries
            // need every machine in the window anyway, so they lose nothing by the ordering.
            entity.HasKey(e => new { e.Granularity, e.MachineId, e.BucketStart });
            entity.Property(e => e.MachineId).IsRequired().HasMaxLength(50);

            // Time-leading secondary index for the two access paths that do not name a machine:
            // fleet-wide window scans, and retention purges by bucket age.
            entity.HasIndex(e => new { e.Granularity, e.BucketStart });
        });

        modelBuilder.Entity<TelemetryStatusRollup>(entity =>
        {
            // Time-leading here instead, because every read of this table is fleet-wide
            // ("break the last N minutes down by status") and none of them name a machine.
            entity.HasKey(e => new { e.Granularity, e.BucketStart, e.MachineId, e.Status });
            entity.Property(e => e.MachineId).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50);
        });

        modelBuilder.Entity<RollupCheckpoint>(entity =>
        {
            entity.HasKey(e => e.Granularity);
            entity.Property(e => e.Granularity).ValueGeneratedNever();
        });

        // ── Roster tier: one running row per machine ────────────────────────────────────────
        modelBuilder.Entity<MachineSummary>(entity =>
        {
            entity.HasKey(e => e.MachineId);
            entity.Property(e => e.MachineId).HasMaxLength(50).ValueGeneratedNever();
        });
    }
}
