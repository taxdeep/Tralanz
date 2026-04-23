namespace Web.Shell.Services;

public sealed record class ShellAccountingSourceDocumentBrowserItem
{
    public string SourceType { get; init; } = string.Empty;

    public string SourceTypeLabel { get; init; } = string.Empty;

    public Guid Id { get; init; }

    public Guid CompanyId { get; init; }

    public string EntityNumber { get; init; } = string.Empty;

    public string DisplayNumber { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public DateOnly DocumentDate { get; init; }

    public DateOnly? DueDate { get; init; }

    public string CounterpartyLabel { get; init; } = string.Empty;

    public Guid? CounterpartyId { get; init; }

    public string? CounterpartyDisplayName { get; init; }

    public string TransactionCurrencyCode { get; init; } = string.Empty;

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public decimal TotalAmount { get; init; }

    public Guid? JournalEntryId { get; init; }

    public string? JournalEntryDisplayNumber { get; init; }

    public string? JournalEntryStatus { get; init; }

    public DateTimeOffset? JournalEntryPostedAt { get; init; }

    public DateTimeOffset? JournalEntryVoidedAt { get; init; }

    public DateTimeOffset? JournalEntryReversedAt { get; init; }

    public decimal? TotalOrderedQuantity { get; init; }

    public int? LineCount { get; init; }

    public string? VendorReference { get; init; }

    public string? AnchorGovernanceSummary { get; init; }

    public string? ReceiptActivationStatus { get; init; }

    public string? ReceiptValuationStatus { get; init; }

    public string? ReceiptCostLayerEmissionStatus { get; init; }

    public string? ReceiptGrIrBridgeStatus { get; init; }

    public string? ReceiptGrIrSettlementStatus { get; init; }

    public string? ReceiptPurchaseVarianceStatus { get; init; }

    public string? BillReceiptMatchStatus { get; init; }

    public string? BillReceiptPostingGateLabel { get; init; }

    public string? BillReceiptPostingGateSummary { get; init; }

    public bool? BillReceiptAllowsPost { get; init; }

    public int? BillReceiptOpenDiscrepancyCount { get; init; }

    public string? BillReceiptInvestigationSummary { get; init; }

    public string? InvoiceIssueMatchStatus { get; init; }

    public string? InvoiceIssuePostingGateLabel { get; init; }

    public string? InvoiceIssuePostingGateSummary { get; init; }

    public bool? InvoiceIssueAllowsPost { get; init; }

    public string? InvoiceShipmentMatchStatus { get; init; }

    public string? InvoiceShipmentPostingGateLabel { get; init; }

    public string? InvoiceShipmentPostingGateSummary { get; init; }

    public bool? InvoiceShipmentAllowsPost { get; init; }

    public string? InvoiceCoverageStatus { get; init; }

    public string? InvoiceCoverageSummary { get; init; }

    public string? ShipmentIssueMatchStatus { get; init; }

    public decimal? ShipmentIssuedQuantity { get; init; }

    public decimal? ShipmentRemainingToIssueQuantity { get; init; }

    public string? ShipmentCarrierName { get; init; }

    public string? ShipmentTrackingNumber { get; init; }

    public string? SalesOrderAggregateStatus { get; init; }

    public string? SalesOrderShipmentCoverageStatus { get; init; }

    public decimal? SalesOrderShippableQuantity { get; init; }

    public decimal? SalesOrderShippedQuantity { get; init; }

    public decimal? SalesOrderRemainingToShipQuantity { get; init; }

    public string? SalesOrderInvoiceCoverageStatus { get; init; }

    public decimal? SalesOrderInvoicedQuantity { get; init; }

    public decimal? SalesOrderRemainingToInvoiceQuantity { get; init; }
}
