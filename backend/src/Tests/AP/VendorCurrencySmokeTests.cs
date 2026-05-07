using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.AP;
using Infrastructure.PostgreSQL.Company;
using Modules.AP.VendorCurrency;
using Npgsql;

namespace Tests.AP;

public sealed class VendorCurrencySmokeTests
{
    private static readonly CompanyId CompanyId = CompanyId.FromOrdinal(1);
    private static readonly UserId UserId = UserId.FromOrdinal(1);
    private static readonly Guid VendorId = Guid.Parse("96000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task ChangeDefaultCurrencyAsync_RejectsVendorWithHistoryAndPersistsLock()
    {
        var connectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var store = new PostgreSqlVendorCurrencyStore(connectionFactory);
        var companyStore = new PostgreSqlCompanyCurrencyProvisioningStore(connectionFactory);
        var workflow = new VendorCurrencyWorkflow(store, companyStore);

        // The workflow auto-locks the vendor on first GetPreferenceAsync once
        // HasTransactionHistory is true. Seed a single bill so this test owns
        // the history rather than relying on rich global seed data.
        var (billId, createdUser) = await SeedVendorBillAsync(connectionFactory, CancellationToken.None);

        try
        {
            var preference = await workflow.GetPreferenceAsync(VendorId, CancellationToken.None);
            Assert.True(preference.HasTransactionHistory);
            Assert.True(preference.IsLocked);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.ChangeDefaultCurrencyAsync(
                VendorId,
                "CAD",
                UserId,
                CancellationToken.None));

            Assert.Contains("locked", error.Message, StringComparison.OrdinalIgnoreCase);

            await using var connection = await connectionFactory.OpenAsync(CancellationToken.None);
            var currencyLocked = await ReadCurrencyLockedAsync(connection, VendorId, CancellationToken.None);
            Assert.True(currencyLocked);
        }
        finally
        {
            await CleanupBillAsync(connectionFactory, billId, createdUser, CancellationToken.None);
            await CleanupAsync(connectionFactory, VendorId, CancellationToken.None);
        }
    }

    private static async Task<(Guid BillId, bool CreatedUser)> SeedVendorBillAsync(
        PostgreSqlConnectionFactory connectionFactory,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        // Idempotent user upsert — the test only "owns" the user if it inserted
        // it, so cleanup can decide whether to delete.
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

        var billId = Guid.NewGuid();
        var entityNumber = EntityNumber.Create(DateTime.UtcNow.Year, Random.Shared.Next(0, 60_466_175)).Value;

        await using (var billCommand = connection.CreateCommand())
        {
            billCommand.CommandText =
                """
                insert into bills (
                  id, company_id, entity_number, bill_number, vendor_id,
                  bill_date, due_date,
                  document_currency_code, base_currency_code,
                  fx_requested_date, fx_effective_date,
                  created_by_user_id
                )
                values (
                  @id, @company_id, @entity_number, @bill_number, @vendor_id,
                  @bill_date, @due_date,
                  'USD', 'USD',
                  @bill_date, @bill_date,
                  @user_id
                );
                """;
            billCommand.Parameters.AddWithValue("id", billId);
            billCommand.Parameters.AddWithValue("company_id", CompanyId.Value);
            billCommand.Parameters.AddWithValue("entity_number", entityNumber);
            billCommand.Parameters.AddWithValue("bill_number", $"BILL-{billId:N}"[..15]);
            billCommand.Parameters.AddWithValue("vendor_id", VendorId);
            billCommand.Parameters.AddWithValue("bill_date", new DateOnly(2026, 4, 14));
            billCommand.Parameters.AddWithValue("due_date", new DateOnly(2026, 5, 14));
            billCommand.Parameters.AddWithValue("user_id", UserId.Value);
            await billCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        return (billId, createdUser);
    }

    private static async Task CleanupBillAsync(
        PostgreSqlConnectionFactory connectionFactory,
        Guid billId,
        bool createdUser,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        await using (var deleteBill = connection.CreateCommand())
        {
            deleteBill.CommandText = "delete from bills where id = @id;";
            deleteBill.Parameters.AddWithValue("id", billId);
            await deleteBill.ExecuteNonQueryAsync(cancellationToken);
        }

        if (createdUser)
        {
            await using var deleteUser = connection.CreateCommand();
            deleteUser.CommandText = "delete from users where id = @id;";
            deleteUser.Parameters.AddWithValue("id", UserId.Value);
            await deleteUser.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static string GetConnectionString() =>
        Environment.GetEnvironmentVariable("CITUS_ACCOUNTING_DB")
        ?? "Host=localhost;Port=5432;Database=citus_accounting;Username=postgres;Password=change-me";

    private static async Task<bool> ReadCurrencyLockedAsync(
        NpgsqlConnection connection,
        Guid vendorId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select currency_locked
            from vendors
            where id = @vendor_id;
            """;
        command.Parameters.AddWithValue("vendor_id", vendorId);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static async Task CleanupAsync(
        PostgreSqlConnectionFactory connectionFactory,
        Guid vendorId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            update vendors
            set currency_locked = false,
                updated_at = now()
            where id = @vendor_id;
            """;
        command.Parameters.AddWithValue("vendor_id", vendorId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
