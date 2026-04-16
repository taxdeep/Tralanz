namespace Web.Shell.Services;

public sealed record class ShellOpenItemAdjustmentAccountMappingListResponse
{
    public Guid CompanyId { get; init; }

    public string? OpenItemType { get; init; }

    public string? AdjustmentType { get; init; }

    public bool IncludeInactive { get; init; }

    public IReadOnlyList<ShellOpenItemAdjustmentAccountMappingRecord> Mappings { get; init; } =
        Array.Empty<ShellOpenItemAdjustmentAccountMappingRecord>();
}

public sealed record class ShellOpenItemAdjustmentAccountMappingRecord
{
    public Guid MappingId { get; init; }

    public Guid CompanyId { get; init; }

    public Guid? BookId { get; init; }

    public string? BookCode { get; init; }

    public string? AccountingStandard { get; init; }

    public string OpenItemType { get; init; } = string.Empty;

    public string AdjustmentType { get; init; } = string.Empty;

    public Guid AdjustmentAccountId { get; init; }

    public string AdjustmentAccountCode { get; init; } = string.Empty;

    public string AdjustmentAccountName { get; init; } = string.Empty;

    public string AdjustmentAccountRootType { get; init; } = string.Empty;

    public bool IsActive { get; init; }

    public Guid? CreatedByUserId { get; init; }

    public Guid? UpdatedByUserId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? DeactivatedAt { get; init; }

    public string ScopeLabel => string.IsNullOrWhiteSpace(BookCode)
        ? "Company default"
        : $"{BookCode} / {AccountingStandard ?? "Book"}";

    public string OpenItemLabel => OpenItemType switch
    {
        "ar_open_item" => "AR Open Item",
        "ap_open_item" => "AP Open Item",
        _ => OpenItemType
    };

    public string AdjustmentLabel => AdjustmentType switch
    {
        "write_off" => "Write Off",
        "small_balance_adjustment" => "Small Balance",
        _ => AdjustmentType
    };

    public string AccountLabel => $"{AdjustmentAccountCode} {AdjustmentAccountName}";
}

public sealed record class ShellOpenItemAdjustmentAccountMappingSaveRequest
{
    public Guid CompanyId { get; init; }

    public Guid? UserId { get; init; }

    public Guid? BookId { get; init; }

    public string OpenItemType { get; init; } = string.Empty;

    public string AdjustmentType { get; init; } = string.Empty;

    public Guid AdjustmentAccountId { get; init; }
}

public sealed record class ShellOpenItemAdjustmentAccountMappingSaveResult
{
    public ShellOpenItemAdjustmentAccountMappingRecord? Mapping { get; init; }

    public string OutcomeCode { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}

public sealed record class ShellOpenItemAdjustmentAccountMappingTransitionResult
{
    public ShellOpenItemAdjustmentAccountMappingRecord? Mapping { get; init; }

    public string TransitionCode { get; init; } = string.Empty;

    public string OutcomeCode { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}

public sealed record class ShellOpenItemAdjustmentAccountMappingError
{
    public string? OutcomeCode { get; init; }

    public string Message { get; init; } = string.Empty;
}
