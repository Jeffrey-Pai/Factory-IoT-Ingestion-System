using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryIoT.Infrastructure.Migrations
{
    /// <summary>
    /// Turns the two ever-growing raw tables into a tiered store: a short-retention hot tier with a
    /// physical layout built for time-series ingestion, pre-aggregated warm/cold tiers, and a
    /// maintained fleet roster.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This migration rebuilds the clustered index on <c>Telemetries</c> and
    /// <c>SensorReadings</c>.</b> On an empty database that is instantaneous. On one that already
    /// holds data it is an offline table rebuild — SQL Server Express has no online index rebuild —
    /// so plan a maintenance window proportional to the current table size, and make sure there is
    /// free space for a copy of the largest table while it is sorted.
    /// </para>
    /// <para>
    /// The rebuild is the point of the migration, not incidental to it. A random GUID as the
    /// clustered key scatters every insert to a random page, so ingestion degrades continuously as
    /// the table grows; leading with <c>Timestamp</c> makes ingestion an append and makes both
    /// aggregation and retention contiguous range operations.
    /// </para>
    /// </remarks>
    public partial class TieredStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── Hot tier: re-key Telemetries on (Timestamp, Id) ─────────────────────────────
            // Nonclustered indexes are dropped first: dropping a clustered index rebuilds every
            // nonclustered index on the table, so removing them up front avoids paying for a
            // rebuild that is about to be thrown away.
            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.indexes
           WHERE name = N'IX_Telemetries_MachineId_Timestamp'
             AND object_id = OBJECT_ID(N'[dbo].[Telemetries]'))
    DROP INDEX [IX_Telemetries_MachineId_Timestamp] ON [dbo].[Telemetries];");

            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.key_constraints
           WHERE name = N'PK_Telemetries'
             AND parent_object_id = OBJECT_ID(N'[dbo].[Telemetries]'))
    ALTER TABLE [dbo].[Telemetries] DROP CONSTRAINT [PK_Telemetries];");

            // Page compression typically halves this data: the columns are highly repetitive down
            // a page (same machine ids, timestamps within the same second, statuses drawn from a
            // tiny vocabulary). Under Express's 10 GB per-database ceiling that is not a
            // micro-optimisation — it is roughly a doubling of how much history fits.
            migrationBuilder.Sql(@"
ALTER TABLE [dbo].[Telemetries]
    ADD CONSTRAINT [PK_Telemetries] PRIMARY KEY CLUSTERED ([Timestamp] ASC, [Id] ASC)
    WITH (DATA_COMPRESSION = PAGE);");

            // Covering index for the per-machine 'latest N' query: seek to the machine, walk
            // newest-first, and read the measurements from the leaf without touching the table.
            migrationBuilder.Sql(@"
CREATE NONCLUSTERED INDEX [IX_Telemetries_MachineId_Timestamp]
    ON [dbo].[Telemetries] ([MachineId] ASC, [Timestamp] DESC)
    INCLUDE ([Temperature], [Pressure], [Status])
    WITH (DATA_COMPRESSION = PAGE);");

            // ── Hot tier: re-key SensorReadings on (Timestamp, Id) ──────────────────────────
            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.indexes
           WHERE name = N'IX_SensorReadings_MachineId_Timestamp'
             AND object_id = OBJECT_ID(N'[dbo].[SensorReadings]'))
    DROP INDEX [IX_SensorReadings_MachineId_Timestamp] ON [dbo].[SensorReadings];");

            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.key_constraints
           WHERE name = N'PK_SensorReadings'
             AND parent_object_id = OBJECT_ID(N'[dbo].[SensorReadings]'))
    ALTER TABLE [dbo].[SensorReadings] DROP CONSTRAINT [PK_SensorReadings];");

            migrationBuilder.Sql(@"
ALTER TABLE [dbo].[SensorReadings]
    ADD CONSTRAINT [PK_SensorReadings] PRIMARY KEY CLUSTERED ([Timestamp] ASC, [Id] ASC)
    WITH (DATA_COMPRESSION = PAGE);");

            migrationBuilder.Sql(@"
CREATE NONCLUSTERED INDEX [IX_SensorReadings_MachineId_Timestamp]
    ON [dbo].[SensorReadings] ([MachineId] ASC, [Timestamp] DESC)
    INCLUDE ([SensorType], [Value], [Unit])
    WITH (DATA_COMPRESSION = PAGE);");

            // ── Warm / cold tiers ───────────────────────────────────────────────────────────
            migrationBuilder.CreateTable(
                name: "TelemetryRollups",
                columns: table => new
                {
                    Granularity = table.Column<int>(type: "int", nullable: false),
                    MachineId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    BucketStart = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SampleCount = table.Column<int>(type: "int", nullable: false),
                    FirstReading = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastReading = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    MinTemperature = table.Column<double>(type: "float", nullable: false),
                    MaxTemperature = table.Column<double>(type: "float", nullable: false),
                    SumTemperature = table.Column<double>(type: "float", nullable: false),
                    MinPressure = table.Column<double>(type: "float", nullable: false),
                    MaxPressure = table.Column<double>(type: "float", nullable: false),
                    SumPressure = table.Column<double>(type: "float", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelemetryRollups", x => new { x.Granularity, x.MachineId, x.BucketStart });
                });

            migrationBuilder.CreateTable(
                name: "TelemetryStatusRollups",
                columns: table => new
                {
                    Granularity = table.Column<int>(type: "int", nullable: false),
                    BucketStart = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    MachineId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Count = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelemetryStatusRollups", x => new { x.Granularity, x.BucketStart, x.MachineId, x.Status });
                });

            migrationBuilder.CreateTable(
                name: "RollupCheckpoints",
                columns: table => new
                {
                    Granularity = table.Column<int>(type: "int", nullable: false),
                    LastCompletedBucketStart = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RollupCheckpoints", x => x.Granularity);
                });

            // ── Roster tier ─────────────────────────────────────────────────────────────────
            migrationBuilder.CreateTable(
                name: "MachineSummaries",
                columns: table => new
                {
                    MachineId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SampleCount = table.Column<long>(type: "bigint", nullable: false),
                    FirstSeen = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSeen = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    MinTemperature = table.Column<double>(type: "float", nullable: false),
                    MaxTemperature = table.Column<double>(type: "float", nullable: false),
                    SumTemperature = table.Column<double>(type: "float", nullable: false),
                    MinPressure = table.Column<double>(type: "float", nullable: false),
                    MaxPressure = table.Column<double>(type: "float", nullable: false),
                    SumPressure = table.Column<double>(type: "float", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MachineSummaries", x => x.MachineId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TelemetryRollups_Granularity_BucketStart",
                table: "TelemetryRollups",
                columns: new[] { "Granularity", "BucketStart" });

            migrationBuilder.Sql(@"
ALTER INDEX [PK_TelemetryRollups] ON [dbo].[TelemetryRollups] REBUILD WITH (DATA_COMPRESSION = PAGE);");
            migrationBuilder.Sql(@"
ALTER INDEX [IX_TelemetryRollups_Granularity_BucketStart] ON [dbo].[TelemetryRollups] REBUILD WITH (DATA_COMPRESSION = PAGE);");
            migrationBuilder.Sql(@"
ALTER INDEX [PK_TelemetryStatusRollups] ON [dbo].[TelemetryStatusRollups] REBUILD WITH (DATA_COMPRESSION = PAGE);");

            // ── Seed the roster and the aggregation watermark from whatever is already stored ─
            //
            // Without this an existing database would show an empty fleet roster until enough new
            // telemetry had been aggregated, and would re-aggregate its entire history on first
            // start. The roster is a lifetime running total, so it is seeded from every row present.
            migrationBuilder.Sql(@"
INSERT INTO [dbo].[MachineSummaries]
    ([MachineId], [SampleCount], [FirstSeen], [LastSeen],
     [MinTemperature], [MaxTemperature], [SumTemperature],
     [MinPressure], [MaxPressure], [SumPressure])
SELECT
    [MachineId], COUNT_BIG(*), MIN([Timestamp]), MAX([Timestamp]),
    MIN([Temperature]), MAX([Temperature]), SUM([Temperature]),
    MIN([Pressure]), MAX([Pressure]), SUM([Pressure])
FROM [dbo].[Telemetries]
GROUP BY [MachineId];");

            // The checkpoint is stored as a raw timestamp rather than a floored bucket start; the
            // reader floors it to the granularity, so both forms behave identically. Seeding it to
            // the newest existing reading means aggregation resumes from there instead of replaying
            // history that the roster backfill above has already counted. The HAVING clause makes
            // the whole statement a no-op on an empty table, which is the fresh-install case.
            migrationBuilder.Sql(@"
INSERT INTO [dbo].[RollupCheckpoints] ([Granularity], [LastCompletedBucketStart])
SELECT 1, MAX([Timestamp]) FROM [dbo].[Telemetries] HAVING MAX([Timestamp]) IS NOT NULL;");

            migrationBuilder.Sql(@"
INSERT INTO [dbo].[RollupCheckpoints] ([Granularity], [LastCompletedBucketStart])
SELECT 2, MAX([Timestamp]) FROM [dbo].[Telemetries] HAVING MAX([Timestamp]) IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "MachineSummaries");
            migrationBuilder.DropTable(name: "RollupCheckpoints");
            migrationBuilder.DropTable(name: "TelemetryStatusRollups");
            migrationBuilder.DropTable(name: "TelemetryRollups");

            // Restore the original (Id) primary key and uncompressed indexes. Note that reverting
            // does not bring back rows that retention has already deleted.
            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.indexes
           WHERE name = N'IX_SensorReadings_MachineId_Timestamp'
             AND object_id = OBJECT_ID(N'[dbo].[SensorReadings]'))
    DROP INDEX [IX_SensorReadings_MachineId_Timestamp] ON [dbo].[SensorReadings];");

            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.key_constraints
           WHERE name = N'PK_SensorReadings'
             AND parent_object_id = OBJECT_ID(N'[dbo].[SensorReadings]'))
    ALTER TABLE [dbo].[SensorReadings] DROP CONSTRAINT [PK_SensorReadings];");

            migrationBuilder.Sql(@"
ALTER TABLE [dbo].[SensorReadings]
    ADD CONSTRAINT [PK_SensorReadings] PRIMARY KEY CLUSTERED ([Id] ASC);");

            migrationBuilder.Sql(@"
CREATE NONCLUSTERED INDEX [IX_SensorReadings_MachineId_Timestamp]
    ON [dbo].[SensorReadings] ([MachineId] ASC, [Timestamp] ASC);");

            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.indexes
           WHERE name = N'IX_Telemetries_MachineId_Timestamp'
             AND object_id = OBJECT_ID(N'[dbo].[Telemetries]'))
    DROP INDEX [IX_Telemetries_MachineId_Timestamp] ON [dbo].[Telemetries];");

            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.key_constraints
           WHERE name = N'PK_Telemetries'
             AND parent_object_id = OBJECT_ID(N'[dbo].[Telemetries]'))
    ALTER TABLE [dbo].[Telemetries] DROP CONSTRAINT [PK_Telemetries];");

            migrationBuilder.Sql(@"
ALTER TABLE [dbo].[Telemetries]
    ADD CONSTRAINT [PK_Telemetries] PRIMARY KEY CLUSTERED ([Id] ASC);");

            migrationBuilder.Sql(@"
CREATE NONCLUSTERED INDEX [IX_Telemetries_MachineId_Timestamp]
    ON [dbo].[Telemetries] ([MachineId] ASC, [Timestamp] ASC);");
        }
    }
}
