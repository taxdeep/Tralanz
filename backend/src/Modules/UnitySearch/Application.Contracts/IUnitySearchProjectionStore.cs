namespace Citus.Modules.UnitySearch.Application.Contracts;

public interface IUnitySearchProjectionStore
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task EnsureProjectionFreshAsync(Guid companyId, CancellationToken cancellationToken);

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
