using Modules.CompanyAccess.SessionContext;
using Npgsql;
using SharedKernel.CompanyAccess;

namespace Infrastructure.PostgreSQL.CompanyAccess;

public sealed class PostgreSqlCompanySessionContextStore : ICompanySessionContextStore
{
    private readonly PostgreSqlConnectionFactory _connections;

    public PostgreSqlCompanySessionContextStore(PostgreSqlConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    public async Task<CompanyAccessSessionContext?> GetAsync(
        Guid userId,
        Guid? preferredActiveCompanyId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        var user = await ReadUserAsync(connection, userId, cancellationToken);
        if (user is null)
        {
            return null;
        }

        var companies = await ReadCompaniesAsync(connection, userId, cancellationToken);
        if (companies.Count == 0)
        {
            return null;
        }

        var activeCompany = ResolveActiveCompany(preferredActiveCompanyId, companies);
        return new CompanyAccessSessionContext
        {
            User = user with { Roles = companies.Select(company => company.MembershipRole).Distinct(StringComparer.Ordinal).OrderBy(static role => role, StringComparer.Ordinal).ToArray() },
            ActiveCompany = ToSummary(activeCompany),
            AvailableCompanies = companies.Select(ToSummary).ToArray()
        };
    }

    private static CompanyAccessCompanySummary ToSummary(CompanyMembershipCompanyRecord company) =>
        new()
        {
            Id = company.Id,
            CompanyCode = company.CompanyCode,
            CompanyName = company.CompanyName,
            BaseCurrencyCode = company.BaseCurrencyCode,
            MultiCurrencyEnabled = company.MultiCurrencyEnabled
        };

    private static CompanyMembershipCompanyRecord ResolveActiveCompany(
        Guid? preferredActiveCompanyId,
        IReadOnlyList<CompanyMembershipCompanyRecord> companies)
    {
        if (preferredActiveCompanyId.HasValue)
        {
            var configured = companies.FirstOrDefault(company => company.Id == preferredActiveCompanyId.Value);
            if (configured is not null)
            {
                return configured;
            }
        }

        return companies[0];
    }

    private static async Task<CompanyAccessUserSummary?> ReadUserAsync(
        NpgsqlConnection connection,
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select id, email, username
            from users
            where id = @user_id
              and is_active = true
            limit 1;
            """;
        command.Parameters.AddWithValue("user_id", userId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var email = reader.GetString(reader.GetOrdinal("email")).Trim();
        var username = reader.IsDBNull(reader.GetOrdinal("username"))
            ? string.Empty
            : reader.GetString(reader.GetOrdinal("username")).Trim();

        return new CompanyAccessUserSummary
        {
            Id = reader.GetGuid(reader.GetOrdinal("id")),
            Email = email,
            Username = username,
            DisplayName = !string.IsNullOrWhiteSpace(username) ? username : email
        };
    }

    private static async Task<IReadOnlyList<CompanyMembershipCompanyRecord>> ReadCompaniesAsync(
        NpgsqlConnection connection,
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select
              c.id,
              c.entity_number,
              c.legal_name,
              c.base_currency_code,
              c.multi_currency_enabled,
              m.role
            from company_memberships m
            inner join companies c on c.id = m.company_id
            where m.user_id = @user_id
              and m.is_active = true
              and c.status = 'active'
            order by c.entity_number, c.legal_name;
            """;
        command.Parameters.AddWithValue("user_id", userId);

        var companies = new List<CompanyMembershipCompanyRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            companies.Add(
                new CompanyMembershipCompanyRecord(
                    reader.GetGuid(reader.GetOrdinal("id")),
                    reader.GetString(reader.GetOrdinal("entity_number")).Trim().ToUpperInvariant(),
                    reader.GetString(reader.GetOrdinal("legal_name")).Trim(),
                    reader.GetString(reader.GetOrdinal("base_currency_code")).Trim().ToUpperInvariant(),
                    reader.GetBoolean(reader.GetOrdinal("multi_currency_enabled")),
                    reader.GetString(reader.GetOrdinal("role")).Trim().ToLowerInvariant()));
        }

        return companies;
    }

    private sealed record CompanyMembershipCompanyRecord(
        Guid Id,
        string CompanyCode,
        string CompanyName,
        string BaseCurrencyCode,
        bool MultiCurrencyEnabled,
        string MembershipRole);
}
