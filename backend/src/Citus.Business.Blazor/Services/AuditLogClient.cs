using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Citus.Business.Blazor.Services;

/// <summary>
/// HTTP client for the audit log reader. Read-only — every audit
/// row is written by the path that emitted the action (period
/// transitions, role changes, adjustment approvals, etc.). The
/// page filters are sent as query-string params; the server clamps
/// the limit so a misconfigured client can't dump the whole table.
/// </summary>
public sealed class AuditLogClient(HttpClient httpClient, ILogger<AuditLogClient> logger)
{
    public async Task<IReadOnlyList<AuditLogEntryDto>> ListAsync(
        DateTimeOffset? since,
        string? action,
        string? entityType,
        int? limit,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var query = new List<string>(4);
            if (since.HasValue) query.Add($"since={Uri.EscapeDataString(since.Value.ToString("o"))}");
            if (!string.IsNullOrWhiteSpace(action)) query.Add($"action={Uri.EscapeDataString(action.Trim())}");
            if (!string.IsNullOrWhiteSpace(entityType)) query.Add($"entityType={Uri.EscapeDataString(entityType.Trim())}");
            if (limit.HasValue) query.Add($"limit={limit.Value}");
            var url = query.Count > 0 ? $"accounting/audit-logs?{string.Join("&", query)}" : "accounting/audit-logs";

            var rows = await httpClient.GetFromJsonAsync<AuditLogEntryDto[]>(url, cancellationToken);
            return rows ?? Array.Empty<AuditLogEntryDto>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read audit logs.");
            return Array.Empty<AuditLogEntryDto>();
        }
    }
}

public sealed record AuditLogEntryDto(
    Guid Id,
    CompanyId? CompanyId,
    string ActorType,
    UserId? ActorId,
    string? ActorDisplay,
    string EntityType,
    Guid EntityId,
    string Action,
    string PayloadJson,
    DateTimeOffset CreatedAt);
