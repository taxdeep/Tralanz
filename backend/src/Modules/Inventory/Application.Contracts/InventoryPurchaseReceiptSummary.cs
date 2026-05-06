namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryPurchaseReceiptSummary(
    Guid Id,
    CompanyId CompanyId,
    string DocumentNumber,
    string Status,
    DateOnly PostingDate,
    Guid VendorId,
    string VendorDisplayName,
    string TransactionCurrencyCode,
    string BaseCurrencyCode,
    decimal FxRateToBase,
    decimal TotalQuantity,
    decimal TotalCostBase,
    int LineCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PostedAt,
    string? Memo);
