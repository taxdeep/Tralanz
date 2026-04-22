namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryInvoiceIssuePostingGateSnapshot(
    Guid InvoiceDocumentId,
    int InvoiceOutboundLineCount,
    decimal InvoiceOutboundQuantity,
    int IssueCount,
    decimal IssuedQuantity,
    decimal RemainingQuantity,
    string MatchStatus,
    DateTimeOffset? LatestIssuePostedAt);
