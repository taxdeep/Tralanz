using Engines.FX.FxRateLookup;
using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.AR;
using Infrastructure.PostgreSQL.Company;
using Infrastructure.PostgreSQL.FX;
using Modules.AR.CustomerCurrency;
using Modules.AR.ReceivePayment;
using Modules.Company.MultiCurrency;
using Npgsql;
using SharedKernel.FX;

namespace Tests.AR;

public sealed class ReceivePaymentDraftPreparationSmokeTests
{
    private static readonly Guid CompanyId = Guid.Parse("5e492df2-37ab-47df-a1bb-2d559c876cbc");
    private static readonly Guid CustomerId = Guid.Parse("91000000-0000-0000-0000-000000000002");

    [Fact]
    public async Task PrepareDraftAsync_PersistsDraftWithCustomerCurrency()
    {
        var connectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var draftStore = new PostgreSqlReceivePaymentDraftPreparationStore(connectionFactory);
        var customerStore = new PostgreSqlCustomerCurrencyStore(connectionFactory);
        var companyStore = new PostgreSqlCompanyCurrencyProvisioningStore(connectionFactory);

        var workflow = new ReceivePaymentDraftPreparationWorkflow(
            draftStore,
            new CustomerCurrencyWorkflow(customerStore, companyStore),
            companyStore,
            new StubFxRateResolver(),
            new StubFxRateStore());

        Guid documentId = Guid.Empty;
        Guid bankAccountId = Guid.Empty;
        Guid userId = Guid.Empty;
        var createdUser = false;
        var originalLock = await ReadCustomerLockAsync(connectionFactory, CustomerId, CancellationToken.None);

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            bankAccountId = await CreateBankAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            var openItem = await LoadOpenItemAsync(connectionFactory, CustomerId, CancellationToken.None);

            var appliedAmount = Math.Min(openItem.OpenAmountTx, 50m);
            var result = await workflow.PrepareDraftAsync(
                new ReceivePaymentDraftContext(
                    CompanyId,
                    userId,
                    CustomerId,
                    bankAccountId,
                    new DateOnly(2026, 4, 14),
                    null,
                    null,
                    "Smoke test draft"),
                [new ReceivePaymentDraftLine(openItem.OpenItemId, appliedAmount)],
                CancellationToken.None);

            documentId = result.DocumentId;
            Assert.Equal("USD", result.DocumentCurrencyCode);
            Assert.Equal("USD", result.BaseCurrencyCode);

            await using var connection = await connectionFactory.OpenAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                select document_currency_code, base_currency_code, status
                from receive_payments
                where id = @document_id;
                """;
            command.Parameters.AddWithValue("document_id", result.DocumentId);
            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.Equal("USD", reader.GetString(0));
            Assert.Equal("USD", reader.GetString(1));
            Assert.Equal("draft", reader.GetString(2));
        }
        finally
        {
            await CleanupDraftAsync(connectionFactory, documentId, CancellationToken.None);
            await CleanupBankAccountAsync(connectionFactory, bankAccountId, CancellationToken.None);
            await RestoreCustomerLockAsync(connectionFactory, CustomerId, originalLock, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    private static string GetConnectionString() =>
        Environment.GetEnvironmentVariable("CITUS_ACCOUNTING_DB")
        ?? "Host=localhost;Port=5432;Database=citus_accounting;Username=postgres;Password=change-me";

    private static async Task<(Guid OpenItemId, decimal OpenAmountTx)> LoadOpenItemAsync(
        PostgreSqlConnectionFactory connectionFactory,
        Guid customerId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select id, open_amount_tx
            from ar_open_items
            where customer_id = @customer_id
              and status in ('open', 'partially_applied')
              and open_amount_tx > 0
            order by created_at
            limit 1;
            """;
        command.Parameters.AddWithValue("customer_id", customerId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("No AR open item found for receive payment smoke test.");
        }

        return (reader.GetGuid(0), reader.GetDecimal(1));
    }

    private static async Task<Guid> CreateBankAccountAsync(
        PostgreSqlConnectionFactory connectionFactory,
        Guid companyId,
        CancellationToken cancellationToken)
    {
        var accountId = Guid.NewGuid();
        var entityNumber = await ReserveEntityNumberAsync(connectionFactory, cancellationToken);

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into accounts (
              id,
              company_id,
              entity_number,
              code,
              name,
              root_type,
              detail_type,
              is_active,
              is_system,
              is_system_default,
              allow_manual_posting,
              created_at,
              updated_at
            )
            values (
              @id,
              @company_id,
              @entity_number,
              @code,
              @name,
              'asset',
              'bank',
              true,
              false,
              false,
              true,
              now(),
              now()
            );
            """;
        command.Parameters.AddWithValue("id", accountId);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("entity_number", entityNumber);
        command.Parameters.AddWithValue("code", $"BANK-{entityNumber[^6..]}");
        command.Parameters.AddWithValue("name", "Smoke Test Bank");
        await command.ExecuteNonQueryAsync(cancellationToken);
        return accountId;
    }

    private static async Task<(Guid UserId, bool Created)> GetOrCreateUserAsync(
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
        if (existing is Guid userId)
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

    private static async Task<string> ReserveEntityNumberAsync(
        PostgreSqlConnectionFactory connectionFactory,
        CancellationToken cancellationToken)
    {
        var year = DateTime.UtcNow.Year;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var seed = Random.Shared.Next(0, 100_000_000);
            var candidate = $"EN{year}{seed:00000000}";
            if (!await EntityNumberExistsAsync(connectionFactory, candidate, cancellationToken))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not reserve a unique entity number for AR smoke test.");
    }

    private static async Task<bool> EntityNumberExistsAsync(
        PostgreSqlConnectionFactory connectionFactory,
        string entityNumber,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            with all_entities as (
              select entity_number from manual_journal_documents
              union all
              select entity_number from journal_entries
              union all
              select entity_number from invoices
              union all
              select entity_number from bills
              union all
              select entity_number from credit_notes
              union all
              select entity_number from vendor_credits
              union all
              select entity_number from receive_payments
              union all
              select entity_number from pay_bills
              union all
              select entity_number from fx_revaluation_batches
              union all
              select entity_number from accounts
            )
            select 1
            from all_entities
            where entity_number = @entity_number
            limit 1;
            """;
        command.Parameters.AddWithValue("entity_number", entityNumber);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is not null;
    }

    private static async Task<bool> ReadCustomerLockAsync(
        PostgreSqlConnectionFactory connectionFactory,
        Guid customerId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
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

    private static async Task RestoreCustomerLockAsync(
        PostgreSqlConnectionFactory connectionFactory,
        Guid customerId,
        bool currencyLocked,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            update customers
            set currency_locked = @currency_locked,
                updated_at = now()
            where id = @customer_id;
            """;
        command.Parameters.AddWithValue("currency_locked", currencyLocked);
        command.Parameters.AddWithValue("customer_id", customerId);
        await command.ExecuteNonQueryAsync(cancellationToken);
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
                delete from receive_payment_lines
                where receive_payment_id = @document_id;
                """;
            lineCommand.Parameters.AddWithValue("document_id", documentId);
            await lineCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var headerCommand = connection.CreateCommand())
        {
            headerCommand.Transaction = transaction;
            headerCommand.CommandText =
                """
                delete from receive_payments
                where id = @document_id;
                """;
            headerCommand.Parameters.AddWithValue("document_id", documentId);
            await headerCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task CleanupBankAccountAsync(
        PostgreSqlConnectionFactory connectionFactory,
        Guid bankAccountId,
        CancellationToken cancellationToken)
    {
        if (bankAccountId == Guid.Empty)
        {
            return;
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            delete from accounts
            where id = @account_id;
            """;
        command.Parameters.AddWithValue("account_id", bankAccountId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CleanupUserAsync(
        PostgreSqlConnectionFactory connectionFactory,
        Guid userId,
        bool created,
        CancellationToken cancellationToken)
    {
        if (!created || userId == Guid.Empty)
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
        command.Parameters.AddWithValue("user_id", userId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed class StubFxRateResolver : IFxRateResolver
    {
        public Task<FxRateResolution> ResolveAsync(
            FxRateLookupRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(FxRateResolution.Identity(request.RequestedDate));
    }

    private sealed class StubFxRateStore : IFxRateStore
    {
        public Task<IReadOnlyList<FxSnapshotRecord>> ListCompanySnapshotsAsync(
            Guid companyId,
            string baseCurrencyCode,
            string quoteCurrencyCode,
            DateOnly requestedDate,
            int take,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FxSnapshotRecord>>([]);

        public Task<IReadOnlyList<FxMarketRateRecord>> ListMarketRatesAsync(
            string providerKey,
            string baseCurrencyCode,
            string quoteCurrencyCode,
            DateOnly requestedDate,
            int take,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FxMarketRateRecord>>([]);

        public Task<FxSnapshotRecord?> FindCompanySnapshotByIdAsync(
            Guid companyId,
            Guid snapshotId,
            CancellationToken cancellationToken) => Task.FromResult<FxSnapshotRecord?>(null);

        public Task<FxMarketRateRecord?> FindMarketRateByIdAsync(
            Guid marketRateId,
            CancellationToken cancellationToken) => Task.FromResult<FxMarketRateRecord?>(null);

        public Task<FxSnapshotRecord?> FindLatestCompanySnapshotAsync(
            Guid companyId,
            string baseCurrencyCode,
            string quoteCurrencyCode,
            DateOnly requestedDate,
            string providerKey,
            int lookbackDays,
            string rateType,
            string quoteBasis,
            string rateUseCase,
            CancellationToken cancellationToken) => Task.FromResult<FxSnapshotRecord?>(null);

        public Task<FxMarketRateRecord?> FindLatestMarketRateAsync(
            string providerKey,
            string baseCurrencyCode,
            string quoteCurrencyCode,
            DateOnly requestedDate,
            int lookbackDays,
            string rateType,
            string quoteBasis,
            CancellationToken cancellationToken) => Task.FromResult<FxMarketRateRecord?>(null);

        public Task<IReadOnlyList<FxMarketRateRecord>> UpsertMarketRatesAsync(
            IReadOnlyList<FxMarketRateRecord> marketRates,
            CancellationToken cancellationToken) => Task.FromResult(marketRates);

        public Task<FxSnapshotRecord> UpsertCompanySnapshotAsync(
            Guid companyId,
            Guid? createdByUserId,
            string baseCurrencyCode,
            string quoteCurrencyCode,
            DateOnly requestedDate,
            FxMarketRateRecord marketRate,
            string providerKey,
            string rateType,
            string quoteBasis,
            string rateUseCase,
            string postingReason,
            CancellationToken cancellationToken) =>
            Task.FromResult(new FxSnapshotRecord(
                Guid.NewGuid(),
                companyId,
                baseCurrencyCode,
                quoteCurrencyCode,
                requestedDate,
                marketRate.MarketDate,
                marketRate.Rate,
                rateType,
                quoteBasis,
                rateUseCase,
                postingReason,
                providerKey,
                FxSourceSemantics.ProviderFetched,
                FxSourceSemantics.SystemStored,
                marketRate.Id,
                DateTimeOffset.UtcNow));

        public Task<FxSnapshotRecord> CreateManualCompanySnapshotAsync(
            Guid companyId,
            Guid? createdByUserId,
            string baseCurrencyCode,
            string quoteCurrencyCode,
            DateOnly requestedDate,
            decimal rate,
            string providerKey,
            string rateType,
            string quoteBasis,
            string rateUseCase,
            string postingReason,
            CancellationToken cancellationToken) =>
            Task.FromResult(new FxSnapshotRecord(
                Guid.NewGuid(),
                companyId,
                baseCurrencyCode,
                quoteCurrencyCode,
                requestedDate,
                requestedDate,
                rate,
                rateType,
                quoteBasis,
                rateUseCase,
                postingReason,
                providerKey,
                FxSourceSemantics.Manual,
                FxSourceSemantics.Manual,
                null,
                DateTimeOffset.UtcNow));
    }
}
