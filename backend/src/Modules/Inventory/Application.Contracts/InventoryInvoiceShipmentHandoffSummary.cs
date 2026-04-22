namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryInvoiceShipmentHandoffSummary(
    Guid InvoiceDocumentId,
    int InvoiceOutboundLineCount,
    decimal InvoiceOutboundQuantity,
    int ShipmentCount,
    decimal ShippedQuantity,
    decimal RemainingQuantity,
    string MatchStatus,
    DateTimeOffset? LatestShipmentPostedAt,
    IReadOnlyList<InventoryShipmentSummary> RecentShipments,
    IReadOnlyList<InventoryInvoiceShipmentHandoffLineSummary> LineSummaries);
