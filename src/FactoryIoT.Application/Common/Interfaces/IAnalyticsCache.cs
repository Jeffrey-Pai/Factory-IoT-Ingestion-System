namespace FactoryIoT.Application.Common.Interfaces;

/// <summary>
/// A short-lived, read-through cache for the analytics read path — the port the read side uses to
/// avoid recomputing an identical aggregate for every dashboard poll, k6 request and Prometheus
/// scrape that lands within the same second or two.
/// </summary>
/// <remarks>
/// <para>
/// The contract is deliberately technology-agnostic: it names no backing store, so the inner layers
/// stay ignorant of Redis exactly the way <see cref="ITelemetryConsumer"/> keeps them ignorant of
/// RabbitMQ. The Infrastructure implementation is free to be a distributed cache (the shipped one),
/// an in-process one, or a no-op — the read path calls this the same way regardless.
/// </para>
/// <para>
/// Two properties the implementation is required to honour, because callers depend on them:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <b>Fail-open.</b> A cache is an optimisation, never a dependency of correctness. If the backing
/// store is unreachable or errors, the implementation must still run <paramref name="factory"/> and
/// return its result — a cache outage may only make reads slower, never fail them.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Negatives are cacheable.</b> <typeparamref name="T"/> may be a nullable type whose
/// <c>null</c> is a meaningful answer (e.g. "this machine reported nothing in the window", the 404
/// case). A cached <c>null</c> must be distinguishable from a cache miss, so that answer is served
/// from cache for its TTL rather than re-hitting the database on every poll for a quiet machine.
/// </description>
/// </item>
/// </list>
/// </remarks>
public interface IAnalyticsCache
{
    /// <summary>
    /// Returns the cached value for (<paramref name="region"/>, <paramref name="key"/>) when one is
    /// live, otherwise invokes <paramref name="factory"/>, stores its result for
    /// <paramref name="ttl"/>, and returns it.
    /// </summary>
    /// <typeparam name="T">
    /// The cached value's type. Must round-trip through the implementation's serializer; the read
    /// models on this path (<c>MachineTelemetrySummary</c>, <c>TelemetryStatistics</c>,
    /// <c>FleetStatus</c>) are immutable records and do.
    /// </typeparam>
    /// <param name="region">
    /// A coarse, low-cardinality bucket for the key — used both to namespace the stored key and as a
    /// metric label. Pass a fixed family name (e.g. <c>"roster"</c>, <c>"fleet"</c>), never anything
    /// carrying a machine id or timestamp, or the metric's cardinality explodes.
    /// </param>
    /// <param name="key">The unique key within <paramref name="region"/>.</param>
    /// <param name="ttl">
    /// How long a stored value stays live. A non-positive value means "do not cache": the
    /// implementation must invoke <paramref name="factory"/> and return its result without touching
    /// the store, which is how a single region is turned off from configuration.
    /// </param>
    /// <param name="factory">Computes the value on a miss. Receives the cancellation token.</param>
    /// <param name="cancellationToken">Cancels the factory and any store access.</param>
    Task<T> GetOrCreateAsync<T>(
        string region,
        string key,
        TimeSpan ttl,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken = default);
}
