namespace Citus.Accounting.Api;

public sealed record UnitysearchUsageHttpRequest
{
    public Guid CompanyId { get; init; }
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
