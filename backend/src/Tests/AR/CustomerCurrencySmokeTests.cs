using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.AR;
using Infrastructure.PostgreSQL.Company;
using Modules.AR.CustomerCurrency;
using Npgsql;

namespace Tests.AR;

public sealed class CustomerCurrencySmokeTests
{
    private static readonly Guid CustomerId = Guid.Parse("91000000-0000-0000-0000-000000000002");

    [Fact]
    public async Task ChangeDefaultCurrencyAsync_RejectsCustomerWithHistoryAndPersistsLock()
    {
        var connectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var store = new PostgreSqlCustomerCurrencyStore(connectionFactory);
        var companyStore = new PostgreSqlCompanyCurrencyProvisioningStore(connectionFactory);
        var workflow = new CustomerCurrencyWorkflow(store, companyStore);

        try
        {
            var preference = await workflow.GetPreferenceAsync(CustomerId, CancellationToken.None);
            Assert.True(preference.HasTransactionHistory);
            Assert.True(preference.IsLocked);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.ChangeDefaultCurrencyAsync(
                CustomerId,
                "CAD",
                UserId.FromOrdinal(1),
                CancellationToken.None));

            Assert.Contains("locked", error.Message, StringComparison.OrdinalIgnoreCase);

            await using var connection = await connectionFactory.OpenAsync(CancellationToken.None);
            var currencyLocked = await ReadCurrencyLockedAsync(connection, CustomerId, CancellationToken.None);
            Assert.True(currencyLocked);
        }
        finally
        {
            await CleanupAsync(connectionFactory, CustomerId, CancellationToken.None);
        }
    }

    private static string GetConnectionString() =>
        Environment.GetEnvironmentVariable("CITUS_ACCOUNTING_DB")
        ?? "Host=localhost;Port=5432;Database=citus_accounting;Username=postgres;Password=change-me";

    private static async Task<bool> ReadCurrencyLockedAsync(
        NpgsqlConnection connection,
        Guid customerId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select currency_locked
            from customers
            where id = @customer_id;
            """;
        command.Parameters.AddWithValue("customer_id", customerId);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static async Task CleanupAsync(
        PostgreSqlConnectionFactory connectionFactory,
        Guid customerId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            update customers
            set currency_locked = false,
                updated_at = now()
            where id = @customer_id;
            """;
        command.Parameters.AddWithValue("customer_id", customerId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
