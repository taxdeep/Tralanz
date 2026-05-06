using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.AP;
using Infrastructure.PostgreSQL.Company;
using Modules.AP.VendorCreditApplication;
using Modules.AP.VendorCurrency;
using Modules.Company.MultiCurrency;

namespace Tests.AP;

public sealed class VendorCreditApplicationDraftPreparationSmokeTests
{
    private static readonly CompanyId CompanyId = Guid.Parse("5e492df2-37ab-47df-a1bb-2d559c876cbc");
    private static readonly Guid VendorId = Guid.Parse("96000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task PrepareDraftAsync_PersistsDraftAndCalculatesRealizedFxBoundary()
    {
        var connectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var draftStore = new PostgreSqlVendorCreditApplicationDraftPreparationStore(connectionFactory);
        var vendorStore = new PostgreSqlVendorCurrencyStore(connectionFactory);
        var companyStore = new PostgreSqlCompanyCurrencyProvisioningStore(connectionFactory);

        var workflow = new VendorCreditApplicationDraftPreparationWorkflow(
            draftStore,
            new VendorCurrencyWorkflow(vendorStore, companyStore),
            companyStore);

        Guid documentId = Guid.Empty;
        UserId userId = Guid.Empty;
        Guid sourceOpenItemId = Guid.Empty;
        Guid targetOpenItemId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            sourceOpenItemId = await CreateApOpenItemAsync(
                connectionFactory,
                CompanyId,
                VendorId,
                "vendor_credit",
                "debit",
                "USD",
                100m,
                135m,
                CancellationToken.None);
            targetOpenItemId = await CreateApOpenItemAsync(
                connectionFactory,
                CompanyId,
                VendorId,
                "bill",
                "credit",
                "USD",
                100m,
                138m,
                CancellationToken.None);

            var result = await workflow.PrepareDraftAsync(
                new VendorCreditApplicationDraftContext(
                    CompanyId,
                    userId,
                    VendorId,
                    new DateOnly(2026, 4, 14),
                    null,
                    "Smoke test vendor credit application"),
                [new VendorCreditApplicationDraftLine(sourceOpenItemId, targetOpenItemId, 100m)],
                CancellationToken.None);

            documentId = result.DocumentId;
            Assert.Equal("USD", result.DocumentCurrencyCode);
            Assert.Equal(100m, result.TotalAmount);
            Assert.Equal(-3m, result.RealizedFxAmountBase);

            await using var connection = await connectionFactory.OpenAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                select document_currency_code, total_amount, status
                from vendor_credit_applications
                where id = @document_id;
                """;
            command.Parameters.AddWithValue("document_id", result.DocumentId);
            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.Equal("USD", reader.GetString(0));
            Assert.Equal(100m, reader.GetDecimal(1));
            Assert.Equal("draft", reader.GetString(2));
        }
        finally
        {
            await CleanupDraftAsync(connectionFactory, documentId, CancellationToken.None);
            await CleanupApOpenItemAsync(connectionFactory, sourceOpenItemId, CancellationToken.None);
            await CleanupApOpenItemAsync(connectionFactory, targetOpenItemId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    private static string GetConnectionString() =>
        Environment.GetEnvironmentVariable("CITUS_ACCOUNTING_DB")
        ?? "Host=localhost;Port=5432;Database=citus_accounting;Username=postgres;Password=change-me";

    private static async Task<Guid> CreateApOpenItemAsync(
        PostgreSqlConnectionFactory connectionFactory,
        CompanyId companyId,
        Guid vendorId,
        string sourceType,
        string balanceSide,
        string documentCurrencyCode,
        decimal openAmountTx,
        decimal openAmountBase,
        CancellationToken cancellationToken)
    {
        var openItemId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into ap_open_items (
              id,
              company_id,
              vendor_id,
              source_type,
              source_id,
              due_date,
              document_currency_code,
              base_currency_code,
              original_amount_tx,
              original_amount_base,
              open_amount_tx,
              open_amount_base,
              balance_side,
              status,
              created_at,
              updated_at
            )
            values (
              @id,
              @company_id,
              @vendor_id,
              @source_type,
              @source_id,
              @due_date,
              @document_currency_code,
              @base_currency_code,
              @original_amount_tx,
              @original_amount_base,
              @open_amount_tx,
              @open_amount_base,
              @balance_side,
              'open',
              now(),
              now()
            );
            """;
        command.Parameters.AddWithValue("id", openItemId);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("vendor_id", vendorId);
        command.Parameters.AddWithValue("source_type", sourceType);
        command.Parameters.AddWithValue("source_id", sourceId);
        command.Parameters.AddWithValue("due_date", new DateOnly(2026, 5, 1));
        command.Parameters.AddWithValue("document_currency_code", documentCurrencyCode);
        command.Parameters.AddWithValue("base_currency_code", "CAD");
        command.Parameters.AddWithValue("original_amount_tx", openAmountTx);
        command.Parameters.AddWithValue("original_amount_base", openAmountBase);
        command.Parameters.AddWithValue("open_amount_tx", openAmountTx);
        command.Parameters.AddWithValue("open_amount_base", openAmountBase);
        command.Parameters.AddWithValue("balance_side", balanceSide);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return openItemId;
    }

    private static async Task<(UserId UserId, bool Created)> GetOrCreateUserAsync(
        PostgreSqlConnectionFactory connectionFactory,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var findCommand = connection.CreateCommand();
        findCommand.CommandText =
            """
            select id
            from users
            order by created_at
            limit 1;
            """;
        var existing = await findCommand.ExecuteScalarAsync(cancellationToken);
        if (existing is UserId userId)
        {
            return (userId, false);
        }

        var newUserId = Guid.NewGuid();
        await using var insertCommand = connection.CreateCommand();
        insertCommand.CommandText =
            """
            insert into users (
              id,
              email,
              username,
              password_hash,
              is_active
            )
            values (
              @id,
              @email,
              @username,
              @password_hash,
              true
            );
            """;
        insertCommand.Parameters.AddWithValue("id", newUserId);
        insertCommand.Parameters.AddWithValue("email", $"smoke-{newUserId:N}@citus.local");
        insertCommand.Parameters.AddWithValue("username", $"smoke-{newUserId:N}");
        insertCommand.Parameters.AddWithValue("password_hash", "smoke-hash");
        await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        return (newUserId, true);
    }

    private static async Task CleanupDraftAsync(
        PostgreSqlConnectionFactory connectionFactory,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        if (documentId == Guid.Empty)
        {
            return;
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var lineCommand = connection.CreateCommand())
        {
            lineCommand.Transaction = transaction;
            lineCommand.CommandText =
                """
                delete from vendor_credit_application_lines
                where vendor_credit_application_id = @document_id;
                """;
            lineCommand.Parameters.AddWithValue("document_id", documentId);
            await lineCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var headerCommand = connection.CreateCommand())
        {
            headerCommand.Transaction = transaction;
            headerCommand.CommandText =
                """
                delete from vendor_credit_applications
                where id = @document_id;
                """;
            headerCommand.Parameters.AddWithValue("document_id", documentId);
            await headerCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task CleanupApOpenItemAsync(
        PostgreSqlConnectionFactory connectionFactory,
        Guid openItemId,
        CancellationToken cancellationToken)
    {
        if (openItemId == Guid.Empty)
        {
            return;
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            delete from ap_open_items
            where id = @open_item_id;
            """;
        command.Parameters.AddWithValue("open_item_id", openItemId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CleanupUserAsync(
        PostgreSqlConnectionFactory connectionFactory,
        UserId userId,
        bool createdUser,
        CancellationToken cancellationToken)
    {
        if (!createdUser || userId.Value is null)
        {
            return;
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            delete from users
            where id = @user_id;
            """;
        command.Parameters.AddWithValue("user_id", userId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
