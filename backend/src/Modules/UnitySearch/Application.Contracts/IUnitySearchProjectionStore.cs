namespace Citus.Modules.UnitySearch.Application.Contracts;

public interface IUnitySearchProjectionStore
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task EnsureProjectionFreshAsync(Guid companyId, CancellationToken cancellationToken);

    /// <summary>
    /// Drops the in-memory "last refreshed" timestamp for the company so
    /// the next <see cref="EnsureProjectionFreshAsync"/> call rebuilds the
    /// projection immediately instead of waiting out the 5-minute refresh
    /// window. Call this from CRUD endpoints that mutate searchable
    /// entities (customers, vendors, invoices, journal entries, …) so a
    /// freshly-created row shows up the next time the operator searches.
    ///
    /// Best-effort: a no-op when the timestamp isn't tracked yet (no
    /// operator has searched this company yet) since the next search
    /// will rebuild from scratch anyway.
    /// </summary>
    Task InvalidateAsync(Guid companyId, CancellationToken cancellationToken);

    /// <summary>
    /// Bulk lookup of <c>(entity_type, source_id)</c> → display text. Used
    /// by the unityAI hint-distillation flow to enrich raw usage-stat rows
    /// with the human-readable name the LLM needs to reason about. Returns
    /// only pairs that exist in <c>search_documents</c>; missing pairs are
    /// silently dropped (caller decides whether to fall back).
    /// </summary>
    Task<IReadOnlyDictionary<(string EntityType, Guid SourceId), SearchDocumentDisplay>> GetDisplayNamesAsync(
        Guid companyId,
        IReadOnlyCollection<(string EntityType, Guid SourceId)> keys,
        CancellationToken cancellationToken);
}

public sealed record SearchDocumentDisplay(
    string PrimaryText,
    string SecondaryText,
    bool IsActive);
