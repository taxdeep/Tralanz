namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class ReceiptInventoryCostLayerEmissionReconciliationSummary(
    Guid ReceiptDocumentId,
    string ReconciliationStatus,
    int EmissionLineCount,
    int CostLayerCount,
    int MissingCostLayerCount,
    int OrphanCostLayerCount,
    decimal EmittedQuantity,
    decimal CostLayerQuantity,
    decimal EmittedCostBase,
    decimal CostLayerOriginalCostBase,
    DateTimeOffset? LastEmittedAt);
