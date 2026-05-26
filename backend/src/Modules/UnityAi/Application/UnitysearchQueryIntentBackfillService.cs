using System.Text.Json;
using System.Text.Json.Serialization;
using Citus.Modules.UnityAi.Application.Contracts;
using Citus.Modules.UnitySearch.Application.Contracts;
using Microsoft.Extensions.Logging;

namespace Citus.Modules.UnityAi.Application;

/// <summary>
/// Plan B backfill orchestrator. Called fire-and-forget from the search
/// hot path on a cache miss. Reserves a 'pending' slot in the intent
/// cache (de-dup), calls the AI gateway, and either promotes to 'ready'
/// or marks 'failed' so future searches don't re-fire the LLM for the
/// same dead query.
///
/// AI-disabled is a first-class outcome — when the gateway returns
/// <see cref="UnityAiTaskOutcome.Disabled"/> or
/// <see cref="UnityAiTaskOutcome.Skipped"/>, the row stays as 'failed'
/// (with reason "ai_disabled") for the 14-day TTL window so the engine
/// stops asking. Future searches naturally degrade to PG-only (Plan A)
/// behaviour with zero hot-path latency penalty.
/// </summary>
public interface IUnitysearchQueryIntentBackfillService
{
    Task BackfillForQueryAsync(
        CompanyId companyId,
        string normalizedQuery,
        string queryHash,
        IReadOnlyList<string> allowedEntityTypes,
        CancellationToken cancellationToken);
}

public sealed class UnitysearchQueryIntentBackfillService : IUnitysearchQueryIntentBackfillService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IUnityAiGateway _gateway;
    private readonly IUnitysearchQueryIntentCacheStore _cache;
    private readonly IUnityAiEmbeddingProvider? _embeddingProvider;
    private readonly UnityAiFeatureFlagAccessor _flags;
    private readonly ILogger<UnitysearchQueryIntentBackfillService> _logger;

    public UnitysearchQueryIntentBackfillService(
        IUnityAiGateway gateway,
        IUnitysearchQueryIntentCacheStore cache,
        UnityAiFeatureFlagAccessor flags,
        ILogger<UnitysearchQueryIntentBackfillService> logger,
        IUnityAiEmbeddingProvider? embeddingProvider = null)
    {
        _gateway = gateway;
        _cache = cache;
        _embeddingProvider = embeddingProvider;
        _flags = flags;
        _logger = logger;
    }

    public async Task BackfillForQueryAsync(
        CompanyId companyId,
        string normalizedQuery,
        string queryHash,
        IReadOnlyList<string> allowedEntityTypes,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery) || string.IsNullOrWhiteSpace(queryHash))
        {
            return;
        }

        // Short-circuit before reserving a row when the gateway flag is
        // off at all. If we reserved and immediately failed, the table
        // would fill up with rows we'd want to retry the instant the
        // operator re-enables AI. By skipping the row insert here, the
        // very next search after AI is enabled will create a fresh
        // 'pending' row and try again.
        if (!_flags.GatewayEnabled)
        {
            _logger.LogDebug(
                "Skipping query-intent backfill for {Company} '{Query}': UnityAi gateway is disabled.",
                companyId, normalizedQuery);
            return;
        }

        bool reserved;
        try
        {
            reserved = await _cache.TryReservePendingAsync(
                companyId, queryHash, normalizedQuery, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to reserve query-intent slot for {Company} '{Query}'.",
                companyId, normalizedQuery);
            return;
        }

        if (!reserved)
        {
            // Another worker has the slot (or a stale row blocks it).
            // Step out of the way — the in-flight worker will populate.
            return;
        }

        var input = new DistillationInput(normalizedQuery, allowedEntityTypes);

        UnityAiTaskResult<DistillationOutput>? result = null;
        try
        {
            result = await _gateway.RunStructuredTaskAsync<DistillationInput, DistillationOutput>(
                new UnityAiTaskRequest<DistillationInput>(
                    TaskType: UnityAiPromptRegistry.UnitysearchQueryIntentV1,
                    Input: input,
                    Context: new UnityAiInvocationContext(
                        CompanyId: companyId,
                        UserId: null,
                        JobRunId: null,
                        ScopeLabel: "unitysearch.query_intent_backfill")),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Gateway threw while backfilling query intent for {Company} '{Query}'.",
                companyId, normalizedQuery);
            await SafeMarkFailedAsync(companyId, queryHash, $"exception: {ex.Message}", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (result is null
            || result.Outcome == UnityAiTaskOutcome.Disabled
            || result.Outcome == UnityAiTaskOutcome.Skipped)
        {
            await SafeMarkFailedAsync(companyId, queryHash, "ai_disabled", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (result.Outcome != UnityAiTaskOutcome.Succeeded || result.Output is null)
        {
            await SafeMarkFailedAsync(
                companyId, queryHash,
                $"outcome={result.Outcome}; {result.ErrorMessage ?? "no_output"}",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        // Filter priors to the picker-allowed set — the AI might suggest
        // entity types the active context doesn't actually surface.
        // Keeping those in the cache would be harmless (the SQL ranker
        // would just never see a doc with that type in this call) but
        // dirty.
        var allowedSet = new HashSet<string>(allowedEntityTypes, StringComparer.OrdinalIgnoreCase);
        var priors = result.Output.EntityTypePriors?
            .Where(kv => allowedSet.Contains(kv.Key))
            .ToDictionary(kv => kv.Key.ToLowerInvariant(), kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        var expandedTerms = ((IEnumerable<string>?)result.Output.ExpandedTerms ?? Array.Empty<string>())
            .Where(static t => !string.IsNullOrWhiteSpace(t))
            .Select(static t => t.Trim().ToLowerInvariant())
            .Where(t => !string.Equals(t, normalizedQuery, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToArray();

        // Optional: also fetch the query embedding so the next search
        // for the same (company, query) pair can pgvector-recall. The
        // embedding step is gated independently on
        // UnityAiFeatureFlagAccessor.EmbeddingsEnabled — if the operator
        // hasn't opted into embeddings, intent (priors + terms) still
        // gets written, just without an embedding column value.
        string? queryEmbeddingLiteral = null;
        if (_flags.EmbeddingsEnabled && _embeddingProvider is not null)
        {
            try
            {
                var embedResult = await _embeddingProvider.EmbedAsync(
                    new UnityAiEmbeddingRequest(
                        Inputs: new[] { normalizedQuery },
                        Context: new UnityAiInvocationContext(
                            CompanyId: companyId,
                            UserId: null,
                            JobRunId: null,
                            ScopeLabel: "unitysearch.query_embedding")),
                    cancellationToken).ConfigureAwait(false);

                if (embedResult.Outcome == UnityAiEmbeddingOutcome.Succeeded
                    && embedResult.Embeddings.Count == 1
                    && embedResult.Embeddings[0] is { Length: > 0 } vec)
                {
                    queryEmbeddingLiteral = FormatPgvectorLiteral(vec);
                }
                else if (embedResult.Outcome is UnityAiEmbeddingOutcome.Failed
                         or UnityAiEmbeddingOutcome.Disabled
                         or UnityAiEmbeddingOutcome.Skipped)
                {
                    _logger.LogDebug(
                        "Embedding skipped for {Company} '{Query}': {Outcome} ({Reason}).",
                        companyId, normalizedQuery, embedResult.Outcome, embedResult.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Embedding provider threw while backfilling query for {Company} '{Query}'.",
                    companyId, normalizedQuery);
            }
        }

        var intent = new UnitysearchQueryIntent(
            EntityTypePriors: priors,
            ExpandedTerms: expandedTerms,
            Confidence: Math.Clamp(result.Output.Confidence ?? 0m, 0m, 1m))
        {
            QueryEmbeddingLiteral = queryEmbeddingLiteral,
        };

        try
        {
            await _cache.MarkReadyAsync(companyId, queryHash, intent, "ai", cancellationToken)
                .ConfigureAwait(false);
            _logger.LogDebug(
                "Query-intent backfill persisted for {Company} '{Query}' ({Priors} priors, {Terms} terms, embedding={HasEmbedding}).",
                companyId, normalizedQuery, priors.Count, expandedTerms.Length, queryEmbeddingLiteral is not null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to write 'ready' query-intent row for {Company} '{Query}'.",
                companyId, normalizedQuery);
        }
    }

    private async Task SafeMarkFailedAsync(
        CompanyId companyId,
        string queryHash,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            await _cache.MarkFailedAsync(companyId, queryHash, reason, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to mark query-intent row 'failed' (reason={Reason}).", reason);
        }
    }

    /// <summary>
    /// Formats a float[] as a pgvector text literal — e.g.
    /// "[0.012345,0.034567,...]". Invariant culture so the decimal
    /// separator is always '.', no thousand separators, R round-trip
    /// precision so floats deserialize back identically. The size
    /// budget for 1536 dims × ~9 chars per number ≈ 14 KB per row,
    /// well under the 8K row limit (toasted).
    /// </summary>
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

    /// <summary>Wire shape sent to the LLM. Keep field names stable — they're in the prompt.</summary>
    public sealed record DistillationInput(
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("allowed_entity_types")] IReadOnlyList<string> AllowedEntityTypes);

    /// <summary>Wire shape returned by the LLM (validated by UnityAiStructuredOutputValidator).</summary>
    public sealed record DistillationOutput(
        [property: JsonPropertyName("entity_type_priors")] Dictionary<string, decimal>? EntityTypePriors,
        [property: JsonPropertyName("expanded_terms")] List<string>? ExpandedTerms,
        [property: JsonPropertyName("confidence")] decimal? Confidence);
}
