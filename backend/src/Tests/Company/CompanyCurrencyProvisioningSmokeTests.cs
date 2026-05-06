using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.Company;
using Modules.Company.MultiCurrency;
using Npgsql;

namespace Tests.Company;

public sealed class CompanyCurrencyProvisioningSmokeTests
{
    private static readonly CompanyId CompanyId = CompanyId.FromOrdinal(1);

    [Fact]
    public async Task EnableCurrencyAsync_UpsertsCompanyCurrencyAndForeignControlAccounts()
    {
        var connectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var store = new PostgreSqlCompanyCurrencyProvisioningStore(connectionFactory);
        var workflow = new CompanyCurrencyGovernanceWorkflow(store);
        const string currencyCode = "CAD";

        try
        {
            var result = await workflow.EnableCurrencyAsync(CompanyId, currencyCode, Guid.NewGuid(), CancellationToken.None);

            Assert.True(result.Profile.IsCurrencyEnabled(currencyCode));
            Assert.Contains(result.ProvisionedControlAccounts, account => account.SystemRole == "accounts_receivable:CAD");
            Assert.Contains(result.ProvisionedControlAccounts, account => account.SystemRole == "accounts_payable:CAD");

            await using var connection = await connectionFactory.OpenAsync(CancellationToken.None);

            var enabledCount = await ReadIntAsync(
                connection,
                """
                select count(*)
                from company_currencies
                where company_id = @company_id
                  and currency_code in ('USD', 'CAD')
                  and is_enabled = true;
                """,
                CancellationToken.None);
            Assert.Equal(2, enabledCount);

            var controlAccountCount = await ReadIntAsync(
                connection,
                """
                select count(*)
                from accounts
                where company_id = @company_id
                  and system_role in ('accounts_receivable:CAD', 'accounts_payable:CAD');
                """,
                CancellationToken.None);
            Assert.Equal(2, controlAccountCount);
        }
        finally
        {
            await CleanupAsync(connectionFactory, CancellationToken.None);
        }
    }

    private static string GetConnectionString() =>
        Environment.GetEnvironmentVariable("CITUS_ACCOUNTING_DB")
        ?? "Host=localhost;Port=5432;Database=citus_accounting;Username=postgres;Password=change-me";

    private static async Task<int> ReadIntAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("company_id", CompanyId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
    }

    private static async Task CleanupAsync(
        PostgreSqlConnectionFactory connectionFactory,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await ExecuteAsync(
            connection,
            transaction,
            """
            delete from accounts
            where company_id = @company_id
              and system_role in ('accounts_receivable:CAD', 'accounts_payable:CAD');
            """,
            cancellationToken);

        await ExecuteAsync(
            connection,
            transaction,
            """
            delete from company_currencies
            where company_id = @company_id
              and currency_code = 'CAD';
            """,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("company_id", CompanyId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
