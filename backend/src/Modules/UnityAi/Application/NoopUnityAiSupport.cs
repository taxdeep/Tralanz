using Citus.Modules.UnityAi.Application.Contracts;

namespace Citus.Modules.UnityAi.Application;

/// <summary>
/// Default model router that always returns <c>null</c>. The gateway
/// reacts to this by short-circuiting to <see cref="UnityAiTaskOutcome.Disabled"/>.
/// Real implementations will replace this once a provider is configured.
/// </summary>
public sealed class NoopUnityAiModelRouter : IUnityAiModelRouter
{
    public AiModelSelection? Select(string taskType, UnityAiInvocationContext context) => null;
}

/// <summary>
/// Default prompt registry. Returns <c>null</c> so the gateway treats the
/// task as not yet wired and short-circuits.
/// </summary>
public sealed class NoopUnityAiPromptRegistry : IUnityAiPromptRegistry
{
    public PromptTemplate? Get(string taskType, string? requestedVersion) => null;
}

/// <summary>
/// Default validator. Accepts any output (returns <c>null</c> = no error).
/// Real validators will pin a JSON schema per task type.
/// </summary>
public sealed class NoopUnityAiStructuredOutputValidator : IUnityAiStructuredOutputValidator
{
    public string? Validate(string taskType, string outputJson) => null;
}
