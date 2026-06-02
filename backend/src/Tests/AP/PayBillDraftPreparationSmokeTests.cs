using Engines.FX.FxRateLookup;
using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.AP;
using Infrastructure.PostgreSQL.Company;
using Infrastructure.PostgreSQL.FX;
using Modules.AP.PayBill;
using Modules.AP.VendorCurrency;
using Modules.Company.MultiCurrency;
using Npgsql;
using SharedKernel.FX;

namespace Tests.AP;

public sealed class PayBillDraftPreparationSmokeTests
{
    private static readonly CompanyId CompanyId = CompanyId.FromOrdinal(1);
    private static readonly Guid VendorId = Guid.Parse("96000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task PrepareDraftAsync_PersistsDraftWithVendorCurrency()
    {
        var connectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var draftStore = new PostgreSqlPayBillDraftPreparationStore(connectionFactory);
        var vendorStore = new PostgreSqlVendorCurrencyStore(connectionFactory);
        var companyStore = new PostgreSqlCompanyCurrencyProvisioningStore(connectionFactory);

        var workflow = new PayBillDraftPreparationWorkflow(
            draftStore,
            new VendorCurrencyWorkflow(vendorStore, companyStore),
            companyStore,
            new StubFxRateResolver(),
            new StubFxRateStore());

        Guid documentId = Guid.Empty;
        Guid bankAccountId = default;
        Guid seededBillId = Guid.Empty;
        Guid seededOpenItemId = Guid.Empty;
        UserId userId = default;
        var createdUser = false;
        var createdVendor = false;
        var originalLock = await ReadVendorLockAsync(connectionFactory, VendorId, CancellationToken.None);

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            createdVendor = await EnsureVendorAsync(connectionFactory, VendorId, CancellationToken.None);
            bankAccountId = await CreateBankAccountAsync(connectionFactory, CompanyId, CancellationToken.None);
            (seededBillId, seededOpenItemId) = await SeedBillAndOpenItemAsync(
                connectionFactory, userId, CancellationToken.None);
            var openItem = await LoadOpenItemAsync(connectionFactory, seededOpenItemId, CancellationToken.None);

            var appliedAmount = Math.Min(openItem.OpenAmountTx, 50m);
            var result = await workflow.PrepareDraftAsync(
                new PayBillDraftContext(
                    CompanyId,
                    userId,
                    VendorId,
                    bankAccountId,
                    new DateOnly(2026, 4, 14),
                    null,
                    null,
                    "Smoke test draft"),
                [new PayBillDraftLine(openItem.OpenItemId, appliedAmount)],
                CancellationToken.None);

            documentId = result.DocumentId;
            Assert.Equal("USD", result.DocumentCurrencyCode);
            Assert.Equal("USD", result.BaseCurrencyCode);

            await using var connection = await connectionFactory.OpenAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                select document_currency_code, base_currency_code, status
                from pay_bills
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
            await CleanupOpenItemAndBillAsync(
                connectionFactory, seededOpenItemId, seededBillId, CancellationToken.None);
            await CleanupBankAccountAsync(connectionFactory, bankAccountId, CancellationToken.None);
            if (createdVendor)
            {
                await CleanupVendorAsync(connectionFactory, VendorId, CancellationToken.None);
            }
            else
            {
                await RestoreVendorLockAsync(connectionFactory, VendorId, originalLock, CancellationToken.None);
            }
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    private static async Task<(Guid BillId, Guid OpenItemId)> SeedBillAndOpenItemAsync(
        PostgreSqlConnectionFactory connectionFactory,
        UserId userId,
        CancellationToken cancellationToken)
    {
        var billId = Guid.NewGuid();
        var openItemId = Guid.NewGuid();
        var entityNumber = await ReserveEntityNumberAsync(connectionFactory, cancellationToken);

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var billCommand = connection.CreateCommand())
        {
            billCommand.Transaction = transaction;
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
            billCommand.Parameters.AddWithValue("user_id", userId.Value);
            await billCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var openItemCommand = connection.CreateCommand())
        {
            openItemCommand.Transaction = transaction;
            openItemCommand.CommandText =
                """
                insert into ap_open_items (
                  id, company_id, vendor_id, source_type, source_id,
                  balance_side,
                  document_currency_code, base_currency_code,
                  original_amount_tx, original_amount_base,
                  open_amount_tx, open_amount_base,
                  status, due_date
                )
                values (
                  @id, @company_id, @vendor_id, 'bill', @source_id,
                  'credit',
                  'USD', 'USD',
                  100, 100,
                  100, 100,
                  'open', @due_date
                );
                """;
            openItemCommand.Parameters.AddWithValue("id", openItemId);
            openItemCommand.Parameters.AddWithValue("company_id", CompanyId.Value);
            openItemCommand.Parameters.AddWithValue("vendor_id", VendorId);
            openItemCommand.Parameters.AddWithValue("source_id", billId);
            openItemCommand.Parameters.AddWithValue("due_date", new DateOnly(2026, 5, 14));
            await openItemCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return (billId, openItemId);
    }

    private static async Task CleanupOpenItemAndBillAsync(
        PostgreSqlConnectionFactory connectionFactory,
        Guid openItemId,
        Guid billId,
        CancellationToken cancellationToken)
    {
        if (openItemId == Guid.Empty && billId == Guid.Empty)
        {
            return;
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        if (openItemId != Guid.Empty)
        {
            await using var deleteOpenItem = connection.CreateCommand();
            deleteOpenItem.Transaction = transaction;
            deleteOpenItem.CommandText = "delete from ap_open_items where id = @id;";
            deleteOpenItem.Parameters.AddWithValue("id", openItemId);
            await deleteOpenItem.ExecuteNonQueryAsync(cancellationToken);
        }

        if (billId != Guid.Empty)
        {
            await using var deleteBill = connection.CreateCommand();
            deleteBill.Transaction = transaction;
            deleteBill.CommandText = "delete from bills where id = @id;";
            deleteBill.Parameters.AddWithValue("id", billId);
            await deleteBill.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static string GetConnectionString() =>
        Environment.GetEnvironmentVariable("CITUS_ACCOUNTING_DB")
        ?? "Host=localhost;Port=5432;Database=citus_accounting;Username=postgres;Password=change-me";

    private static async Task<(Guid OpenItemId, decimal OpenAmountTx)> LoadOpenItemAsync(
        PostgreSqlConnectionFactory connectionFactory,
        Guid openItemId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select id, open_amount_tx
            from ap_open_items
            where id = @open_item_id
              and status in ('open', 'partially_applied')
              and open_amount_tx > 0
            limit 1;
            """;
        command.Parameters.AddWithValue("open_item_id", openItemId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("No AP open item found for pay bill smoke test.");
        }

        return (reader.GetGuid(0), reader.GetDecimal(1));
    }

    private static async Task<Guid> CreateBankAccountAsync(
        PostgreSqlConnectionFactory connectionFactory,
        CompanyId companyId,
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
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("entity_number", entityNumber);
        command.Parameters.AddWithValue("code", $"BANK-{entityNumber[^6..]}");
        command.Parameters.AddWithValue("name", "Smoke Test Bank");
        await command.ExecuteNonQueryAsync(cancellationToken);
        return accountId;
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
        if (existing is string userIdString && UserId.TryParse(userIdString, out var userId))
        {
            return (userId, false);
        }

        var newUserId = UserId.FromOrdinal(1);
        await using var insertCommand = connection.CreateCommand();
        insertCommand.CommandText =
            """
            insert into users (
              id,
              email,
              username,
              password_hash,
              status
            )
            values (
              @id,
              @email,
              @username,
              @password_hash,
              'active'
            );
            """;
        insertCommand.Parameters.AddWithValue("id", newUserId.Value);
        insertCommand.Parameters.AddWithValue("email", $"smoke-{newUserId.Value}@citus.local");
        insertCommand.Parameters.AddWithValue("username", $"smoke-{newUserId.Value}");
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
            var seed = Random.Shared.Next(0, 60_466_176);
            var candidate = EntityNumber.Create(year, seed).Value;
            if (!await EntityNumberExistsAsync(connectionFactory, candidate, cancellationToken))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not reserve a unique entity number for AP smoke test.");
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

    private static async Task<bool> ReadVendorLockAsync(
        PostgreSqlConnectionFactory connectionFactory,
        Guid vendorId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
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

    private static async Task RestoreVendorLockAsync(
        PostgreSqlConnectionFactory connectionFactory,
        Guid vendorId,
        bool currencyLocked,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            update vendors
            set currency_locked = @currency_locked,
                updated_at = now()
            where id = @vendor_id;
            """;
        command.Parameters.AddWithValue("currency_locked", currencyLocked);
        command.Parameters.AddWithValue("vendor_id", vendorId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> EnsureVendorAsync(
        PostgreSqlConnectionFactory connectionFactory,
        Guid vendorId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using (var findCommand = connection.CreateCommand())
        {
            findCommand.CommandText =
                """
                select 1
                from vendors
                where company_id = @company_id
                  and id = @vendor_id
                limit 1;
                """;
            findCommand.Parameters.AddWithValue("company_id", CompanyId.Value);
            findCommand.Parameters.AddWithValue("vendor_id", vendorId);
            if (await findCommand.ExecuteScalarAsync(cancellationToken) is not null)
            {
                return false;
            }
        }

        await using var insertCommand = connection.CreateCommand();
        insertCommand.CommandText =
            """
            insert into vendors (
              id,
              company_id,
              entity_number,
              display_name,
              default_currency_code,
              is_active,
              currency_locked
            )
            values (
              @id,
              @company_id,
              @entity_number,
              @display_name,
              'USD',
              true,
              false
            );
            """;
        insertCommand.Parameters.AddWithValue("id", vendorId);
        insertCommand.Parameters.AddWithValue("company_id", CompanyId.Value);
        insertCommand.Parameters.AddWithValue("entity_number", "EN2026PBVND");
        insertCommand.Parameters.AddWithValue("display_name", "Pay bill smoke vendor");
        await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    private static async Task CleanupVendorAsync(
        PostgreSqlConnectionFactory connectionFactory,
        Guid vendorId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            delete from vendors
            where id = @vendor_id;
            """;
        command.Parameters.AddWithValue("vendor_id", vendorId);
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
                delete from pay_bill_lines
                where pay_bill_id = @document_id;
                """;
            lineCommand.Parameters.AddWithValue("document_id", documentId);
            await lineCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var headerCommand = connection.CreateCommand())
        {
            headerCommand.Transaction = transaction;
            headerCommand.CommandText =
                """
                delete from pay_bills
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
        UserId userId,
        bool created,
        CancellationToken cancellationToken)
    {
        if (!created || userId.Value is null)
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
            CompanyId companyId,
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
            CompanyId companyId,
            Guid snapshotId,
            CancellationToken cancellationToken) => Task.FromResult<FxSnapshotRecord?>(null);

        public Task<FxMarketRateRecord?> FindMarketRateByIdAsync(
            Guid marketRateId,
            CancellationToken cancellationToken) => Task.FromResult<FxMarketRateRecord?>(null);

        public Task<FxSnapshotRecord?> FindLatestCompanySnapshotAsync(
            CompanyId companyId,
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
            CompanyId companyId,
            UserId? createdByUserId,
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
            CompanyId companyId,
            UserId? createdByUserId,
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
