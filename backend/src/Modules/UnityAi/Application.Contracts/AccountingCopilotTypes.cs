namespace Citus.Modules.UnityAi.Application.Contracts;

public sealed record AccountingCommandInput(
    CompanyId CompanyId,
    UserId? UserId,
    string Utterance,
    string? Locale = null);

public sealed record AccountingActionPlan(
    string Intent,
    string Description,
    string? RawUtterance,
    bool RequiresConfirmation,
    IReadOnlyDictionary<string, string?>? Slots);

public sealed record AccountingValidationResult(
    bool IsValid,
    string? Message);

public sealed record AccountingDraftResult(
    bool Created,
    string? DraftType,
    Guid? DraftId,
    string? Message);

/// <summary>
/// Future Accounting Copilot planner. V1 ships only the Noop implementation
/// — see <c>NoopAccountingCopilotPlanner</c>. No code path here writes to
/// any business engine; drafts go through the same handlers a UI form uses.
/// </summary>
public interface IAccountingCopilotPlanner
{
    Task<AccountingActionPlan> ParseCommandAsync(AccountingCommandInput input, CancellationToken cancellationToken);

    Task<AccountingValidationResult> ValidateActionPlanAsync(AccountingActionPlan plan, CancellationToken cancellationToken);

    Task<AccountingDraftResult> CreateDraftAfterConfirmationAsync(AccountingActionPlan plan, CancellationToken cancellationToken);
}
