namespace Web.Shell.Services;

public sealed record class ShellBillReceiptMatchingSummary
{
    public Guid BillDocumentId { get; init; }

    public int BillInboundLineCount { get; init; }

    public decimal BillInboundQuantity { get; init; }

    public int ReceiptCount { get; init; }

    public decimal CoveredQuantity { get; init; }

    public decimal RemainingQuantity { get; init; }

    public string MatchStatus { get; init; } = string.Empty;

    public DateTimeOffset? LatestReceiptPostedAt { get; init; }

    public int OpenDiscrepancyCount { get; init; }

    public IReadOnlyList<ShellBillReceiptMatchingReceiptSummary> RecentReceipts { get; init; } = Array.Empty<ShellBillReceiptMatchingReceiptSummary>();

    public IReadOnlyList<ShellBillReceiptMatchingLineSummary> LineSummaries { get; init; } = Array.Empty<ShellBillReceiptMatchingLineSummary>();

    public IReadOnlyList<ShellBillReceiptMatchingDiscrepancySummary> Discrepancies { get; init; } = Array.Empty<ShellBillReceiptMatchingDiscrepancySummary>();
}

public sealed record class ShellBillReceiptMatchingReceiptSummary
{
    public Guid ReceiptDocumentId { get; init; }

    public string DisplayNumber { get; init; } = string.Empty;

    public DateOnly ReceiptDate { get; init; }

    public string Status { get; init; } = string.Empty;

    public decimal ReceiptQuantity { get; init; }

    public decimal MatchedQuantity { get; init; }

    public string? VendorReference { get; init; }

    public string? SourceReference { get; init; }

    public DateTimeOffset? PostedAt { get; init; }
}

public sealed record class ShellBillReceiptMatchingLineSummary
{
    public int BillLineNumber { get; init; }

    public Guid ItemId { get; init; }

    public string ItemCode { get; init; } = string.Empty;

    public string ItemName { get; init; } = string.Empty;

    public Guid WarehouseId { get; init; }

    public string WarehouseCode { get; init; } = string.Empty;

    public string WarehouseName { get; init; } = string.Empty;

    public string UomCode { get; init; } = string.Empty;

    public decimal BillQuantity { get; init; }

    public decimal CoveredQuantity { get; init; }

    public decimal RemainingQuantity { get; init; }

    public int ReceiptCount { get; init; }

    public string MatchStatus { get; init; } = string.Empty;
}

public sealed record class ShellBillReceiptMatchingDiscrepancySummary
{
    public Guid BillDocumentId { get; init; }

    public int BillLineNumber { get; init; }

    public string DiscrepancyType { get; init; } = string.Empty;

    public string InvestigationStatus { get; init; } = string.Empty;

    public Guid ItemId { get; init; }

    public string ItemCode { get; init; } = string.Empty;

    public string ItemName { get; init; } = string.Empty;

    public Guid WarehouseId { get; init; }

    public string WarehouseCode { get; init; } = string.Empty;

    public string WarehouseName { get; init; } = string.Empty;

    public string UomCode { get; init; } = string.Empty;

    public decimal BillQuantity { get; init; }

    public decimal CoveredQuantity { get; init; }

    public decimal RemainingQuantity { get; init; }

    public string Summary { get; init; } = string.Empty;

    public DateTimeOffset FirstDetectedAt { get; init; }

    public DateTimeOffset LastDetectedAt { get; init; }
}
