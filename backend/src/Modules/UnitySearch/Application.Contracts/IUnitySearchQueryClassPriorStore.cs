namespace Citus.Modules.UnitySearch.Application.Contracts;

/// <summary>
/// Per-user (per-company) prior on "when this user types a query of class X,
/// they tend to click results of entity_type Y." Used by the ranker as a
/// tiebreaker among same-tier matches — e.g. when a numeric_decimal query
/// matches both a JE and a Bill at exactly the same amount, the prior
/// decides which surfaces first.
///
/// Orthogonal to <see cref="IUnitySearchStatsStore"/> (which is per-document)
/// and to <c>IUnitysearchUsageStatStore</c> (the unityAI learning loop's
/// own per-document aggregate). This store specifically captures the
/// query-class → entity-type signal that's invisible at the document level.
/// </summary>
public interface IUnitySearchQueryClassPriorStore
{
    /// <summary>
    /// Increment the (user, query_class, entity_type) click bucket. No-ops on
    /// empty / unknown query class — the legacy text path doesn't need a
    /// prior (existing rank_boost already encodes a sensible default).
    /// </summary>
    Task RecordSelectAsync(
        CompanyId companyId,
        UserId userId,
        string queryClassTag,
        string entityType,
        CancellationToken cancellationToken);
}
