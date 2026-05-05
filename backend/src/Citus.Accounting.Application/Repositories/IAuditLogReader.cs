using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Application.Repositories;

/// <summary>
/// Read-side projection over the platform <c>audit_logs</c> table,
/// scoped to one company. The table itself is shared with the
/// platform layer (memberships, sysadmin actions, etc.); this reader
/// applies the company filter so a Settings page in business shell
/// only sees its own activity.
///
/// V1 is a list with a few coarse filters (since / action / entity
/// type / limit). Drill-down into a specific row's payload happens
/// client-side from the page (the JSON is returned inline).
/// </summary>
public interface IAuditLogReader
{
    Task<IReadOnlyList<AuditLogEntry>> ListAsync(
        CompanyId companyId,
        AuditLogQuery query,
        CancellationToken cancellationToken);
}

public sealed record AuditLogQuery(
    DateTimeOffset? Since,
    string? Action,
    string? EntityType,
    int Limit);

public sealed record AuditLogEntry(
    Guid Id,
    Guid? CompanyId,
    string ActorType,
    Guid? ActorId,
    string? ActorDisplay,
    string EntityType,
    Guid EntityId,
    string Action,
    string PayloadJson,
    DateTimeOffset CreatedAt);
