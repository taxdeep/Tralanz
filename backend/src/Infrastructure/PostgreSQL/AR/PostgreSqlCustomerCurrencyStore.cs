using Modules.AR.CustomerCurrency;
using Npgsql;

namespace Infrastructure.PostgreSQL.AR;

public sealed class PostgreSqlCustomerCurrencyStore : ICustomerCurrencyStore
{
    private readonly PostgreSqlConnectionFactory _connections;

    public PostgreSqlCustomerCurrencyStore(PostgreSqlConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    public async Task<CustomerCurrencyPreference> GetPreferenceAsync(
        Guid customerId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        return await GetPreferenceAsync(connection, transaction: null, customerId, cancellationToken);
    }

    public async Task<CustomerCurrencyPreference> SavePreferenceAsync(
        Guid customerId,
        string defaultCurrencyCode,
        bool currencyLocked,
        CancellationToken cancellationToken)
    {
        var normalizedCurrencyCode = NormalizeCurrencyCode(defaultCurrencyCode);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                update customers
                set default_currency_code = @currency_code,
                    currency_locked = @currency_locked,
                    updated_at = now()
                where id = @customer_id;
                """;
            command.Parameters.AddWithValue("currency_code", normalizedCurrencyCode);
            command.Parameters.AddWithValue("currency_locked", currencyLocked);
            command.Parameters.AddWithValue("customer_id", customerId);

            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                throw new InvalidOperationException($"Customer {customerId:D} was not found.");
            }
        }

        var preference = await GetPreferenceAsync(connection, transaction, customerId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return preference;
    }

    private static async Task<CustomerCurrencyPreference> GetPreferenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid customerId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              c.id,
              c.company_id,
              c.display_name,
              c.default_currency_code,
              c.currency_locked,
              exists (
                select 1
                from invoices i
                where i.company_id = c.company_id
                  and i.customer_id = c.id
                union all
                select 1
                from credit_notes n
                where n.company_id = c.company_id
                  and n.customer_id = c.id
                union all
                select 1
                from receive_payments rp
                where rp.company_id = c.company_id
                  and rp.customer_id = c.id
                union all
                select 1
                from credit_applications ca
                where ca.company_id = c.company_id
                  and ca.customer_id = c.id
              ) as has_transaction_history
            from customers c
            where c.id = @customer_id
            limit 1;
            """;
        command.Parameters.AddWithValue("customer_id", customerId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException($"Customer {customerId:D} was not found.");
        }

        return new CustomerCurrencyPreference(
            reader.GetGuid(reader.GetOrdinal("id")),
            CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
            reader.GetString(reader.GetOrdinal("display_name")),
            reader.GetString(reader.GetOrdinal("default_currency_code")),
            reader.GetBoolean(reader.GetOrdinal("currency_locked")),
            reader.GetBoolean(reader.GetOrdinal("has_transaction_history")));
    }

    private static string NormalizeCurrencyCode(string currencyCode)
    {
        if (string.IsNullOrWhiteSpace(currencyCode))
        {
            throw new InvalidOperationException("A currency code is required.");
        }

        return currencyCode.Trim().ToUpperInvariant();
    }
}
