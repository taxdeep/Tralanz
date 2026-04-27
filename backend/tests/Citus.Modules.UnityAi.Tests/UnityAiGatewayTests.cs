using Citus.Modules.UnityAi.Application;
using Citus.Modules.UnityAi.Application.Contracts;
using Citus.Modules.UnityAi.Domain.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Citus.Modules.UnityAi.Tests;

public sealed class UnityAiGatewayTests
{
    [Fact]
    public async Task Gateway_ReturnsDisabled_WhenFlagOff()
    {
        var (gateway, logs, _) = BuildGateway(gatewayEnabled: false);
        var result = await gateway.RunStructuredTaskAsync<object, object>(new UnityAiTaskRequest<object>(
            TaskType: UnityAiTaskType.UnitysearchLearningSummary,
            Input: new { },
            Context: new UnityAiInvocationContext(Guid.NewGuid(), null, null)),
            CancellationToken.None);

        Assert.Equal(UnityAiTaskOutcome.Disabled, result.Outcome);
        Assert.Single(logs.Records);
        Assert.Equal(AiRequestLogStatus.Skipped, logs.Records[0].Status);
        Assert.NotNull(result.RequestLogId);
    }

    [Fact]
    public async Task Gateway_ReturnsDisabled_WhenRouterReturnsNull()
    {
        // Flag on, but router returns null (no provider configured) → still disabled.
        var (gateway, logs, _) = BuildGateway(gatewayEnabled: true);
        var result = await gateway.RunStructuredTaskAsync<object, object>(new UnityAiTaskRequest<object>(
            TaskType: UnityAiTaskType.AccountingCommandParse,
            Input: new { },
            Context: new UnityAiInvocationContext(Guid.NewGuid(), null, null)),
            CancellationToken.None);

        Assert.Equal(UnityAiTaskOutcome.Disabled, result.Outcome);
        Assert.Single(logs.Records);
        Assert.Equal(AiRequestLogStatus.Skipped, logs.Records[0].Status);
        Assert.Equal("no provider for task", logs.Records[0].ErrorMessage);
    }

    private static (UnityAiGateway Gateway, InMemoryAiRequestLogStore Logs, IConfiguration Config) BuildGateway(bool gatewayEnabled)
    {
        var settings = new Dictionary<string, string?>
        {
            [UnityAiFeatureFlagKeys.AiGatewayEnabled] = gatewayEnabled ? "true" : "false",
        };
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var flags = new UnityAiFeatureFlagAccessor(config);
        var logs = new InMemoryAiRequestLogStore();

        var gateway = new UnityAiGateway(
            flags,
            new NoopUnityAiModelRouter(),
            new NoopUnityAiPromptRegistry(),
            new NoopUnityAiStructuredOutputValidator(),
            new IUnityAiProvider[] { new NoopAiProvider() },
            logs,
            NullLogger<UnityAiGateway>.Instance);

        return (gateway, logs, config);
    }
}
