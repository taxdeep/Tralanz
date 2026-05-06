using Citus.Modules.UnityAi.Application;
using Citus.Modules.UnityAi.Application.Contracts;
using Citus.Modules.UnityAi.Domain.Shared;

namespace Citus.Modules.UnityAi.Tests;

public sealed class NoopAccountingCopilotPlannerTests
{
    [Fact]
    public async Task NoopPlanner_ReturnsUnsupported_AndDoesNotWrite()
    {
        var planner = new NoopAccountingCopilotPlanner();
        var input = new AccountingCommandInput(CompanyId.FromOrdinal(1), UserId.FromOrdinal(1), "test command");

        var plan = await planner.ParseCommandAsync(input, CancellationToken.None);
        Assert.Equal("unsupported", plan.Intent);
        Assert.True(plan.RequiresConfirmation);

        var validation = await planner.ValidateActionPlanAsync(plan, CancellationToken.None);
        Assert.False(validation.IsValid);

        var draft = await planner.CreateDraftAfterConfirmationAsync(plan, CancellationToken.None);
        Assert.False(draft.Created);
        Assert.Null(draft.DraftId);
    }

    [Fact]
    public void ActionLevelEnum_HasExpectedValues()
    {
        // V1 ships levels 0..2 explicitly; 3..4 are reserved for future.
        Assert.Equal(0, (int)UnityAiActionLevel.ReadOnly);
        Assert.Equal(1, (int)UnityAiActionLevel.SuggestOnly);
        Assert.Equal(2, (int)UnityAiActionLevel.CreateDraft);
        Assert.Equal(3, (int)UnityAiActionLevel.PreparePosting);
        Assert.Equal(4, (int)UnityAiActionLevel.AutoPostWithPolicy);
    }
}
