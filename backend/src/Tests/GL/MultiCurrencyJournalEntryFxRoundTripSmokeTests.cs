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

/// <summary>
/// M0 (smoke-test scaffolding) round-trip guard for the FX-direction
/// bug class. Posts a foreign-currency JE end-to-end through the live
/// Postgres workflow, then asserts the GL ledger_entries.base amounts
/// equal <c>tx_amount × stored_fx_rate</c>. If the FX rate ever gets
/// stored / applied in the wrong direction (the production bug fixed
/// in commit 0c10402), this test fails on the first run.
///
/// Requires CITUS_ACCOUNTING_DB env var pointing at a writable test
/// database with the demo company seeded (same prerequisite as
/// JournalEntryLifecycleSmokeTests).
/// </summary>
public sealed class MultiCurrencyJournalEntryFxRoundTripSmokeTests
{
    private static readonly CompanyId CompanyId = CompanyId.FromOrdinal(1);
    private static readonly UserId UserId = Guid.Parse("7bd0e908-cfe7-4f7b-8a0d-f19292e4186d");

    /// <summary>
    /// Worked example: company base = USD (per the demo company seed),
    /// transaction = CNY, FxRate = 0.1385 means "1 CNY = 0.1385 USD".
    /// Posting engine convention: baseAmount = txAmount × rate.
    /// So a 100 CNY debit must produce a 13.85 USD base debit on the
    /// ledger_entries row. Symmetric for the credit leg. If the rate
    /// were ever inverted (1/0.1385 ≈ 7.22), the assertions catch it.
    /// </summary>
    [Fact]
    public async Task PostedForeignCurrencyJe_StoresFxRateUnflippedAndAppliesItAsTxTimesRateForBaseAmounts()
    {
        var fixture = await CreateFixtureAsync();

        try
        {
            var (storedFxRate, ledgerLines) = await ReadPostedFxRateAndLedgerAsync(
                fixture.ConnectionFactory,
                fixture.Posted.JournalEntryId,
                CancellationToken.None);

            // Stored rate must round-trip the value the workflow received.
            Assert.Equal(fixture.SubmittedFxRate, storedFxRate);

            // Every ledger line's base amount must equal tx × rate. This
            // is the assertion that catches a flipped rate: with rate
            // 0.1385, a 100-CNY tx debit must produce 13.85 USD, not
            // 722.02 USD.
            Assert.NotEmpty(ledgerLines);
            foreach (var line in ledgerLines)
            {
                var expectedDebitBase = Math.Round(line.TxDebit * storedFxRate, 2, MidpointRounding.ToEven);
                var expectedCreditBase = Math.Round(line.TxCredit * storedFxRate, 2, MidpointRounding.ToEven);
                Assert.Equal(expectedDebitBase, line.Debit);
                Assert.Equal(expectedCreditBase, line.Credit);
            }

            // GL must remain balanced in both currencies.
            var totalTxDebit = ledgerLines.Sum(l => l.TxDebit);
            var totalTxCredit = ledgerLines.Sum(l => l.TxCredit);
            var totalBaseDebit = ledgerLines.Sum(l => l.Debit);
            var totalBaseCredit = ledgerLines.Sum(l => l.Credit);
            Assert.Equal(totalTxDebit, totalTxCredit);
            Assert.Equal(totalBaseDebit, totalBaseCredit);
        }
        finally
        {
            await CleanupFixtureAsync(fixture);
        }
    }

    private static async Task<RoundTripFixture> CreateFixtureAsync()
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

        var companyCurrency = await companyCurrencyWorkflow.EnableCurrencyAsync(CompanyId, "CNY", UserId, CancellationToken.None);
        var accounts = await accountCatalog.ListManualPostingAccountsAsync(CompanyId, CancellationToken.None);
        Assert.True(accounts.Count >= 2, "Expected at least two manual-posting accounts in the demo company.");

        var journalDate = await ReserveUniqueJournalDateAsync(
            connectionFactory,
            companyCurrency.Profile.BaseCurrencyCode,
            "CNY",
            CancellationToken.None);

        const decimal submittedFxRate = 0.1385m;

        var draft = JournalEntryEditorState.CreateDarkModeDemo().Draft;
        draft.CompanyId = CompanyId;
        draft.JournalDate = journalDate;
        draft.CurrencyCode = "CNY";
        draft.BaseCurrencyCode = companyCurrency.Profile.BaseCurrencyCode;
        draft.FxRate = submittedFxRate;
        draft.FxEffectiveDate = draft.JournalDate;
        draft.FxSourceSemantics = "manual";
        draft.Memo = $"FX round-trip smoke {Guid.NewGuid():N}";
        draft.Lines[0].Account = accounts[0];
        draft.Lines[0].DebitAmount = 100m;
        draft.Lines[0].Description = "FX round-trip debit";
        draft.Lines[1].Account = accounts[1];
        draft.Lines[1].CreditAmount = 100m;
        draft.Lines[1].Description = "FX round-trip credit";

        var saved = await workflow.SaveDraftAsync(draft, UserId, CancellationToken.None);
        var posted = await workflow.PostDraftAsync(draft, UserId, CancellationToken.None);

        return new RoundTripFixture(
            connectionFactory,
            saved.DocumentId,
            draft.FxSnapshotId,
            submittedFxRate,
            posted);
    }

    private static async Task<(decimal Rate, IReadOnlyList<LedgerSnapshot> Lines)> ReadPostedFxRateAndLedgerAsync(
        PostgreSqlConnectionFactory connectionFactory,
        Guid journalEntryId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        decimal rate;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "select exchange_rate from journal_entries where id = @id;";
            command.Parameters.AddWithValue("id", journalEntryId);
            var raw = await command.ExecuteScalarAsync(cancellationToken);
            Assert.NotNull(raw);
            rate = Convert.ToDecimal(raw);
        }

        var lines = new List<LedgerSnapshot>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                select tx_debit, tx_credit, debit, credit
                from ledger_entries
                where journal_entry_id = @id
                order by line_number;
                """;
            command.Parameters.AddWithValue("id", journalEntryId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                lines.Add(new LedgerSnapshot(
                    TxDebit: reader.GetDecimal(0),
                    TxCredit: reader.GetDecimal(1),
                    Debit: reader.GetDecimal(2),
                    Credit: reader.GetDecimal(3)));
            }
        }

        return (rate, lines);
    }

    private static async Task CleanupFixtureAsync(RoundTripFixture fixture)
    {
        await using var connection = await fixture.ConnectionFactory.OpenAsync(CancellationToken.None);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);

        var journalEntryIds = new List<Guid>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                select id from journal_entries
                where company_id = @company_id and source_id = @source_id;
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
            await ExecuteDeleteAsync(connection, transaction, "delete from ledger_entries where journal_entry_id = @id;", journalEntryId);
            await ExecuteDeleteAsync(connection, transaction, "delete from journal_entry_lines where journal_entry_id = @id;", journalEntryId);
            await ExecuteDeleteAsync(connection, transaction, "delete from journal_entries where id = @id;", journalEntryId);
        }
        await ExecuteDeleteAsync(connection, transaction, "delete from manual_journal_document_lines where manual_journal_document_id = @id;", fixture.DocumentId);
        await ExecuteDeleteAsync(connection, transaction, "delete from manual_journal_documents where id = @id;", fixture.DocumentId);
        if (fixture.FxSnapshotId.HasValue)
        {
            await ExecuteDeleteAsync(connection, transaction, "delete from company_fx_rate_snapshots where id = @id;", fixture.FxSnapshotId.Value);
        }

        await transaction.CommitAsync(CancellationToken.None);
    }

    private static async Task ExecuteDeleteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, Guid id)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
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
        // Pick a date with no existing FX snapshot for this pair so the
        // workflow's PersistManualSnapshotAsync gets to land a fresh
        // snapshot deterministically rather than collide with prior test
        // residue.
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        var probe = DateOnly.FromDateTime(DateTime.UtcNow.Date).AddDays(-7);
        for (var i = 0; i < 365; i++)
        {
            var candidate = probe.AddDays(-i);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                select 1 from company_fx_rate_snapshots
                where company_id = @company_id
                  and base_currency_code = @base
                  and quote_currency_code = @quote
                  and effective_date = @effective
                limit 1;
                """;
            command.Parameters.AddWithValue("company_id", CompanyId);
            command.Parameters.AddWithValue("base", baseCurrencyCode);
            command.Parameters.AddWithValue("quote", quoteCurrencyCode);
            command.Parameters.AddWithValue("effective", candidate);
            var hit = await command.ExecuteScalarAsync(cancellationToken);
            if (hit is null)
            {
                return candidate;
            }
        }
        throw new InvalidOperationException("Could not find a free FX-snapshot date in the lookback window.");
    }

    private sealed record RoundTripFixture(
        PostgreSqlConnectionFactory ConnectionFactory,
        Guid DocumentId,
        Guid? FxSnapshotId,
        decimal SubmittedFxRate,
        Modules.GL.JournalEntry.JournalEntryPostResult Posted);

    private sealed record LedgerSnapshot(
        decimal TxDebit,
        decimal TxCredit,
        decimal Debit,
        decimal Credit);
}
