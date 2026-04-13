using NpgsqlTypes;

namespace Citus.Accounting.Infrastructure.Persistence;

internal static class PostgresAccountLookup
{
    public static async Task<Guid?> TryResolveActiveAccountIdAsync(
        PostgresCommandScope scope,
        Guid companyId,
        CancellationToken cancellationToken,
        params string[] markers)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var normalizedMarkers = markers
            .Where(static marker => !string.IsNullOrWhiteSpace(marker))
            .Select(static marker => marker.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedMarkers.Length == 0)
        {
            return null;
        }

        await using var command = scope.CreateCommand(
            """
            select id
            from accounts
            where company_id = @company_id
              and is_active = true
              and (
                system_role = any(@markers)
                or system_key = any(@markers)
              )
            order by
              case
                when system_role = any(@markers) then 0
                when system_key = any(@markers) then 1
                else 2
              end,
              code
            limit 1;
            """);

        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("markers", NpgsqlDbType.Array | NpgsqlDbType.Text, normalizedMarkers);

        var resolved = await command.ExecuteScalarAsync(cancellationToken);
        return resolved is Guid accountId ? accountId : null;
    }
}
