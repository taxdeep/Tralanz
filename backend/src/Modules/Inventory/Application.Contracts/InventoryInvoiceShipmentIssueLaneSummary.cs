namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryInvoiceShipmentIssueLaneSummary(
    Guid InvoiceDocumentId,
    int InvoiceOutboundLineCount,
    decimal InvoiceOutboundQuantity,
    int ShipmentCount,
    decimal ShippedQuantity,
    decimal RemainingToShipQuantity,
    string ShipmentMatchStatus,
    decimal InvoicedQuantity,
    decimal RemainingToInvoiceQuantity,
    string InvoiceCoverageStatus,
    int IssueCount,
    decimal IssuedQuantity,
    decimal RemainingToIssueQuantity,
    string IssueMatchStatus,
    DateTimeOffset? LatestShipmentPostedAt,
    DateTimeOffset? InvoicePostedAt,
    DateTimeOffset? LatestIssuePostedAt,
    IReadOnlyList<InventoryShipmentSummary> RecentShipments,
    IReadOnlyList<InventorySalesIssueSummary> RecentIssues,
    IReadOnlyList<InventoryOutboundDiscrepancySummary> Discrepancies,
    IReadOnlyList<InventoryInvoiceShipmentIssueLineLaneSummary> LineSummaries);
