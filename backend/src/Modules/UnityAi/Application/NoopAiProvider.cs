using Citus.Modules.UnityAi.Application.Contracts;

namespace Citus.Modules.UnityAi.Application;

/// <summary>
/// The default unityAI provider. Performs no network I/O. Returns
/// <see cref="UnityAiTaskOutcome.Skipped"/> for every request so the
/// gateway can exist (and emit observability rows) without any external
/// dependency configured. Always safe to register in DI.
/// </summary>
public sealed class NoopAiProvider : IUnityAiProvider
{
    public string Name => "noop";

    /// <summary>
    /// The noop provider declares support for every task / capability so it
    /// can be picked as a last-resort selection by the model router. Real
    /// providers will be picked first when configured.
    /// </summary>
    public bool Supports(string taskType, string capability) => true;

    public Task<AiResponse> CompleteStructuredAsync(AiRequest request, CancellationToken cancellationToken)
    {
        var response = new AiResponse(
            Outcome: UnityAiTaskOutcome.Skipped,
            OutputJson: null,
            TokenInputCount: 0,
            TokenOutputCount: 0,
            EstimatedCost: 0m,
            LatencyMs: 0,
            ErrorMessage: null);

        return Task.FromResult(response);
    }
}
