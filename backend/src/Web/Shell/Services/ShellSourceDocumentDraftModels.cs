namespace Web.Shell.Services;

public sealed record class ShellSourceDocumentDraftSaveResult
{
    public Guid DocumentId { get; init; }

    public string EntityNumber { get; init; } = string.Empty;

    public string DisplayNumber { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;
}

public sealed record class ShellSourceDocumentPostResult
{
    public Guid JournalEntryId { get; init; }

    public string JournalEntryDisplayNumber { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public DateTimeOffset PostedAt { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed record class ShellSalesSourceDocumentDraftReadModel
{
    public Guid Id { get; init; }
    public Guid CompanyId { get; init; }
    public string EntityNumber { get; init; } = string.Empty;
    public string DisplayNumber { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public Guid CustomerId { get; init; }
    public DateOnly DocumentDate { get; init; }
    public DateOnly DueDate { get; init; }
    public string TransactionCurrencyCode { get; init; } = string.Empty;
    public string BaseCurrencyCode { get; init; } = string.Empty;
    public Guid? FxSnapshotId { get; init; }
    public decimal? FxRate { get; init; }
    public DateOnly? FxEffectiveDate { get; init; }
    public string? FxSource { get; init; }
    public string? Memo { get; init; }
    public IReadOnlyList<ShellSalesSourceDocumentDraftReadLine> Lines { get; init; } = Array.Empty<ShellSalesSourceDocumentDraftReadLine>();
}

public sealed record class ShellSalesSourceDocumentDraftReadLine
{
    public int LineNumber { get; init; }
    public Guid RevenueAccountId { get; init; }
    public string Description { get; init; } = string.Empty;
    public decimal Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal LineAmount { get; init; }
    public Guid? TaxCodeId { get; init; }
    public decimal TaxAmount { get; init; }
    public Guid? ItemId { get; init; }
    public Guid? WarehouseId { get; init; }
    public string? UomCode { get; init; }
}

public sealed record class ShellPurchaseSourceDocumentDraftReadModel
{
    public Guid Id { get; init; }
    public Guid CompanyId { get; init; }
    public string EntityNumber { get; init; } = string.Empty;
    public string DisplayNumber { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public Guid VendorId { get; init; }
    public DateOnly DocumentDate { get; init; }
    public DateOnly DueDate { get; init; }
    public string TransactionCurrencyCode { get; init; } = string.Empty;
    public string BaseCurrencyCode { get; init; } = string.Empty;
    public Guid? FxSnapshotId { get; init; }
    public decimal? FxRate { get; init; }
    public DateOnly? FxEffectiveDate { get; init; }
    public string? FxSource { get; init; }
    public string? Memo { get; init; }
    public IReadOnlyList<ShellPurchaseSourceDocumentDraftReadLine> Lines { get; init; } = Array.Empty<ShellPurchaseSourceDocumentDraftReadLine>();
}

public sealed record class ShellPurchaseSourceDocumentDraftReadLine
{
    public int LineNumber { get; init; }
    public Guid ExpenseAccountId { get; init; }
    public string Description { get; init; } = string.Empty;
    public decimal LineAmount { get; init; }
    public Guid? TaxCodeId { get; init; }
    public decimal TaxAmount { get; init; }
    public bool IsTaxRecoverable { get; init; }
    public Guid? ItemId { get; init; }
    public Guid? WarehouseId { get; init; }
    public string? UomCode { get; init; }
    public decimal? Quantity { get; init; }
    public decimal? UnitCost { get; init; }
}

public sealed record class ShellSalesSourceDocumentDraftSaveRequest
{
    public Guid CompanyId { get; init; }

    public Guid UserId { get; init; }

    public Guid CustomerId { get; init; }

    public DateOnly DocumentDate { get; init; }

    public DateOnly DueDate { get; init; }

    public string TransactionCurrencyCode { get; init; } = string.Empty;

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public Guid? FxSnapshotId { get; init; }

    public decimal? FxRate { get; init; }

    public DateOnly? FxEffectiveDate { get; init; }

    public string? FxSource { get; init; }

    public string? Memo { get; init; }

    public IReadOnlyList<ShellSalesSourceDocumentDraftLineSaveRequest> Lines { get; init; } = Array.Empty<ShellSalesSourceDocumentDraftLineSaveRequest>();
}

public sealed record class ShellSalesSourceDocumentDraftLineSaveRequest
{
    public int LineNumber { get; init; }

    public Guid RevenueAccountId { get; init; }

    public string Description { get; init; } = string.Empty;

    public decimal Quantity { get; init; }

    public decimal UnitPrice { get; init; }

    public Guid? TaxCodeId { get; init; }

    public decimal TaxAmount { get; init; }

    public Guid? ItemId { get; init; }

    public Guid? WarehouseId { get; init; }

    public string? UomCode { get; init; }
}

public sealed record class ShellPurchaseSourceDocumentDraftSaveRequest
{
    public Guid CompanyId { get; init; }

    public Guid UserId { get; init; }

    public Guid VendorId { get; init; }

    public DateOnly DocumentDate { get; init; }

    public DateOnly DueDate { get; init; }

    public string TransactionCurrencyCode { get; init; } = string.Empty;

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public Guid? FxSnapshotId { get; init; }

    public decimal? FxRate { get; init; }

    public DateOnly? FxEffectiveDate { get; init; }

    public string? FxSource { get; init; }

    public string? Memo { get; init; }

    public IReadOnlyList<ShellPurchaseSourceDocumentDraftLineSaveRequest> Lines { get; init; } = Array.Empty<ShellPurchaseSourceDocumentDraftLineSaveRequest>();
}

public sealed record class ShellPurchaseSourceDocumentDraftLineSaveRequest
{
    public int LineNumber { get; init; }

    public Guid ExpenseAccountId { get; init; }

    public string Description { get; init; } = string.Empty;

    public decimal LineAmount { get; init; }

    public Guid? TaxCodeId { get; init; }

    public decimal TaxAmount { get; init; }

    public bool IsTaxRecoverable { get; init; }

    public Guid? ItemId { get; init; }

    public Guid? WarehouseId { get; init; }

    public string? UomCode { get; init; }

    public decimal? Quantity { get; init; }

    public decimal? UnitCost { get; init; }
}

public sealed record class ShellPurchaseOrderDraftSaveRequest
{
    public Guid CompanyId { get; init; }

    public Guid UserId { get; init; }

    public Guid VendorId { get; init; }

    public DateOnly OrderDate { get; init; }

    public DateOnly? ExpectedDate { get; init; }

    public string? VendorReference { get; init; }

    public string? Memo { get; init; }

    public IReadOnlyList<ShellPurchaseOrderDraftLineSaveRequest> Lines { get; init; } = Array.Empty<ShellPurchaseOrderDraftLineSaveRequest>();
}

public sealed record class ShellPurchaseOrderDraftLineSaveRequest
{
    public int LineNumber { get; init; }

    public Guid ItemId { get; init; }

    public decimal OrderedQuantity { get; init; }

    public string UomCode { get; init; } = string.Empty;

    public string? Description { get; init; }

    public decimal? UnitCost { get; init; }
}
