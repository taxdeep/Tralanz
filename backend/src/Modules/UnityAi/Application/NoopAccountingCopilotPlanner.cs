using Citus.Modules.UnityAi.Application.Contracts;

namespace Citus.Modules.UnityAi.Application;

/// <summary>
/// Future Accounting Copilot — V1 ships this no-op implementation only.
/// No external AI call. No backend writes. Returns "unsupported" so any
/// caller can fall back to existing UI flows.
/// </summary>
public sealed class NoopAccountingCopilotPlanner : IAccountingCopilotPlanner
{
    public Task<AccountingActionPlan> ParseCommandAsync(AccountingCommandInput input, CancellationToken cancellationToken)
    {
        return Task.FromResult(new AccountingActionPlan(
            Intent: "unsupported",
            Description: "Accounting Copilot is not enabled in this build.",
            RawUtterance: input.Utterance,
            RequiresConfirmation: true,
            Slots: null));
    }

    public Task<AccountingValidationResult> ValidateActionPlanAsync(AccountingActionPlan plan, CancellationToken cancellationToken)
    {
        return Task.FromResult(new AccountingValidationResult(
            IsValid: false,
            Message: "Accounting Copilot is not enabled in this build."));
    }

    public Task<AccountingDraftResult> CreateDraftAfterConfirmationAsync(AccountingActionPlan plan, CancellationToken cancellationToken)
    {
        return Task.FromResult(new AccountingDraftResult(
            Created: false,
            DraftType: null,
            DraftId: null,
            Message: "Accounting Copilot is not enabled in this build."));
    }
}
