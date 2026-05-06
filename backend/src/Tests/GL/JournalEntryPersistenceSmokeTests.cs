using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Posting;
using Citus.Accounting.Infrastructure.Persistence;
using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.GL;
using Infrastructure.PostgreSQL.Numbering;
using Infrastructure.PostgreSQL.FX;
using Engines.FX.FxRateLookup;
using Modules.GL.JournalEntry;
using Npgsql;
using Infrastructure.PostgreSQL.Company;
using AccountingJournalEntryDraft = Citus.Accounting.Domain.Journal.JournalEntryDraft;
using AccountingJournalEntryDraftLine = Citus.Accounting.Domain.Journal.JournalEntryDraftLine;

namespace Tests.GL;

public sealed class JournalEntryPersistenceSmokeTests
{
    private static readonly CompanyId CompanyId = CompanyId.FromOrdinal(1);
    private static readonly UserId UserId = UserId.FromOrdinal(1);

    [Fact]
    public async Task SaveDraftAndPost_PersistsManualJournalAndLedgerTruth()
    {
        var connectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var accountCatalog = new PostgreSqlJournalEntryAccountCatalog(connectionFactory);
        var draftStore = new PostgreSqlJournalEntryDraftStore(connectionFactory);
        var numberLookup = new PostgreSqlJournalEntryNumberLookup(connectionFactory);
        var postingStore = new PostgreSqlJournalEntryPostingStore(connectionFactory, numberLookup);
        var fxSelectionService = new FxRateSelectionService(new PostgreSqlFxRateStore(connectionFactory));
        var companyCurrencyCatalog = new PostgreSqlCompanyCurrencyProvisioningStore(connectionFactory);
        var workflow = new JournalEntryWorkflow(accountCatalog, draftStore, postingStore, fxSelectionService, companyCurrencyCatalog);
        var companyProfile = await companyCurrencyCatalog.GetProfileAsync(CompanyId, CancellationToken.None);

        Guid? documentId = null;
        Guid? journalEntryId = null;

        try
        {
            var accounts = await accountCatalog.ListManualPostingAccountsAsync(CompanyId, CancellationToken.None);
            Assert.True(accounts.Count >= 2, "Expected at least two manual-posting accounts in the demo company.");

            var draft = JournalEntryEditorState.CreateDarkModeDemo().Draft;
            draft.CompanyId = CompanyId;
            draft.JournalDate = new DateOnly(2026, 4, 13);
            draft.CurrencyCode = companyProfile.BaseCurrencyCode;
            draft.BaseCurrencyCode = companyProfile.BaseCurrencyCode;
            draft.FxRate = 1m;
            draft.FxEffectiveDate = draft.JournalDate;
            draft.FxSourceSemantics = "identity";
            draft.Memo = $"Smoke test {Guid.NewGuid():N}";
            draft.Lines[0].Account = accounts[0];
            draft.Lines[0].DebitAmount = 100m;
            draft.Lines[0].Description = "Smoke debit";
            draft.Lines[1].Account = accounts[1];
            draft.Lines[1].CreditAmount = 100m;
            draft.Lines[1].Description = "Smoke credit";

            var saved = await workflow.SaveDraftAsync(draft, UserId, CancellationToken.None);
            documentId = saved.DocumentId;

            Assert.StartsWith("MJ-", saved.DocumentNumber, StringComparison.Ordinal);
            Assert.Equal("draft", saved.Status);

            var posted = await workflow.PostDraftAsync(draft, UserId, CancellationToken.None);
            journalEntryId = posted.JournalEntryId;

            Assert.StartsWith("JE-", posted.JournalDisplayNumber, StringComparison.Ordinal);
            Assert.Equal("posted", draft.Status);

            var reposted = await workflow.PostDraftAsync(draft, UserId, CancellationToken.None);
            Assert.Equal(posted.JournalEntryId, reposted.JournalEntryId);
            Assert.Equal(posted.JournalDisplayNumber, reposted.JournalDisplayNumber);
            Assert.Equal(saved.DocumentNumber, reposted.DocumentNumber);

            await using var connection = await connectionFactory.OpenAsync(CancellationToken.None);

            var documentStatus = await ReadSingleStringAsync(
                connection,
                """
                select status
                from manual_journal_documents
                where id = @id;
                """,
                documentId.Value,
                CancellationToken.None);
            Assert.Equal("posted", documentStatus);

            var sourceLinkCount = await ReadSingleIntAsync(
                connection,
                """
                select count(*)
                from journal_entries
                where id = @id
                  and source_type = 'manual_journal'
                  and source_id = @source_id
                  and status = 'posted';
                """,
                journalEntryId.Value,
                documentId.Value,
                CancellationToken.None);
            Assert.Equal(1, sourceLinkCount);

            var duplicatePostedJournalCount = await ReadSingleIntByCompanyAndSourceAsync(
                connection,
                """
                select count(*)
                from journal_entries
                where company_id = @company_id
                  and source_type = 'manual_journal'
                  and source_id = @source_id
                  and status = 'posted';
                """,
                CompanyId,
                documentId.Value,
                CancellationToken.None);
            Assert.Equal(1, duplicatePostedJournalCount);

            var lineCount = await ReadCountByIdAsync(
                connection,
                "select count(*) from journal_entry_lines where journal_entry_id = @id;",
                journalEntryId.Value,
                CancellationToken.None);
            Assert.Equal(2, lineCount);

            var ledgerCount = await ReadCountByIdAsync(
                connection,
                "select count(*) from ledger_entries where journal_entry_id = @id;",
                journalEntryId.Value,
                CancellationToken.None);
            Assert.Equal(2, ledgerCount);
        }
        finally
        {
            if (documentId.HasValue || journalEntryId.HasValue)
            {
                await CleanupAsync(connectionFactory, documentId, journalEntryId, CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task WriteAsync_RejectsNewPostingLockedByPrimaryBookClosedPeriod()
    {
        var setupConnectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        if (!await BookGovernanceTablesExistAsync(setupConnectionFactory, CancellationToken.None))
        {
            return;
        }

        var companyId = CompanyId.FromOrdinal(1);
        var bookId = Guid.NewGuid();
        var signalId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var postingDate = new DateOnly(2026, 4, 14);
        var closedThrough = new DateOnly(2026, 4, 30);

        try
        {
            await SeedClosedPrimaryBookAsync(
                setupConnectionFactory,
                companyId,
                bookId,
                signalId,
                closedThrough,
                CancellationToken.None);

            var accountingConnectionFactory = new PostgresConnectionFactory(GetConnectionString());
            var usd = new CurrencyCode("USD");
            var draft = new AccountingJournalEntryDraft(
                CompanyId.Parse(companyId.ToString()),
                "invoice",
                sourceId,
                usd,
                usd,
                new FxSnapshotRef(Guid.NewGuid(), usd, usd, 1m, postingDate, postingDate, "identity"),
                [
                    new AccountingJournalEntryDraftLine(1, Guid.NewGuid(), "Closed period debit", 100m, 0m, 100m, 0m),
                    new AccountingJournalEntryDraftLine(2, Guid.NewGuid(), "Closed period credit", 0m, 100m, 0m, 100m)
                ]);
            var writer = new PostgresJournalEntryWriter(
                accountingConnectionFactory,
                new PostgresExecutionContextAccessor());

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                writer.WriteAsync(
                    draft,
                    new PostingContext(
                        CompanyId.Parse(companyId.ToString()),
                        UserId.FromOrdinal(1),
                        usd,
                        null,
                        $"closed-period:{sourceId:N}",
                        new DateTimeOffset(2026, 4, 14, 12, 0, 0, TimeSpan.Zero)),
                    CancellationToken.None));

            Assert.Contains("locked", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("2026-04-30", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupClosedPrimaryBookAsync(
                setupConnectionFactory,
                companyId,
                bookId,
                signalId,
                CancellationToken.None);
        }
    }

    private static string GetConnectionString() =>
        Environment.GetEnvironmentVariable("CITUS_ACCOUNTING_DB")
        ?? "Host=localhost;Port=5432;Database=citus_accounting;Username=postgres;Password=change-me";

    private static async Task CleanupAsync(
        PostgreSqlConnectionFactory connectionFactory,
        Guid? documentId,
        Guid? journalEntryId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        if (journalEntryId.HasValue)
        {
            await ExecuteDeleteAsync(connection, transaction, "delete from ledger_entries where journal_entry_id = @id;", journalEntryId.Value, cancellationToken);
            await ExecuteDeleteAsync(connection, transaction, "delete from journal_entry_lines where journal_entry_id = @id;", journalEntryId.Value, cancellationToken);
            await ExecuteDeleteAsync(connection, transaction, "delete from journal_entries where id = @id;", journalEntryId.Value, cancellationToken);
        }

        if (documentId.HasValue)
        {
            await ExecuteDeleteAsync(connection, transaction, "delete from manual_journal_document_lines where manual_journal_document_id = @id;", documentId.Value, cancellationToken);
            await ExecuteDeleteAsync(connection, transaction, "delete from manual_journal_documents where id = @id;", documentId.Value, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<bool> BookGovernanceTablesExistAsync(
        PostgreSqlConnectionFactory connectionFactory,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select to_regclass('public.company_books') is not null
               and to_regclass('public.company_book_governance_signals') is not null;
            """;
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static async Task SeedClosedPrimaryBookAsync(
        PostgreSqlConnectionFactory connectionFactory,
        CompanyId companyId,
        Guid bookId,
        Guid signalId,
        DateOnly closedThrough,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var currencyCommand = connection.CreateCommand())
        {
            currencyCommand.Transaction = transaction;
            currencyCommand.CommandText =
                """
                insert into currency_catalog (code, name, minor_unit, is_active)
                values ('USD', 'US Dollar', 2, true)
                on conflict (code) do nothing;
                """;
            await currencyCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var companyCommand = connection.CreateCommand())
        {
            companyCommand.Transaction = transaction;
            companyCommand.CommandText =
                """
                insert into companies (
                  id,
                  entity_number,
                  legal_name,
                  base_currency_code,
                  multi_currency_enabled,
                  status
                )
                values (
                  @id,
                  @entity_number,
                  'Closed Period Writer Test',
                  'USD',
                  true,
                  'active'
                );
                """;
            companyCommand.Parameters.AddWithValue("id", companyId);
            companyCommand.Parameters.AddWithValue("entity_number", $"EN{DateTime.UtcNow:yyyy}{Random.Shared.Next(1, 999999):D6}");
            await companyCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var bookCommand = connection.CreateCommand())
        {
            bookCommand.Transaction = transaction;
            bookCommand.CommandText =
                """
                insert into company_books (
                  id,
                  company_id,
                  book_code,
                  book_name,
                  book_role,
                  accounting_standard,
                  book_base_currency_code,
                  functional_currency_code,
                  presentation_currency_code,
                  is_primary,
                  is_adjustment_only,
                  effective_from,
                  is_active
                )
                values (
                  @id,
                  @company_id,
                  'PRIMARY',
                  'Primary Book',
                  'primary',
                  'ASPE',
                  'USD',
                  'USD',
                  'USD',
                  true,
                  false,
                  '2026-01-01',
                  true
                );
                """;
            bookCommand.Parameters.AddWithValue("id", bookId);
            bookCommand.Parameters.AddWithValue("company_id", companyId.Value);
            await bookCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var signalCommand = connection.CreateCommand())
        {
            signalCommand.Transaction = transaction;
            signalCommand.CommandText =
                """
                insert into company_book_governance_signals (
                  id,
                  company_id,
                  company_book_id,
                  signal_type,
                  signal_date,
                  reference_label,
                  notes,
                  created_by_user_id
                )
                values (
                  @id,
                  @company_id,
                  @company_book_id,
                  'closed_period',
                  @signal_date,
                  'April close',
                  'Writer guard smoke test',
                  null
                );
                """;
            signalCommand.Parameters.AddWithValue("id", signalId);
            signalCommand.Parameters.AddWithValue("company_id", companyId.Value);
            signalCommand.Parameters.AddWithValue("company_book_id", bookId);
            signalCommand.Parameters.AddWithValue("signal_date", closedThrough);
            await signalCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task CleanupClosedPrimaryBookAsync(
        PostgreSqlConnectionFactory connectionFactory,
        CompanyId companyId,
        Guid bookId,
        Guid signalId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await ExecuteDeleteAsync(
            connection,
            transaction,
            "delete from company_book_governance_signals where id = @id;",
            signalId,
            cancellationToken);
        await ExecuteDeleteAsync(
            connection,
            transaction,
            "delete from company_books where id = @id;",
            bookId,
            cancellationToken);
        await ExecuteDeleteAsync(
            connection,
            transaction,
            "delete from companies where id = @id;",
            companyId.Value,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task ExecuteDeleteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        object id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> ReadCountByIdAsync(
        NpgsqlConnection connection,
        string sql,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("id", id);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
    }

    private static async Task<int> ReadSingleIntAsync(
        NpgsqlConnection connection,
        string sql,
        Guid id,
        Guid sourceId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("source_id", sourceId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
    }

    private static async Task<int> ReadSingleIntByCompanyAndSourceAsync(
        NpgsqlConnection connection,
        string sql,
        CompanyId companyId,
        Guid sourceId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("source_id", sourceId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
    }

    private static async Task<string> ReadSingleStringAsync(
        NpgsqlConnection connection,
        string sql,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("id", id);
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }
}
