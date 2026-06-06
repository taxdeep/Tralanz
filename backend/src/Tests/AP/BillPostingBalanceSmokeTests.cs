using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Posting;
using Citus.Accounting.Infrastructure.Fx;
using Citus.Accounting.Infrastructure.Persistence;
using Citus.Accounting.Infrastructure.Posting;
using Citus.Modules.SalesTax.Application;
using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.SalesTax;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Tests.AP;

/// <summary>
/// P-20 — accounting-truth coverage for the bill POST path, the AP
/// mirror of <c>InvoicePostingBalanceSmokeTests</c>. A real
/// <see cref="BillDocument"/> is driven through the production
/// <see cref="DefaultPostingEngine"/> (wired as Program.cs wires it)
/// inside a <see cref="PostgresUnitOfWork"/> — the same Step 1
/// mechanism PostBillCommandHandler uses — and the persisted
/// journal_entry_lines are read straight back to assert the entry
/// balances on BOTH axes and hits the expected accounts: AP control
/// credited the gross, expense debited the net, and the recoverable
/// (ITC) purchase-tax leg debited from the tax code component's
/// recoverable account. SalesTaxV2 is enabled so the engine computes
/// GST 5% and the fragment builder emits the per-snapshot ITC leg.
/// </summary>
public sealed class BillPostingBalanceSmokeTests
{
    private static readonly CompanyId CompanyId = CompanyId.FromOrdinal(1);
    private static readonly Guid VendorId = Guid.Parse("96000000-0000-0000-0000-000000000001");

    [SkippableFact]
    public async Task PostBill_WithRecoverableSalesTaxV2_WritesBalancedJournalEntryHittingApExpenseAndRecoverableTax()
    {
        var connectionString = GetConnectionString();
        var connectionFactory = new PostgresConnectionFactory(connectionString);
        var executionContextAccessor = new PostgresExecutionContextAccessor();

        var infrastructureConnectionFactory = new PostgreSqlConnectionFactory(connectionString);
        var salesTaxEngine = new SalesTaxEngine(new PostgreSqlSalesTaxCatalogReader(infrastructureConnectionFactory));
        var snapshotPersister = new PostgreSqlTaxSnapshotPersister(infrastructureConnectionFactory);

        var billRepository = new PostgresBillDocumentRepository(
            connectionFactory,
            executionContextAccessor,
            salesTaxEngine,
            snapshotPersister,
            Options.Create(new SalesTaxV2Options { Enabled = true }));

        var (postingEngine, unitOfWork) = BuildPostingEngine(connectionFactory, executionContextAccessor);

        Guid expenseAccountId = default;
        Guid recoverableTaxAccountId = default;
        UserId userId = default;
        Guid billId = Guid.Empty;
        Guid legacyTaxCodeId = Guid.Empty;
        Guid salesTaxCodeId = Guid.Empty;
        Guid journalEntryId = Guid.Empty;
        var createdUser = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionFactory, CancellationToken.None);
            expenseAccountId = await CreateAccountAsync(
                connectionFactory, CompanyId, "expense", "expense", "EXP", "Smoke Expense (P-20)", CancellationToken.None);
            recoverableTaxAccountId = await CreateAccountAsync(
                connectionFactory, CompanyId, "asset", "input_tax_credit", "ITC", "Smoke Recoverable Tax (P-20)", CancellationToken.None);
            (legacyTaxCodeId, salesTaxCodeId) = await CreateGstFivePercentTaxCodeAsync(
                connectionFactory, CompanyId, recoverableTaxAccountId, CancellationToken.None);

            // $100 fully-recoverable taxable purchase; engine computes GST 5%
            // = $5.00 recoverable. Operator tax 0 — engine is the authority.
            var saveResult = await billRepository.SaveDraftAsync(
                new BillDraftSaveModel(
                    null,
                    CompanyId,
                    userId,
                    VendorId,
                    new DateOnly(2026, 4, 14),
                    new DateOnly(2026, 5, 14),
                    "USD",
                    "USD",
                    null,
                    null,
                    null,
                    null,
                    "P-20 bill post balance",
                    [new BillDraftLineSaveModel(1, expenseAccountId, "Taxable purchase", 100m, legacyTaxCodeId, 0m, IsTaxRecoverable: true)]),
                CancellationToken.None);
            billId = saveResult.DocumentId;

            // Re-load to learn the routed AP control account.
            var document = await billRepository.GetForPostingAsync(CompanyId, billId, CancellationToken.None);
            Assert.NotNull(document);
            Assert.Equal(105.00m, document!.TotalAmount);
            Assert.Equal(5.00m, document.TaxAmount);
            var payableAccountId = document.PayableAccountId;

            var postingResult = await unitOfWork.ExecuteAsync(
                ct => postingEngine.PostAsync(
                    document,
                    new PostingContext(
                        CompanyId,
                        userId,
                        document.BaseCurrencyCode,
                        AcceptedFxSnapshotId: null,
                        $"p20-bill:{billId:N}",
                        DateTimeOffset.UtcNow),
                    ct),
                CancellationToken.None);

            journalEntryId = postingResult.JournalEntryId;
            Assert.Equal("posted", postingResult.Status);

            var lines = await ReadJournalLinesAsync(connectionString, CompanyId, journalEntryId, CancellationToken.None);

            // --- Core accounting truth: the JE balances on BOTH axes. ---
            Assert.Equal(lines.Sum(l => l.Debit), lines.Sum(l => l.Credit));
            Assert.Equal(lines.Sum(l => l.TxDebit), lines.Sum(l => l.TxCredit));
            Assert.Equal(105.00m, lines.Sum(l => l.Debit));
            Assert.Equal(105.00m, lines.Sum(l => l.Credit));

            // --- The expected accounts/roles were hit. ---
            // AP control credited the gross 105.
            var apLine = Assert.Single(lines, l => l.AccountId == payableAccountId);
            Assert.Equal("accounts_payable", apLine.ControlRole);
            Assert.Equal(0m, apLine.Debit);
            Assert.Equal(105.00m, apLine.Credit);

            // Expense debited the net 100 (tax is fully recoverable, so none
            // of it folds into the expense).
            var expenseLine = Assert.Single(lines, l => l.AccountId == expenseAccountId);
            Assert.Equal(100.00m, expenseLine.Debit);
            Assert.Equal(0m, expenseLine.Credit);

            // Recoverable purchase tax (ITC) debited the engine-computed 5,
            // stamped with the purchase_tax_recoverable component type.
            var recoverableLine = Assert.Single(lines, l => l.AccountId == recoverableTaxAccountId);
            Assert.Equal("purchase_tax_recoverable", recoverableLine.TaxComponentType);
            Assert.Equal(5.00m, recoverableLine.Debit);
            Assert.Equal(0m, recoverableLine.Credit);
        }
        finally
        {
            await CleanupJournalEntryAsync(connectionFactory, journalEntryId, CancellationToken.None);
            await DeleteSnapshotsAsync(connectionFactory, "bill", billId, CancellationToken.None);
            await CleanupDraftAsync(connectionFactory, "bill_lines", "bill_id", "bills", billId, CancellationToken.None);
            await DeleteSalesTaxCodeAsync(connectionFactory, salesTaxCodeId, CancellationToken.None);
            await DeleteLegacyTaxCodeAsync(connectionFactory, legacyTaxCodeId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, expenseAccountId, CancellationToken.None);
            await CleanupAccountAsync(connectionFactory, recoverableTaxAccountId, CancellationToken.None);
            await CleanupUserAsync(connectionFactory, userId, createdUser, CancellationToken.None);
        }
    }

    // Wire the posting engine identically to Program.cs (lines 323-338).
    private static (IPostingEngine Engine, IUnitOfWork UnitOfWork) BuildPostingEngine(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        var ambient = new SharedKernel.Persistence.AmbientDatabaseTransactionAccessor();
        var fxResolution = new LocalFirstFxResolutionService(
            new PostgresFxSnapshotRepository(connections, executionContextAccessor));
        var engine = new DefaultPostingEngine(
            new DefaultPostingValidator(),
            new PostgresPostingPeriodPolicyValidator(connections, executionContextAccessor),
            new NullTaxEngine(),
            fxResolution,
            new AccountingPostingFragmentBuilder(),
            new DefaultJournalAggregator(),
            new PostgresJournalEntryWriter(connections, executionContextAccessor));
        var unitOfWork = new PostgresUnitOfWork(connections, executionContextAccessor, ambient);
        return (engine, unitOfWork);
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("CITUS_POSTGRESQL_INTEGRATION_TEST_DB");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "DB-backed test skipped: set CITUS_POSTGRESQL_INTEGRATION_TEST_DB to a dedicated test database to run it.");

        return connectionString!;
    }

    private static async Task<IReadOnlyList<JournalLine>> ReadJournalLinesAsync(
        string connectionString,
        CompanyId companyId,
        Guid journalEntryId,
        CancellationToken cancellationToken)
    {
        var rows = new List<JournalLine>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select account_id, tx_debit, tx_credit, debit, credit, control_role, tax_component_type
              from journal_entry_lines
             where company_id = @company_id
               and journal_entry_id = @journal_entry_id
             order by line_number;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("journal_entry_id", journalEntryId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new JournalLine(
                reader.GetGuid(0),
                reader.GetDecimal(1),
                reader.GetDecimal(2),
                reader.GetDecimal(3),
                reader.GetDecimal(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return rows;
    }

    private static async Task<(UserId UserId, bool Created)> GetOrCreateUserAsync(
        PostgresConnectionFactory connectionFactory,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var findCommand = connection.CreateCommand();
        findCommand.CommandText = "select id from users order by created_at limit 1;";
        var existing = await findCommand.ExecuteScalarAsync(cancellationToken);
        if (existing is string userIdString && UserId.TryParse(userIdString, out var userId))
        {
            return (userId, false);
        }

        var newUserId = UserId.FromOrdinal(1);
        await using var insertCommand = connection.CreateCommand();
        insertCommand.CommandText =
            """
            insert into users (id, email, username, password_hash, status)
            values (@id, @email, @username, @password_hash, 'active');
            """;
        insertCommand.Parameters.AddWithValue("id", newUserId.Value);
        insertCommand.Parameters.AddWithValue("email", $"p20bill-{newUserId.Value}@tralanz.local");
        insertCommand.Parameters.AddWithValue("username", $"p20bill-{newUserId.Value}");
        insertCommand.Parameters.AddWithValue("password_hash", "smoke-hash");
        await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        return (newUserId, true);
    }

    private static async Task<Guid> CreateAccountAsync(
        PostgresConnectionFactory connectionFactory,
        CompanyId companyId,
        string rootType,
        string detailType,
        string codePrefix,
        string name,
        CancellationToken cancellationToken)
    {
        var accountId = Guid.NewGuid();
        var entityNumber = await ReserveEntityNumberAsync(connectionFactory, cancellationToken);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into accounts (
              id, company_id, entity_number, code, name,
              root_type, detail_type,
              is_active, is_system, is_system_default,
              allow_manual_posting, created_at, updated_at)
            values (
              @id, @company_id, @entity_number, @code, @name,
              @root_type, @detail_type,
              true, false, false,
              true, now(), now());
            """;
        command.Parameters.AddWithValue("id", accountId);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("entity_number", entityNumber);
        command.Parameters.AddWithValue("code", $"{codePrefix}-{entityNumber[^6..]}");
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("root_type", rootType);
        command.Parameters.AddWithValue("detail_type", detailType);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return accountId;
    }

    // Single-component GST 5% tax code (legacy + v2), full recoverable on the
    // purchase side. The component carries the recoverable (ITC) GL account so
    // the posting fragment builder can route the per-snapshot ITC leg. CA GST
    // jurisdiction comes from the S1 catalog seed.
    private static async Task<(Guid LegacyTaxCodeId, Guid SalesTaxCodeId)> CreateGstFivePercentTaxCodeAsync(
        PostgresConnectionFactory connectionFactory,
        CompanyId companyId,
        Guid recoverableAccountId,
        CancellationToken cancellationToken)
    {
        var legacyTaxCodeId = Guid.NewGuid();
        var salesTaxCodeId = Guid.NewGuid();
        var componentId = Guid.NewGuid();
        var rateId = Guid.NewGuid();
        var entityNumber = await ReserveEntityNumberAsync(connectionFactory, cancellationToken);
        var suffix = entityNumber[^5..];

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        Guid jurisdictionId;
        await using (var jurisdictionCommand = connection.CreateCommand())
        {
            jurisdictionCommand.CommandText =
                """
                select id
                from sales_tax_jurisdictions
                where country_code = 'CA' and regime_type = 'gst'
                order by created_at
                limit 1;
                """;
            var resolved = await jurisdictionCommand.ExecuteScalarAsync(cancellationToken);
            jurisdictionId = resolved is Guid g
                ? g
                : throw new InvalidOperationException("S1 catalog seed missing: no CA GST jurisdiction found.");
        }

        await using (var legacyCommand = connection.CreateCommand())
        {
            legacyCommand.CommandText =
                """
                insert into tax_codes (
                  id, company_id, entity_number, code, name, rate_percent,
                  applies_to, is_active, created_at, updated_at)
                values (
                  @id, @company_id, @entity_number, @code, @name, 5,
                  'both', true, now(), now());
                """;
            legacyCommand.Parameters.AddWithValue("id", legacyTaxCodeId);
            legacyCommand.Parameters.AddWithValue("company_id", companyId.Value);
            legacyCommand.Parameters.AddWithValue("entity_number", entityNumber);
            legacyCommand.Parameters.AddWithValue("code", $"GST5-{suffix}");
            legacyCommand.Parameters.AddWithValue("name", "GST 5% (P-20)");
            await legacyCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var codeCommand = connection.CreateCommand())
        {
            codeCommand.CommandText =
                """
                insert into sales_tax_codes (
                  id, company_id, code, name, treatment, applies_to,
                  is_active, legacy_tax_code_id, created_at, updated_at)
                values (
                  @id, @company_id, @code, @name, 'taxable', 'both',
                  true, @legacy_id, now(), now());
                """;
            codeCommand.Parameters.AddWithValue("id", salesTaxCodeId);
            codeCommand.Parameters.AddWithValue("company_id", companyId.Value);
            codeCommand.Parameters.AddWithValue("code", $"GST5V2-{suffix}");
            codeCommand.Parameters.AddWithValue("name", "GST 5% v2 (P-20)");
            codeCommand.Parameters.AddWithValue("legacy_id", legacyTaxCodeId);
            await codeCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var componentCommand = connection.CreateCommand())
        {
            componentCommand.CommandText =
                """
                insert into sales_tax_code_components (
                  id, company_id, tax_code_id, jurisdiction_id, sequence,
                  is_compound, recoverability_mode,
                  recoverable_account_id,
                  created_at, updated_at)
                values (
                  @id, @company_id, @tax_code_id, @jurisdiction_id, 1,
                  false, 'full',
                  @recoverable_account_id,
                  now(), now());
                """;
            componentCommand.Parameters.AddWithValue("id", componentId);
            componentCommand.Parameters.AddWithValue("company_id", companyId.Value);
            componentCommand.Parameters.AddWithValue("tax_code_id", salesTaxCodeId);
            componentCommand.Parameters.AddWithValue("jurisdiction_id", jurisdictionId);
            componentCommand.Parameters.AddWithValue("recoverable_account_id", recoverableAccountId);
            await componentCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var rateCommand = connection.CreateCommand())
        {
            rateCommand.CommandText =
                """
                insert into sales_tax_code_component_rates (
                  id, component_id, rate_percent, effective_from, created_at)
                values (
                  @id, @component_id, 5, date '2000-01-01', now());
                """;
            rateCommand.Parameters.AddWithValue("id", rateId);
            rateCommand.Parameters.AddWithValue("component_id", componentId);
            await rateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        return (legacyTaxCodeId, salesTaxCodeId);
    }

    private static async Task<string> ReserveEntityNumberAsync(
        PostgresConnectionFactory connectionFactory,
        CancellationToken cancellationToken)
    {
        var year = DateTime.UtcNow.Year;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var seed = Random.Shared.Next(0, 60_466_176);
            var candidate = EntityNumber.Create(year, seed).Value;
            if (!await EntityNumberExistsAsync(connectionFactory, candidate, cancellationToken))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not reserve a unique entity number for bill posting smoke test.");
    }

    private static async Task<bool> EntityNumberExistsAsync(
        PostgresConnectionFactory connectionFactory,
        string entityNumber,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            with all_entities as (
              select entity_number from journal_entries
              union all
              select entity_number from invoices
              union all
              select entity_number from bills
              union all
              select entity_number from tax_codes
              union all
              select entity_number from accounts
            )
            select 1 from all_entities where entity_number = @entity_number limit 1;
            """;
        command.Parameters.AddWithValue("entity_number", entityNumber);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task CleanupJournalEntryAsync(
        PostgresConnectionFactory connectionFactory,
        Guid journalEntryId,
        CancellationToken cancellationToken)
    {
        if (journalEntryId == Guid.Empty)
        {
            return;
        }

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        foreach (var sql in new[]
                 {
                     "delete from ledger_entries where journal_entry_id = @id;",
                     "delete from journal_entry_lines where journal_entry_id = @id;",
                     "delete from journal_entries where id = @id;",
                 })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("id", journalEntryId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task CleanupDraftAsync(
        PostgresConnectionFactory connectionFactory,
        string lineTable,
        string lineForeignKey,
        string headerTable,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        if (documentId == Guid.Empty)
        {
            return;
        }

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var lineCommand = connection.CreateCommand();
        lineCommand.CommandText = $"delete from {lineTable} where {lineForeignKey} = @document_id;";
        lineCommand.Parameters.AddWithValue("document_id", documentId);
        await lineCommand.ExecuteNonQueryAsync(cancellationToken);

        await using var headerCommand = connection.CreateCommand();
        headerCommand.CommandText = $"delete from {headerTable} where id = @document_id;";
        headerCommand.Parameters.AddWithValue("document_id", documentId);
        await headerCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteSnapshotsAsync(
        PostgresConnectionFactory connectionFactory,
        string documentType,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        if (documentId == Guid.Empty)
        {
            return;
        }

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "delete from document_line_sales_tax_snapshots where document_type = @document_type and document_id = @document_id;";
        command.Parameters.AddWithValue("document_type", documentType);
        command.Parameters.AddWithValue("document_id", documentId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteSalesTaxCodeAsync(
        PostgresConnectionFactory connectionFactory,
        Guid salesTaxCodeId,
        CancellationToken cancellationToken)
    {
        if (salesTaxCodeId == Guid.Empty)
        {
            return;
        }

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "delete from sales_tax_codes where id = @id;";
        command.Parameters.AddWithValue("id", salesTaxCodeId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteLegacyTaxCodeAsync(
        PostgresConnectionFactory connectionFactory,
        Guid legacyTaxCodeId,
        CancellationToken cancellationToken)
    {
        if (legacyTaxCodeId == Guid.Empty)
        {
            return;
        }

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "delete from tax_codes where id = @id;";
        command.Parameters.AddWithValue("id", legacyTaxCodeId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CleanupAccountAsync(
        PostgresConnectionFactory connectionFactory,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        if (accountId == Guid.Empty)
        {
            return;
        }

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "delete from accounts where id = @account_id;";
        command.Parameters.AddWithValue("account_id", accountId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CleanupUserAsync(
        PostgresConnectionFactory connectionFactory,
        UserId userId,
        bool createdUser,
        CancellationToken cancellationToken)
    {
        if (!createdUser || userId.Value is null)
        {
            return;
        }

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "delete from users where id = @user_id;";
        command.Parameters.AddWithValue("user_id", userId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record JournalLine(
        Guid AccountId,
        decimal TxDebit,
        decimal TxCredit,
        decimal Debit,
        decimal Credit,
        string? ControlRole,
        string? TaxComponentType);
}
