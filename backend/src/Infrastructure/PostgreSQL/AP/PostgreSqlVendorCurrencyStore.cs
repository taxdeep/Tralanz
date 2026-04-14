using Modules.AP.VendorCurrency;
using Npgsql;

namespace Infrastructure.PostgreSQL.AP;

public sealed class PostgreSqlVendorCurrencyStore : IVendorCurrencyStore
{
    private readonly PostgreSqlConnectionFactory _connections;

    public PostgreSqlVendorCurrencyStore(PostgreSqlConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    public async Task<VendorCurrencyPreference> GetPreferenceAsync(
        Guid vendorId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        return await GetPreferenceAsync(connection, transaction: null, vendorId, cancellationToken);
    }

    public async Task<VendorCurrencyPreference> SavePreferenceAsync(
        Guid vendorId,
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
                update vendors
                set default_currency_code = @currency_code,
                    currency_locked = @currency_locked,
                    updated_at = now()
                where id = @vendor_id;
                """;
            command.Parameters.AddWithValue("currency_code", normalizedCurrencyCode);
            command.Parameters.AddWithValue("currency_locked", currencyLocked);
            command.Parameters.AddWithValue("vendor_id", vendorId);

            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                throw new InvalidOperationException($"Vendor {vendorId:D} was not found.");
            }
        }

        var preference = await GetPreferenceAsync(connection, transaction, vendorId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return preference;
    }

    private static async Task<VendorCurrencyPreference> GetPreferenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid vendorId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              v.id,
              v.company_id,
              v.display_name,
              v.default_currency_code,
              v.currency_locked,
              exists (
                select 1
                from bills b
                where b.company_id = v.company_id
                  and b.vendor_id = v.id
                union all
                select 1
                from vendor_credits vc
                where vc.company_id = v.company_id
                  and vc.vendor_id = v.id
                union all
                select 1
                from pay_bills pb
                where pb.company_id = v.company_id
                  and pb.vendor_id = v.id
                union all
                select 1
                from vendor_credit_applications vca
                where vca.company_id = v.company_id
                  and vca.vendor_id = v.id
              ) as has_transaction_history
            from vendors v
            where v.id = @vendor_id
            limit 1;
            """;
        command.Parameters.AddWithValue("vendor_id", vendorId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException($"Vendor {vendorId:D} was not found.");
        }

        return new VendorCurrencyPreference(
            reader.GetGuid(reader.GetOrdinal("id")),
            reader.GetGuid(reader.GetOrdinal("company_id")),
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
