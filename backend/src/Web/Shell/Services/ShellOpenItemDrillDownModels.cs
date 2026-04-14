namespace Web.Shell.Services;

public sealed record class ShellOpenItemDrillDownResponse
{
    public ShellOpenItemDetail? OpenItem { get; init; }

    public IReadOnlyList<ShellOpenItemApplicationDetail> Applications { get; init; } = Array.Empty<ShellOpenItemApplicationDetail>();
}

public sealed record class ShellOpenItemDetail
{
    public Guid OpenItemId { get; init; }

    public string OpenItemType { get; init; } = string.Empty;

    public Guid CompanyId { get; init; }

    public string PartyRole { get; init; } = string.Empty;

    public Guid PartyId { get; init; }

    public string PartyEntityNumber { get; init; } = string.Empty;

    public string PartyDisplayName { get; init; } = string.Empty;

    public string SourceType { get; init; } = string.Empty;

    public Guid SourceDocumentId { get; init; }

    public string SourceDocumentDisplayNumber { get; init; } = string.Empty;

    public DateOnly DocumentDate { get; init; }

    public DateOnly? DueDate { get; init; }

    public string DocumentCurrencyCode { get; init; } = string.Empty;

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public string BalanceSide { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public decimal OriginalAmountTx { get; init; }

    public decimal OriginalAmountBase { get; init; }

    public decimal OpenAmountTx { get; init; }

    public decimal OpenAmountBase { get; init; }
}

public sealed record class ShellOpenItemApplicationDetail
{
    public Guid ApplicationId { get; init; }

    public string ApplicationType { get; init; } = string.Empty;

    public string SourceType { get; init; } = string.Empty;

    public Guid SourceDocumentId { get; init; }

    public string SourceDocumentDisplayNumber { get; init; } = string.Empty;

    public DateOnly SourceDocumentDate { get; init; }

    public decimal AppliedAmountTx { get; init; }

    public decimal AppliedAmountBase { get; init; }

    public decimal? SettlementFxRate { get; init; }

    public decimal? RealizedFxAmount { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

