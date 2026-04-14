using Infrastructure.PostgreSQL;

namespace Web.Business.AR.SettlementLookup;

public sealed class ArSettlementLookupService(PostgreSqlConnectionFactory connections) : IArSettlementLookupService
{
    public async Task<IReadOnlyList<(Guid Id, string DisplayLabel, string CurrencyCode)>> ListCustomersAsync(
        Guid companyId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select
              id,
              display_name,
              default_currency_code
            from customers
            where company_id = @company_id
              and is_active = true
            order by display_name asc, id asc;
            """;
        command.Parameters.AddWithValue("company_id", companyId);

        var items = new List<(Guid Id, string DisplayLabel, string CurrencyCode)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetGuid(reader.GetOrdinal("id"));
            var displayName = reader.GetString(reader.GetOrdinal("display_name"));
            var currencyCode = reader.GetString(reader.GetOrdinal("default_currency_code"));
            items.Add((id, $"{displayName} ({currencyCode})", currencyCode));
        }

        return items;
    }

    public async Task<IReadOnlyList<(Guid Id, string DisplayLabel)>> ListBankAccountsAsync(
        Guid companyId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select
              id,
              code,
              name
            from accounts
            where company_id = @company_id
              and is_active = true
              and root_type = 'asset'
              and detail_type = 'bank'
            order by code asc, name asc, id asc;
            """;
        command.Parameters.AddWithValue("company_id", companyId);

        var items = new List<(Guid Id, string DisplayLabel)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetGuid(reader.GetOrdinal("id"));
            var code = reader.GetString(reader.GetOrdinal("code"));
            var name = reader.GetString(reader.GetOrdinal("name"));
            items.Add((id, $"{code} - {name}"));
        }

        return items;
    }
}
