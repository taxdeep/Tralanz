using Engines.FX.FxRateLookup;
using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.FX;
using Infrastructure.PostgreSQL.GL;
using Infrastructure.PostgreSQL.Numbering;
using Modules.GL.JournalEntry;
using Npgsql;

namespace Tests.GL;

public sealed class JournalEntryLifecycleSmokeTests
{
    private static readonly Guid CompanyId = Guid.Parse("5e492df2-37ab-47df-a1bb-2d559c876cbc");
    private static readonly Guid UserId = Guid.Parse("7bd0e908-cfe7-4f7b-8a0d-f19292e4186d");

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
            Assert.NotNull(sourceReview);
            Assert.Equal("voided", sourceReview!.Status);
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
            Assert.NotNull(sourceReview);
            Assert.Equal("reversed", sourceReview!.Status);
            Assert.Contains(sourceReview.RelatedEntries, entry => entry.Id == lifecycle.CompensationJournalEntryId);
        }
        finally
        {
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
        var workflow = new JournalEntryWorkflow(accountCatalog, draftStore, postingStore, fxSelectionService);
        var lifecycleWorkflow = new JournalEntryLifecycleWorkflow(lifecycleStore);
        var reviewStore = new PostgreSqlJournalEntryReviewStore(connectionFactory);
        var sourceReviewStore = new PostgreSqlManualJournalSourceReviewStore(connectionFactory);

        var accounts = await accountCatalog.ListManualPostingAccountsAsync(CompanyId, CancellationToken.None);
        Assert.True(accounts.Count >= 2, "Expected at least two manual-posting accounts in the demo company.");

        var journalDate = BuildUniqueJournalDate();
        var draft = JournalEntryEditorState.CreateDarkModeDemo().Draft;
        draft.CompanyId = CompanyId;
        draft.JournalDate = journalDate;
        draft.CurrencyCode = "USD";
        draft.BaseCurrencyCode = "CAD";
        draft.FxRate = 1.3915m;
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
            command.Parameters.AddWithValue("company_id", CompanyId);
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

    private static DateOnly BuildUniqueJournalDate() =>
        DateOnly.FromDateTime(DateTime.UtcNow.Date).AddDays(Math.Abs(Guid.NewGuid().GetHashCode() % 300) + 60);

    private sealed record class LifecycleFixture(
        PostgreSqlConnectionFactory ConnectionFactory,
        IJournalEntryLifecycleWorkflow LifecycleWorkflow,
        PostgreSqlJournalEntryReviewStore ReviewStore,
        PostgreSqlManualJournalSourceReviewStore SourceReviewStore,
        Guid DocumentId,
        Guid? FxSnapshotId,
        JournalEntryPostResult Posted);
}
