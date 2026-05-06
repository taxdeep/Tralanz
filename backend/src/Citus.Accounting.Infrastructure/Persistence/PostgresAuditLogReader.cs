using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Infrastructure.Persistence;

public sealed class PostgresAuditLogReader : IAuditLogReader
{
    private const int MaxLimit = 1000;
    private const int DefaultLimit = 200;

    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresAuditLogReader(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<IReadOnlyList<AuditLogEntry>> ListAsync(
        CompanyId companyId,
        AuditLogQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var limit = Math.Clamp(query.Limit <= 0 ? DefaultLimit : query.Limit, 1, MaxLimit);

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections, _executionContextAccessor, cancellationToken);

        // LEFT JOIN users to surface a friendly actor display
        // (email or display_name) instead of a bare UUID. NULL when
        // actor_id is null (sysadmin actions before user attribution
        // landed) or when the user record has been hard-deleted.
        // payload is returned as text — the front end pretty-prints.
        await using var command = scope.CreateCommand(
            """
            select
              al.id,
              al.company_id,
              al.actor_type,
              al.actor_id,
              coalesce(u.display_name, u.email) as actor_display,
              al.entity_type,
              al.entity_id,
              al.action,
              al.payload::text as payload_json,
              al.created_at
            from audit_logs al
            left join users u on u.id = al.actor_id
            where al.company_id = @company_id
              and (@since is null or al.created_at >= @since)
              and (@action is null or al.action = @action)
              and (@entity_type is null or al.entity_type = @entity_type)
            order by al.created_at desc
            limit @limit;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("since", (object?)query.Since ?? DBNull.Value);
        command.Parameters.AddWithValue("action", (object?)NormalizeFilter(query.Action) ?? DBNull.Value);
        command.Parameters.AddWithValue("entity_type", (object?)NormalizeFilter(query.EntityType) ?? DBNull.Value);
        command.Parameters.AddWithValue("limit", limit);

        var rows = new List<AuditLogEntry>(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AuditLogEntry(
                Id: reader.GetGuid(0),
                CompanyId: reader.IsDBNull(1) ? (CompanyId?)null : CompanyId.Parse(reader.GetString(1)),
                ActorType: reader.GetString(2),
                ActorId: reader.IsDBNull(3) ? (UserId?)null : UserId.Parse(reader.GetString(3)),
                ActorDisplay: reader.IsDBNull(4) ? null : reader.GetString(4),
                EntityType: reader.GetString(5),
                EntityId: reader.GetGuid(6),
                Action: reader.GetString(7),
                PayloadJson: reader.IsDBNull(8) ? "{}" : reader.GetString(8),
                CreatedAt: reader.GetFieldValue<DateTimeOffset>(9)));
        }
        return rows;
    }

    private static string? NormalizeFilter(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : raw.Trim().ToLowerInvariant();
}
