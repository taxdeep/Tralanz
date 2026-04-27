using Citus.Modules.UnityAi.Application;
using Citus.Modules.UnityAi.Application.Contracts;

namespace Citus.Modules.UnityAi.Tests;

public sealed class NoopAiProviderTests
{
    [Fact]
    public async Task NoopProvider_ReturnsSkipped_AndDoesNotCallExternalApi()
    {
        var provider = new NoopAiProvider();

        var request = new AiRequest(
            TaskType: "any",
            Provider: "noop",
            Model: "noop",
            PromptVersion: "v1",
            SystemPrompt: "",
            UserPrompt: "",
            ResponseSchemaName: "",
            MaxOutputTokens: null,
            TimeoutMs: null,
            Context: new UnityAiInvocationContext(CompanyId: Guid.NewGuid(), UserId: null, JobRunId: null));

        var response = await provider.CompleteStructuredAsync(request, CancellationToken.None);

        Assert.Equal(UnityAiTaskOutcome.Skipped, response.Outcome);
        Assert.Null(response.OutputJson);
        Assert.Equal(0, response.TokenInputCount);
        Assert.Equal(0, response.TokenOutputCount);
    }

    [Fact]
    public void NoopProvider_DeclaresAllTaskCapabilities()
    {
        var provider = new NoopAiProvider();
        Assert.True(provider.Supports("any_task", "any_capability"));
    }
}
