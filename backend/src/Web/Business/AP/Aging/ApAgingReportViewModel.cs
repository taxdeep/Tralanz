namespace Web.Business.AP.Aging;

public sealed record class ApAgingReportResult
{
    public ApAgingReportViewModel? Value { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    public bool IsNotFound { get; init; }
}

public sealed record class ApAgingReportViewModel
{
    public Guid CompanyId { get; init; }
    public DateOnly AsOfDate { get; init; }
    public string BaseCurrencyCode { get; init; } = string.Empty;
    public int VendorCount { get; init; }
    public int OpenItemCount { get; init; }
    public decimal CurrentAmountBase { get; init; }
    public decimal Days1To30AmountBase { get; init; }
    public decimal Days31To60AmountBase { get; init; }
    public decimal Days61To90AmountBase { get; init; }
    public decimal DaysOver90AmountBase { get; init; }
    public decimal TotalOverdueAmountBase { get; init; }
    public decimal TotalOutstandingAmountBase { get; init; }
    public IReadOnlyList<ApAgingVendorRowViewModel> VendorRows { get; init; } = Array.Empty<ApAgingVendorRowViewModel>();
    public IReadOnlyList<ApAgingOpenItemRowViewModel> DetailRows { get; init; } = Array.Empty<ApAgingOpenItemRowViewModel>();
}

public sealed record class ApAgingVendorRowViewModel
{
    public Guid VendorId { get; init; }
    public string VendorEntityNumber { get; init; } = string.Empty;
    public string VendorDisplayName { get; init; } = string.Empty;
    public bool VendorIsActive { get; init; }
    public int OpenItemCount { get; init; }
    public DateOnly? OldestDueDate { get; init; }
    public decimal CurrentAmountBase { get; init; }
    public decimal Days1To30AmountBase { get; init; }
    public decimal Days31To60AmountBase { get; init; }
    public decimal Days61To90AmountBase { get; init; }
    public decimal DaysOver90AmountBase { get; init; }
    public decimal TotalOverdueAmountBase { get; init; }
    public decimal TotalOutstandingAmountBase { get; init; }
    public IReadOnlyList<ApAgingOpenItemRowViewModel> OpenItems { get; init; } = Array.Empty<ApAgingOpenItemRowViewModel>();
}

public sealed record class ApAgingOpenItemRowViewModel
{
    public Guid OpenItemId { get; init; }
    public Guid VendorId { get; init; }
    public string VendorEntityNumber { get; init; } = string.Empty;
    public string VendorDisplayName { get; init; } = string.Empty;
    public bool VendorIsActive { get; init; }
    public string SourceType { get; init; } = string.Empty;
    public Guid SourceDocumentId { get; init; }
    public string DisplayNumber { get; init; } = string.Empty;
    public DateOnly DocumentDate { get; init; }
    public DateOnly? DueDate { get; init; }
    public int DaysPastDue { get; init; }
    public string AgingBucket { get; init; } = string.Empty;
    public string DocumentCurrencyCode { get; init; } = string.Empty;
    public string BaseCurrencyCode { get; init; } = string.Empty;
    public string BalanceSide { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public decimal OriginalAmountTx { get; init; }
    public decimal OriginalAmountBase { get; init; }
    public decimal OpenAmountTx { get; init; }
    public decimal OpenAmountBase { get; init; }
    public decimal SignedOpenAmountTx { get; init; }
    public decimal SignedOpenAmountBase { get; init; }
}
