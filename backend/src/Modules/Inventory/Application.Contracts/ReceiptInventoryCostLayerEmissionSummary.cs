namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class ReceiptInventoryCostLayerEmissionSummary(
    Guid ReceiptDocumentId,
    string EmissionStatus,
    decimal ActivatedQuantity,
    decimal ValuationBackedQuantity,
    decimal EmissionEligibleQuantity,
    decimal EmittedQuantity,
    decimal UnemittedQuantity,
    int EmissionLineCount,
    decimal EmittedCostBase,
    DateTimeOffset? LastEmittedAt);
