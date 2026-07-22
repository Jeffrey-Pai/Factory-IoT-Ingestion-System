using FactoryIoT.Application.Common.Interfaces;
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

// Configure PostgreSQL with EF Core
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Connection string 'Default' not found.");
builder.Services.AddDbContext<FactoryIoTDbContext>(options =>
    options.UseNpgsql(connectionString));

// Register repositories
builder.Services.AddScoped<ITelemetryRepository, TelemetryRepository>();

// Register RabbitMQ consumer as singleton
var rabbitHost = builder.Configuration.GetValue<string>("RabbitMQ:Host") 
    ?? Environment.GetEnvironmentVariable("RABBITMQ_HOST") 
    ?? "localhost";
builder.Services.AddSingleton<ITelemetryConsumer>(sp =>
    RabbitMqTelemetryConsumer.CreateAsync(rabbitHost).GetAwaiter().GetResult());

// Register background worker
builder.Services.AddHostedService<TelemetryIngestionWorker>();

var app = builder.Build();

// Run database migrations on startup
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<FactoryIoTDbContext>();
    await dbContext.Database.MigrateAsync();
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

app.Run();
