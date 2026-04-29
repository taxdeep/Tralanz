using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Citus.Modules.UnityAi.Application.Contracts;
using Citus.Modules.UnityAi.Domain.Shared;
using Microsoft.Extensions.Logging;

namespace Citus.Modules.UnityAi.Application;

/// <summary>
/// Default gateway. Disabled by default — short-circuits to
/// <see cref="UnityAiTaskOutcome.Disabled"/> when the gateway flag is off,
/// no provider is configured, or no prompt template is registered.
///
/// Every call (including disabled / skipped / failed) writes a row to
/// <see cref="IAiRequestLogStore"/> so the system stays non-black-box.
/// </summary>
public sealed class UnityAiGateway : IUnityAiGateway
{
    /// <summary>Substitution token recognized in <see cref="PromptTemplate.UserPromptTemplate"/>.</summary>
    public const string InputJsonToken = "{{INPUT_JSON}}";

    private readonly UnityAiFeatureFlagAccessor _flags;
    private readonly IUnityAiModelRouter _router;
    private readonly IUnityAiPromptRegistry _prompts;
    private readonly IUnityAiStructuredOutputValidator _validator;
    private readonly IEnumerable<IUnityAiProvider> _providers;
    private readonly IAiRequestLogStore _requestLogStore;
    private readonly ILogger<UnityAiGateway> _logger;

    public UnityAiGateway(
        UnityAiFeatureFlagAccessor flags,
        IUnityAiModelRouter router,
        IUnityAiPromptRegistry prompts,
        IUnityAiStructuredOutputValidator validator,
        IEnumerable<IUnityAiProvider> providers,
        IAiRequestLogStore requestLogStore,
        ILogger<UnityAiGateway> logger)
    {
        _flags = flags;
        _router = router;
        _prompts = prompts;
        _validator = validator;
        _providers = providers;
        _requestLogStore = requestLogStore;
        _logger = logger;
    }

    public async Task<UnityAiTaskResult<TOutput>> RunStructuredTaskAsync<TInput, TOutput>(
        UnityAiTaskRequest<TInput> request,
        CancellationToken cancellationToken)
    {
        var inputJson = SerializeRedacted(request.Input);
        var inputHash = ComputeHash(inputJson);

        // Gate 1: feature flag.
        if (!_flags.GatewayEnabled)
        {
            return await LogAndReturnAsync<TOutput>(
                request, AiRequestLogStatus.Skipped, UnityAiTaskOutcome.Disabled,
                provider: null, model: null, promptVersion: null,
                inputJson, inputHash, errorMessage: "gateway disabled by feature flag",
                latencyMs: null, cancellationToken).ConfigureAwait(false);
        }

        // Gate 2: model router selects a provider/model.
        var selection = _router.Select(request.TaskType, request.Context);
        if (selection is null)
        {
            return await LogAndReturnAsync<TOutput>(
                request, AiRequestLogStatus.Skipped, UnityAiTaskOutcome.Disabled,
                provider: null, model: null, promptVersion: null,
                inputJson, inputHash, errorMessage: "no provider for task",
                latencyMs: null, cancellationToken).ConfigureAwait(false);
        }

        // Gate 3: prompt template is registered.
        var prompt = _prompts.Get(request.TaskType, selection.PromptVersion ?? request.PromptVersion);
        if (prompt is null)
        {
            return await LogAndReturnAsync<TOutput>(
                request, AiRequestLogStatus.Skipped, UnityAiTaskOutcome.Disabled,
                selection.Provider, selection.Model, selection.PromptVersion,
                inputJson, inputHash, errorMessage: "no prompt template registered",
                latencyMs: null, cancellationToken).ConfigureAwait(false);
        }

        // Gate 4: a provider for the selected name actually exists.
        var provider = _providers.FirstOrDefault(p => string.Equals(p.Name, selection.Provider, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            return await LogAndReturnAsync<TOutput>(
                request, AiRequestLogStatus.Skipped, UnityAiTaskOutcome.Disabled,
                selection.Provider, selection.Model, selection.PromptVersion,
                inputJson, inputHash, errorMessage: $"provider '{selection.Provider}' is not registered",
                latencyMs: null, cancellationToken).ConfigureAwait(false);
        }

        // Render the user prompt with the redacted input JSON. Templates
        // address the input via the literal token {{INPUT_JSON}}; templates
        // that don't include the token simply send a static user prompt
        // (useful for tasks that only need the system prompt). Anything
        // more elaborate (named slots, conditionals) is deferred until a
        // real task asks for it — not worth the abstraction yet.
        var renderedUserPrompt = prompt.UserPromptTemplate.Contains(InputJsonToken, StringComparison.Ordinal)
            ? prompt.UserPromptTemplate.Replace(InputJsonToken, inputJson, StringComparison.Ordinal)
            : prompt.UserPromptTemplate;

        var aiRequest = new AiRequest(
            TaskType: request.TaskType,
            Provider: selection.Provider,
            Model: selection.Model,
            PromptVersion: prompt.Version,
            SystemPrompt: prompt.SystemPrompt,
            UserPrompt: renderedUserPrompt,
            ResponseSchemaName: prompt.ResponseSchemaName,
            MaxOutputTokens: null,
            TimeoutMs: request.TimeoutMs,
            Context: request.Context);

        var stopwatch = Stopwatch.StartNew();
        AiResponse aiResponse;
        try
        {
            aiResponse = await provider.CompleteStructuredAsync(aiRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "unityAI provider {Provider} threw for task {TaskType}", provider.Name, request.TaskType);
            return await LogAndReturnAsync<TOutput>(
                request, AiRequestLogStatus.Failed, UnityAiTaskOutcome.Failed,
                selection.Provider, selection.Model, selection.PromptVersion,
                inputJson, inputHash, errorMessage: ex.Message,
                latencyMs: (int)stopwatch.ElapsedMilliseconds, cancellationToken).ConfigureAwait(false);
        }
        stopwatch.Stop();
        var latencyMs = (int)stopwatch.ElapsedMilliseconds;

        if (aiResponse.Outcome == UnityAiTaskOutcome.Skipped)
        {
            return await LogAndReturnAsync<TOutput>(
                request, AiRequestLogStatus.Skipped, UnityAiTaskOutcome.Skipped,
                selection.Provider, selection.Model, selection.PromptVersion,
                inputJson, inputHash, errorMessage: aiResponse.ErrorMessage,
                latencyMs, cancellationToken).ConfigureAwait(false);
        }

        if (aiResponse.Outcome == UnityAiTaskOutcome.Failed || aiResponse.OutputJson is null)
        {
            return await LogAndReturnAsync<TOutput>(
                request, AiRequestLogStatus.Failed, UnityAiTaskOutcome.Failed,
                selection.Provider, selection.Model, selection.PromptVersion,
                inputJson, inputHash, errorMessage: aiResponse.ErrorMessage ?? "provider failed",
                latencyMs, cancellationToken).ConfigureAwait(false);
        }

        var validation = _validator.Validate(request.TaskType, aiResponse.OutputJson);
        if (validation is not null)
        {
            return await LogAndReturnAsync<TOutput>(
                request, AiRequestLogStatus.InvalidOutput, UnityAiTaskOutcome.InvalidOutput,
                selection.Provider, selection.Model, selection.PromptVersion,
                inputJson, inputHash, errorMessage: validation,
                latencyMs, cancellationToken).ConfigureAwait(false);
        }

        TOutput? output = default;
        try
        {
            output = JsonSerializer.Deserialize<TOutput>(aiResponse.OutputJson);
        }
        catch (JsonException ex)
        {
            return await LogAndReturnAsync<TOutput>(
                request, AiRequestLogStatus.InvalidOutput, UnityAiTaskOutcome.InvalidOutput,
                selection.Provider, selection.Model, selection.PromptVersion,
                inputJson, inputHash, errorMessage: ex.Message,
                latencyMs, cancellationToken).ConfigureAwait(false);
        }

        var logId = await _requestLogStore.WriteAsync(new AiRequestLogRecord(
            Id: Guid.NewGuid(),
            CompanyId: request.Context.CompanyId,
            JobRunId: request.Context.JobRunId,
            TaskType: request.TaskType,
            Provider: selection.Provider,
            Model: selection.Model,
            RequestSchemaVersion: null,
            ResponseSchemaVersion: null,
            InputHash: inputHash,
            InputRedactedJson: null,
            OutputRedactedJson: null,
            Status: AiRequestLogStatus.Succeeded,
            ErrorMessage: null,
            PromptVersion: selection.PromptVersion,
            TokenInputCount: aiResponse.TokenInputCount,
            TokenOutputCount: aiResponse.TokenOutputCount,
            EstimatedCost: aiResponse.EstimatedCost,
            LatencyMs: latencyMs,
            CreatedAt: DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);

        return new UnityAiTaskResult<TOutput>(
            UnityAiTaskOutcome.Succeeded,
            output,
            selection.Provider,
            selection.Model,
            selection.PromptVersion,
            aiResponse.TokenInputCount,
            aiResponse.TokenOutputCount,
            aiResponse.EstimatedCost,
            latencyMs,
            null,
            logId);
    }

    private async Task<UnityAiTaskResult<TOutput>> LogAndReturnAsync<TOutput>(
        object request,
        string logStatus,
        UnityAiTaskOutcome outcome,
        string? provider,
        string? model,
        string? promptVersion,
        string inputJson,
        string inputHash,
        string? errorMessage,
        int? latencyMs,
        CancellationToken cancellationToken)
    {
        var typed = (dynamic)request;
        UnityAiInvocationContext context = typed.Context;
        string taskType = typed.TaskType;

        Guid logId = Guid.Empty;
        try
        {
            logId = await _requestLogStore.WriteAsync(new AiRequestLogRecord(
                Id: Guid.NewGuid(),
                CompanyId: context.CompanyId,
                JobRunId: context.JobRunId,
                TaskType: taskType,
                Provider: provider,
                Model: model,
                RequestSchemaVersion: null,
                ResponseSchemaVersion: null,
                InputHash: inputHash,
                InputRedactedJson: null,
                OutputRedactedJson: null,
                Status: logStatus,
                ErrorMessage: errorMessage,
                PromptVersion: promptVersion,
                TokenInputCount: null,
                TokenOutputCount: null,
                EstimatedCost: null,
                LatencyMs: latencyMs,
                CreatedAt: DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "unityAI request log write failed (task={TaskType}, status={Status})", taskType, logStatus);
        }

        return new UnityAiTaskResult<TOutput>(
            Outcome: outcome,
            Output: default,
            Provider: provider,
            Model: model,
            PromptVersion: promptVersion,
            TokenInputCount: null,
            TokenOutputCount: null,
            EstimatedCost: null,
            LatencyMs: latencyMs,
            ErrorMessage: errorMessage,
            RequestLogId: logId == Guid.Empty ? null : logId);
    }

    private static string SerializeRedacted<TInput>(TInput input)
    {
        try
        {
            return JsonSerializer.Serialize(input);
        }
        catch
        {
            return "{}";
        }
    }

    private static string ComputeHash(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
