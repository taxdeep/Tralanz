namespace Web.Shell.Services;

public record class ShellSalesCommercialDocumentSummary
{
    public Guid Id { get; init; }

    public Guid CompanyId { get; init; }

    public string DocumentType { get; init; } = string.Empty;

    public string EntityNumber { get; init; } = string.Empty;

    public string DisplayNumber { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public Guid CustomerId { get; init; }

    public DateOnly DocumentDate { get; init; }

    public DateOnly? ExpiresOn { get; init; }

    public DateOnly? RequestedShipDate { get; init; }

    public string TransactionCurrencyCode { get; init; } = string.Empty;

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public decimal TotalAmount { get; init; }

    public int LineCount { get; init; }

    public Guid? SourceQuoteId { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record class ShellSalesCommercialDocumentReadModel : ShellSalesCommercialDocumentSummary
{
    public string? Memo { get; init; }

    public IReadOnlyList<ShellSalesCommercialDocumentLineReadModel> Lines { get; init; } = Array.Empty<ShellSalesCommercialDocumentLineReadModel>();
}

public sealed record class ShellSalesCommercialDocumentLineReadModel
{
    public int LineNumber { get; init; }

    public Guid? RevenueAccountId { get; init; }

    public string Description { get; init; } = string.Empty;

    public decimal Quantity { get; init; }

    public decimal UnitPrice { get; init; }

    public Guid? ItemId { get; init; }

    public Guid? WarehouseId { get; init; }

    public string? UomCode { get; init; }

    public decimal LineAmount => decimal.Round(Quantity * UnitPrice, 2, MidpointRounding.AwayFromZero);
}

public sealed record class ShellSalesCommercialDocumentSaveRequest
{
    public Guid CompanyId { get; init; }

    public Guid UserId { get; init; }

    public string DocumentType { get; init; } = string.Empty;

    public Guid CustomerId { get; init; }

    public DateOnly DocumentDate { get; init; }

    public DateOnly? ExpiresOn { get; init; }

    public DateOnly? RequestedShipDate { get; init; }

    public string TransactionCurrencyCode { get; init; } = string.Empty;

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public string? Memo { get; init; }

    public Guid? SourceQuoteId { get; init; }

    public IReadOnlyList<ShellSalesCommercialDocumentLineSaveRequest> Lines { get; init; } = Array.Empty<ShellSalesCommercialDocumentLineSaveRequest>();
}

public sealed record class ShellSalesCommercialDocumentLineSaveRequest
{
    public int LineNumber { get; init; }

    public Guid? RevenueAccountId { get; init; }

    public string Description { get; init; } = string.Empty;

    public decimal Quantity { get; init; }

    public decimal UnitPrice { get; init; }

    public Guid? ItemId { get; init; }

    public Guid? WarehouseId { get; init; }

    public string? UomCode { get; init; }
}

public sealed record class ShellSalesCommercialInvoiceAnchorRequest
{
    public Guid InvoiceDocumentId { get; init; }

    public string InvoiceDisplayNumber { get; init; } = string.Empty;

    public string InvoiceStatus { get; init; } = string.Empty;

    public DateOnly DocumentDate { get; init; }

    public decimal TotalAmount { get; init; }

    public IReadOnlyList<ShellSalesCommercialInvoiceAnchorLineRequest> Lines { get; init; } = Array.Empty<ShellSalesCommercialInvoiceAnchorLineRequest>();

    public decimal Quantity => Lines.Sum(static line => line.Quantity);
}

public sealed record class ShellSalesCommercialInvoiceAnchorLineRequest
{
    public int LineNumber { get; init; }

    public string Description { get; init; } = string.Empty;

    public decimal Quantity { get; init; }

    public decimal UnitPrice { get; init; }

    public Guid? ItemId { get; init; }

    public Guid? WarehouseId { get; init; }

    public string? UomCode { get; init; }
}

public sealed record class ShellSalesOrderInvoiceCoverageSummary
{
    public Guid SalesOrderId { get; init; }

    public decimal OrderQuantity { get; init; }

    public decimal InvoicedQuantity { get; init; }

    public decimal RemainingToInvoiceQuantity { get; init; }

    public string InvoiceCoverageStatus { get; init; } = string.Empty;

    public int InvoiceCount { get; init; }

    public int PostedInvoiceCount { get; init; }

    public DateTimeOffset? LatestInvoiceUpdatedAt { get; init; }

    public IReadOnlyList<ShellSalesOrderInvoiceCoverageLineSummary> Lines { get; init; } = Array.Empty<ShellSalesOrderInvoiceCoverageLineSummary>();

    public IReadOnlyList<ShellSalesOrderInvoiceCoverageInvoiceSummary> RecentInvoices { get; init; } = Array.Empty<ShellSalesOrderInvoiceCoverageInvoiceSummary>();
}

public sealed record class ShellSalesOrderInvoiceCoverageLineSummary
{
    public int LineNumber { get; init; }

    public string Description { get; init; } = string.Empty;

    public decimal OrderQuantity { get; init; }

    public decimal InvoicedQuantity { get; init; }

    public decimal RemainingToInvoiceQuantity { get; init; }

    public string InvoiceCoverageStatus { get; init; } = string.Empty;

    public Guid? ItemId { get; init; }

    public Guid? WarehouseId { get; init; }

    public string? UomCode { get; init; }
}

public sealed record class ShellSalesOrderInvoiceCoverageInvoiceSummary
{
    public Guid InvoiceDocumentId { get; init; }

    public string InvoiceDisplayNumber { get; init; } = string.Empty;

    public string InvoiceStatus { get; init; } = string.Empty;

    public DateOnly DocumentDate { get; init; }

    public decimal TotalAmount { get; init; }

    public decimal Quantity { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record class ShellSalesOrderOutboundControlSummary
{
    public Guid SalesOrderId { get; init; }

    public string SalesOrderStatus { get; init; } = string.Empty;

    public string AggregateStatus { get; init; } = string.Empty;

    public decimal OrderQuantity { get; init; }

    public decimal ShippableQuantity { get; init; }

    public decimal ShippedQuantity { get; init; }

    public decimal RemainingToShipQuantity { get; init; }

    public string ShipmentCoverageStatus { get; init; } = string.Empty;

    public int ShipmentCount { get; init; }

    public decimal InvoicedQuantity { get; init; }

    public decimal RemainingToInvoiceQuantity { get; init; }

    public string InvoiceCoverageStatus { get; init; } = string.Empty;

    public int InvoiceCount { get; init; }

    public int PostedInvoiceCount { get; init; }

    public DateTimeOffset? LatestShipmentPostedAt { get; init; }

    public DateTimeOffset? LatestInvoiceUpdatedAt { get; init; }

    public IReadOnlyList<ShellSalesOrderOutboundControlLineSummary> Lines { get; init; } = Array.Empty<ShellSalesOrderOutboundControlLineSummary>();

    public IReadOnlyList<ShellSalesOrderOutboundShipmentSummary> RecentShipments { get; init; } = Array.Empty<ShellSalesOrderOutboundShipmentSummary>();

    public IReadOnlyList<ShellSalesOrderInvoiceCoverageInvoiceSummary> RecentInvoices { get; init; } = Array.Empty<ShellSalesOrderInvoiceCoverageInvoiceSummary>();
}

public sealed record class ShellSalesOrderOutboundControlLineSummary
{
    public int LineNumber { get; init; }

    public string Description { get; init; } = string.Empty;

    public decimal OrderQuantity { get; init; }

    public bool IsInventoryShippable { get; init; }

    public decimal ShippedQuantity { get; init; }

    public decimal RemainingToShipQuantity { get; init; }

    public string ShipmentCoverageStatus { get; init; } = string.Empty;

    public decimal InvoicedQuantity { get; init; }

    public decimal RemainingToInvoiceQuantity { get; init; }

    public string InvoiceCoverageStatus { get; init; } = string.Empty;

    public Guid? ItemId { get; init; }

    public Guid? WarehouseId { get; init; }

    public string? UomCode { get; init; }
}

public sealed record class ShellSalesOrderOutboundShipmentSummary
{
    public Guid ShipmentDocumentId { get; init; }

    public string DocumentNumber { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public DateOnly PostingDate { get; init; }

    public decimal Quantity { get; init; }

    public string? CarrierName { get; init; }

    public string? TrackingNumber { get; init; }

    public DateTimeOffset? PostedAt { get; init; }
}
