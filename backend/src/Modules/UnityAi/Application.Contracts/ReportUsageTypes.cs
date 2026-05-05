namespace Citus.Modules.UnityAi.Application.Contracts;

public sealed record ReportUsageEventInput(
    CompanyId CompanyId,
    Guid? UserId,
    string ReportKey,
    string EventType,
    string? DateRangeKey,
    string? FiltersJson,
    string? SourceRoute,
    string? MetadataJson);

public sealed record ReportUsageStatRecord(
    Guid Id,
    CompanyId CompanyId,
    string ScopeType,
    Guid? UserId,
    string ReportKey,
    int OpenCount,
    int ExportCount,
    int PrintCount,
    int DrilldownCount,
    int FilterCount,
    DateTimeOffset? LastOpenedAt,
    DateTimeOffset? LastUsedAt,
    string? CommonDateRangeKey,
    DateTimeOffset UpdatedAt);

public interface IReportUsageEventStore
{
    Task RecordAsync(ReportUsageEventInput input, CancellationToken cancellationToken);
}

public interface IReportUsageStatStore
{
    Task UpsertAsync(ReportUsageEventInput input, DateTimeOffset occurredAt, CancellationToken cancellationToken);

    Task<IReadOnlyList<ReportUsageStatRecord>> GetForCompanyAsync(
        CompanyId companyId,
        UserId? userId,
        string scopeType,
        CancellationToken cancellationToken);
}
