using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.Company;
using Modules.Company.MultiCurrency;
using Npgsql;

namespace Tests.Company;

public sealed class CompanyCurrencyProvisioningSmokeTests
{
    private static readonly CompanyId CompanyId = CompanyId.FromOrdinal(1);

    [SkippableFact]
    public async Task EnableCurrencyAsync_UpsertsCompanyCurrencyAndForeignControlAccounts()
    {
        var connectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var store = new PostgreSqlCompanyCurrencyProvisioningStore(connectionFactory);
        var workflow = new CompanyCurrencyGovernanceWorkflow(store);
        const string currencyCode = "CAD";

        try
        {
            var result = await workflow.EnableCurrencyAsync(CompanyId, currencyCode, UserId.FromOrdinal(1), CancellationToken.None);

            Assert.True(result.Profile.IsCurrencyEnabled(currencyCode));

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

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("CITUS_POSTGRESQL_INTEGRATION_TEST_DB");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "DB-backed test skipped: set CITUS_POSTGRESQL_INTEGRATION_TEST_DB to a dedicated test database to run it.");

        return connectionString!;
    }

    private static async Task<int> ReadIntAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("company_id", CompanyId.Value);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
    }

    private static async Task CleanupAsync(
        PostgreSqlConnectionFactory connectionFactory,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

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
        command.Parameters.AddWithValue("company_id", CompanyId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
