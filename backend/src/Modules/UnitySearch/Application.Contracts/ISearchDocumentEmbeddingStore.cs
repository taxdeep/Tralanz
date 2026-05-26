namespace Citus.Modules.UnitySearch.Application.Contracts;

/// <summary>
/// Read/write surface for the <c>search_documents.embedding</c> column.
/// Used exclusively by the doc-embedding back-fill job — the hot search
/// path reads the column inline via <c>PostgreSqlUnitySearchQueryService</c>'s
/// SQL and never goes through this store.
///
/// Both methods scope by <see cref="CompanyId"/>. The back-fill never sees
/// rows from another tenant.
/// </summary>
public interface ISearchDocumentEmbeddingStore
{
    /// <summary>
    /// Returns up to <paramref name="batchSize"/> rows with
    /// <c>embedding IS NULL</c> for the company, ordered deterministically
    /// (id ASC) so consecutive batches don't fight for the same rows.
    /// The partial index <c>ix_search_documents_embedding_pending</c>
    /// serves the lookup.
    /// </summary>
    Task<IReadOnlyList<SearchDocumentEmbeddingCandidate>> ListPendingAsync(
        CompanyId companyId,
        int batchSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes embeddings back as pgvector text literals (cast to
    /// <c>vector</c> inside SQL). Returns the number of rows actually
    /// updated — a row gone missing between the LIST and UPDATE (e.g.
    /// projection rebuild) is silently skipped, not an error.
    /// </summary>
    Task<int> UpdateEmbeddingsAsync(
        CompanyId companyId,
        IReadOnlyList<(Guid Id, string EmbeddingLiteral)> pairs,
        CancellationToken cancellationToken);
}

public sealed record SearchDocumentEmbeddingCandidate(
    Guid Id,
    string EntityType,
    string PrimaryText,
    string? SearchText);
