using FactoryIoT.Infrastructure.Configuration;
using FactoryIoT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace FactoryIoT.Tests.Infrastructure;

/// <summary>
/// Shared setup for repository tests running against the EF Core in-memory provider, so the real
/// LINQ executes without needing a live SQL Server.
/// </summary>
internal static class TestDatabase
{
    /// <summary>
    /// A context backed by its own isolated in-memory store.
    /// </summary>
    /// <remarks>
    /// Transactions are ignored rather than rejected. The in-memory provider has no transaction
    /// support and throws by default when one is opened, but the lifecycle repository opens one
    /// per bucket on purpose — that atomicity is what makes the running roster totals safe to
    /// accumulate. Downgrading the warning lets the surrounding aggregation logic be exercised
    /// here; the transactional guarantee itself is a SQL Server behaviour and is not what these
    /// tests assert.
    /// </remarks>
    public static FactoryIoTDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<FactoryIoTDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    /// <summary>Retention settings wrapped for injection, optionally customised per test.</summary>
    public static IOptions<DataRetentionOptions> Retention(Action<DataRetentionOptions>? configure = null)
    {
        var options = new DataRetentionOptions();
        configure?.Invoke(options);
        return Options.Create(options);
    }
}
