namespace Citus.Modules.UnityAi.Application.Contracts;

/// <summary>
/// Egress for text-embedding requests. Separate interface from
/// <see cref="IUnityAiProvider"/> because embeddings don't fit the
/// structured-completion shape: no system / user prompt split, no JSON
/// schema, no temperature — just text in, float vector out. Each provider
/// registers its own implementation; the embedding service selects by
/// <see cref="Name"/> (string match) just like the completion provider
/// dispatch.
///
/// Calls MUST be idempotent + side-effect-free: a back-fill job can re-run
/// against the same input set and the provider must return semantically
/// equivalent embeddings (OpenAI guarantees this for a given model + input
/// pair).
///
/// Implementations must NEVER throw; all failure modes flow through
/// <see cref="UnityAiEmbeddingOutcome"/> so callers degrade gracefully —
/// the search hot path never sees an exception from this surface.
/// </summary>
public interface IUnityAiEmbeddingProvider
{
    string Name { get; }

    Task<UnityAiEmbeddingResult> EmbedAsync(
        UnityAiEmbeddingRequest request,
        CancellationToken cancellationToken);
}

public sealed record UnityAiEmbeddingRequest(
    /// <summary>Texts to embed in one batch. Provider may apply its own per-call cap; the service should batch upstream.</summary>
    IReadOnlyList<string> Inputs,
    UnityAiInvocationContext Context,
    /// <summary>Optional override; null = provider picks (e.g. text-embedding-3-small).</summary>
    string? Model = null);

public enum UnityAiEmbeddingOutcome
{
    /// <summary>Embeddings feature flag is off OR no provider configured.</summary>
    Disabled,
    /// <summary>Provider was a no-op (e.g. NoopEmbeddingProvider during tests).</summary>
    Skipped,
    Succeeded,
    /// <summary>Provider reached but errored (network, auth, rate limit, model error).</summary>
    Failed,
}

public sealed record UnityAiEmbeddingResult(
    UnityAiEmbeddingOutcome Outcome,
    /// <summary>One float[] per input, in the same order. Empty when not Succeeded.</summary>
    IReadOnlyList<float[]> Embeddings,
    string? Provider,
    string? Model,
    int? TokenInputCount,
    decimal? EstimatedCost,
    int? LatencyMs,
    string? ErrorMessage);
