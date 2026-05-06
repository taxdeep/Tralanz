namespace Citus.Modules.UnityAi.Application.Contracts;

/// <summary>
/// Carrier passed to every gateway call so the gateway can correlate
/// audit / log / company-isolation context.
/// </summary>
public sealed record UnityAiInvocationContext(
    CompanyId? CompanyId,
    UserId? UserId,
    Guid? JobRunId,
    string? ScopeLabel = null);

/// <summary>
/// A request to the gateway. <typeparamref name="TInput"/> is the redacted
/// input payload that will be JSON-serialized and validated against the
/// task's schema before it reaches the provider.
/// </summary>
public sealed record UnityAiTaskRequest<TInput>(
    string TaskType,
    TInput Input,
    UnityAiInvocationContext Context,
    string? PromptVersion = null,
    int? TimeoutMs = null);

public enum UnityAiTaskOutcome
{
    /// <summary>The gateway short-circuited because the task was disabled by configuration.</summary>
    Disabled,
    /// <summary>The provider was a no-op (e.g. NoopAIProvider).</summary>
    Skipped,
    Succeeded,
    Failed,
    InvalidOutput,
}

/// <summary>
/// Result of a gateway call. Business modules should treat
/// <see cref="UnityAiTaskOutcome.Disabled"/> and
/// <see cref="UnityAiTaskOutcome.Skipped"/> as "AI not available right now"
/// and continue with deterministic fallbacks.
/// </summary>
public sealed record UnityAiTaskResult<TOutput>(
    UnityAiTaskOutcome Outcome,
    TOutput? Output,
    string? Provider,
    string? Model,
    string? PromptVersion,
    int? TokenInputCount,
    int? TokenOutputCount,
    decimal? EstimatedCost,
    int? LatencyMs,
    string? ErrorMessage,
    Guid? RequestLogId);

/// <summary>
/// Provider-side request shape — what the gateway sends after structured
/// output validation, redaction, and prompt registration.
/// </summary>
public sealed record AiRequest(
    string TaskType,
    string? Provider,
    string? Model,
    string PromptVersion,
    string SystemPrompt,
    string UserPrompt,
    string ResponseSchemaName,
    int? MaxOutputTokens,
    int? TimeoutMs,
    UnityAiInvocationContext Context);

public sealed record AiResponse(
    UnityAiTaskOutcome Outcome,
    string? OutputJson,
    int? TokenInputCount,
    int? TokenOutputCount,
    decimal? EstimatedCost,
    int? LatencyMs,
    string? ErrorMessage);

public sealed record AiModelSelection(
    string Provider,
    string Model,
    string Capability,
    string PromptVersion);

public sealed record PromptTemplate(
    string TaskType,
    string Version,
    string SystemPrompt,
    string UserPromptTemplate,
    string ResponseSchemaName);
