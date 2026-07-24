using FactoryIoT.Application.Common.Interfaces;
using FactoryIoT.Application.DTOs;
using FactoryIoT.Domain.Interfaces;
using FactoryIoT.Infrastructure.Messaging;
using FactoryIoT.Infrastructure.Persistence;
using FactoryIoT.Infrastructure.Workers;
using Microsoft.EntityFrameworkCore;
using Prometheus;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Register Prometheus HTTP metrics collection
builder.Services.AddHttpClient();

// Configure SQL Server with EF Core
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Connection string 'Default' not found.");
builder.Services.AddDbContext<FactoryIoTDbContext>(options =>
    options.UseSqlServer(connectionString));

// Register repositories
builder.Services.AddScoped<ITelemetryRepository, TelemetryRepository>();
builder.Services.AddScoped<ISensorReadingRepository, SensorReadingRepository>();

// Register RabbitMQ connection configuration (host/port/credentials).
//
// IMPORTANT: the plain RABBITMQ_* env vars (single underscore) are read FIRST.
// They do NOT bind to the hierarchical "RabbitMQ:*" keys the way ASP.NET's
// double-underscore convention does, so if we consulted appsettings first the
// hardcoded "localhost" there would always win and the RABBITMQ_HOST=rabbitmq
// override supplied by docker-compose would be silently ignored — which is exactly
// what made the worker dial localhost inside its own container and never consume.
// Order: RABBITMQ_* env var → "RabbitMQ:*" appsettings section → default.
static string? EnvOrConfig(IConfiguration config, string envVar, string configKey)
    => Environment.GetEnvironmentVariable(envVar) is { Length: > 0 } value
        ? value
        : config.GetValue<string>(configKey);

var rabbitConfig = new RabbitMqConfig
{
    Host = EnvOrConfig(builder.Configuration, "RABBITMQ_HOST", "RabbitMQ:Host") ?? "localhost",
    Port = int.TryParse(EnvOrConfig(builder.Configuration, "RABBITMQ_PORT", "RabbitMQ:Port"), out var port) ? port : 5672,
    Username = EnvOrConfig(builder.Configuration, "RABBITMQ_USER", "RabbitMQ:Username") ?? "guest",
    Password = EnvOrConfig(builder.Configuration, "RABBITMQ_PASS", "RabbitMQ:Password") ?? "guest",
};
builder.Services.AddSingleton(rabbitConfig);

// Register background worker as singleton to enable health checks
builder.Services.AddSingleton<TelemetryIngestionWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TelemetryIngestionWorker>());

// If the worker's BackgroundService ever faults, keep the host (and its API/health
// endpoints) running instead of the default behavior of shutting the whole process down.
builder.Services.Configure<HostOptions>(options =>
{
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
});

var app = builder.Build();

// Run database migrations on startup
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<FactoryIoTDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    
    try
    {
        logger.LogInformation("Running database migrations...");
        await dbContext.Database.MigrateAsync();
        logger.LogInformation("Database migrations completed successfully");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to run database migrations");
        throw;
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Expose Prometheus /metrics endpoint (scraped by prometheus.yml)
app.UseHttpMetrics();
app.MapMetrics();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
    .WithName("HealthCheck")
    .WithOpenApi();

// Enhanced health check for worker status
app.MapGet("/health/worker", (TelemetryIngestionWorker worker) =>
{
    var status = new
    {
        isHealthy = worker.IsHealthy,
        lastMessageReceived = worker.LastMessageReceived,
        lastBatchFlushed = worker.LastBatchFlushed,
        timeSinceLastMessage = worker.LastMessageReceived.HasValue 
            ? DateTimeOffset.UtcNow - worker.LastMessageReceived.Value 
            : (TimeSpan?)null,
        timeSinceLastFlush = worker.LastBatchFlushed.HasValue 
            ? DateTimeOffset.UtcNow - worker.LastBatchFlushed.Value 
            : (TimeSpan?)null
    };
    
    return worker.IsHealthy 
        ? Results.Ok(status) 
        : Results.Json(status, statusCode: 503);
})
.WithName("WorkerHealthCheck")
.WithOpenApi();

// Telemetry API endpoint
app.MapGet("/api/v1/telemetry/{machineId}/latest", async (
    string machineId,
    int count,
    ITelemetryRepository repository) =>
{
    if (count <= 0 || count > 100)
    {
        return Results.BadRequest(new { error = "count must be between 1 and 100" });
    }

    var telemetries = await repository.GetLatestByMachineAsync(machineId, count);
    return Results.Ok(telemetries);
})
.WithName("GetLatestTelemetry")
.WithOpenApi();

// Normalized per-sensor readings endpoint. Unlike the wide /telemetry snapshot, this can
// answer per-sensor questions — e.g. the last N Pressure readings for a machine — via the
// optional sensorType filter (Temperature, Pressure, ...).
app.MapGet("/api/v1/sensors/{machineId}/readings", async (
    string machineId,
    int count,
    string? sensorType,
    ISensorReadingRepository repository) =>
{
    if (count <= 0 || count > 100)
    {
        return Results.BadRequest(new { error = "count must be between 1 and 100" });
    }

    var readings = await repository.GetLatestAsync(machineId, sensorType, count);
    var response = readings
        .Select(r => new SensorReadingDto(r.MachineId, r.SensorType, r.Value, r.Unit, r.Timestamp))
        .ToList();
    return Results.Ok(response);
})
.WithName("GetLatestSensorReadings")
.WithOpenApi();

app.Run();
