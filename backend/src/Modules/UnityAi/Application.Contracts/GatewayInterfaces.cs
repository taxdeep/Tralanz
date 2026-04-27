namespace Citus.Modules.UnityAi.Application.Contracts;

/// <summary>
/// Single egress point for AI calls. Business modules call this; never a
/// provider directly. Disabled by default — returns
/// <see cref="UnityAiTaskOutcome.Disabled"/> when the gateway feature flag
/// is off or no compatible provider is configured.
/// </summary>
public interface IUnityAiGateway
{
    Task<UnityAiTaskResult<TOutput>> RunStructuredTaskAsync<TInput, TOutput>(
        UnityAiTaskRequest<TInput> request,
        CancellationToken cancellationToken);
}

public interface IUnityAiProvider
{
    string Name { get; }
    bool Supports(string taskType, string capability);
    Task<AiResponse> CompleteStructuredAsync(AiRequest request, CancellationToken cancellationToken);
}

public interface IUnityAiModelRouter
{
    /// <summary>Returns the selected model, or <c>null</c> if no provider can serve the task.</summary>
    AiModelSelection? Select(string taskType, UnityAiInvocationContext context);
}

public interface IUnityAiPromptRegistry
{
    PromptTemplate? Get(string taskType, string? requestedVersion);
}

public interface IUnityAiStructuredOutputValidator
{
    /// <summary>Returns null on success, an error message on validation failure.</summary>
    string? Validate(string taskType, string outputJson);
}
