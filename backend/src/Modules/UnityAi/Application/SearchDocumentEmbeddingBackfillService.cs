using Citus.Modules.UnityAi.Application.Contracts;
using Citus.Modules.UnitySearch.Application.Contracts;
using Microsoft.Extensions.Logging;

namespace Citus.Modules.UnityAi.Application;

/// <summary>
/// Per-company batch embedding back-fill for <c>search_documents</c>.
/// Mirrors <see cref="IUnitysearchHintDistillationService"/>'s shape:
/// admin-triggered, single-company per call, idempotent (re-runs only
/// pick up rows that are still <c>embedding IS NULL</c>).
///
/// Reads each document's <c>search_text</c> (the FTS-friendly concat
/// the projection already builds for tsvector), batches into one
/// embedding-provider call per N rows, writes the result back as
/// pgvector data. The HNSW index installed in the Plan C foundation
/// migration auto-incorporates the new vectors.
///
/// Gated independently on <see cref="UnityAiFeatureFlagAccessor.EmbeddingsEnabled"/>
/// — the operator can run doc embeddings without enabling the
/// chat-completion gateway and vice versa. When the embedding provider
/// returns Disabled / Skipped / Failed, the back-fill stops cleanly
/// and leaves the column NULL on every doc it hadn't reached yet.
/// </summary>
public interface ISearchDocumentEmbeddingBackfillService
{
    Task<SearchDocumentEmbeddingBackfillResult> BackfillForCompanyAsync(
        CompanyId companyId,
        int maxBatches,
        CancellationToken cancellationToken);
}

public sealed record SearchDocumentEmbeddingBackfillResult(
    int CandidatesScanned,
    int BatchesRun,
    int RowsEmbedded,
    int Failed,
    string? OverallStatus,
    string? Note);

public sealed class SearchDocumentEmbeddingBackfillService : ISearchDocumentEmbeddingBackfillService
{
    /// <summary>OpenAI accepts up to 2048 inputs per call; 64 is a conservative middle ground that keeps a stuck batch cheap to retry.</summary>
    private const int BatchSize = 64;

    private readonly IUnityAiEmbeddingProvider _embeddingProvider;
    private readonly ISearchDocumentEmbeddingStore _store;
    private readonly UnityAiFeatureFlagAccessor _flags;
    private readonly ILogger<SearchDocumentEmbeddingBackfillService> _logger;

    public SearchDocumentEmbeddingBackfillService(
        IUnityAiEmbeddingProvider embeddingProvider,
        ISearchDocumentEmbeddingStore store,
        UnityAiFeatureFlagAccessor flags,
        ILogger<SearchDocumentEmbeddingBackfillService> logger)
    {
        _embeddingProvider = embeddingProvider;
        _store = store;
        _flags = flags;
        _logger = logger;
    }

    public async Task<SearchDocumentEmbeddingBackfillResult> BackfillForCompanyAsync(
        CompanyId companyId,
        int maxBatches,
        CancellationToken cancellationToken)
    {
        if (!_flags.EmbeddingsEnabled)
        {
            return new SearchDocumentEmbeddingBackfillResult(0, 0, 0, 0,
                OverallStatus: "skipped",
                Note: "Embeddings feature flag is off.");
        }

        var totalScanned = 0;
        var batches = 0;
        var embedded = 0;
        var failed = 0;
        var allowedBatches = Math.Clamp(maxBatches, 1, 100);

        for (var batch = 0; batch < allowedBatches; batch++)
        {
            var candidates = await _store.ListPendingAsync(companyId, BatchSize, cancellationToken)
                .ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                break;
            }
            totalScanned += candidates.Count;
            batches++;

            // Use search_text when present (the projection's FTS concat
            // covers name, code, description, kind, etc.). Fall back to
            // primary_text on the rare row where search_text is empty
            // so we always send something embeddable.
            var inputs = candidates
                .Select(c => string.IsNullOrWhiteSpace(c.SearchText) ? c.PrimaryText : c.SearchText)
                .ToArray();

            UnityAiEmbeddingResult embedResult;
            try
            {
                embedResult = await _embeddingProvider.EmbedAsync(
                    new UnityAiEmbeddingRequest(
                        Inputs: inputs,
                        Context: new UnityAiInvocationContext(
                            CompanyId: companyId,
                            UserId: null,
                            JobRunId: null,
                            ScopeLabel: "search.document_embedding")),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Embedding provider threw during doc back-fill batch {Batch} for {Company}.",
                    batch, companyId);
                failed += candidates.Count;
                break;
            }

            if (embedResult.Outcome != UnityAiEmbeddingOutcome.Succeeded)
            {
                _logger.LogInformation(
                    "Doc-embedding back-fill stopped early at batch {Batch} for {Company}: outcome={Outcome} ({Reason}).",
                    batch, companyId, embedResult.Outcome, embedResult.ErrorMessage);
                failed += candidates.Count;
                break;
            }

            if (embedResult.Embeddings.Count != candidates.Count)
            {
                _logger.LogWarning(
                    "Embedding provider returned {Got} vectors for {Want} inputs (batch {Batch}, {Company}); skipping batch.",
                    embedResult.Embeddings.Count, candidates.Count, batch, companyId);
                failed += candidates.Count;
                continue;
            }

            try
            {
                var pairs = new List<(Guid Id, string Literal)>(candidates.Count);
                for (var i = 0; i < candidates.Count; i++)
                {
                    pairs.Add((candidates[i].Id, FormatPgvectorLiteral(embedResult.Embeddings[i])));
                }
                var updated = await _store.UpdateEmbeddingsAsync(companyId, pairs, cancellationToken)
                    .ConfigureAwait(false);
                embedded += updated;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to write embeddings for batch {Batch} ({Company}).", batch, companyId);
                failed += candidates.Count;
            }
        }

        var status = (batches, failed) switch
        {
            (0, _) => "noop",
            (_, 0) => "ok",
            _ => "partial",
        };
        return new SearchDocumentEmbeddingBackfillResult(
            CandidatesScanned: totalScanned,
            BatchesRun: batches,
            RowsEmbedded: embedded,
            Failed: failed,
            OverallStatus: status,
            Note: null);
    }

    private static string FormatPgvectorLiteral(float[] vector)
    {
        var sb = new System.Text.StringBuilder(vector.Length * 10 + 2);
        sb.Append('[');
        for (var i = 0; i < vector.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(vector[i].ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }
        sb.Append(']');
        return sb.ToString();
    }
}
