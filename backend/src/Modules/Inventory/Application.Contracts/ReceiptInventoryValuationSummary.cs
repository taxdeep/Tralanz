namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class ReceiptInventoryValuationSummary(
    Guid ReceiptDocumentId,
    string ValuationStatus,
    decimal ActivatedQuantity,
    decimal BillCoveredQuantity,
    decimal ValuedQuantity,
    decimal UnvaluedQuantity,
    int ValuationLineCount,
    decimal ValuationAmountBase,
    DateTimeOffset? LastValuedAt);
