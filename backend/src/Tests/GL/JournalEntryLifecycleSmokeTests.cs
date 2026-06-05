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

    [SkippableFact]
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

    [SkippableFact]
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

    [SkippableFact]
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

    [SkippableFact]
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

    [SkippableFact]
    public async Task ReverseAsync_RejectsClosedPrimaryBookPeriod()
    {
        var closedPeriodJournalDate = await ReserveUniqueJournalDateAsync(
            new PostgreSqlConnectionFactory(GetConnectionString()),
            "USD",
            "CNY",
            new DateOnly(2025, 1, 1),
            300,
            CancellationToken.None);
        var fixture = await CreateFixtureAsync(closedPeriodJournalDate);
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

    private static async Task<LifecycleFixture> CreateFixtureAsync(DateOnly? journalDateOverride = null)
    {
        if (journalDateOverride.HasValue)
        {
            return await CreateFixtureCoreAsync(journalDateOverride);
        }

        for (var attempt = 1; attempt <= 8; attempt++)
        {
            try
            {
                return await CreateFixtureCoreAsync(null);
            }
            catch (PostgresException ex) when (
                ex.SqlState == "23505" &&
                string.Equals(ex.ConstraintName, "uq_company_fx_rate_snapshots_identity", StringComparison.Ordinal) &&
                attempt < 8)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(15 * attempt), CancellationToken.None);
            }
        }

        throw new InvalidOperationException("Could not create a lifecycle smoke fixture after retrying FX snapshot identity collisions.");
    }

    private static async Task<LifecycleFixture> CreateFixtureCoreAsync(DateOnly? journalDateOverride)
    {
        var connectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var accountCatalog = new PostgreSqlJournalEntryAccountCatalog(connectionFactory);
        var draftStore = new PostgreSqlJournalEntryDraftStore(connectionFactory);
        var numberLookup = new PostgreSqlJournalEntryNumberLookup(connectionFactory);
        var postingStore = new PostgreSqlJournalEntryPostingStore(connectionFactory, numberLookup);
        var lifecycleStore = new PostgreSqlJournalEntryLifecycleStore(connectionFactory, numberLookup, new SharedKernel.Persistence.AmbientDatabaseTransactionAccessor());
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

        var journalDate = journalDateOverride
            ?? await ReserveUniqueJournalDateAsync(
                connectionFactory,
                companyCurrency.Profile.BaseCurrencyCode,
                "CNY",
                DateOnly.FromDateTime(DateTime.UtcNow.Date).AddDays(60),
                720,
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

        var effectiveBookId = bookId;
        await using (var existingBook = connection.CreateCommand())
        {
            existingBook.Transaction = transaction;
            existingBook.CommandText =
                """
                select id
                from company_books
                where company_id = @company_id
                  and book_code = 'PRIMARY'
                limit 1;
                """;
            existingBook.Parameters.AddWithValue("company_id", companyId.Value);
            var existing = await existingBook.ExecuteScalarAsync(cancellationToken);
            if (existing is Guid existingId)
            {
                effectiveBookId = existingId;
            }
        }

        await using (var deleteSignals = connection.CreateCommand())
        {
            deleteSignals.Transaction = transaction;
            deleteSignals.CommandText =
                """
                delete from company_book_governance_signals
                where company_id = @company_id
                  and (
                    company_book_id = @book_id
                    or reference_label = 'Lifecycle smoke closed period'
                  );
                """;
            deleteSignals.Parameters.AddWithValue("company_id", companyId.Value);
            deleteSignals.Parameters.AddWithValue("book_id", effectiveBookId);
            await deleteSignals.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deletePolicy = connection.CreateCommand())
        {
            deletePolicy.Transaction = transaction;
            deletePolicy.CommandText =
                """
                delete from company_book_remeasurement_policies
                where company_book_id = @book_id;
                """;
            deletePolicy.Parameters.AddWithValue("book_id", effectiveBookId);
            await deletePolicy.ExecuteNonQueryAsync(cancellationToken);
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
                  'ASPE',
                  'USD',
                  'USD',
                  'USD',
                  true,
                  true,
                  @effective_from,
              @created_by_user_id,
              now()
            )
            on conflict (company_id, book_code) do nothing;
            """;
            bookCommand.Parameters.AddWithValue("id", effectiveBookId);
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
            signalCommand.Parameters.AddWithValue("book_id", effectiveBookId);
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
                  and (
                    company_book_id = @book_id
                    or reference_label = 'Lifecycle smoke closed period'
                  );
                """;
            signalCommand.Parameters.AddWithValue("company_id", companyId.Value);
            signalCommand.Parameters.AddWithValue("book_id", bookId);
            await signalCommand.ExecuteNonQueryAsync(cancellationToken);
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

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("CITUS_POSTGRESQL_INTEGRATION_TEST_DB");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "DB-backed test skipped: set CITUS_POSTGRESQL_INTEGRATION_TEST_DB to a dedicated test database to run it.");

        return connectionString!;
    }

    private static async Task<DateOnly> ReserveUniqueJournalDateAsync(
        PostgreSqlConnectionFactory connectionFactory,
        string baseCurrencyCode,
        string quoteCurrencyCode,
        DateOnly start,
        int dayWindow,
        CancellationToken cancellationToken)
    {
        // Pick a RANDOM date in a wide future window. The previous sequential
        // scan (start + offset++) is TOCTOU-racy: when these tests run in
        // parallel with AR/AP, two threads can both see "date X is free",
        // both pick X, and one fails on uq_company_fx_rate_snapshots_identity.
        // Randomization across a 720-day window collapses collision odds to
        // near-zero per test, and the retry loop covers the residual races.
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var candidate = start.AddDays(Random.Shared.Next(0, dayWindow));
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
