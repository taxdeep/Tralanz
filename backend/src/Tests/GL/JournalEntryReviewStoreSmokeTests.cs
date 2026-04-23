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

public sealed class JournalEntryReviewStoreSmokeTests
{
    private static readonly Guid CompanyId = Guid.Parse("5e492df2-37ab-47df-a1bb-2d559c876cbc");
    private static readonly Guid UserId = Guid.Parse("7bd0e908-cfe7-4f7b-8a0d-f19292e4186d");

    [Fact]
    public async Task GetAsync_LoadsPostedJournalReviewWithPersistedManualSnapshot()
    {
        var connectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var accountCatalog = new PostgreSqlJournalEntryAccountCatalog(connectionFactory);
        var draftStore = new PostgreSqlJournalEntryDraftStore(connectionFactory);
        var numberLookup = new PostgreSqlJournalEntryNumberLookup(connectionFactory);
        var postingStore = new PostgreSqlJournalEntryPostingStore(connectionFactory, numberLookup);
        var fxSelectionService = new FxRateSelectionService(new PostgreSqlFxRateStore(connectionFactory));
        var companyCurrencyStore = new PostgreSqlCompanyCurrencyProvisioningStore(connectionFactory);
        var companyCurrencyWorkflow = new CompanyCurrencyGovernanceWorkflow(companyCurrencyStore);
        var workflow = new JournalEntryWorkflow(accountCatalog, draftStore, postingStore, fxSelectionService, companyCurrencyStore);
        var reviewStore = new PostgreSqlJournalEntryReviewStore(connectionFactory);
        var sourceReviewStore = new PostgreSqlManualJournalSourceReviewStore(connectionFactory);

        Guid? documentId = null;
        Guid? journalEntryId = null;
        Guid? snapshotId = null;

        try
        {
            var journalDate = BuildUniqueJournalDate();
            var companyCurrency = await companyCurrencyWorkflow.EnableCurrencyAsync(CompanyId, "EUR", UserId, CancellationToken.None);
            var accounts = await accountCatalog.ListManualPostingAccountsAsync(CompanyId, CancellationToken.None);
            Assert.True(accounts.Count >= 2, "Expected at least two manual-posting accounts in the demo company.");

            var draft = JournalEntryEditorState.CreateDarkModeDemo().Draft;
            draft.CompanyId = CompanyId;
            draft.JournalDate = journalDate;
            draft.CurrencyCode = "EUR";
            draft.BaseCurrencyCode = companyCurrency.Profile.BaseCurrencyCode;
            draft.FxRate = 1.12125m;
            draft.FxEffectiveDate = draft.JournalDate;
            draft.FxSourceSemantics = "manual";
            draft.FxSnapshotId = null;
            draft.Memo = $"Review smoke {Guid.NewGuid():N}";
            draft.Lines[0].Account = accounts[0];
            draft.Lines[0].DebitAmount = 150m;
            draft.Lines[0].Description = "Review debit";
            draft.Lines[1].Account = accounts[1];
            draft.Lines[1].CreditAmount = 150m;
            draft.Lines[1].Description = "Review credit";

            var saved = await workflow.SaveDraftAsync(draft, UserId, CancellationToken.None);
            documentId = saved.DocumentId;
            snapshotId = draft.FxSnapshotId;

            Assert.NotNull(snapshotId);

            var posted = await workflow.PostDraftAsync(draft, UserId, CancellationToken.None);
            journalEntryId = posted.JournalEntryId;

            var review = await reviewStore.GetAsync(CompanyId, posted.JournalEntryId, CancellationToken.None);

            Assert.NotNull(review);
            Assert.Equal(posted.JournalDisplayNumber, review!.DisplayNumber);
            Assert.Equal("manual_journal", review.SourceType);
            Assert.Equal("manual", review.ExchangeRateSource);
            Assert.Equal(snapshotId, review.FxSnapshotId);
            Assert.Equal("spot", review.FxRateType);
            Assert.Equal("direct", review.FxQuoteBasis);
            Assert.Equal("general", review.FxRateUseCase);
            Assert.Equal("normal", review.FxPostingReason);
            Assert.Equal("manual", review.FxSnapshotSemantics);
            Assert.Equal("manual", review.FxSnapshotRowOrigin);
            Assert.False(string.IsNullOrWhiteSpace(review.FxProviderKey));
            Assert.Equal(2, review.LineCount);
            Assert.Equal(2, review.Lines.Count);
            Assert.Equal(150m, review.TotalTransactionDebit);
            Assert.Equal(150m, review.TotalTransactionCredit);
            Assert.Equal("Review debit", review.Lines[0].Description);
            Assert.Equal("Review credit", review.Lines[1].Description);

            var recent = await reviewStore.ListRecentAsync(CompanyId, 25, CancellationToken.None);
            var listItem = Assert.Single(recent, item => item.Id == posted.JournalEntryId);

            Assert.Equal(posted.JournalDisplayNumber, listItem.DisplayNumber);
            Assert.True(listItem.IsForeignCurrency);
            Assert.True(listItem.IsBalanced);
            Assert.Equal("EUR", listItem.TransactionCurrencyCode);
            Assert.Equal(companyCurrency.Profile.BaseCurrencyCode, listItem.BaseCurrencyCode);
            Assert.Equal(snapshotId, listItem.FxSnapshotId);
            Assert.Equal("Manual journal", listItem.SourceTypeLabel);

            var accountBalances = await reviewStore.ListAccountBalancesAsync(
                CompanyId,
                journalDate,
                CancellationToken.None);
            var debitAccountBalance = Assert.Single(accountBalances, balance => balance.AccountId == accounts[0].AccountId);
            var debitExposure = Assert.Single(debitAccountBalance.CurrencyExposures, exposure => exposure.CurrencyCode == "EUR");

            Assert.Equal(150m, debitExposure.TransactionDebit);
            Assert.Equal(0m, debitExposure.TransactionCredit);
            Assert.True(debitAccountBalance.NetDebit > 0m);

            var ledgerRows = await reviewStore.ListLedgerEntriesAsync(
                CompanyId,
                accounts[0].AccountId,
                10,
                CancellationToken.None);
            var debitLedgerRow = Assert.Single(ledgerRows, row => row.JournalEntryId == posted.JournalEntryId);

            Assert.Equal("EUR", debitLedgerRow.TransactionCurrencyCode);
            Assert.Equal(companyCurrency.Profile.BaseCurrencyCode, debitLedgerRow.BaseCurrencyCode);
            Assert.True(debitLedgerRow.IsForeignCurrency);
            Assert.Equal(saved.DocumentId, debitLedgerRow.SourceId);
            Assert.Equal(snapshotId, debitLedgerRow.FxSnapshotId);
            Assert.Equal("Manual journal", debitLedgerRow.SourceTypeLabel);
            Assert.Contains("snapshot", debitLedgerRow.FxTraceLabel, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(150m, debitLedgerRow.TransactionDebit);
            Assert.Equal("Review debit", debitLedgerRow.Description);

            var sourceReview = await sourceReviewStore.GetAsync(CompanyId, saved.DocumentId, CancellationToken.None);

            Assert.NotNull(sourceReview);
            Assert.Equal(saved.DocumentNumber, sourceReview!.DisplayNumber);
            Assert.Equal("posted", sourceReview.Status);
            Assert.Equal("manual", sourceReview.FxSource);
            Assert.Equal(snapshotId, sourceReview.FxSnapshotId);
            Assert.Equal(posted.JournalEntryId, sourceReview.LinkedJournalEntryId);
            Assert.Equal(posted.JournalDisplayNumber, sourceReview.LinkedJournalDisplayNumber);
            Assert.Equal(2, sourceReview.Lines.Count);
            Assert.Equal(draft.Memo, sourceReview.Memo);
        }
        finally
        {
            await CleanupAsync(connectionFactory, documentId, journalEntryId, snapshotId, CancellationToken.None);
        }
    }

    private static string GetConnectionString() =>
        Environment.GetEnvironmentVariable("CITUS_ACCOUNTING_DB")
        ?? "Host=localhost;Port=5432;Database=citus_accounting;Username=postgres;Password=change-me";

    private static DateOnly BuildUniqueJournalDate() =>
        DateOnly.FromDateTime(DateTime.UtcNow.Date).AddDays(Math.Abs(Guid.NewGuid().GetHashCode() % 300) + 30);

    private static async Task CleanupAsync(
        PostgreSqlConnectionFactory connectionFactory,
        Guid? documentId,
        Guid? journalEntryId,
        Guid? snapshotId,
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

        if (snapshotId.HasValue)
        {
            await ExecuteDeleteAsync(connection, transaction, "delete from company_fx_rate_snapshots where id = @id;", snapshotId.Value, cancellationToken);
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

}
