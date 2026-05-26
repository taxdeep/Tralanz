namespace Citus.Modules.UnitySearch.Application.Contracts;

/// <summary>
/// Fire-and-forget enqueuer for the Plan B query-intent backfill. The
/// UnitySearch engine calls this on a cache miss and continues serving
/// the request from the PG-only path; the implementation owns its own
/// background task / DI scope and writes the cache row when the AI
/// gateway returns. Errors must NEVER propagate into the search hot
/// path.
///
/// Kept as an interface here so the UnitySearch module doesn't take a
/// hard dependency on Citus.Modules.UnityAi — the AI side wires the
/// actual implementation, and a no-op fallback in the same module
/// short-circuits when the AI module isn't installed.
/// </summary>
public interface IUnitysearchQueryIntentBackfillEnqueuer
{
    void Enqueue(
        CompanyId companyId,
        string normalizedQuery,
        string queryHash,
        IReadOnlyList<string> allowedEntityTypes);
}

/// <summary>
/// Default no-op enqueuer wired when the UnityAi module's
/// concrete implementation is unavailable / not registered. Lets the
/// search engine compile + run without dragging the AI infrastructure
/// in. Silently drops every enqueue.
/// </summary>
public sealed class NoopUnitysearchQueryIntentBackfillEnqueuer : IUnitysearchQueryIntentBackfillEnqueuer
{
    public void Enqueue(
        CompanyId companyId,
        string normalizedQuery,
        string queryHash,
        IReadOnlyList<string> allowedEntityTypes)
    {
        // intentionally empty
    }
}
