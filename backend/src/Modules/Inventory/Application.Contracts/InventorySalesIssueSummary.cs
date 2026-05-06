namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventorySalesIssueSummary(
    Guid DocumentId,
    CompanyId CompanyId,
    string DocumentNumber,
    string Status,
    DateOnly PostingDate,
    Guid CustomerId,
    string CustomerDisplayName,
    decimal TotalQuantity,
    decimal TotalCostBase,
    int LineCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PostedAt,
    string? Memo);
