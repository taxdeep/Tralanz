using Citus.Modules.Inventory.Application.Contracts;
using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.Inventory;
using Npgsql;

namespace Tests.Inventory;

/// <summary>
/// M4 (AUDIT_2026-05-20 P2-10): contract tests for the inventory
/// receipt UoW + ambient execution-context accessor.
///
/// These tests pin down the atomicity guarantee that PostReceiptWorkflow
/// now relies on: ExecuteAsync opens one Npgsql transaction, publishes
/// it on the AsyncLocal accessor, runs the caller's action, then
/// commits — or rolls back on any unhandled exception. Re-entrancy
/// rules: a nested ExecuteAsync joins the outer tx (no new tx). The
/// accessor is cleared in `finally` whether the action succeeded or
/// failed.
///
/// Why a junk table rather than the real receipt tables: the receipt
/// flow seed is heavy (companies, items, warehouses, bills, matching
/// allocations). The UoW's correctness is independent of which
/// downstream table the action touches, so a single-column scratch
/// table proves commit/rollback semantics with zero accidental
/// coupling.
/// </summary>
public sealed class InventoryReceiptUnitOfWorkTests
{
    [SkippableFact]
    public async Task ExecuteAsync_Commits_WhenActionSucceeds()
    {
        var seed = await SeedScenarioAsync();
        try
        {
            var accessor = new InventoryReceiptExecutionContextAccessor();
            var uow = new PostgreSqlInventoryReceiptUnitOfWork(seed.Connections, accessor);
            var marker = Guid.NewGuid();

            await uow.ExecuteAsync(async ct =>
            {
                Assert.NotNull(accessor.Current);
                await InsertMarkerAsync(accessor.Current!.Connection, accessor.Current.Transaction, marker, ct);
            }, CancellationToken.None);

            Assert.Null(accessor.Current);
            Assert.Equal(1, await CountMarkerAsync(seed.Connections, marker));
        }
        finally
        {
            await CleanupAsync(seed);
        }
    }

    [SkippableFact]
    public async Task ExecuteAsync_RollsBack_WhenActionThrows()
    {
        var seed = await SeedScenarioAsync();
        try
        {
            var accessor = new InventoryReceiptExecutionContextAccessor();
            var uow = new PostgreSqlInventoryReceiptUnitOfWork(seed.Connections, accessor);
            var marker = Guid.NewGuid();

            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                uow.ExecuteAsync(async ct =>
                {
                    await InsertMarkerAsync(accessor.Current!.Connection, accessor.Current.Transaction, marker, ct);
                    // Inner write committed inside the tx; the throw
                    // forces ExecuteAsync to roll back, so the marker
                    // row should NOT survive after this call returns.
                    throw new InvalidOperationException("test rollback");
                }, CancellationToken.None));
            Assert.Equal("test rollback", thrown.Message);

            // Accessor cleared even though we threw.
            Assert.Null(accessor.Current);
            // Row never made it to a committed state.
            Assert.Equal(0, await CountMarkerAsync(seed.Connections, marker));
        }
        finally
        {
            await CleanupAsync(seed);
        }
    }

    [SkippableFact]
    public async Task ExecuteAsync_NestedCall_JoinsOuterTxWithoutOpeningNew()
    {
        var seed = await SeedScenarioAsync();
        try
        {
            var accessor = new InventoryReceiptExecutionContextAccessor();
            var uow = new PostgreSqlInventoryReceiptUnitOfWork(seed.Connections, accessor);
            var marker = Guid.NewGuid();

            await uow.ExecuteAsync(async outerCt =>
            {
                var outerContext = accessor.Current;
                Assert.NotNull(outerContext);

                // Nested call: must NOT replace the ambient context,
                // must NOT open a new tx. The inner action sees the
                // outer's connection/transaction.
                await uow.ExecuteAsync(async innerCt =>
                {
                    Assert.Same(outerContext, accessor.Current);
                    await InsertMarkerAsync(accessor.Current!.Connection, accessor.Current.Transaction, marker, innerCt);
                }, outerCt);

                // Outer ambient still intact after nested ExecuteAsync
                // returns (inner did not clear it).
                Assert.Same(outerContext, accessor.Current);
            }, CancellationToken.None);

            // Only one commit happened (the outer's). Inner write
            // landed because the outer committed.
            Assert.Equal(1, await CountMarkerAsync(seed.Connections, marker));
        }
        finally
        {
            await CleanupAsync(seed);
        }
    }

    [SkippableFact]
    public async Task ExecuteAsync_NestedCallInnerThrow_RollsBackEverything()
    {
        var seed = await SeedScenarioAsync();
        try
        {
            var accessor = new InventoryReceiptExecutionContextAccessor();
            var uow = new PostgreSqlInventoryReceiptUnitOfWork(seed.Connections, accessor);
            var outerMarker = Guid.NewGuid();
            var innerMarker = Guid.NewGuid();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                uow.ExecuteAsync(async outerCt =>
                {
                    await InsertMarkerAsync(accessor.Current!.Connection, accessor.Current.Transaction, outerMarker, outerCt);

                    await uow.ExecuteAsync(async innerCt =>
                    {
                        await InsertMarkerAsync(accessor.Current!.Connection, accessor.Current.Transaction, innerMarker, innerCt);
                        // Inner throws — does NOT rollback its own tx
                        // (there is none, it's joined). The exception
                        // propagates to the outer's catch which rolls
                        // the WHOLE tx back, including the outer's
                        // marker insert.
                        throw new InvalidOperationException("inner failure");
                    }, outerCt);
                }, CancellationToken.None));

            // Both markers gone — single tx covers both inserts.
            Assert.Equal(0, await CountMarkerAsync(seed.Connections, outerMarker));
            Assert.Equal(0, await CountMarkerAsync(seed.Connections, innerMarker));
            Assert.Null(accessor.Current);
        }
        finally
        {
            await CleanupAsync(seed);
        }
    }

    [SkippableFact]
    public async Task ExecuteAsync_RollsBack_AcrossMultipleStoresInOneAction()
    {
        // The point of M4: when the workflow's action calls multiple
        // stores in sequence and a later one throws, EVERY prior
        // store's writes (made on the ambient tx) get rolled back.
        // We simulate that here by writing N markers across the
        // single action and then throwing.
        var seed = await SeedScenarioAsync();
        try
        {
            var accessor = new InventoryReceiptExecutionContextAccessor();
            var uow = new PostgreSqlInventoryReceiptUnitOfWork(seed.Connections, accessor);
            var step1 = Guid.NewGuid();
            var step2 = Guid.NewGuid();
            var step3 = Guid.NewGuid();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                uow.ExecuteAsync(async ct =>
                {
                    // Simulate step 1 (activation) — succeeds
                    await InsertMarkerAsync(accessor.Current!.Connection, accessor.Current.Transaction, step1, ct);
                    // Simulate step 2 (valuation) — succeeds
                    await InsertMarkerAsync(accessor.Current!.Connection, accessor.Current.Transaction, step2, ct);
                    // Simulate step 3 (emission) — throws
                    await InsertMarkerAsync(accessor.Current!.Connection, accessor.Current.Transaction, step3, ct);
                    throw new InvalidOperationException("emission failed");
                }, CancellationToken.None));

            // ALL three steps rolled back — the C3-style atomicity
            // guarantee the workflow relies on.
            Assert.Equal(0, await CountMarkerAsync(seed.Connections, step1));
            Assert.Equal(0, await CountMarkerAsync(seed.Connections, step2));
            Assert.Equal(0, await CountMarkerAsync(seed.Connections, step3));
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
        string TableName);

    private static async Task<SeedScenario> SeedScenarioAsync()
    {
        var connections = new PostgreSqlConnectionFactory(GetConnectionString());
        // Scratch table for proving commit/rollback semantics.
        // Per-test schema is overkill; one shared table works because
        // each test inserts a unique marker GUID and queries by it.
        var tableName = $"m4_uow_test_markers";

        await using var connection = await OpenWithBypassAsync(connections);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            create table if not exists {tableName} (
              marker uuid primary key
            );
            """;
        await command.ExecuteNonQueryAsync(CancellationToken.None);

        return new SeedScenario(connections, tableName);
    }

    private static async Task InsertMarkerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid marker,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "insert into m4_uow_test_markers (marker) values (@marker);";
        command.Parameters.AddWithValue("marker", marker);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> CountMarkerAsync(PostgreSqlConnectionFactory connections, Guid marker)
    {
        await using var connection = await OpenWithBypassAsync(connections);
        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*)::int from m4_uow_test_markers where marker = @marker;";
        command.Parameters.AddWithValue("marker", marker);
        return (int)(await command.ExecuteScalarAsync(CancellationToken.None) ?? 0);
    }

    private static async Task CleanupAsync(SeedScenario seed)
    {
        // Drop the scratch table only after all tests run; the test
        // process-end will leave it harmlessly. We DO clean up the
        // markers each test wrote, so tables stay small.
        await using var connection = await OpenWithBypassAsync(seed.Connections);
        await using var command = connection.CreateCommand();
        command.CommandText = "delete from m4_uow_test_markers;";
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
        // m4_uow_test_markers has no company_id, so RLS doesn't apply
        // — but the bypass call is harmless. Other tests in this
        // project rely on the same pattern.
        await using var command = connection.CreateCommand();
        command.CommandText = "select set_config('app.bypass_company_filter', 'true', false);";
        await command.ExecuteNonQueryAsync(CancellationToken.None);
        return connection;
    }
}
