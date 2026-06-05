using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.Inventory.Posting;
using Npgsql;

namespace Tests.Inventory;

/// <summary>
/// P0-3b-3 (AUDIT_2026-05-20 C3 final closure): contract tests for the
/// transfer GL poster. The transfer flow is two-step (Ship and
/// Receive), each producing its own JE keyed on a distinct source_type
/// so the two legs are independently idempotent.
///
/// V1 single-account JE shape for both legs:
///   Dr 14000 Inventory
///   Cr 14000 Inventory
/// Net balance impact is zero; the JEs provide the GL audit trail
/// for warehouse-to-warehouse movement. When the COA evolves to per-
/// warehouse or in-transit accounts, the credit-side resolution
/// changes per leg without touching the rest of the flow.
///
/// Tests verify:
///   1. Ship leg JE shape (source_type='inventory_transfer_ship_gl')
///   2. Receive leg JE shape (source_type='inventory_transfer_receive_gl')
///   3. Per-leg idempotency: a second Append on the same leg is a no-op
///   4. Ship and Receive are independent: posting one doesn't affect the other
///   5. Same-tx rollback: poster writes vanish when caller rolls back
///   6. Missing inventory_asset SystemRole throws before any INSERT
///   7. Zero/negative amount is rejected
/// </summary>
public sealed class InventoryTransferGlPostingTests
{
    [SkippableFact]
    public async Task Ship_ProducesBalancedTwoLineJeOnInventoryAssetAccount()
    {
        var seed = await SeedScenarioAsync();
        try
        {
            var poster = new PostgreSqlInventoryTransferGlPoster();
            var transferId = Guid.NewGuid();
            var amount = 320.00m;

            var (journalEntryId, displayNumber) =
                await PostInSelfManagedTxAsync(seed, poster, transferId, InventoryTransferGlLeg.Ship, amount);

            Assert.NotEqual(Guid.Empty, journalEntryId);
            Assert.StartsWith("JE-", displayNumber);

            await AssertJournalEntryAsync(seed, journalEntryId, expectedTotal: amount, expectedSourceType: "inventory_transfer_ship_gl");
            await AssertLineAsync(seed, journalEntryId, lineNumber: 1, accountId: seed.InventoryAssetAccountId, debit: amount, credit: 0m);
            await AssertLineAsync(seed, journalEntryId, lineNumber: 2, accountId: seed.InventoryAssetAccountId, debit: 0m, credit: amount);
            await AssertLedgerEntryCountAsync(seed, journalEntryId, expected: 2);
        }
        finally
        {
            await CleanupAsync(seed);
        }
    }

    [SkippableFact]
    public async Task Receive_ProducesBalancedTwoLineJeOnInventoryAssetAccount()
    {
        var seed = await SeedScenarioAsync();
        try
        {
            var poster = new PostgreSqlInventoryTransferGlPoster();
            var transferId = Guid.NewGuid();
            var amount = 320.00m;

            var (journalEntryId, _) =
                await PostInSelfManagedTxAsync(seed, poster, transferId, InventoryTransferGlLeg.Receive, amount);

            await AssertJournalEntryAsync(seed, journalEntryId, expectedTotal: amount, expectedSourceType: "inventory_transfer_receive_gl");
            await AssertLineAsync(seed, journalEntryId, lineNumber: 1, accountId: seed.InventoryAssetAccountId, debit: amount, credit: 0m);
            await AssertLineAsync(seed, journalEntryId, lineNumber: 2, accountId: seed.InventoryAssetAccountId, debit: 0m, credit: amount);
        }
        finally
        {
            await CleanupAsync(seed);
        }
    }

    [SkippableFact]
    public async Task ShipAndReceive_AreIndependentlyIdempotentAndCoexistOnSameTransferId()
    {
        var seed = await SeedScenarioAsync();
        try
        {
            var poster = new PostgreSqlInventoryTransferGlPoster();
            var transferId = Guid.NewGuid();
            var amount = 110.00m;

            // Ship leg posts
            var ship1 = await PostInSelfManagedTxAsync(seed, poster, transferId, InventoryTransferGlLeg.Ship, amount);
            Assert.Equal(1, await CountJournalEntriesAsync(seed, transferId, sourceType: "inventory_transfer_ship_gl"));
            Assert.Equal(0, await CountJournalEntriesAsync(seed, transferId, sourceType: "inventory_transfer_receive_gl"));

            // Receive leg posts on the SAME transferId — separate JE, no conflict.
            var receive1 = await PostInSelfManagedTxAsync(seed, poster, transferId, InventoryTransferGlLeg.Receive, amount);
            Assert.Equal(1, await CountJournalEntriesAsync(seed, transferId, sourceType: "inventory_transfer_ship_gl"));
            Assert.Equal(1, await CountJournalEntriesAsync(seed, transferId, sourceType: "inventory_transfer_receive_gl"));
            Assert.NotEqual(ship1.journalEntryId, receive1.journalEntryId);

            // Re-running Ship on the same transferId → returns existing
            // Ship JE, no new row, no impact on Receive.
            var ship2 = await PostInSelfManagedTxAsync(seed, poster, transferId, InventoryTransferGlLeg.Ship, amount, expectAlreadyPosted: true);
            Assert.Equal(ship1.journalEntryId, ship2.journalEntryId);
            Assert.Equal(1, await CountJournalEntriesAsync(seed, transferId, sourceType: "inventory_transfer_ship_gl"));
            Assert.Equal(1, await CountJournalEntriesAsync(seed, transferId, sourceType: "inventory_transfer_receive_gl"));

            // Same for Receive.
            var receive2 = await PostInSelfManagedTxAsync(seed, poster, transferId, InventoryTransferGlLeg.Receive, amount, expectAlreadyPosted: true);
            Assert.Equal(receive1.journalEntryId, receive2.journalEntryId);
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
            var poster = new PostgreSqlInventoryTransferGlPoster();
            var transferId = Guid.NewGuid();

            await using (var connection = await OpenWithBypassAsync(seed.Connections))
            {
                await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);

                var result = await poster.AppendAsync(
                    connection,
                    transaction,
                    BuildRequest(seed, transferId, InventoryTransferGlLeg.Ship, amount: 99.00m),
                    CancellationToken.None);
                Assert.False(result.AlreadyPosted);

                // Visible inside the tx
                Assert.Equal(1, await CountJournalEntriesOnTxAsync(connection, transaction, transferId, "inventory_transfer_ship_gl"));

                await transaction.RollbackAsync(CancellationToken.None);
            }

            // After rollback the rows are gone
            Assert.Equal(0, await CountJournalEntriesAsync(seed, transferId, "inventory_transfer_ship_gl"));
            Assert.Equal(0, await CountJournalEntryLinesAsync(seed, transferId, "inventory_transfer_ship_gl"));
            Assert.Equal(0, await CountLedgerEntriesAsync(seed, transferId, "inventory_transfer_ship_gl"));
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
            var poster = new PostgreSqlInventoryTransferGlPoster();
            var transferId = Guid.NewGuid();

            await using var connection = await OpenWithBypassAsync(seed.Connections);
            await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                poster.AppendAsync(
                    connection,
                    transaction,
                    BuildRequest(seed, transferId, InventoryTransferGlLeg.Receive, amount: 75.00m),
                    CancellationToken.None));
            Assert.Contains("inventory_asset", ex.Message);

            await transaction.RollbackAsync(CancellationToken.None);

            Assert.Equal(0, await CountJournalEntriesAsync(seed, transferId, "inventory_transfer_receive_gl"));
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
            var poster = new PostgreSqlInventoryTransferGlPoster();
            var transferId = Guid.NewGuid();

            await using var connection = await OpenWithBypassAsync(seed.Connections);
            await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                poster.AppendAsync(
                    connection,
                    transaction,
                    BuildRequest(seed, transferId, InventoryTransferGlLeg.Ship, amount: 0m),
                    CancellationToken.None));
            Assert.Contains("strictly positive", ex.Message);

            await transaction.RollbackAsync(CancellationToken.None);
            Assert.Equal(0, await CountJournalEntriesAsync(seed, transferId, "inventory_transfer_ship_gl"));
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
        var rand = Random.Shared.Next(8000, 8999);
        var companyId = CompanyId.FromOrdinal(rand);
        var userId = UserId.FromOrdinal(rand);
        var inventoryAssetAccountId = Guid.NewGuid();

        await using var connection = await OpenWithBypassAsync(connections);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into companies (id, entity_number, legal_name, base_currency_code, multi_currency_enabled, status)
            values (@company_id, @entity_number, 'InventoryTransferGl Test Co', 'USD', false, 'active');

            insert into users (id, email, username, password_hash, status)
            values (@user_id, @user_email, 'gl.trf.test', 'hashed', 'active');

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
        command.Parameters.AddWithValue("user_email", $"{userId.Value}@gltrftest.test");
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

    private static InventoryTransferGlPostingRequest BuildRequest(
        SeedScenario seed,
        Guid transferId,
        InventoryTransferGlLeg leg,
        decimal amount)
    {
        var token = transferId.ToString("N").Substring(0, 8);
        return new InventoryTransferGlPostingRequest(
            CompanyId: seed.CompanyId,
            UserId: seed.UserId,
            TransferId: transferId,
            TransferNumber: $"TRF-{token}",
            LegDocumentNumber: leg == InventoryTransferGlLeg.Ship ? $"TRSHIP-TRF-{token}" : $"TRRCV-TRF-{token}",
            PostingDate: DateOnly.FromDateTime(DateTime.UtcNow.Date),
            BaseCurrencyCode: seed.BaseCurrencyCode,
            Leg: leg,
            TotalCostBase: amount);
    }

    private static async Task<(Guid journalEntryId, string displayNumber)> PostInSelfManagedTxAsync(
        SeedScenario seed,
        IInventoryTransferGlPoster poster,
        Guid transferId,
        InventoryTransferGlLeg leg,
        decimal amount,
        bool expectAlreadyPosted = false)
    {
        await using var connection = await OpenWithBypassAsync(seed.Connections);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);

        var result = await poster.AppendAsync(
            connection,
            transaction,
            BuildRequest(seed, transferId, leg, amount),
            CancellationToken.None);

        await transaction.CommitAsync(CancellationToken.None);

        Assert.Equal(expectAlreadyPosted, result.AlreadyPosted);
        return (result.JournalEntryId, result.JournalEntryDisplayNumber);
    }

    private static async Task AssertJournalEntryAsync(
        SeedScenario seed,
        Guid journalEntryId,
        decimal expectedTotal,
        string expectedSourceType)
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
        Assert.Equal(expectedSourceType, reader.GetString(reader.GetOrdinal("source_type")));
        Assert.Equal(seed.BaseCurrencyCode, reader.GetString(reader.GetOrdinal("transaction_currency_code")));
        Assert.Equal(seed.BaseCurrencyCode, reader.GetString(reader.GetOrdinal("base_currency_code")));
        Assert.Equal(expectedTotal, reader.GetDecimal(reader.GetOrdinal("total_debit")));
        Assert.Equal(expectedTotal, reader.GetDecimal(reader.GetOrdinal("total_credit")));
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

    private static async Task<int> CountJournalEntriesAsync(SeedScenario seed, Guid sourceId, string sourceType)
    {
        await using var connection = await OpenWithBypassAsync(seed.Connections);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select count(*)::int from journal_entries
             where company_id = @company_id
               and source_type = @source_type
               and source_id = @source_id;
            """;
        command.Parameters.AddWithValue("company_id", seed.CompanyId.Value);
        command.Parameters.AddWithValue("source_type", sourceType);
        command.Parameters.AddWithValue("source_id", sourceId);
        return (int)(await command.ExecuteScalarAsync(CancellationToken.None) ?? 0);
    }

    private static async Task<int> CountJournalEntriesOnTxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sourceId,
        string sourceType)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select count(*)::int from journal_entries
             where source_type = @source_type
               and source_id = @source_id;
            """;
        command.Parameters.AddWithValue("source_type", sourceType);
        command.Parameters.AddWithValue("source_id", sourceId);
        return (int)(await command.ExecuteScalarAsync(CancellationToken.None) ?? 0);
    }

    private static async Task<int> CountJournalEntryLinesAsync(SeedScenario seed, Guid sourceId, string sourceType)
    {
        await using var connection = await OpenWithBypassAsync(seed.Connections);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select count(*)::int from journal_entry_lines l
             inner join journal_entries e on e.id = l.journal_entry_id
             where e.company_id = @company_id
               and e.source_type = @source_type
               and e.source_id = @source_id;
            """;
        command.Parameters.AddWithValue("company_id", seed.CompanyId.Value);
        command.Parameters.AddWithValue("source_type", sourceType);
        command.Parameters.AddWithValue("source_id", sourceId);
        return (int)(await command.ExecuteScalarAsync(CancellationToken.None) ?? 0);
    }

    private static async Task<int> CountLedgerEntriesAsync(SeedScenario seed, Guid sourceId, string sourceType)
    {
        await using var connection = await OpenWithBypassAsync(seed.Connections);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select count(*)::int from ledger_entries le
             inner join journal_entries e on e.id = le.journal_entry_id
             where e.company_id = @company_id
               and e.source_type = @source_type
               and e.source_id = @source_id;
            """;
        command.Parameters.AddWithValue("company_id", seed.CompanyId.Value);
        command.Parameters.AddWithValue("source_type", sourceType);
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
               and e.source_type like 'inventory_transfer_%_gl';

            delete from journal_entry_lines l
             using journal_entries e
             where l.journal_entry_id = e.id
               and e.company_id = @company_id
               and e.source_type like 'inventory_transfer_%_gl';

            delete from journal_entries
             where company_id = @company_id
               and source_type like 'inventory_transfer_%_gl';

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
