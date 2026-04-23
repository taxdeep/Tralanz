namespace Web.Shell.Services;

public sealed record class ShellOpenItemDrillDownResponse
{
    public ShellOpenItemDetail? OpenItem { get; init; }

    public IReadOnlyList<ShellOpenItemApplicationDetail> Applications { get; init; } = Array.Empty<ShellOpenItemApplicationDetail>();
}

public sealed record class ShellOpenItemDetail
{
    public Guid OpenItemId { get; init; }

    public string OpenItemType { get; init; } = string.Empty;

    public Guid CompanyId { get; init; }

    public string PartyRole { get; init; } = string.Empty;

    public Guid PartyId { get; init; }

    public string PartyEntityNumber { get; init; } = string.Empty;

    public string PartyDisplayName { get; init; } = string.Empty;

    public string SourceType { get; init; } = string.Empty;

    public Guid SourceDocumentId { get; init; }

    public string SourceDocumentDisplayNumber { get; init; } = string.Empty;

    public DateOnly DocumentDate { get; init; }

    public DateOnly? DueDate { get; init; }

    public string DocumentCurrencyCode { get; init; } = string.Empty;

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public string BalanceSide { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public decimal OriginalAmountTx { get; init; }

    public decimal OriginalAmountBase { get; init; }

    public decimal OpenAmountTx { get; init; }

    public decimal OpenAmountBase { get; init; }
}

public sealed record class ShellOpenItemApplicationDetail
{
    public Guid ApplicationId { get; init; }

    public string ApplicationType { get; init; } = string.Empty;

    public string SourceType { get; init; } = string.Empty;

    public Guid SourceDocumentId { get; init; }

    public string SourceDocumentDisplayNumber { get; init; } = string.Empty;

    public DateOnly SourceDocumentDate { get; init; }

    public decimal AppliedAmountTx { get; init; }

    public decimal AppliedAmountBase { get; init; }

    public decimal? SettlementFxRate { get; init; }

    public decimal? RealizedFxAmount { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record class ShellOpenItemAdjustmentPreviewSummary
{
    public Guid OpenItemId { get; init; }

    public string OpenItemType { get; init; } = string.Empty;

    public Guid CompanyId { get; init; }

    public string PartyRole { get; init; } = string.Empty;

    public Guid PartyId { get; init; }

    public string SourceType { get; init; } = string.Empty;

    public Guid SourceDocumentId { get; init; }

    public string SourceDocumentDisplayNumber { get; init; } = string.Empty;

    public string SourceDocumentStatus { get; init; } = string.Empty;

    public DateOnly DocumentDate { get; init; }

    public DateOnly? DueDate { get; init; }

    public string DocumentCurrencyCode { get; init; } = string.Empty;

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public string BalanceSide { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public decimal OpenAmountTx { get; init; }

    public decimal OpenAmountBase { get; init; }

    public decimal RequestedAdjustmentAmountTx { get; init; }

    public decimal RequestedAdjustmentAmountBase { get; init; }

    public decimal RemainingAmountTx { get; init; }

    public decimal RemainingAmountBase { get; init; }

    public int ApplicationCount { get; init; }

    public string AdjustmentType { get; init; } = string.Empty;

    public DateOnly AdjustmentDate { get; init; }

    public bool RequiresApproval { get; init; }

    public string ApprovalStatus { get; init; } = string.Empty;

    public string ApprovalReason { get; init; } = string.Empty;

    public string ActionCode { get; init; } = string.Empty;

    public string ActionLabel { get; init; } = string.Empty;

    public string AvailabilityMode { get; init; } = string.Empty;

    public bool IsAvailable { get; init; }

    public string ExecutionMode { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;
}

public sealed record class ShellOpenItemAdjustmentRequestSummary
{
    public Guid RequestId { get; init; }

    public Guid OpenItemId { get; init; }

    public string OpenItemType { get; init; } = string.Empty;

    public Guid CompanyId { get; init; }

    public string AdjustmentType { get; init; } = string.Empty;

    public DateOnly AdjustmentDate { get; init; }

    public decimal RequestedAdjustmentAmountTx { get; init; }

    public decimal RequestedAdjustmentAmountBase { get; init; }

    public bool RequiresApproval { get; init; }

    public string ApprovalStatus { get; init; } = string.Empty;

    public string? ApprovedByActorType { get; init; }

    public Guid? ApprovedByActorId { get; init; }

    public DateTimeOffset? ApprovedAt { get; init; }

    public string? RejectedByActorType { get; init; }

    public Guid? RejectedByActorId { get; init; }

    public DateTimeOffset? RejectedAt { get; init; }

    public string RequestStatus { get; init; } = string.Empty;

    public string ExecutionStatus { get; init; } = string.Empty;

    public string RequestedByActorType { get; init; } = string.Empty;

    public Guid? RequestedByActorId { get; init; }

    public DateTimeOffset RequestedAt { get; init; }

    public string? SubmittedByActorType { get; init; }

    public Guid? SubmittedByActorId { get; init; }

    public DateTimeOffset? SubmittedAt { get; init; }

    public string? CancelledByActorType { get; init; }

    public Guid? CancelledByActorId { get; init; }

    public DateTimeOffset? CancelledAt { get; init; }

    public string? Reason { get; init; }
}

public sealed record class ShellOpenItemAdjustmentRequestAttemptSummary
{
    public ShellOpenItemAdjustmentPreviewSummary? Preview { get; init; }

    public bool CommandAccepted { get; init; }

    public bool Executed { get; init; }

    public bool Persisted { get; init; }

    public string OutcomeCode { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public ShellOpenItemAdjustmentRequestSummary? Request { get; init; }
}

public sealed record class ShellOpenItemAdjustmentTransitionResultSummary
{
    public ShellOpenItemAdjustmentRequestSummary? Request { get; init; }

    public string TransitionCode { get; init; } = string.Empty;

    public string OutcomeCode { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}

public sealed record class ShellOpenItemAdjustmentReadinessSummary
{
    public ShellOpenItemAdjustmentRequestSummary? Request { get; init; }

    public DateOnly AsOfDate { get; init; }

    public bool GovernanceReady { get; init; }

    public bool OpenItemReady { get; init; }

    public bool PostingExecutionReady { get; init; }

    public string ExecutionMode { get; init; } = string.Empty;

    public string AvailabilityMode { get; init; } = string.Empty;

    public bool IsAvailable { get; init; }

    public string Reason { get; init; } = string.Empty;
}

public sealed record class ShellOpenItemAdjustmentExecutionPlanSummary
{
    public ShellOpenItemAdjustmentRequestSummary? Request { get; init; }

    public DateOnly AsOfDate { get; init; }

    public string ExecutionMode { get; init; } = string.Empty;

    public bool CanExecute { get; init; }

    public string OverallStatus { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public IReadOnlyList<ShellOpenItemAdjustmentExecutionPlanStepSummary> Steps { get; init; } = Array.Empty<ShellOpenItemAdjustmentExecutionPlanStepSummary>();
}

public sealed record class ShellOpenItemAdjustmentExecutionPlanStepSummary
{
    public int StepNumber { get; init; }

    public string StepCode { get; init; } = string.Empty;

    public string StepLabel { get; init; } = string.Empty;

    public string StepStatus { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;
}

public sealed record class ShellOpenItemAdjustmentExecutionResultSummary
{
    public Guid? JournalEntryId { get; init; }

    public string? JournalEntryDisplayNumber { get; init; }

    public string Status { get; init; } = string.Empty;

    public DateTimeOffset PostedAt { get; init; }

    public decimal AdjustmentAmountTx { get; init; }

    public decimal AdjustmentAmountBase { get; init; }
}

public sealed record class ShellOpenItemAdjustmentErrorSummary
{
    public string? Code { get; init; }

    public string Message { get; init; } = string.Empty;
}
