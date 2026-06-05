using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.AR;
using Infrastructure.PostgreSQL.Company;
using Modules.AR.CustomerCurrency;
using Npgsql;

namespace Tests.AR;

public sealed class CustomerCurrencySmokeTests
{
    private static readonly CompanyId CompanyId = CompanyId.FromOrdinal(1);
    private static readonly UserId UserId = UserId.FromOrdinal(1);
    private static readonly Guid CustomerId = Guid.Parse("91000000-0000-0000-0000-000000000002");

    [SkippableFact]
    public async Task ChangeDefaultCurrencyAsync_RejectsCustomerWithHistoryAndPersistsLock()
    {
        var connectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var store = new PostgreSqlCustomerCurrencyStore(connectionFactory);
        var companyStore = new PostgreSqlCompanyCurrencyProvisioningStore(connectionFactory);
        var workflow = new CustomerCurrencyWorkflow(store, companyStore);

        // The workflow auto-locks the customer on first GetPreferenceAsync once
        // HasTransactionHistory is true. Seed a single invoice so this test
        // owns the history rather than relying on rich global seed data.
        var (invoiceId, createdUser) = await SeedCustomerInvoiceAsync(connectionFactory, CancellationToken.None);

        try
        {
            var preference = await workflow.GetPreferenceAsync(CustomerId, CancellationToken.None);
            Assert.True(preference.HasTransactionHistory);
            Assert.True(preference.IsLocked);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.ChangeDefaultCurrencyAsync(
                CustomerId,
                "CAD",
                UserId,
                CancellationToken.None));

            Assert.Contains("locked", error.Message, StringComparison.OrdinalIgnoreCase);

            await using var connection = await connectionFactory.OpenAsync(CancellationToken.None);
            var currencyLocked = await ReadCurrencyLockedAsync(connection, CustomerId, CancellationToken.None);
            Assert.True(currencyLocked);
        }
        finally
        {
            await CleanupInvoiceAsync(connectionFactory, invoiceId, createdUser, CancellationToken.None);
            await CleanupAsync(connectionFactory, CustomerId, CancellationToken.None);
        }
    }

    private static async Task<(Guid InvoiceId, bool CreatedUser)> SeedCustomerInvoiceAsync(
        PostgreSqlConnectionFactory connectionFactory,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        bool createdUser;
        await using (var userCommand = connection.CreateCommand())
        {
            userCommand.CommandText =
                """
                insert into users (id, email, username, password_hash, status)
                values (@id, @email, @username, 'smoke-hash', 'active')
                on conflict (id) do nothing
                returning 1;
                """;
            userCommand.Parameters.AddWithValue("id", UserId.Value);
            userCommand.Parameters.AddWithValue("email", $"smoke-{UserId.Value}@citus.local");
            userCommand.Parameters.AddWithValue("username", $"smoke-{UserId.Value}");
            createdUser = await userCommand.ExecuteScalarAsync(cancellationToken) is not null;
        }

        var invoiceId = Guid.NewGuid();
        var entityNumber = EntityNumber.Create(DateTime.UtcNow.Year, Random.Shared.Next(0, 60_466_175)).Value;

        await using (var invoiceCommand = connection.CreateCommand())
        {
            invoiceCommand.CommandText =
                """
                insert into invoices (
                  id, company_id, entity_number, invoice_number, customer_id,
                  invoice_date, due_date,
                  document_currency_code, base_currency_code,
                  fx_requested_date, fx_effective_date,
                  created_by_user_id
                )
                values (
                  @id, @company_id, @entity_number, @invoice_number, @customer_id,
                  @invoice_date, @due_date,
                  'USD', 'USD',
                  @invoice_date, @invoice_date,
                  @user_id
                );
                """;
            invoiceCommand.Parameters.AddWithValue("id", invoiceId);
            invoiceCommand.Parameters.AddWithValue("company_id", CompanyId.Value);
            invoiceCommand.Parameters.AddWithValue("entity_number", entityNumber);
            invoiceCommand.Parameters.AddWithValue("invoice_number", $"INV-{invoiceId:N}"[..15]);
            invoiceCommand.Parameters.AddWithValue("customer_id", CustomerId);
            invoiceCommand.Parameters.AddWithValue("invoice_date", new DateOnly(2026, 4, 14));
            invoiceCommand.Parameters.AddWithValue("due_date", new DateOnly(2026, 5, 14));
            invoiceCommand.Parameters.AddWithValue("user_id", UserId.Value);
            await invoiceCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        return (invoiceId, createdUser);
    }

    private static async Task CleanupInvoiceAsync(
        PostgreSqlConnectionFactory connectionFactory,
        Guid invoiceId,
        bool createdUser,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        await using (var deleteInvoice = connection.CreateCommand())
        {
            deleteInvoice.CommandText = "delete from invoices where id = @id;";
            deleteInvoice.Parameters.AddWithValue("id", invoiceId);
            await deleteInvoice.ExecuteNonQueryAsync(cancellationToken);
        }

        if (createdUser)
        {
            await using var deleteUser = connection.CreateCommand();
            deleteUser.CommandText = "delete from users where id = @id;";
            deleteUser.Parameters.AddWithValue("id", UserId.Value);
            await deleteUser.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("CITUS_POSTGRESQL_INTEGRATION_TEST_DB");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "DB-backed test skipped: set CITUS_POSTGRESQL_INTEGRATION_TEST_DB to a dedicated test database to run it.");

        return connectionString!;
    }

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
