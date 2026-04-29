using Citus.Modules.UnityAi.Application.Contracts;
using Citus.Platform.Core.Abstractions;

namespace Citus.Modules.UnityAi.Application;

/// <summary>
/// Routes every gateway task to whatever provider/model SysAdmin has
/// configured in <c>platform_ai_provider_config</c>. Returns <c>null</c>
/// (which the gateway treats as <see cref="UnityAiTaskOutcome.Disabled"/>)
/// when:
///   * no row exists yet,
///   * provider is set to <c>disabled</c>,
///   * the API key is empty.
///
/// V1: one global provider for every task type. Per-task or per-tenant
/// routing (e.g. cheap vs advanced model, regional providers) lands in a
/// later batch driven by <see cref="UnityAiCapability"/> on the request.
/// </summary>
public sealed class PlatformConfigBackedModelRouter : IUnityAiModelRouter
{
    private readonly IPlatformAiProviderRuntimeResolver _resolver;

    public PlatformConfigBackedModelRouter(IPlatformAiProviderRuntimeResolver resolver)
    {
        _resolver = resolver;
    }

    public AiModelSelection? Select(string taskType, UnityAiInvocationContext context)
    {
        var snapshot = _resolver.GetCurrent();
        if (snapshot is null)
        {
            // Cold cache. The first call after process start hits this; the
            // resolver only refreshes on demand, so do a synchronous prime
            // here. Subsequent calls within the 30 s TTL are free.
            snapshot = _resolver.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        if (snapshot is null) return null;
        if (string.Equals(snapshot.Provider, PlatformAiProviderKeys.Disabled, StringComparison.OrdinalIgnoreCase)) return null;
        if (string.IsNullOrWhiteSpace(snapshot.ApiKey)) return null;
        if (string.IsNullOrWhiteSpace(snapshot.Model)) return null;

        return new AiModelSelection(
            Provider: snapshot.Provider,
            Model: snapshot.Model,
            Capability: "default",
            PromptVersion: "v1");
    }
}
