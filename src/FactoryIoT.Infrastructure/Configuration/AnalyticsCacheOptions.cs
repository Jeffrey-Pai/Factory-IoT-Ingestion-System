namespace FactoryIoT.Infrastructure.Configuration;

/// <summary>
/// Whether the analytics read path is cached, where the cache lives, and how long each family of
/// answer may be served stale. Bound from the <c>Cache</c> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// The cache sits in front of the read-only analytics endpoints only — the fleet roster, the
/// fleet-status snapshot, per-machine statistics, and the latest-N snapshot. Every one of those is
/// computed identically for every caller, so a dashboard with several tabs open, a k6 run and a
/// Prometheus scrape landing in the same second collapse onto one database query instead of one
/// each. The ingestion write path is never cached: writes go straight through.
/// </para>
/// <para>
/// The TTLs are short on purpose. This path is already eventually consistent by design — the roster
/// trails the wall clock by the rollup job's safety lag, windowed statistics are exact only up to
/// the last closed bucket — so a one-to-five-second cache adds a rounding error to a staleness the
/// model already has, and one far inside the dashboard's own 30-second liveness threshold. Set any
/// TTL to zero to stop caching that one family while leaving the others on.
/// </para>
/// <para>
/// Bind overrides with the ASP.NET <c>Cache__*</c> double-underscore convention (e.g.
/// <c>Cache__RedisConnection</c>), which maps onto these keys automatically — deliberately unlike
/// the legacy single-underscore <c>RABBITMQ_*</c> variables, whose hand-rolled binding is the foot-
/// gun documented in <c>docs/ARCHITECTURE.md</c>. There is nothing to hand-roll here.
/// </para>
/// </remarks>
public sealed class AnalyticsCacheOptions
{
    public const string SectionName = "Cache";

    /// <summary>
    /// Master switch. When false — or when <see cref="RedisConnection"/> is blank — the read path is
    /// wired straight to the database with no cache in front of it and no Redis dependency at all,
    /// which is the default for a local <c>dotnet run</c> without the Compose stack. Leaving it true
    /// while blanking the connection is the same as off; keeping it as an explicit switch lets an
    /// operator turn the cache off against a live Redis (e.g. to A/B a k6 run) without unwiring it.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// StackExchange.Redis connection string (e.g. <c>redis:6379</c>). Blank means "no distributed
    /// cache configured", which disables caching regardless of <see cref="Enabled"/>. Compose sets
    /// this to the <c>redis</c> service; it is intentionally empty in <c>appsettings.json</c> so the
    /// project still runs with nothing but a database.
    /// </summary>
    public string RedisConnection { get; set; } = string.Empty;

    /// <summary>
    /// Key prefix applied to every entry this instance writes, so the cache is trivially inspectable
    /// (<c>KEYS factoryiot:*</c>) and safely shares a Redis with other tenants. Regions and keys are
    /// appended under it.
    /// </summary>
    public string InstanceName { get; set; } = "factoryiot:";

    // ── Per-family TTLs (seconds) ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Fleet roster (<c>/api/v1/machines</c>). Kept short because <c>LastSeen</c> is the fleet's
    /// liveness signal; two seconds is invisible against the 30-second-plus threshold the dashboard
    /// actually lights the dot with.
    /// </summary>
    public double RosterTtlSeconds { get; set; } = 2;

    /// <summary>Fleet-status snapshot (<c>/api/v1/fleet/status</c>), keyed by window width.</summary>
    public double FleetStatusTtlSeconds { get; set; } = 2;

    /// <summary>Per-machine windowed statistics (<c>/api/v1/telemetry/{id}/stats</c>).</summary>
    public double StatsTtlSeconds { get; set; } = 5;

    /// <summary>
    /// Latest-N raw snapshot (<c>/api/v1/telemetry/{id}/latest</c>) — the endpoint k6 hammers. One
    /// second still collapses a burst of identical polls onto a single index seek while staying
    /// fresher than the dashboard's fastest (two-second) refresh. Set to zero to never cache the
    /// live endpoint.
    /// </summary>
    public double LatestTtlSeconds { get; set; } = 1;

    /// <summary>True when a distributed cache is both switched on and actually configured.</summary>
    public bool IsActive => Enabled && !string.IsNullOrWhiteSpace(RedisConnection);
}
