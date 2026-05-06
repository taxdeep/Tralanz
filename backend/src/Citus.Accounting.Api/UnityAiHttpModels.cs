namespace Citus.Accounting.Api;

public sealed record UnitysearchUsageHttpRequest
{
    public CompanyId CompanyId { get; init; }
    public string? SessionId { get; init; }
    public string Context { get; init; } = string.Empty;
    public string EntityType { get; init; } = string.Empty;
    public string? Query { get; init; }
    public string EventType { get; init; } = string.Empty;
    public Guid? SelectedEntityId { get; init; }
    public int? RankPosition { get; init; }
    public int? ResultCount { get; init; }
    public string? SourceRoute { get; init; }
    public string? AnchorContext { get; init; }
    public string? AnchorEntityType { get; init; }
    public Guid? AnchorEntityId { get; init; }
    public string? MetadataJson { get; init; }
}

public sealed record ReportUsageHttpRequest
{
    public string ReportKey { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public string? DateRangeKey { get; init; }
    public string? FiltersJson { get; init; }
    public string? SourceRoute { get; init; }
    public string? MetadataJson { get; init; }
}

public sealed record DashboardSnoozeHttpRequest
{
    public DateTimeOffset? SnoozedUntil { get; init; }
}

public sealed record ActionCenterSnoozeHttpRequest
{
    public DateTimeOffset? SnoozedUntil { get; init; }
}

public sealed record UpdateDisplayNameHttpRequest
{
    public string? DisplayName { get; init; }
}

public sealed record TaxCodeUpsertHttpRequest
{
    public string? Code { get; init; }
    public string? Name { get; init; }
    public decimal? RatePercent { get; init; }
    public string? AppliesTo { get; init; }
    /// <summary>
    /// Optional. Tax registration number issued to the company by the
    /// taxing authority (e.g. GST/HST number, VAT number, EIN). When
    /// present, downstream document templates surface it on invoices
    /// and tax returns. Null = no registration recorded for this code.
    /// </summary>
    public string? RegistrationNumber { get; init; }
    public bool? IsActive { get; init; }
}

public sealed record AccountUpsertHttpRequest
{
    public string? Code { get; init; }
    public string? Name { get; init; }
    public string? RootType { get; init; }
    public string? DetailType { get; init; }
    public string? CurrencyCode { get; init; }
    public bool? AllowManualPosting { get; init; }
    public bool? IsActive { get; init; }
}
