using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.Inventory.Posting;
using Npgsql;

namespace Tests.Inventory;

/// <summary>
/// P0-3b-2 (AUDIT_2026-05-20 C3 closure): contract tests for the
/// manufacturing GL poster.
///
/// V1 audit-trail JE shape:
///   Dr 14000 Inventory (receipt side, finished goods cost)
///   Cr 14000 Inventory (issue side, raw material cost)
/// Both legs point at the SAME inventory_asset account because the
/// starter COA does not split RM / WIP / FG; net balance change is
/// zero, but the JE records the run for audit purposes and preserves
/// the system-wide "every inventory mutation emits a JE" invariant.
///
/// These tests verify five guarantees, mirroring the
/// InventoryAdjustmentGlPostingTests pattern from P0-3b-1:
///   1. Manufacturing run produces a balanced two-line JE with both
///      lines on the inventory_asset account, total_debit ==
///      total_credit == totalConsumedCostBase.
///   2. Re-running AppendAsync against the same run id is idempotent.
///   3. Caller rollback undoes the poster's INSERTs (same-tx guarantee).
///   4. Missing the inventory_asset SystemRole throws before any
///      INSERT touches the JE tables.
///   5. Zero/negative cost is rejected with an explicit error rather
///      than producing a degenerate zero-amount JE.
/// </summary>
public sealed class InventoryManufacturingGlPostingTests
{
    [SkippableFact]
    public async Task ProducesBalancedTwoLineJeOnInventoryAssetAccount()
    {
        var seed = await SeedScenarioAsync();
        try
        {
            var poster = new PostgreSqlInventoryManufacturingGlPoster();
            var runId = Guid.NewGuid();
            var amount = 250.00m;

            var (journalEntryId, displayNumber) =
                await PostInSelfManagedTxAsync(seed, poster, runId, amount);

            Assert.NotEqual(Guid.Empty, journalEntryId);
            Assert.StartsWith("JE-", displayNumber);

            await AssertJournalEntryAsync(seed, journalEntryId, amount);
            // Line 1 = receipt (debit) side, Line 2 = issue (credit) side.
            // Both reference the SAME inventory_asset account (V1 single-account model).
            await AssertLineAsync(seed, journalEntryId, lineNumber: 1,
                accountId: seed.InventoryAssetAccountId, debit: amount, credit: 0m);
            await AssertLineAsync(seed, journalEntryId, lineNumber: 2,
                accountId: seed.InventoryAssetAccountId, debit: 0m, credit: amount);
            await AssertLedgerEntryCountAsync(seed, journalEntryId, expected: 2);
        }
        finally
        {
            await CleanupAsync(seed);
        }
    }

    [SkippableFact]
    public async Task Idempotent_SecondAppendReturnsExistingJeWithoutInsert()
    {
        var seed = await SeedScenarioAsync();
        try
        {
            var poster = new PostgreSqlInventoryManufacturingGlPoster();
            var runId = Guid.NewGuid();
            var amount = 175.50m;

            var first = await PostInSelfManagedTxAsync(seed, poster, runId, amount);
            Assert.Equal(1, await CountJournalEntriesAsync(seed, runId));

            // Re-run with same run id — must short-circuit on the
            // (company_id, source_type='inventory_manufacturing_gl', source_id) probe.
            var second = await PostInSelfManagedTxAsync(seed, poster, runId, amount, expectAlreadyPosted: true);
            Assert.Equal(first.journalEntryId, second.journalEntryId);
            Assert.Equal(first.displayNumber, second.displayNumber);

            Assert.Equal(1, await CountJournalEntriesAsync(seed, runId));
        }
        finally
        {
            await CleanupAsync(seed);
        }
    }

    [SkippableFact]
    public async Task SameTxRollback_PosterWritesAreUndoneWhenCallerRollsBack()
    {
        var seed = await SeedScenarioAsync();
        try
        {
            var poster = new PostgreSqlInventoryManufacturingGlPoster();
            var runId = Guid.NewGuid();

            await using (var connection = await OpenWithBypassAsync(seed.Connections))
            {
                await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);

                var result = await poster.AppendAsync(
                    connection,
                    transaction,
                    BuildRequest(seed, runId, amount: 99.00m),
                    CancellationToken.None);
                Assert.False(result.AlreadyPosted);
                Assert.NotEqual(Guid.Empty, result.JournalEntryId);

                // INSERT visible inside the tx
                Assert.Equal(1, await CountJournalEntriesOnTxAsync(connection, transaction, runId));

                await transaction.RollbackAsync(CancellationToken.None);
            }

            // After rollback — JE, lines, ledger all gone
            Assert.Equal(0, await CountJournalEntriesAsync(seed, runId));
            Assert.Equal(0, await CountJournalEntryLinesAsync(seed, runId));
            Assert.Equal(0, await CountLedgerEntriesAsync(seed, runId));
        }
        finally
        {
            await CleanupAsync(seed);
        }
    }

    [SkippableFact]
    public async Task MissingInventoryAssetSystemRole_AppendThrowsBeforeAnyInsert()
    {
        var seed = await SeedScenarioAsync(pinInventoryAssetSystemRole: false);
        try
        {
            var poster = new PostgreSqlInventoryManufacturingGlPoster();
            var runId = Guid.NewGuid();

            await using var connection = await OpenWithBypassAsync(seed.Connections);
            await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                poster.AppendAsync(
                    connection,
                    transaction,
                    BuildRequest(seed, runId, amount: 80.00m),
                    CancellationToken.None));
            Assert.Contains("inventory_asset", ex.Message);

            await transaction.RollbackAsync(CancellationToken.None);

            Assert.Equal(0, await CountJournalEntriesAsync(seed, runId));
        }
        finally
        {
            await CleanupAsync(seed);
        }
    }

    [SkippableFact]
    public async Task ZeroOrNegativeAmount_AppendThrowsInsteadOfPostingDegenerateJe()
    {
        var seed = await SeedScenarioAsync();
        try
        {
            var poster = new PostgreSqlInventoryManufacturingGlPoster();
            var runId = Guid.NewGuid();

            await using var connection = await OpenWithBypassAsync(seed.Connections);
            await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);

            // A manufacturing run with zero consumed cost is a defect
            // upstream (BOM components with zero qty, or all consumed
            // layers at zero cost). The poster refuses rather than
            // emitting a JE that reconciles to nothing.
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                poster.AppendAsync(
                    connection,
                    transaction,
                    BuildRequest(seed, runId, amount: 0m),
                    CancellationToken.None));
            Assert.Contains("strictly positive", ex.Message);

            await transaction.RollbackAsync(CancellationToken.None);
            Assert.Equal(0, await CountJournalEntriesAsync(seed, runId));
        }
        finally
        {
            await CleanupAsync(seed);
        }
    }

    // ---------------------------------------------------------------------
    // Seed + helpers
    // ---------------------------------------------------------------------

    private sealed record SeedScenario(
        PostgreSqlConnectionFactory Connections,
        CompanyId CompanyId,
        UserId UserId,
        Guid InventoryAssetAccountId,
        string BaseCurrencyCode);

    private static async Task<SeedScenario> SeedScenarioAsync(bool pinInventoryAssetSystemRole = true)
    {
        var connections = new PostgreSqlConnectionFactory(GetConnectionString());
        var rand = Random.Shared.Next(7000, 7999);
        var companyId = CompanyId.FromOrdinal(rand);
        var userId = UserId.FromOrdinal(rand);
        var inventoryAssetAccountId = Guid.NewGuid();

        await using var connection = await OpenWithBypassAsync(connections);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into companies (id, entity_number, legal_name, base_currency_code, multi_currency_enabled, status)
            values (@company_id, @entity_number, 'InventoryManufacturingGl Test Co', 'USD', false, 'active');

            insert into users (id, email, username, password_hash, status)
            values (@user_id, @user_email, 'gl.mfg.test', 'hashed', 'active');

            insert into accounts (
              id, company_id, code, name, root_type, detail_type, is_active, is_system,
              system_role, system_key, entity_number
            )
            values (
              @asset_account_id, @company_id, '14000', 'Inventory', 'asset', 'inventory', true, true,
              @asset_role, @asset_key, @asset_entity_number);
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("entity_number", BuildEntityNumber());
        command.Parameters.AddWithValue("user_id", userId.Value);
        command.Parameters.AddWithValue("user_email", $"{userId.Value}@glmfgtest.test");
        command.Parameters.AddWithValue("asset_account_id", inventoryAssetAccountId);
        command.Parameters.AddWithValue("asset_entity_number", BuildEntityNumber());
        command.Parameters.AddWithValue(
            "asset_role",
            pinInventoryAssetSystemRole ? (object)"inventory_asset" : DBNull.Value);
        command.Parameters.AddWithValue(
            "asset_key",
            pinInventoryAssetSystemRole ? (object)"inventory:asset" : DBNull.Value);

        await command.ExecuteNonQueryAsync(CancellationToken.None);

        return new SeedScenario(
            connections,
            companyId,
            userId,
            inventoryAssetAccountId,
            "USD");
    }

    private static InventoryManufacturingGlPostingRequest BuildRequest(
        SeedScenario seed,
        Guid runId,
        decimal amount)
    {
        var token = runId.ToString("N").Substring(0, 8);
        return new InventoryManufacturingGlPostingRequest(
            CompanyId: seed.CompanyId,
            UserId: seed.UserId,
            ManufacturingRunId: runId,
            ManufacturingRunNumber: $"RUN-{token}",
            IssueDocumentNumber: $"MFG-ISS-{token}",
            ReceiptDocumentNumber: $"MFG-RCT-{token}",
            PostingDate: DateOnly.FromDateTime(DateTime.UtcNow.Date),
            BaseCurrencyCode: seed.BaseCurrencyCode,
            TotalConsumedCostBase: amount);
    }

    private static async Task<(Guid journalEntryId, string displayNumber)> PostInSelfManagedTxAsync(
        SeedScenario seed,
        IInventoryManufacturingGlPoster poster,
        Guid runId,
        decimal amount,
        bool expectAlreadyPosted = false)
    {
        await using var connection = await OpenWithBypassAsync(seed.Connections);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);

        var result = await poster.AppendAsync(
            connection,
            transaction,
            BuildRequest(seed, runId, amount),
            CancellationToken.None);

        await transaction.CommitAsync(CancellationToken.None);

        Assert.Equal(expectAlreadyPosted, result.AlreadyPosted);
        return (result.JournalEntryId, result.JournalEntryDisplayNumber);
    }

    private static async Task AssertJournalEntryAsync(SeedScenario seed, Guid journalEntryId, decimal expectedTotal)
    {
        await using var connection = await OpenWithBypassAsync(seed.Connections);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select status, source_type, transaction_currency_code, base_currency_code,
                   total_debit, total_credit, total_tx_debit, total_tx_credit
            from journal_entries
            where id = @id;
            """;
        command.Parameters.AddWithValue("id", journalEntryId);

        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None), "Journal entry header row was not found.");
        Assert.Equal("posted", reader.GetString(reader.GetOrdinal("status")));
        Assert.Equal("inventory_manufacturing_gl", reader.GetString(reader.GetOrdinal("source_type")));
        Assert.Equal(seed.BaseCurrencyCode, reader.GetString(reader.GetOrdinal("transaction_currency_code")));
        Assert.Equal(seed.BaseCurrencyCode, reader.GetString(reader.GetOrdinal("base_currency_code")));
        Assert.Equal(expectedTotal, reader.GetDecimal(reader.GetOrdinal("total_debit")));
        Assert.Equal(expectedTotal, reader.GetDecimal(reader.GetOrdinal("total_credit")));
        Assert.Equal(expectedTotal, reader.GetDecimal(reader.GetOrdinal("total_tx_debit")));
        Assert.Equal(expectedTotal, reader.GetDecimal(reader.GetOrdinal("total_tx_credit")));
    }

    private static async Task AssertLineAsync(
        SeedScenario seed,
        Guid journalEntryId,
        int lineNumber,
        Guid accountId,
        decimal debit,
        decimal credit)
    {
        await using var connection = await OpenWithBypassAsync(seed.Connections);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select account_id, debit, credit, tx_debit, tx_credit
            from journal_entry_lines
            where journal_entry_id = @journal_entry_id
              and line_number = @line_number;
            """;
        command.Parameters.AddWithValue("journal_entry_id", journalEntryId);
        command.Parameters.AddWithValue("line_number", lineNumber);

        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None), $"Journal entry line {lineNumber} was not found.");
        Assert.Equal(accountId, reader.GetGuid(reader.GetOrdinal("account_id")));
        Assert.Equal(debit, reader.GetDecimal(reader.GetOrdinal("debit")));
        Assert.Equal(credit, reader.GetDecimal(reader.GetOrdinal("credit")));
        Assert.Equal(debit, reader.GetDecimal(reader.GetOrdinal("tx_debit")));
        Assert.Equal(credit, reader.GetDecimal(reader.GetOrdinal("tx_credit")));
    }

    private static async Task AssertLedgerEntryCountAsync(SeedScenario seed, Guid journalEntryId, int expected)
    {
        await using var connection = await OpenWithBypassAsync(seed.Connections);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select count(*)::int from ledger_entries where journal_entry_id = @journal_entry_id;
            """;
        command.Parameters.AddWithValue("journal_entry_id", journalEntryId);
        var actual = (int)(await command.ExecuteScalarAsync(CancellationToken.None) ?? 0);
        Assert.Equal(expected, actual);
    }

    private static async Task<int> CountJournalEntriesAsync(SeedScenario seed, Guid sourceId)
    {
        await using var connection = await OpenWithBypassAsync(seed.Connections);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select count(*)::int from journal_entries
             where company_id = @company_id
               and source_type = 'inventory_manufacturing_gl'
               and source_id = @source_id;
            """;
        command.Parameters.AddWithValue("company_id", seed.CompanyId.Value);
        command.Parameters.AddWithValue("source_id", sourceId);
        return (int)(await command.ExecuteScalarAsync(CancellationToken.None) ?? 0);
    }

    private static async Task<int> CountJournalEntriesOnTxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sourceId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select count(*)::int from journal_entries
             where source_type = 'inventory_manufacturing_gl'
               and source_id = @source_id;
            """;
        command.Parameters.AddWithValue("source_id", sourceId);
        return (int)(await command.ExecuteScalarAsync(CancellationToken.None) ?? 0);
    }

    private static async Task<int> CountJournalEntryLinesAsync(SeedScenario seed, Guid sourceId)
    {
        await using var connection = await OpenWithBypassAsync(seed.Connections);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select count(*)::int from journal_entry_lines l
             inner join journal_entries e on e.id = l.journal_entry_id
             where e.company_id = @company_id
               and e.source_type = 'inventory_manufacturing_gl'
               and e.source_id = @source_id;
            """;
        command.Parameters.AddWithValue("company_id", seed.CompanyId.Value);
        command.Parameters.AddWithValue("source_id", sourceId);
        return (int)(await command.ExecuteScalarAsync(CancellationToken.None) ?? 0);
    }

    private static async Task<int> CountLedgerEntriesAsync(SeedScenario seed, Guid sourceId)
    {
        await using var connection = await OpenWithBypassAsync(seed.Connections);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select count(*)::int from ledger_entries le
             inner join journal_entries e on e.id = le.journal_entry_id
             where e.company_id = @company_id
               and e.source_type = 'inventory_manufacturing_gl'
               and e.source_id = @source_id;
            """;
        command.Parameters.AddWithValue("company_id", seed.CompanyId.Value);
        command.Parameters.AddWithValue("source_id", sourceId);
        return (int)(await command.ExecuteScalarAsync(CancellationToken.None) ?? 0);
    }

    private static async Task CleanupAsync(SeedScenario seed)
    {
        await using var connection = await OpenWithBypassAsync(seed.Connections);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            delete from ledger_entries le
             using journal_entries e
             where le.journal_entry_id = e.id
               and e.company_id = @company_id
               and e.source_type = 'inventory_manufacturing_gl';

            delete from journal_entry_lines l
             using journal_entries e
             where l.journal_entry_id = e.id
               and e.company_id = @company_id
               and e.source_type = 'inventory_manufacturing_gl';

            delete from journal_entries
             where company_id = @company_id
               and source_type = 'inventory_manufacturing_gl';

            delete from company_numbering_sequences where company_id = @company_id;
            delete from company_entity_number_sequences where company_id = @company_id;
            delete from accounts where company_id = @company_id;
            delete from companies where id = @company_id;
            delete from users where id = @user_id;
            """;
        command.Parameters.AddWithValue("company_id", seed.CompanyId.Value);
        command.Parameters.AddWithValue("user_id", seed.UserId.Value);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("CITUS_POSTGRESQL_INTEGRATION_TEST_DB");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "DB-backed test skipped: set CITUS_POSTGRESQL_INTEGRATION_TEST_DB to a dedicated test database to run it.");

        return connectionString!;
    }

    private static async Task<NpgsqlConnection> OpenWithBypassAsync(PostgreSqlConnectionFactory connections)
    {
        var connection = await connections.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "select set_config('app.bypass_company_filter', 'true', false);";
        await command.ExecuteNonQueryAsync(CancellationToken.None);
        return connection;
    }

    private static string BuildEntityNumber()
    {
        var ordinal = Math.Abs(BitConverter.ToInt32(Guid.NewGuid().ToByteArray(), 0)) % 60_466_176;
        return EntityNumber.Create(2099, ordinal).Value;
    }
}
