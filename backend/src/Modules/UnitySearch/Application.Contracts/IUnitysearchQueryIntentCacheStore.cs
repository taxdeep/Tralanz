namespace Citus.Modules.UnitySearch.Application.Contracts;

/// <summary>
/// Plan B per-company query-intent cache. Reads sit on the search hot
/// path (single point-lookup by (company_id, query_hash) hitting a
/// partial index — sub-millisecond). Writes are off the hot path: the
/// backfill service inserts a 'pending' row, calls the AI gateway, then
/// upgrades to 'ready' (or 'failed' if the gateway is disabled /
/// errored).
///
/// Cache lifetime is bounded by <c>expires_at</c> (default 14 days).
/// Reads filter on <c>status = 'ready' AND expires_at > now()</c> so a
/// stale row falls through to the same PG-only path as a miss.
///
/// Hard isolation contract: every entry is keyed by
/// <see cref="CompanyId"/>. Implementations MUST scope all reads + writes
/// to the caller's company. Cross-tenant intent leakage is non-negotiable.
/// </summary>
public interface IUnitysearchQueryIntentCacheStore
{
    /// <summary>
    /// Returns the cached intent for (companyId, queryHash) when a
    /// 'ready', unexpired row exists. Returns null on miss / stale /
    /// failed — all three are indistinguishable from the engine's
    /// point of view so PG-only behaviour kicks in.
    /// </summary>
    Task<UnitysearchQueryIntent?> GetReadyAsync(
        CompanyId companyId,
        string queryHash,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reserves a slot for an in-flight backfill. Inserts (or no-ops on
    /// existing) a row with <c>status = 'pending'</c>. Returns true if
    /// the caller won the race and should proceed with the LLM call,
    /// false if another worker already owns the slot (an existing
    /// pending / ready / failed row is in the way).
    /// </summary>
    Task<bool> TryReservePendingAsync(
        CompanyId companyId,
        string queryHash,
        string normalizedQuery,
        CancellationToken cancellationToken);

    /// <summary>
    /// Promotes the row to <c>'ready'</c> with the AI-distilled intent
    /// payload. Idempotent for the same (companyId, queryHash).
    ///
    /// <see cref="UnitysearchQueryIntent.QueryEmbeddingLiteral"/> may
    /// be null — Plan C-Population writes both intent + embedding in
    /// one call, but the embedding is independently optional (e.g. when
    /// the embeddings flag is off but the gateway is on).
    /// </summary>
    Task MarkReadyAsync(
        CompanyId companyId,
        string queryHash,
        UnitysearchQueryIntent intent,
        string source,
        CancellationToken cancellationToken);

    /// <summary>
    /// Marks the row as <c>'failed'</c> with a reason. The TTL stays in
    /// place so future searches don't keep re-firing the LLM call for
    /// the same dead query.
    /// </summary>
    Task MarkFailedAsync(
        CompanyId companyId,
        string queryHash,
        string reason,
        CancellationToken cancellationToken);
}
