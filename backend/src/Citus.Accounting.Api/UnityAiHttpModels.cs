using Citus.Modules.UnityAi.Application.Contracts;

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

internal static class UnityAiHttpRequestMapper
{
    public static string? NormalizeSearchQuery(string? query) =>
        string.IsNullOrWhiteSpace(query) ? null : query.Trim().ToLowerInvariant();

    public static UnitysearchEventInput BuildUnitysearchEventInput(
        CompanyId companyId,
        UserId userId,
        UnitysearchUsageHttpRequest request) =>
        new(
            CompanyId: companyId,
            UserId: userId,
            SessionId: request.SessionId,
            Context: request.Context.Trim(),
            EntityType: request.EntityType.Trim(),
            Query: request.Query,
            NormalizedQuery: NormalizeSearchQuery(request.Query),
            EventType: request.EventType.Trim(),
            SelectedEntityId: request.SelectedEntityId,
            RankPosition: request.RankPosition,
            ResultCount: request.ResultCount,
            SourceRoute: request.SourceRoute,
            AnchorContext: request.AnchorContext,
            AnchorEntityType: request.AnchorEntityType,
            AnchorEntityId: request.AnchorEntityId,
            MetadataJson: request.MetadataJson);

    public static ReportUsageEventInput BuildReportUsageEventInput(
        CompanyId companyId,
        UserId userId,
        ReportUsageHttpRequest request) =>
        new(
            CompanyId: companyId,
            UserId: userId,
            ReportKey: request.ReportKey.Trim(),
            EventType: request.EventType.Trim(),
            DateRangeKey: request.DateRangeKey,
            FiltersJson: request.FiltersJson,
            SourceRoute: request.SourceRoute,
            MetadataJson: request.MetadataJson);
}
