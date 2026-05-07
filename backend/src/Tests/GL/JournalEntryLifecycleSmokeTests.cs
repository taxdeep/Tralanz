using Engines.FX.FxRateLookup;
using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.Company;
using Infrastructure.PostgreSQL.FX;
using Infrastructure.PostgreSQL.GL;
using Infrastructure.PostgreSQL.Numbering;
using Modules.Company.MultiCurrency;
using Modules.GL.JournalEntry;
using Npgsql;

namespace Tests.GL;

public sealed class JournalEntryLifecycleSmokeTests
{
    private static readonly CompanyId CompanyId = CompanyId.FromOrdinal(1);
    private static readonly UserId UserId = UserId.FromOrdinal(1);

    [Fact]
    public async Task VoidAsync_CreatesCompensationJournalAndMarksOriginalVoided()
    {
        var fixture = await CreateFixtureAsync();

        try
        {
            var lifecycle = await fixture.LifecycleWorkflow.VoidAsync(CompanyId, fixture.Posted.JournalEntryId, UserId, CancellationToken.None);
            var originalReview = await fixture.ReviewStore.GetAsync(CompanyId, fixture.Posted.JournalEntryId, CancellationToken.None);
            var compensationReview = await fixture.ReviewStore.GetAsync(CompanyId, lifecycle.CompensationJournalEntryId, CancellationToken.None);
            var sourceReview = await fixture.SourceReviewStore.GetAsync(CompanyId, fixture.DocumentId, CancellationToken.None);

            Assert.Equal("voided", lifecycle.OriginalStatus);
            Assert.NotNull(originalReview);
            Assert.Equal("voided", originalReview!.Status);
            Assert.NotNull(originalReview.VoidedAt);
            Assert.Contains(originalReview.RelatedEntries, entry => entry.Id == lifecycle.CompensationJournalEntryId && entry.SourceType == "manual_journal_void");
            Assert.NotNull(compensationReview);
            Assert.Equal("manual_journal_void", compensationReview!.SourceType);
            Assert.Equal("posted", compensationReview.Status);
            Assert.Equal(fixture.DocumentId, compensationReview.SourceId);
            Assert.Equal("CNY", compensationReview.TransactionCurrencyCode);
            Assert.Equal(originalReview.TransactionCurrencyCode, compensationReview.TransactionCurrencyCode);
            Assert.Equal(originalReview.BaseCurrencyCode, compensationReview.BaseCurrencyCode);
            Assert.Equal(originalReview.ExchangeRateSource, compensationReview.ExchangeRateSource);
            Assert.Equal(originalReview.FxSnapshotId, compensationReview.FxSnapshotId);
            Assert.Equal(originalReview.FxRateType, compensationReview.FxRateType);
            Assert.Equal(originalReview.FxQuoteBasis, compensationReview.FxQuoteBasis);
            Assert.Equal(originalReview.FxRateUseCase, compensationReview.FxRateUseCase);
            Assert.Equal(originalReview.FxPostingReason, compensationReview.FxPostingReason);
            Assert.NotNull(sourceReview);
            Assert.Equal("voided", sourceReview!.Status);
            Assert.Equal(fixture.FxSnapshotId, sourceReview.FxSnapshotId);
            Assert.Contains(sourceReview.RelatedEntries, entry => entry.Id == lifecycle.CompensationJournalEntryId);
        }
        finally
        {
            await CleanupFixtureAsync(fixture);
        }
    }

    [Fact]
    public async Task ReverseAsync_CreatesCompensationJournalAndMarksOriginalReversed()
    {
        var fixture = await CreateFixtureAsync();

        try
        {
            var lifecycle = await fixture.LifecycleWorkflow.ReverseAsync(CompanyId, fixture.Posted.JournalEntryId, UserId, CancellationToken.None);
            var originalReview = await fixture.ReviewStore.GetAsync(CompanyId, fixture.Posted.JournalEntryId, CancellationToken.None);
            var compensationReview = await fixture.ReviewStore.GetAsync(CompanyId, lifecycle.CompensationJournalEntryId, CancellationToken.None);
            var sourceReview = await fixture.SourceReviewStore.GetAsync(CompanyId, fixture.DocumentId, CancellationToken.None);

            Assert.Equal("reversed", lifecycle.OriginalStatus);
            Assert.NotNull(originalReview);
            Assert.Equal("reversed", originalReview!.Status);
            Assert.NotNull(originalReview.ReversedAt);
            Assert.Contains(originalReview.RelatedEntries, entry => entry.Id == lifecycle.CompensationJournalEntryId && entry.SourceType == "manual_journal_reversal");
            Assert.NotNull(compensationReview);
            Assert.Equal("manual_journal_reversal", compensationReview!.SourceType);
            Assert.Equal("posted", compensationReview.Status);
            Assert.Equal(fixture.DocumentId, compensationReview.SourceId);
            Assert.Equal("CNY", compensationReview.TransactionCurrencyCode);
            Assert.Equal(originalReview.TransactionCurrencyCode, compensationReview.TransactionCurrencyCode);
            Assert.Equal(originalReview.BaseCurrencyCode, compensationReview.BaseCurrencyCode);
            Assert.Equal(originalReview.ExchangeRateSource, compensationReview.ExchangeRateSource);
            Assert.Equal(originalReview.FxSnapshotId, compensationReview.FxSnapshotId);
            Assert.Equal(originalReview.FxRateType, compensationReview.FxRateType);
            Assert.Equal(originalReview.FxQuoteBasis, compensationReview.FxQuoteBasis);
            Assert.Equal(originalReview.FxRateUseCase, compensationReview.FxRateUseCase);
            Assert.Equal(originalReview.FxPostingReason, compensationReview.FxPostingReason);
            Assert.NotNull(sourceReview);
            Assert.Equal("reversed", sourceReview!.Status);
            Assert.Equal(fixture.FxSnapshotId, sourceReview.FxSnapshotId);
            Assert.Contains(sourceReview.RelatedEntries, entry => entry.Id == lifecycle.CompensationJournalEntryId);
        }
        finally
        {
            await CleanupFixtureAsync(fixture);
        }
    }

    [Fact]
    public async Task ReverseAsync_PreservesForeignCurrencyTraceAcrossReviewAndSourceChain()
    {
        var fixture = await CreateFixtureAsync();

        try
        {
            var originalReviewBeforeReverse = await fixture.ReviewStore.GetAsync(
                CompanyId,
                fixture.Posted.JournalEntryId,
                CancellationToken.None);
            var sourceReviewBeforeReverse = await fixture.SourceReviewStore.GetAsync(
                CompanyId,
                fixture.DocumentId,
                CancellationToken.None);

            Assert.NotNull(originalReviewBeforeReverse);
            Assert.NotNull(sourceReviewBeforeReverse);
            Assert.True(originalReviewBeforeReverse!.IsForeignCurrency);
            Assert.Equal("CNY", originalReviewBeforeReverse.TransactionCurrencyCode);
            Assert.Equal(fixture.FxSnapshotId, originalReviewBeforeReverse.FxSnapshotId);
            Assert.Contains("snapshot", originalReviewBeforeReverse.FxTraceLabel, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(fixture.Posted.JournalEntryId, sourceReviewBeforeReverse!.LinkedJournalEntryId);
            Assert.Contains(
                sourceReviewBeforeReverse.RelatedEntries,
                entry => entry.Id == fixture.Posted.JournalEntryId && entry.SourceType == "manual_journal");

            var lifecycle = await fixture.LifecycleWorkflow.ReverseAsync(
                CompanyId,
                fixture.Posted.JournalEntryId,
                UserId,
                CancellationToken.None);

            var originalReviewAfterReverse = await fixture.ReviewStore.GetAsync(
                CompanyId,
                fixture.Posted.JournalEntryId,
                CancellationToken.None);
            var compensationReview = await fixture.ReviewStore.GetAsync(
                CompanyId,
                lifecycle.CompensationJournalEntryId,
                CancellationToken.None);
            var sourceReviewAfterReverse = await fixture.SourceReviewStore.GetAsync(
                CompanyId,
                fixture.DocumentId,
                CancellationToken.None);

            Assert.NotNull(originalReviewAfterReverse);
            Assert.NotNull(compensationReview);
            Assert.NotNull(sourceReviewAfterReverse);

            Assert.Equal("reversed", originalReviewAfterReverse!.Status);
            Assert.Equal(fixture.FxSnapshotId, originalReviewAfterReverse.FxSnapshotId);
            Assert.Equal(originalReviewBeforeReverse.FxTraceLabel, originalReviewAfterReverse.FxTraceLabel);
            Assert.Contains(
                originalReviewAfterReverse.RelatedEntries,
                entry => entry.Id == lifecycle.CompensationJournalEntryId && entry.SourceType == "manual_journal_reversal");

            Assert.Equal("manual_journal_reversal", compensationReview!.SourceType);
            Assert.True(compensationReview.IsForeignCurrency);
            Assert.Equal("CNY", compensationReview.TransactionCurrencyCode);
            Assert.Equal(originalReviewBeforeReverse.BaseCurrencyCode, compensationReview.BaseCurrencyCode);
            Assert.Equal(originalReviewBeforeReverse.ExchangeRate, compensationReview.ExchangeRate);
            Assert.Equal(originalReviewBeforeReverse.ExchangeRateDate, compensationReview.ExchangeRateDate);
            Assert.Equal(originalReviewBeforeReverse.ExchangeRateSource, compensationReview.ExchangeRateSource);
            Assert.Equal(originalReviewBeforeReverse.FxSnapshotId, compensationReview.FxSnapshotId);
            Assert.Equal(originalReviewBeforeReverse.FxRateType, compensationReview.FxRateType);
            Assert.Equal(originalReviewBeforeReverse.FxQuoteBasis, compensationReview.FxQuoteBasis);
            Assert.Equal(originalReviewBeforeReverse.FxRateUseCase, compensationReview.FxRateUseCase);
            Assert.Equal(originalReviewBeforeReverse.FxPostingReason, compensationReview.FxPostingReason);
            Assert.Contains("snapshot", compensationReview.FxTraceLabel, StringComparison.OrdinalIgnoreCase);

            Assert.Equal("reversed", sourceReviewAfterReverse!.Status);
            Assert.Equal(fixture.FxSnapshotId, sourceReviewAfterReverse.FxSnapshotId);
            Assert.Equal(fixture.Posted.JournalEntryId, sourceReviewAfterReverse.LinkedJournalEntryId);
            Assert.Contains(
                sourceReviewAfterReverse.RelatedEntries,
                entry => entry.Id == fixture.Posted.JournalEntryId && entry.SourceType == "manual_journal");
            Assert.Contains(
                sourceReviewAfterReverse.RelatedEntries,
                entry => entry.Id == lifecycle.CompensationJournalEntryId && entry.SourceType == "manual_journal_reversal");
            Assert.Equal(2, sourceReviewAfterReverse.RelatedEntries.Count);
        }
        finally
        {
            await CleanupFixtureAsync(fixture);
        }
    }

    [Fact]
    public async Task VoidAsync_PreservesForeignCurrencyTraceAcrossReviewAndSourceChain()
    {
        var fixture = await CreateFixtureAsync();

        try
        {
            var originalReviewBeforeVoid = await fixture.ReviewStore.GetAsync(
                CompanyId,
                fixture.Posted.JournalEntryId,
                CancellationToken.None);
            var sourceReviewBeforeVoid = await fixture.SourceReviewStore.GetAsync(
                CompanyId,
                fixture.DocumentId,
                CancellationToken.None);

            Assert.NotNull(originalReviewBeforeVoid);
            Assert.NotNull(sourceReviewBeforeVoid);
            Assert.True(originalReviewBeforeVoid!.IsForeignCurrency);
            Assert.Equal("CNY", originalReviewBeforeVoid.TransactionCurrencyCode);
            Assert.Equal(fixture.FxSnapshotId, originalReviewBeforeVoid.FxSnapshotId);
            Assert.Contains("snapshot", originalReviewBeforeVoid.FxTraceLabel, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(fixture.Posted.JournalEntryId, sourceReviewBeforeVoid!.LinkedJournalEntryId);
            Assert.Contains(
                sourceReviewBeforeVoid.RelatedEntries,
                entry => entry.Id == fixture.Posted.JournalEntryId && entry.SourceType == "manual_journal");

            var lifecycle = await fixture.LifecycleWorkflow.VoidAsync(
                CompanyId,
                fixture.Posted.JournalEntryId,
                UserId,
                CancellationToken.None);

            var originalReviewAfterVoid = await fixture.ReviewStore.GetAsync(
                CompanyId,
                fixture.Posted.JournalEntryId,
                CancellationToken.None);
            var compensationReview = await fixture.ReviewStore.GetAsync(
                CompanyId,
                lifecycle.CompensationJournalEntryId,
                CancellationToken.None);
            var sourceReviewAfterVoid = await fixture.SourceReviewStore.GetAsync(
                CompanyId,
                fixture.DocumentId,
                CancellationToken.None);

            Assert.NotNull(originalReviewAfterVoid);
            Assert.NotNull(compensationReview);
            Assert.NotNull(sourceReviewAfterVoid);

            Assert.Equal("voided", originalReviewAfterVoid!.Status);
            Assert.Equal(fixture.FxSnapshotId, originalReviewAfterVoid.FxSnapshotId);
            Assert.Equal(originalReviewBeforeVoid.FxTraceLabel, originalReviewAfterVoid.FxTraceLabel);
            Assert.Contains(
                originalReviewAfterVoid.RelatedEntries,
                entry => entry.Id == lifecycle.CompensationJournalEntryId && entry.SourceType == "manual_journal_void");

            Assert.Equal("manual_journal_void", compensationReview!.SourceType);
            Assert.True(compensationReview.IsForeignCurrency);
            Assert.Equal("CNY", compensationReview.TransactionCurrencyCode);
            Assert.Equal(originalReviewBeforeVoid.BaseCurrencyCode, compensationReview.BaseCurrencyCode);
            Assert.Equal(originalReviewBeforeVoid.ExchangeRate, compensationReview.ExchangeRate);
            Assert.Equal(originalReviewBeforeVoid.ExchangeRateDate, compensationReview.ExchangeRateDate);
            Assert.Equal(originalReviewBeforeVoid.ExchangeRateSource, compensationReview.ExchangeRateSource);
            Assert.Equal(originalReviewBeforeVoid.FxSnapshotId, compensationReview.FxSnapshotId);
            Assert.Equal(originalReviewBeforeVoid.FxRateType, compensationReview.FxRateType);
            Assert.Equal(originalReviewBeforeVoid.FxQuoteBasis, compensationReview.FxQuoteBasis);
            Assert.Equal(originalReviewBeforeVoid.FxRateUseCase, compensationReview.FxRateUseCase);
            Assert.Equal(originalReviewBeforeVoid.FxPostingReason, compensationReview.FxPostingReason);
            Assert.Contains("snapshot", compensationReview.FxTraceLabel, StringComparison.OrdinalIgnoreCase);

            Assert.Equal("voided", sourceReviewAfterVoid!.Status);
            Assert.Equal(fixture.FxSnapshotId, sourceReviewAfterVoid.FxSnapshotId);
            Assert.Equal(fixture.Posted.JournalEntryId, sourceReviewAfterVoid.LinkedJournalEntryId);
            Assert.Contains(
                sourceReviewAfterVoid.RelatedEntries,
                entry => entry.Id == fixture.Posted.JournalEntryId && entry.SourceType == "manual_journal");
            Assert.Contains(
                sourceReviewAfterVoid.RelatedEntries,
                entry => entry.Id == lifecycle.CompensationJournalEntryId && entry.SourceType == "manual_journal_void");
            Assert.Equal(2, sourceReviewAfterVoid.RelatedEntries.Count);
        }
        finally
        {
            await CleanupFixtureAsync(fixture);
        }
    }

    [Fact]
    public async Task ReverseAsync_RejectsClosedPrimaryBookPeriod()
    {
        var fixture = await CreateFixtureAsync();
        var governanceBookId = Guid.NewGuid();

        try
        {
            if (!await BookGovernanceTablesExistAsync(fixture.ConnectionFactory, CancellationToken.None))
            {
                return;
            }

            await SeedClosedPrimaryBookAsync(
                fixture.ConnectionFactory,
                CompanyId,
                governanceBookId,
                fixture.JournalDate,
                CancellationToken.None);

            var exception = await Assert.ThrowsAsync<JournalEntryLifecycleException>(() =>
                fixture.LifecycleWorkflow.ReverseAsync(CompanyId, fixture.Posted.JournalEntryId, UserId, CancellationToken.None));

            Assert.Equal("posting_period_closed", exception.ErrorCode);
        }
        finally
        {
            await CleanupClosedPrimaryBookAsync(fixture.ConnectionFactory, CompanyId, governanceBookId, CancellationToken.None);
            await CleanupFixtureAsync(fixture);
        }
    }

    private static async Task<LifecycleFixture> CreateFixtureAsync()
    {
        var connectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var accountCatalog = new PostgreSqlJournalEntryAccountCatalog(connectionFactory);
        var draftStore = new PostgreSqlJournalEntryDraftStore(connectionFactory);
        var numberLookup = new PostgreSqlJournalEntryNumberLookup(connectionFactory);
        var postingStore = new PostgreSqlJournalEntryPostingStore(connectionFactory, numberLookup);
        var lifecycleStore = new PostgreSqlJournalEntryLifecycleStore(connectionFactory, numberLookup);
        var fxSelectionService = new FxRateSelectionService(new PostgreSqlFxRateStore(connectionFactory));
        var companyCurrencyStore = new PostgreSqlCompanyCurrencyProvisioningStore(connectionFactory);
        var companyCurrencyWorkflow = new CompanyCurrencyGovernanceWorkflow(companyCurrencyStore);
        var workflow = new JournalEntryWorkflow(accountCatalog, draftStore, postingStore, fxSelectionService, companyCurrencyStore);
        var lifecycleWorkflow = new JournalEntryLifecycleWorkflow(lifecycleStore);
        var reviewStore = new PostgreSqlJournalEntryReviewStore(connectionFactory);
        var sourceReviewStore = new PostgreSqlManualJournalSourceReviewStore(connectionFactory);

        var companyCurrency = await companyCurrencyWorkflow.EnableCurrencyAsync(CompanyId, "CNY", UserId, CancellationToken.None);
        var accounts = await accountCatalog.ListManualPostingAccountsAsync(CompanyId, CancellationToken.None);
        Assert.True(accounts.Count >= 2, "Expected at least two manual-posting accounts in the demo company.");

        var journalDate = await ReserveUniqueJournalDateAsync(
            connectionFactory,
            companyCurrency.Profile.BaseCurrencyCode,
            "CNY",
            CancellationToken.None);
        var draft = JournalEntryEditorState.CreateDarkModeDemo().Draft;
        draft.CompanyId = CompanyId;
        draft.JournalDate = journalDate;
        draft.CurrencyCode = "CNY";
        draft.BaseCurrencyCode = companyCurrency.Profile.BaseCurrencyCode;
        draft.FxRate = 0.1385m;
        draft.FxEffectiveDate = draft.JournalDate;
        draft.FxSourceSemantics = "manual";
        draft.Memo = $"Lifecycle smoke {Guid.NewGuid():N}";
        draft.Lines[0].Account = accounts[0];
        draft.Lines[0].DebitAmount = 120m;
        draft.Lines[0].Description = "Lifecycle debit";
        draft.Lines[1].Account = accounts[1];
        draft.Lines[1].CreditAmount = 120m;
        draft.Lines[1].Description = "Lifecycle credit";

        var saved = await workflow.SaveDraftAsync(draft, UserId, CancellationToken.None);
        var posted = await workflow.PostDraftAsync(draft, UserId, CancellationToken.None);

        return new LifecycleFixture(
            connectionFactory,
            lifecycleWorkflow,
            reviewStore,
            sourceReviewStore,
            saved.DocumentId,
            draft.FxSnapshotId,
            journalDate,
            posted);
    }

    private static async Task CleanupFixtureAsync(LifecycleFixture fixture)
    {
        await using var connection = await fixture.ConnectionFactory.OpenAsync(CancellationToken.None);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);

        var journalEntryIds = new List<Guid>();

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                select id
                from journal_entries
                where company_id = @company_id
                  and source_id = @source_id;
                """;
            command.Parameters.AddWithValue("company_id", CompanyId.Value);
            command.Parameters.AddWithValue("source_id", fixture.DocumentId);

            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
            while (await reader.ReadAsync(CancellationToken.None))
            {
                journalEntryIds.Add(reader.GetGuid(0));
            }
        }

        foreach (var journalEntryId in journalEntryIds)
        {
            await ExecuteDeleteAsync(connection, transaction, "delete from ledger_entries where journal_entry_id = @id;", journalEntryId, CancellationToken.None);
            await ExecuteDeleteAsync(connection, transaction, "delete from journal_entry_lines where journal_entry_id = @id;", journalEntryId, CancellationToken.None);
            await ExecuteDeleteAsync(connection, transaction, "delete from journal_entries where id = @id;", journalEntryId, CancellationToken.None);
        }

        await ExecuteDeleteAsync(connection, transaction, "delete from manual_journal_document_lines where manual_journal_document_id = @id;", fixture.DocumentId, CancellationToken.None);
        await ExecuteDeleteAsync(connection, transaction, "delete from manual_journal_documents where id = @id;", fixture.DocumentId, CancellationToken.None);

        if (fixture.FxSnapshotId.HasValue)
        {
            await ExecuteDeleteAsync(connection, transaction, "delete from company_fx_rate_snapshots where id = @id;", fixture.FxSnapshotId.Value, CancellationToken.None);
        }

        await transaction.CommitAsync(CancellationToken.None);
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
        DateOnly journalDate,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var deleteSignals = connection.CreateCommand())
        {
            deleteSignals.Transaction = transaction;
            deleteSignals.CommandText =
                """
                delete from company_book_governance_signals
                where company_id = @company_id
                  and company_book_id = @book_id;
                """;
            deleteSignals.Parameters.AddWithValue("company_id", companyId.Value);
            deleteSignals.Parameters.AddWithValue("book_id", bookId);
            await deleteSignals.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteBook = connection.CreateCommand())
        {
            deleteBook.Transaction = transaction;
            // Match by (company_id, book_code='PRIMARY') as well as id, so we
            // also clean up any leftover 'PRIMARY' row that another test
            // (e.g. FxRevaluationWorkflowSmokeTests' EnsureDefaultPrimaryBook)
            // may have inserted with a different id. Without this we trip
            // company_books_unique on (company_id, book_code).
            deleteBook.CommandText =
                """
                delete from company_book_remeasurement_policies
                where company_book_id in (
                  select id from company_books
                  where company_id = @company_id
                    and (id = @book_id or book_code = 'PRIMARY')
                );

                delete from company_books
                where company_id = @company_id
                  and (id = @book_id or book_code = 'PRIMARY');
                """;
            deleteBook.Parameters.AddWithValue("company_id", companyId.Value);
            deleteBook.Parameters.AddWithValue("book_id", bookId);
            await deleteBook.ExecuteNonQueryAsync(cancellationToken);
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
                  is_active,
                  is_primary,
                  effective_from,
                  created_by_user_id,
                  created_at
                )
                values (
                  @id,
                  @company_id,
                  'PRIMARY',
                  'Primary Book',
                  'primary',
                  'IFRS',
                  'USD',
                  'USD',
                  'USD',
                  true,
                  true,
                  @effective_from,
                  @created_by_user_id,
                  now()
                );
                """;
            bookCommand.Parameters.AddWithValue("id", bookId);
            bookCommand.Parameters.AddWithValue("company_id", companyId.Value);
            bookCommand.Parameters.AddWithValue("effective_from", journalDate.AddDays(-30));
            bookCommand.Parameters.AddWithValue("created_by_user_id", UserId.Value);
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
                  created_by_user_id,
                  created_at
                )
                values (
                  @id,
                  @company_id,
                  @book_id,
                  'closed_period',
                  @signal_date,
                  'Lifecycle smoke closed period',
                  @created_by_user_id,
                  now()
                );
                """;
            signalCommand.Parameters.AddWithValue("id", Guid.NewGuid());
            signalCommand.Parameters.AddWithValue("company_id", companyId.Value);
            signalCommand.Parameters.AddWithValue("book_id", bookId);
            signalCommand.Parameters.AddWithValue("signal_date", journalDate.AddDays(5));
            signalCommand.Parameters.AddWithValue("created_by_user_id", UserId.Value);
            await signalCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task CleanupClosedPrimaryBookAsync(
        PostgreSqlConnectionFactory connectionFactory,
        CompanyId companyId,
        Guid bookId,
        CancellationToken cancellationToken)
    {
        if (!await BookGovernanceTablesExistAsync(connectionFactory, cancellationToken))
        {
            return;
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var signalCommand = connection.CreateCommand())
        {
            signalCommand.Transaction = transaction;
            signalCommand.CommandText =
                """
                delete from company_book_governance_signals
                where company_id = @company_id
                  and company_book_id = @book_id;
                """;
            signalCommand.Parameters.AddWithValue("company_id", companyId.Value);
            signalCommand.Parameters.AddWithValue("book_id", bookId);
            await signalCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var bookCommand = connection.CreateCommand())
        {
            bookCommand.Transaction = transaction;
            bookCommand.CommandText =
                """
                delete from company_books
                where company_id = @company_id
                  and id = @book_id;
                """;
            bookCommand.Parameters.AddWithValue("company_id", companyId.Value);
            bookCommand.Parameters.AddWithValue("book_id", bookId);
            await bookCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task ExecuteDeleteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string GetConnectionString() =>
        Environment.GetEnvironmentVariable("CITUS_ACCOUNTING_DB")
        ?? "Host=localhost;Port=5432;Database=citus_accounting;Username=postgres;Password=change-me";

    private static async Task<DateOnly> ReserveUniqueJournalDateAsync(
        PostgreSqlConnectionFactory connectionFactory,
        string baseCurrencyCode,
        string quoteCurrencyCode,
        CancellationToken cancellationToken)
    {
        // Pick a RANDOM date in a wide future window. The previous sequential
        // scan (start + offset++) is TOCTOU-racy: when these tests run in
        // parallel with AR/AP, two threads can both see "date X is free",
        // both pick X, and one fails on uq_company_fx_rate_snapshots_identity.
        // Randomization across a 720-day window collapses collision odds to
        // near-zero per test, and the retry loop covers the residual races.
        var start = DateOnly.FromDateTime(DateTime.UtcNow.Date).AddDays(60);
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var candidate = start.AddDays(Random.Shared.Next(0, 720));
            if (!await SnapshotIdentityExistsAsync(
                    connectionFactory,
                    baseCurrencyCode,
                    quoteCurrencyCode,
                    candidate,
                    cancellationToken))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not reserve a unique lifecycle smoke journal date.");
    }

    private static async Task<bool> SnapshotIdentityExistsAsync(
        PostgreSqlConnectionFactory connectionFactory,
        string baseCurrencyCode,
        string quoteCurrencyCode,
        DateOnly requestedDate,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select 1
            from company_fx_rate_snapshots
            where company_id = @company_id
              and base_currency_code = @base_currency_code
              and quote_currency_code = @quote_currency_code
              and requested_date = @requested_date
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", CompanyId.Value);
        command.Parameters.AddWithValue("base_currency_code", baseCurrencyCode);
        command.Parameters.AddWithValue("quote_currency_code", quoteCurrencyCode);
        command.Parameters.AddWithValue("requested_date", requestedDate);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private sealed record class LifecycleFixture(
        PostgreSqlConnectionFactory ConnectionFactory,
        IJournalEntryLifecycleWorkflow LifecycleWorkflow,
        PostgreSqlJournalEntryReviewStore ReviewStore,
        PostgreSqlManualJournalSourceReviewStore SourceReviewStore,
        Guid DocumentId,
        Guid? FxSnapshotId,
        DateOnly JournalDate,
        JournalEntryPostResult Posted);
}
