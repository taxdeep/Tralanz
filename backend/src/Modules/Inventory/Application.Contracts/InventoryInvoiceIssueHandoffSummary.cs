namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryInvoiceIssueHandoffSummary(
    Guid InvoiceDocumentId,
    int InvoiceOutboundLineCount,
    decimal InvoiceOutboundQuantity,
    int IssueCount,
    decimal IssuedQuantity,
    decimal RemainingQuantity,
    string MatchStatus,
    DateTimeOffset? LatestIssuePostedAt,
    IReadOnlyList<InventorySalesIssueSummary> RecentIssues,
    IReadOnlyList<InventoryInvoiceIssueHandoffLineSummary> LineSummaries);
