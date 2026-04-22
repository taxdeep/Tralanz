namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class ReceiptGrIrBridgeSummary(
    Guid ReceiptDocumentId,
    string BridgeStatus,
    int BridgeLineCount,
    int EligibleLineCount,
    int BlockedReconciliationLineCount,
    int BlockedVarianceLineCount,
    int PostedLineCount,
    decimal BridgeQuantity,
    decimal BridgeAmountBase,
    decimal EligibleAmountBase,
    decimal BlockedAmountBase,
    decimal PostedAmountBase,
    Guid? JournalEntryId,
    string? JournalEntryDisplayNumber,
    DateTimeOffset? LastPostedAt,
    DateTimeOffset? LastRefreshedAt);
