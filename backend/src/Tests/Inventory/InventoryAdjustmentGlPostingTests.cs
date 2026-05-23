using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.Inventory.Posting;
using Npgsql;

namespace Tests.Inventory;

/// <summary>
/// P0-3b-1 (AUDIT_2026-05-20 C3 closure): contract tests for the
/// inventory adjustment GL poster.
///
/// These tests exercise the poster directly against a real Postgres
/// (CITUS_ACCOUNTING_DB env var, same as the existing inventory
/// idempotency smoke tests). They verify the four Q2=A guarantees:
///
///   1. Gain produces a JE with Dr Inventory Asset / Cr Inventory Adjustment
///   2. Loss produces a JE with Dr Inventory Adjustment / Cr Inventory Asset
///   3. Write-off uses the same Dr/Cr shape as Loss (single
///      "Inventory Adjustment" account, no separate write-off account)
///   4. Idempotent: a second AppendAsync against the same source_id
///      returns the prior JE's identifiers and inserts NO new rows
///   5. Same-tx rollback: if the caller rolls back its tx after a
///      successful AppendAsync, the JE/lines/ledger rows do not
///      persist — which is the C3 atomicity guarantee
///
/// The poster's same-tx invariant is naturally satisfied: it writes
/// every row on the connection+tx the caller passes, so any rollback
/// upstream undoes the GL writes too.
/// </summary>
public sealed class InventoryAdjustmentGlPostingTests
{
    [Fact]
    public async Task Gain_ProducesDrInventoryCrAdjustmentInBaseCurrency()
    {
        var seed = await SeedScenarioAsync();
        try
        {
            var poster = new PostgreSqlInventoryAdjustmentGlPoster();
            var documentId = Guid.NewGuid();
            var amount = 123.45m;

            var (journalEntryId, displayNumber) =
                await PostInSelfManagedTxAsync(seed, poster, documentId, InventoryAdjustmentGlKind.Gain, amount);

            Assert.NotEqual(Guid.Empty, journalEntryId);
            Assert.StartsWith("JE-", displayNumber);

            await AssertJournalEntryAsync(seed, journalEntryId, amount);
            await AssertLineAsync(seed, journalEntryId, lineNumber: 1, accountId: seed.InventoryAssetAccountId, debit: amount, credit: 0m);
            await AssertLineAsync(seed, journalEntryId, lineNumber: 2, accountId: seed.InventoryAdjustmentAccountId, debit: 0m, credit: amount);
            await AssertLedgerEntryCountAsync(seed, journalEntryId, expected: 2);
        }
        finally
        {
            await CleanupAsync(seed);
        }
    }

    [Fact]
    public async Task Loss_ProducesDrAdjustmentCrInventoryInBaseCurrency()
    {
        var seed = await SeedScenarioAsync();
        try
        {
            var poster = new PostgreSqlInventoryAdjustmentGlPoster();
            var documentId = Guid.NewGuid();
            var amount = 87.65m;

            var (journalEntryId, _) =
                await PostInSelfManagedTxAsync(seed, poster, documentId, InventoryAdjustmentGlKind.Loss, amount);

            await AssertJournalEntryAsync(seed, journalEntryId, amount);
            // Loss: Dr Inventory Adjustment / Cr Inventory Asset (Q2=A)
            await AssertLineAsync(seed, journalEntryId, lineNumber: 1, accountId: seed.InventoryAdjustmentAccountId, debit: amount, credit: 0m);
            await AssertLineAsync(seed, journalEntryId, lineNumber: 2, accountId: seed.InventoryAssetAccountId, debit: 0m, credit: amount);
        }
        finally
        {
            await CleanupAsync(seed);
        }
    }

    [Fact]
    public async Task WriteOff_UsesSameDrCrShapeAsLoss()
    {
        var seed = await SeedScenarioAsync();
        try
        {
            var poster = new PostgreSqlInventoryAdjustmentGlPoster();
            var documentId = Guid.NewGuid();
            var amount = 50.00m;

            var (journalEntryId, _) =
                await PostInSelfManagedTxAsync(seed, poster, documentId, InventoryAdjustmentGlKind.WriteOff, amount);

            await AssertJournalEntryAsync(seed, journalEntryId, amount);
            // Write-off uses the same single-adjustment-account shape as Loss (Q2=A).
            await AssertLineAsync(seed, journalEntryId, lineNumber: 1, accountId: seed.InventoryAdjustmentAccountId, debit: amount, credit: 0m);
            await AssertLineAsync(seed, journalEntryId, lineNumber: 2, accountId: seed.InventoryAssetAccountId, debit: 0m, credit: amount);
        }
        finally
        {
            await CleanupAsync(seed);
        }
    }

    [Fact]
    public async Task Idempotent_SecondAppendReturnsExistingJeWithoutInsert()
    {
        var seed = await SeedScenarioAsync();
        try
        {
            var poster = new PostgreSqlInventoryAdjustmentGlPoster();
            var documentId = Guid.NewGuid();

            var first = await PostInSelfManagedTxAsync(seed, poster, documentId, InventoryAdjustmentGlKind.Gain, 100m);
            var firstCount = await CountJournalEntriesAsync(seed, documentId);
            Assert.Equal(1, firstCount);

            // Same source — second call must short-circuit on the
            // (company_id, source_type='inventory_adjustment_gl', source_id) probe.
            var second = await PostInSelfManagedTxAsync(seed, poster, documentId, InventoryAdjustmentGlKind.Gain, 100m, expectAlreadyPosted: true);
            Assert.Equal(first.journalEntryId, second.journalEntryId);
            Assert.Equal(first.displayNumber, second.displayNumber);

            // Still exactly one JE row; the duplicate probe prevented
            // a second INSERT entirely.
            var secondCount = await CountJournalEntriesAsync(seed, documentId);
            Assert.Equal(1, secondCount);
        }
        finally
        {
            await CleanupAsync(seed);
        }
    }

    [Fact]
    public async Task SameTxRollback_PosterWritesAreUndoneWhenCallerRollsBack()
    {
        var seed = await SeedScenarioAsync();
        try
        {
            var poster = new PostgreSqlInventoryAdjustmentGlPoster();
            var documentId = Guid.NewGuid();

            // Open a tx, run AppendAsync, then INTENTIONALLY rollback
            // (simulating what the inventory store would do if a
            // subsequent write threw). The GL rows must disappear with
            // the tx — that's the C3 atomicity guarantee.
            await using (var connection = await OpenWithBypassAsync(seed.Connections))
            {
                await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);

                var result = await poster.AppendAsync(
                    connection,
                    transaction,
                    BuildRequest(seed, documentId, InventoryAdjustmentGlKind.Loss, 75m),
                    CancellationToken.None);
                Assert.False(result.AlreadyPosted);
                Assert.NotEqual(Guid.Empty, result.JournalEntryId);

                // Visible inside the same tx — sanity check that the
                // INSERT actually ran before the rollback.
                Assert.Equal(1, await CountJournalEntriesOnTxAsync(connection, transaction, documentId));

                await transaction.RollbackAsync(CancellationToken.None);
            }

            // After the rollback, the JE / lines / ledger rows must
            // not exist. No leftover ghost rows from the partial run.
            Assert.Equal(0, await CountJournalEntriesAsync(seed, documentId));
            Assert.Equal(0, await CountJournalEntryLinesAsync(seed, documentId));
            Assert.Equal(0, await CountLedgerEntriesAsync(seed, documentId));
        }
        finally
        {
            await CleanupAsync(seed);
        }
    }

    [Fact]
    public async Task MissingAdjustmentSystemRole_AppendThrowsBeforeAnyInsert()
    {
        var seed = await SeedScenarioAsync(pinAdjustmentSystemRole: false);
        try
        {
            var poster = new PostgreSqlInventoryAdjustmentGlPoster();
            var documentId = Guid.NewGuid();

            // Without the inventory_adjustment system role bound,
            // ResolveAccountIdAsync returns null and the poster throws.
            // The caller's catch (== inventory store's outer try/catch)
            // is the line of defense — no partial state lands.
            await using var connection = await OpenWithBypassAsync(seed.Connections);
            await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                poster.AppendAsync(
                    connection,
                    transaction,
                    BuildRequest(seed, documentId, InventoryAdjustmentGlKind.Loss, 50m),
                    CancellationToken.None));
            Assert.Contains("inventory_adjustment", ex.Message);

            await transaction.RollbackAsync(CancellationToken.None);

            Assert.Equal(0, await CountJournalEntriesAsync(seed, documentId));
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
        Guid InventoryAdjustmentAccountId,
        string BaseCurrencyCode);

    private static async Task<SeedScenario> SeedScenarioAsync(bool pinAdjustmentSystemRole = true)
    {
        var connections = new PostgreSqlConnectionFactory(GetConnectionString());
        var rand = Random.Shared.Next(6000, 6999);
        var companyId = CompanyId.FromOrdinal(rand);
        var userId = UserId.FromOrdinal(rand);
        var inventoryAssetAccountId = Guid.NewGuid();
        var inventoryAdjustmentAccountId = Guid.NewGuid();

        // M13 RLS is forced on every table with company_id; the test
        // connection runs as citus_app which is non-bypassrls, so we
        // set the bypass GUC for the test's setup INSERTs. The
        // production connection factory is expected to do the same
        // (or set app.company_id to scope queries) at request-open
        // time; tests bypass to keep seed/cleanup simple.
        await using var connection = await OpenWithBypassAsync(connections);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            insert into companies (id, entity_number, legal_name, base_currency_code, multi_currency_enabled, status)
            values (@company_id, @entity_number, 'InventoryAdjustmentGl Test Co', 'USD', false, 'active');

            insert into users (id, email, username, password_hash, status)
            values (@user_id, @user_email, 'gl.adj.test', 'hashed', 'active');

            insert into accounts (
              id, company_id, code, name, root_type, detail_type, is_active, is_system,
              system_role, system_key, entity_number
            )
            values
              (@asset_account_id, @company_id, '14000', 'Inventory', 'asset', 'inventory', true, true,
               'inventory_asset', 'inventory:asset', @asset_entity_number),
              (@adjustment_account_id, @company_id, '64600', 'Inventory Adjustment', 'expense', 'inventory_adjustment', true, true,
               @adjustment_role, @adjustment_key, @adjustment_entity_number);
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("entity_number", BuildEntityNumber());
        command.Parameters.AddWithValue("user_id", userId.Value);
        command.Parameters.AddWithValue("user_email", $"{userId.Value}@gladjtest.test");
        command.Parameters.AddWithValue("asset_account_id", inventoryAssetAccountId);
        command.Parameters.AddWithValue("asset_entity_number", BuildEntityNumber());
        command.Parameters.AddWithValue("adjustment_account_id", inventoryAdjustmentAccountId);
        command.Parameters.AddWithValue("adjustment_entity_number", BuildEntityNumber());
        command.Parameters.AddWithValue(
            "adjustment_role",
            pinAdjustmentSystemRole ? (object)"inventory_adjustment" : DBNull.Value);
        command.Parameters.AddWithValue(
            "adjustment_key",
            pinAdjustmentSystemRole ? (object)"inventory:adjustment" : DBNull.Value);

        await command.ExecuteNonQueryAsync(CancellationToken.None);

        return new SeedScenario(
            connections,
            companyId,
            userId,
            inventoryAssetAccountId,
            inventoryAdjustmentAccountId,
            "USD");
    }

    private static InventoryAdjustmentGlPostingRequest BuildRequest(
        SeedScenario seed,
        Guid documentId,
        InventoryAdjustmentGlKind kind,
        decimal amount)
    {
        return new InventoryAdjustmentGlPostingRequest(
            CompanyId: seed.CompanyId,
            UserId: seed.UserId,
            InventoryDocumentId: documentId,
            InventoryDocumentNumber: $"ADJ-TEST-{documentId.ToString("N").Substring(0, 8)}",
            PostingDate: DateOnly.FromDateTime(DateTime.UtcNow.Date),
            BaseCurrencyCode: seed.BaseCurrencyCode,
            Kind: kind,
            TotalCostBase: amount);
    }

    private static async Task<(Guid journalEntryId, string displayNumber)> PostInSelfManagedTxAsync(
        SeedScenario seed,
        IInventoryAdjustmentGlPoster poster,
        Guid documentId,
        InventoryAdjustmentGlKind kind,
        decimal amount,
        bool expectAlreadyPosted = false)
    {
        await using var connection = await OpenWithBypassAsync(seed.Connections);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);

        var result = await poster.AppendAsync(
            connection,
            transaction,
            BuildRequest(seed, documentId, kind, amount),
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
        Assert.Equal("inventory_adjustment_gl", reader.GetString(reader.GetOrdinal("source_type")));
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
               and source_type = 'inventory_adjustment_gl'
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
             where source_type = 'inventory_adjustment_gl'
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
               and e.source_type = 'inventory_adjustment_gl'
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
               and e.source_type = 'inventory_adjustment_gl'
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
               and e.source_type = 'inventory_adjustment_gl';

            delete from journal_entry_lines l
             using journal_entries e
             where l.journal_entry_id = e.id
               and e.company_id = @company_id
               and e.source_type = 'inventory_adjustment_gl';

            delete from journal_entries
             where company_id = @company_id
               and source_type = 'inventory_adjustment_gl';

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

    private static string GetConnectionString() =>
        Environment.GetEnvironmentVariable("CITUS_ACCOUNTING_DB")
        ?? "Host=localhost;Port=5432;Database=citus_accounting;Username=postgres;Password=change-me";

    // M13 RLS bypass for test connections. The test role (citus_app)
    // is non-bypassrls, so without setting this GUC every INSERT into
    // a tenant-scoped table fails its WITH CHECK clause. Production
    // uses the same mechanism, just driven by the connection factory.
    private static async Task<NpgsqlConnection> OpenWithBypassAsync(PostgreSqlConnectionFactory connections)
    {
        var connection = await connections.OpenAsync(CancellationToken.None);
        await SetBypassRlsAsync(connection, CancellationToken.None);
        return connection;
    }

    private static async Task SetBypassRlsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "select set_config('app.bypass_company_filter', 'true', false);";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string BuildEntityNumber()
    {
        var ordinal = Math.Abs(BitConverter.ToInt32(Guid.NewGuid().ToByteArray(), 0)) % 60_466_176;
        return EntityNumber.Create(2099, ordinal).Value;
    }
}
