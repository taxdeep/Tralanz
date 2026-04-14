using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.GL;
using Infrastructure.PostgreSQL.Numbering;
using Infrastructure.PostgreSQL.FX;
using Engines.FX.FxRateLookup;
using Modules.GL.JournalEntry;
using Npgsql;
using Infrastructure.PostgreSQL.Company;

namespace Tests.GL;

public sealed class JournalEntryPersistenceSmokeTests
{
    private static readonly Guid CompanyId = Guid.Parse("5e492df2-37ab-47df-a1bb-2d559c876cbc");
    private static readonly Guid UserId = Guid.Parse("7bd0e908-cfe7-4f7b-8a0d-f19292e4186d");

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
