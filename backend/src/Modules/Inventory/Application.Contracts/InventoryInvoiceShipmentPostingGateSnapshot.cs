namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryInvoiceShipmentPostingGateSnapshot(
    Guid InvoiceDocumentId,
    int InvoiceOutboundLineCount,
    decimal InvoiceOutboundQuantity,
    int ShipmentCount,
    decimal ShippedQuantity,
    decimal RemainingQuantity,
    string MatchStatus,
    DateTimeOffset? LatestShipmentPostedAt,
    decimal InvoicedQuantity,
    decimal RemainingToInvoiceQuantity,
    string InvoiceCoverageStatus,
    DateTimeOffset? InvoicePostedAt);
