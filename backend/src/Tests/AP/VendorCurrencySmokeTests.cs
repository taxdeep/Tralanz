using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.AP;
using Infrastructure.PostgreSQL.Company;
using Modules.AP.VendorCurrency;
using Npgsql;

namespace Tests.AP;

public sealed class VendorCurrencySmokeTests
{
    private static readonly Guid VendorId = Guid.Parse("96000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task ChangeDefaultCurrencyAsync_RejectsVendorWithHistoryAndPersistsLock()
    {
        var connectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var store = new PostgreSqlVendorCurrencyStore(connectionFactory);
        var companyStore = new PostgreSqlCompanyCurrencyProvisioningStore(connectionFactory);
        var workflow = new VendorCurrencyWorkflow(store, companyStore);

        try
        {
            var preference = await workflow.GetPreferenceAsync(VendorId, CancellationToken.None);
            Assert.True(preference.HasTransactionHistory);
            Assert.True(preference.IsLocked);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.ChangeDefaultCurrencyAsync(
                VendorId,
                "CAD",
                Guid.NewGuid(),
                CancellationToken.None));

            Assert.Contains("locked", error.Message, StringComparison.OrdinalIgnoreCase);

            await using var connection = await connectionFactory.OpenAsync(CancellationToken.None);
            var currencyLocked = await ReadCurrencyLockedAsync(connection, VendorId, CancellationToken.None);
            Assert.True(currencyLocked);
        }
        finally
        {
            await CleanupAsync(connectionFactory, VendorId, CancellationToken.None);
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
