namespace Web.Shell.Services;

public sealed record class ShellAccountingDocumentReviewSummary
{
    public string SourceType { get; init; } = string.Empty;

    public string SourceTypeLabel { get; init; } = string.Empty;

    public Guid Id { get; init; }

    public Guid CompanyId { get; init; }

    public string EntityNumber { get; init; } = string.Empty;

    public string DisplayNumber { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public DateOnly DocumentDate { get; init; }

    public DateOnly? DueDate { get; init; }

    public string CounterpartyLabel { get; init; } = string.Empty;

    public Guid? CounterpartyId { get; init; }

    public string ControlAccountLabel { get; init; } = string.Empty;

    public Guid? ControlAccountId { get; init; }

    public Guid? JournalEntryId { get; init; }

    public string? JournalEntryDisplayNumber { get; init; }

    public string? JournalEntryStatus { get; init; }

    public DateTimeOffset? JournalEntryPostedAt { get; init; }

    public DateTimeOffset? JournalEntryVoidedAt { get; init; }

    public DateTimeOffset? JournalEntryReversedAt { get; init; }

    public string LifecycleMode { get; init; } = string.Empty;

    public bool CanEditDraft { get; init; }

    public bool CanPostDraft { get; init; }

    public string LifecycleReason { get; init; } = string.Empty;

    public IReadOnlyList<ShellAccountingDocumentLifecycleActionSummary> LifecycleActions { get; init; } = Array.Empty<ShellAccountingDocumentLifecycleActionSummary>();

    public string TransactionCurrencyCode { get; init; } = string.Empty;

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public decimal SubtotalAmount { get; init; }

    public decimal TaxAmount { get; init; }

    public decimal TotalAmount { get; init; }

    public string? Memo { get; init; }

    public IReadOnlyList<ShellAccountingDocumentReviewLineSummary> Lines { get; init; } = Array.Empty<ShellAccountingDocumentReviewLineSummary>();
}

public sealed record class ShellAccountingDocumentReviewLineSummary
{
    public int LineNumber { get; init; }

    public Guid AccountId { get; init; }

    public string AccountCode { get; init; } = string.Empty;

    public string AccountName { get; init; } = string.Empty;

    public string AccountLabel { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public decimal? Quantity { get; init; }

    public decimal? UnitPrice { get; init; }

    public decimal LineAmount { get; init; }

    public decimal TaxAmount { get; init; }

    public bool? IsTaxRecoverable { get; init; }

    public Guid? TaxAccountId { get; init; }

    public decimal? TxDebit { get; init; }

    public decimal? TxCredit { get; init; }

    public Guid? SourceOpenItemId { get; init; }

    public string? SourceDocumentType { get; init; }

    public Guid? SourceDocumentId { get; init; }

    public string? SourceDocumentDisplayNumber { get; init; }

    public Guid? TargetOpenItemId { get; init; }

    public string? TargetDocumentType { get; init; }

    public Guid? TargetDocumentId { get; init; }

    public string? TargetDocumentDisplayNumber { get; init; }
}

public sealed record class ShellPurchaseOrderReviewSummary
{
    public Guid Id { get; init; }

    public Guid CompanyId { get; init; }

    public string EntityNumber { get; init; } = string.Empty;

    public string DisplayNumber { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public Guid VendorId { get; init; }

    public DateOnly OrderDate { get; init; }

    public DateOnly? ExpectedDate { get; init; }

    public string? VendorReference { get; init; }

    public string? Memo { get; init; }

    public DateTimeOffset? ApprovedAt { get; init; }

    public DateTimeOffset? IssuedAt { get; init; }

    public DateTimeOffset? ClosedAt { get; init; }

    public DateTimeOffset? CancelledAt { get; init; }

    public DateTimeOffset? AmendmentStartedAt { get; init; }

    public decimal? EstimatedAmount { get; init; }

    public ShellPurchaseOrderAnchorGovernanceSummary AnchorGovernance { get; init; } = new();

    public ShellPurchaseOrderApprovalAuthoritySummary ApprovalAuthority { get; init; } = new();

    public ShellPurchaseOrderThreeQuantitySummary? ThreeQuantity { get; init; }

    public IReadOnlyList<ShellPurchaseOrderLineSummary> Lines { get; init; } = Array.Empty<ShellPurchaseOrderLineSummary>();
}

public sealed record class ShellPurchaseOrderLineSummary
{
    public int LineNumber { get; init; }

    public Guid ItemId { get; init; }

    public decimal OrderedQuantity { get; init; }

    public string UomCode { get; init; } = string.Empty;

    public string? Description { get; init; }

    public decimal? UnitCost { get; init; }
}

public sealed record class ShellPurchaseOrderAnchorGovernanceSummary
{
    public bool AllowsNewAnchors { get; init; }

    public string Summary { get; init; } = string.Empty;
}

public sealed record class ShellPurchaseOrderApprovalAuthoritySummary
{
    public decimal? EstimatedOrderAmount { get; init; }

    public decimal ThresholdAmount { get; init; }

    public bool RequiresGovernanceApproval { get; init; }

    public string Summary { get; init; } = string.Empty;
}

public sealed record class ShellPurchaseOrderThreeQuantitySummary
{
    public Guid PurchaseOrderId { get; init; }

    public decimal OrderedQuantity { get; init; }

    public decimal ReceivedQuantity { get; init; }

    public decimal BilledQuantity { get; init; }

    public decimal RemainingToReceiveQuantity { get; init; }

    public decimal RemainingToBillQuantity { get; init; }

    public string ReceiptStatus { get; init; } = string.Empty;

    public string BillStatus { get; init; } = string.Empty;

    public string QuantityStatus { get; init; } = string.Empty;

    public int OpenDiscrepancyCount { get; init; }
}

public sealed record class ShellPurchaseOrderLifecycleAuditEntry
{
    public Guid AuditId { get; init; }

    public Guid PurchaseOrderId { get; init; }

    public string Action { get; init; } = string.Empty;

    public string ActorType { get; init; } = string.Empty;

    public Guid? ActorId { get; init; }

    public string? FromStatus { get; init; }

    public string? ToStatus { get; init; }

    public string? EntityNumber { get; init; }

    public string? DisplayNumber { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record class ShellAccountingDocumentLifecycleActionSummary
{
    public string ActionCode { get; init; } = string.Empty;

    public string ActionLabel { get; init; } = string.Empty;

    public string AvailabilityMode { get; init; } = string.Empty;

    public bool IsAvailable { get; init; }

    public string Reason { get; init; } = string.Empty;
}

public sealed record class ShellAccountingDocumentReverseRequestSummary
{
    public Guid RequestId { get; init; }

    public Guid CompanyId { get; init; }

    public string SourceType { get; init; } = string.Empty;

    public string SourceTypeLabel { get; init; } = string.Empty;

    public Guid Id { get; init; }

    public string EntityNumber { get; init; } = string.Empty;

    public string DisplayNumber { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public Guid? JournalEntryId { get; init; }

    public string? JournalEntryDisplayNumber { get; init; }

    public string? JournalEntryStatus { get; init; }

    public string LifecycleMode { get; init; } = string.Empty;

    public string ActionCode { get; init; } = string.Empty;

    public string ActionLabel { get; init; } = string.Empty;

    public string AvailabilityMode { get; init; } = string.Empty;

    public bool IsAvailable { get; init; }

    public string Reason { get; init; } = string.Empty;

    public string RequestStatus { get; init; } = string.Empty;

    public string RequestedByActorType { get; init; } = string.Empty;

    public Guid? RequestedByActorId { get; init; }

    public DateTimeOffset RequestedAt { get; init; }

    public string? SubmittedByActorType { get; init; }

    public Guid? SubmittedByActorId { get; init; }

    public DateTimeOffset? SubmittedAt { get; init; }

    public string? CancelledByActorType { get; init; }

    public Guid? CancelledByActorId { get; init; }

    public DateTimeOffset? CancelledAt { get; init; }

    public string ExecutionStatus { get; init; } = string.Empty;

    public string? ExecutionRequestedByActorType { get; init; }

    public Guid? ExecutionRequestedByActorId { get; init; }

    public DateTimeOffset? ExecutionRequestedAt { get; init; }

    public string? ExecutionCompletedByActorType { get; init; }

    public Guid? ExecutionCompletedByActorId { get; init; }

    public DateTimeOffset? ExecutionCompletedAt { get; init; }

    public Guid? CompensationJournalEntryId { get; init; }

    public string? CompensationJournalEntryDisplayNumber { get; init; }

    public string? CompensationSourceType { get; init; }
}

public sealed record class ShellAccountingDocumentReverseCommandResultSummary
{
    public string? TransitionCode { get; init; }

    public string OutcomeCode { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public Guid? RequestId { get; init; }

    public Guid CompanyId { get; init; }

    public string SourceType { get; init; } = string.Empty;

    public string SourceTypeLabel { get; init; } = string.Empty;

    public Guid Id { get; init; }

    public string EntityNumber { get; init; } = string.Empty;

    public string DisplayNumber { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public Guid? JournalEntryId { get; init; }

    public string? JournalEntryDisplayNumber { get; init; }

    public string? JournalEntryStatus { get; init; }

    public string LifecycleMode { get; init; } = string.Empty;

    public string ActionCode { get; init; } = string.Empty;

    public string ActionLabel { get; init; } = string.Empty;

    public string AvailabilityMode { get; init; } = string.Empty;

    public string ExecutionMode { get; init; } = string.Empty;

    public bool CommandAccepted { get; init; }

    public bool Executed { get; init; }

    public bool Persisted { get; init; }

    public string RequestStatus { get; init; } = string.Empty;

    public string ExecutionStatus { get; init; } = string.Empty;

    public DateOnly? AsOfDate { get; init; }

    public string? ExecutionRequestedByActorType { get; init; }

    public Guid? ExecutionRequestedByActorId { get; init; }

    public DateTimeOffset? ExecutionRequestedAt { get; init; }

    public string? ExecutionCompletedByActorType { get; init; }

    public Guid? ExecutionCompletedByActorId { get; init; }

    public DateTimeOffset? ExecutionCompletedAt { get; init; }

    public Guid? CompensationJournalEntryId { get; init; }

    public string? CompensationJournalEntryDisplayNumber { get; init; }

    public string? CompensationSourceType { get; init; }
}

public sealed record class ShellAccountingDocumentReverseApplyReadinessSummary
{
    public Guid RequestId { get; init; }

    public Guid CompanyId { get; init; }

    public string SourceType { get; init; } = string.Empty;

    public string SourceTypeLabel { get; init; } = string.Empty;

    public Guid Id { get; init; }

    public string EntityNumber { get; init; } = string.Empty;

    public string DisplayNumber { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string RequestStatus { get; init; } = string.Empty;

    public string LifecycleMode { get; init; } = string.Empty;

    public DateOnly AsOfDate { get; init; }

    public bool GovernanceReady { get; init; }

    public bool ApplyReady { get; init; }

    public string ExecutionMode { get; init; } = string.Empty;

    public string AvailabilityMode { get; init; } = string.Empty;

    public bool IsAvailable { get; init; }

    public string Reason { get; init; } = string.Empty;
}

public sealed record class ShellAccountingDocumentReverseExecutionPlanSummary
{
    public Guid RequestId { get; init; }

    public Guid CompanyId { get; init; }

    public string SourceType { get; init; } = string.Empty;

    public string SourceTypeLabel { get; init; } = string.Empty;

    public Guid Id { get; init; }

    public string EntityNumber { get; init; } = string.Empty;

    public string DisplayNumber { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string RequestStatus { get; init; } = string.Empty;

    public string ExecutionStatus { get; init; } = string.Empty;

    public string LifecycleMode { get; init; } = string.Empty;

    public DateOnly AsOfDate { get; init; }

    public string ExecutionMode { get; init; } = string.Empty;

    public bool CanExecute { get; init; }

    public string OverallStatus { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public IReadOnlyList<ShellAccountingDocumentReverseExecutionPlanStepSummary> Steps { get; init; } = Array.Empty<ShellAccountingDocumentReverseExecutionPlanStepSummary>();
}

public sealed record class ShellAccountingDocumentReverseExecutionPlanStepSummary
{
    public int StepNumber { get; init; }

    public string StepCode { get; init; } = string.Empty;

    public string StepLabel { get; init; } = string.Empty;

    public string StepStatus { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;
}

public sealed record class ShellSubledgerReverseBlockerSummary
{
    public Guid SettlementApplicationId { get; init; }

    public string ApplicationType { get; init; } = string.Empty;

    public string SettlementSourceType { get; init; } = string.Empty;

    public string SettlementSourceTypeLabel { get; init; } = string.Empty;

    public Guid SettlementSourceId { get; init; }

    public string SettlementSourceDisplayNumber { get; init; } = string.Empty;

    public DateOnly? SettlementSourceDocumentDate { get; init; }

    public string TargetOpenItemType { get; init; } = string.Empty;

    public Guid TargetOpenItemId { get; init; }

    public string TargetSourceType { get; init; } = string.Empty;

    public string TargetSourceTypeLabel { get; init; } = string.Empty;

    public Guid TargetSourceId { get; init; }

    public string TargetSourceDisplayNumber { get; init; } = string.Empty;

    public decimal AppliedAmountTx { get; init; }

    public decimal AppliedAmountBase { get; init; }

    public decimal? SettlementFxRate { get; init; }

    public decimal? RealizedFxAmount { get; init; }

    public DateTimeOffset AppliedAt { get; init; }

    public Guid? ReverseRequestId { get; init; }

    public string ReverseRequestStatus { get; init; } = string.Empty;

    public string ReverseExecutionStatus { get; init; } = string.Empty;

    public DateTimeOffset? ReverseRequestedAt { get; init; }
}

public sealed record class ShellSettlementApplicationReversalSummary
{
    public Guid ReversalEventId { get; init; }

    public Guid RequestId { get; init; }

    public Guid SettlementApplicationId { get; init; }

    public string ApplicationType { get; init; } = string.Empty;

    public string SourceType { get; init; } = string.Empty;

    public string SourceTypeLabel { get; init; } = string.Empty;

    public Guid SourceId { get; init; }

    public string TargetOpenItemType { get; init; } = string.Empty;

    public Guid TargetOpenItemId { get; init; }

    public decimal AppliedAmountTx { get; init; }

    public decimal AppliedAmountBase { get; init; }

    public decimal? SettlementFxRate { get; init; }

    public decimal? RealizedFxAmount { get; init; }

    public DateTimeOffset OriginalApplicationCreatedAt { get; init; }

    public Guid? OriginalApplicationCreatedByUserId { get; init; }

    public DateTimeOffset ReversedAt { get; init; }

    public string ReversedByActorType { get; init; } = string.Empty;

    public Guid? ReversedByActorId { get; init; }

    public string ReversalMode { get; init; } = string.Empty;
}
