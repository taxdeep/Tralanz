using Citus.Accounting.Domain.Currencies;

namespace Citus.Accounting.Infrastructure.Persistence;

internal static class PostgresSettlementDraftingSupport
{
    public static async Task<string> LoadCompanyBaseCurrencyCodeAsync(
        PostgresCommandScope scope,
        Guid companyId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select base_currency_code
            from companies
            where id = @company_id
            limit 1;
            """);

        command.Parameters.AddWithValue("company_id", companyId);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        if (scalar is null || scalar == DBNull.Value)
        {
            throw new InvalidOperationException("Company was not found in the active context.");
        }

        return (string)scalar;
    }

    public static async Task EnsureActiveBankAccountAsync(
        PostgresCommandScope scope,
        Guid companyId,
        Guid bankAccountId,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select id
            from accounts
            where company_id = @company_id
              and id = @bank_account_id
              and is_active = true
            limit 1;
            """);

        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("bank_account_id", bankAccountId);

        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        if (scalar is null || scalar == DBNull.Value)
        {
            throw new InvalidOperationException(errorMessage);
        }
    }

    public static async Task<FxSnapshotRef?> LoadAcceptedFxSnapshotAsync(
        PostgresCommandScope scope,
        Guid companyId,
        string baseCurrencyCode,
        string transactionCurrencyCode,
        DateOnly requestedDate,
        Guid? snapshotId,
        CancellationToken cancellationToken)
    {
        var sql = snapshotId is { } resolvedSnapshotId && resolvedSnapshotId != Guid.Empty
            ? """
              select
                s.id,
                s.base_currency_code,
                s.quote_currency_code,
                s.rate,
                s.requested_date,
                s.effective_date,
                s.snapshot_semantics
              from company_fx_rate_snapshots s
              where s.company_id = @company_id
                and s.id = @snapshot_id
                and s.base_currency_code = @base_currency_code
                and s.quote_currency_code = @transaction_currency_code
              limit 1;
              """
            : """
              select
                s.id,
                s.base_currency_code,
                s.quote_currency_code,
                s.rate,
                s.requested_date,
                s.effective_date,
                s.snapshot_semantics
              from company_fx_rate_snapshots s
              where s.company_id = @company_id
                and s.base_currency_code = @base_currency_code
                and s.quote_currency_code = @transaction_currency_code
                and s.requested_date <= @requested_date
                and s.effective_date <= @requested_date
              order by s.requested_date desc, s.effective_date desc, s.created_at desc
              limit 1;
              """;

        await using var command = scope.CreateCommand(sql);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("base_currency_code", baseCurrencyCode);
        command.Parameters.AddWithValue("transaction_currency_code", transactionCurrencyCode);
        command.Parameters.AddWithValue("requested_date", requestedDate);

        if (snapshotId is { } explicitSnapshotId && explicitSnapshotId != Guid.Empty)
        {
            command.Parameters.AddWithValue("snapshot_id", explicitSnapshotId);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new FxSnapshotRef(
            reader.GetGuid(reader.GetOrdinal("id")),
            new CurrencyCode(reader.GetString(reader.GetOrdinal("base_currency_code"))),
            new CurrencyCode(reader.GetString(reader.GetOrdinal("quote_currency_code"))),
            reader.GetDecimal(reader.GetOrdinal("rate")),
            reader.GetFieldValue<DateOnly>(reader.GetOrdinal("requested_date")),
            reader.GetFieldValue<DateOnly>(reader.GetOrdinal("effective_date")),
            reader.GetString(reader.GetOrdinal("snapshot_semantics")));
    }

    public static FxSnapshotRef CreateIdentitySnapshot(
        string currencyCode,
        DateOnly documentDate) =>
        new(
            Guid.Empty,
            new CurrencyCode(currencyCode),
            new CurrencyCode(currencyCode),
            1m,
            documentDate,
            documentDate,
            "identity");
}
