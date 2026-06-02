using Citus.Modules.Inventory.Application.Contracts;
using Citus.Modules.Inventory.Domain.Shared;
using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.Inventory;
using Npgsql;
using Xunit;

namespace Citus.Accounting.IntegrationTests;

public sealed class PostgreSqlInventoryAdjustmentIdempotencyIntegrationTests
{
    [Fact]
    public async Task PostAsync_WithSameClientRequestId_DoesNotDuplicateInventoryOrGlTruth()
    {
        var baseConnectionString = GetPostgreSqlConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            return;
        }

        var schemaName = $"citus_inv_adj_{Guid.NewGuid():N}";
        await CreateSchemaAsync(baseConnectionString, schemaName);

        try
        {
            var schemaConnectionString = BuildSchemaConnectionString(baseConnectionString, schemaName);
            var connectionFactory = new PostgreSqlConnectionFactory(schemaConnectionString);
            var foundationStore = new PostgreSqlInventoryFoundationStore(connectionFactory);
            var adjustmentStore = new PostgreSqlInventoryAdjustmentStore(connectionFactory, foundationStore);

            var companyId = CompanyId.FromOrdinal(1);
            var userId = UserId.FromOrdinal(1);
            var inventoryAssetAccountId = Guid.NewGuid();
            var adjustmentAccountId = Guid.NewGuid();
            var clientRequestId = Guid.NewGuid();

            await SeedCompanyAsync(schemaConnectionString, companyId);
            await SeedPostingFoundationAsync(schemaConnectionString);
            await SeedAccountAsync(
                schemaConnectionString,
                companyId,
                inventoryAssetAccountId,
                "1200",
                "Inventory Asset",
                "asset",
                systemRole: "inventory_asset");
            await SeedAccountAsync(
                schemaConnectionString,
                companyId,
                adjustmentAccountId,
                "4910",
                "Inventory Adjustment",
                "revenue",
                systemRole: "inventory_adjustment");

            await foundationStore.EnsureSchemaAsync(CancellationToken.None);
            await adjustmentStore.EnsureSchemaAsync(CancellationToken.None);
            await foundationStore.EnsureCompanyFoundationAsync(
                new InventoryFoundationEnsureRequest(
                    companyId,
                    userId,
                    InventoryCostingMethod.Fifo,
                    NegativeStockAllowed: false,
                    RequireWriteOffApproval: true),
                CancellationToken.None);

            var warehouseId = await foundationStore.SaveWarehouseAsync(
                new InventoryWarehouseUpsertRequest(
                    companyId,
                    userId,
                    WarehouseId: null,
                    "MAIN",
                    "Main",
                    null),
                CancellationToken.None);
            var itemId = await foundationStore.SaveItemAsync(
                new InventoryItemUpsertRequest(
                    companyId,
                    userId,
                    ItemId: null,
                    "SKU-ADJ",
                    "Adjustment Item",
                    null,
                    InventoryItemKind.Stock,
                    "EA",
                    ManageInventoryMethod.ManageStock,
                    InventoryCostingMethod.Fifo,
                    InventoryBackorderMode.Disallow,
                    InventoryLowStockActivity.Nothing,
                    inventoryAssetAccountId,
                    DefaultCogsAccountId: null,
                    DefaultWriteOffAccountId: null,
                    DefaultPurchaseVarianceAccountId: null,
                    DefaultSalesRevenueAccountId: null,
                    DefaultDropShipClearingAccountId: null,
                    DefaultSalesPrice: null,
                    DefaultPurchasePrice: null,
                    DefaultSalesTaxCodeId: null,
                    DefaultPurchaseTaxCodeId: null),
                CancellationToken.None);

            var request = new InventoryAdjustmentPostRequest(
                companyId,
                userId,
                InventoryAdjustmentKind.Gain,
                warehouseId,
                new DateOnly(2026, 5, 23),
                "idempotency test",
                new[]
                {
                    new InventoryAdjustmentLineInput(
                        1,
                        itemId,
                        "EA",
                        2m,
                        10m,
                        null,
                        null)
                },
                ClientRequestId: clientRequestId);

            var results = await Task.WhenAll(
                adjustmentStore.PostAsync(request, CancellationToken.None),
                adjustmentStore.PostAsync(request, CancellationToken.None));

            Assert.Equal(clientRequestId, results[0].DocumentId);
            Assert.Equal(results[0].DocumentId, results[1].DocumentId);
            Assert.Equal(results[0].DocumentNumber, results[1].DocumentNumber);
            Assert.Equal("posted", results[0].Status);
            Assert.Equal(2m, results[0].TotalQuantity);
            Assert.Equal(20m, results[0].TotalCostBase);

            Assert.Equal(1, await CountRowsAsync(schemaConnectionString, "inventory_documents"));
            Assert.Equal(1, await CountRowsAsync(schemaConnectionString, "inventory_document_lines"));
            Assert.Equal(1, await CountRowsAsync(schemaConnectionString, "inventory_ledger_entries"));
            Assert.Equal(1, await CountRowsAsync(schemaConnectionString, "inventory_cost_layers"));
            Assert.Equal(1, await CountRowsAsync(schemaConnectionString, "journal_entries"));
            Assert.Equal(2, await CountRowsAsync(schemaConnectionString, "journal_entry_lines"));
            Assert.Equal(2, await CountRowsAsync(schemaConnectionString, "ledger_entries"));
            Assert.Equal(1, await CountRowsAsync(schemaConnectionString, "audit_logs"));

            Assert.Equal("inventory_adjustment_gain", await ReadScalarAsync<string>(
                schemaConnectionString,
                "select source_type from journal_entries limit 1;"));
            Assert.Equal(2m, await ReadScalarAsync<decimal>(
                schemaConnectionString,
                "select on_hand_qty from item_warehouse_balances where item_id = @id;",
                ("id", itemId)));
            Assert.Equal(20m, await ReadScalarAsync<decimal>(
                schemaConnectionString,
                "select remaining_cost_base from inventory_cost_layers where source_document_id = @id;",
                ("id", clientRequestId)));
            Assert.Equal(results[0].DocumentNumber, await ReadScalarAsync<string>(
                schemaConnectionString,
                "select document_number from inventory_documents where id = @id;",
                ("id", clientRequestId)));

            var conflictingRequest = new InventoryAdjustmentPostRequest(
                companyId,
                userId,
                InventoryAdjustmentKind.Gain,
                warehouseId,
                new DateOnly(2026, 5, 23),
                "idempotency test changed",
                new[]
                {
                    new InventoryAdjustmentLineInput(
                        1,
                        itemId,
                        "EA",
                        3m,
                        10m,
                        null,
                        null)
                },
                ClientRequestId: clientRequestId);

            var conflict = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                adjustmentStore.PostAsync(conflictingRequest, CancellationToken.None));
            Assert.Contains("different inventory request payload", conflict.Message);

            Assert.Equal(1, await CountRowsAsync(schemaConnectionString, "inventory_documents"));
            Assert.Equal(1, await CountRowsAsync(schemaConnectionString, "inventory_ledger_entries"));
            Assert.Equal(1, await CountRowsAsync(schemaConnectionString, "journal_entries"));
            Assert.Equal(1, await CountRowsAsync(schemaConnectionString, "audit_logs"));
        }
        finally
        {
            await DropSchemaAsync(baseConnectionString, schemaName);
        }
    }

    [Fact]
    public async Task PostAsync_WithWrongAdjustmentAccountRootType_RollsBackBeforeInventoryOrGlMutation()
    {
        var baseConnectionString = GetPostgreSqlConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            return;
        }

        var schemaName = $"citus_inv_adj_{Guid.NewGuid():N}";
        await CreateSchemaAsync(baseConnectionString, schemaName);

        try
        {
            var schemaConnectionString = BuildSchemaConnectionString(baseConnectionString, schemaName);
            var connectionFactory = new PostgreSqlConnectionFactory(schemaConnectionString);
            var foundationStore = new PostgreSqlInventoryFoundationStore(connectionFactory);
            var adjustmentStore = new PostgreSqlInventoryAdjustmentStore(connectionFactory, foundationStore);

            var companyId = CompanyId.FromOrdinal(1);
            var userId = UserId.FromOrdinal(1);
            var inventoryAssetAccountId = Guid.NewGuid();
            var wrongAdjustmentAccountId = Guid.NewGuid();

            await SeedCompanyAsync(schemaConnectionString, companyId);
            await SeedPostingFoundationAsync(schemaConnectionString);
            await SeedAccountAsync(
                schemaConnectionString,
                companyId,
                inventoryAssetAccountId,
                "1200",
                "Inventory Asset",
                "asset",
                systemRole: "inventory_asset");
            await SeedAccountAsync(
                schemaConnectionString,
                companyId,
                wrongAdjustmentAccountId,
                "2300",
                "Wrong Adjustment Liability",
                "liability",
                systemRole: "inventory_adjustment");

            await foundationStore.EnsureSchemaAsync(CancellationToken.None);
            await adjustmentStore.EnsureSchemaAsync(CancellationToken.None);
            await foundationStore.EnsureCompanyFoundationAsync(
                new InventoryFoundationEnsureRequest(
                    companyId,
                    userId,
                    InventoryCostingMethod.Fifo,
                    NegativeStockAllowed: false,
                    RequireWriteOffApproval: true),
                CancellationToken.None);

            var warehouseId = await foundationStore.SaveWarehouseAsync(
                new InventoryWarehouseUpsertRequest(
                    companyId,
                    userId,
                    WarehouseId: null,
                    "MAIN",
                    "Main",
                    null),
                CancellationToken.None);
            var itemId = await foundationStore.SaveItemAsync(
                new InventoryItemUpsertRequest(
                    companyId,
                    userId,
                    ItemId: null,
                    "SKU-BAD",
                    "Bad Adjustment Item",
                    null,
                    InventoryItemKind.Stock,
                    "EA",
                    ManageInventoryMethod.ManageStock,
                    InventoryCostingMethod.Fifo,
                    InventoryBackorderMode.Disallow,
                    InventoryLowStockActivity.Nothing,
                    inventoryAssetAccountId,
                    DefaultCogsAccountId: null,
                    DefaultWriteOffAccountId: null,
                    DefaultPurchaseVarianceAccountId: null,
                    DefaultSalesRevenueAccountId: null,
                    DefaultDropShipClearingAccountId: null,
                    DefaultSalesPrice: null,
                    DefaultPurchasePrice: null,
                    DefaultSalesTaxCodeId: null,
                    DefaultPurchaseTaxCodeId: null),
                CancellationToken.None);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                adjustmentStore.PostAsync(
                    new InventoryAdjustmentPostRequest(
                        companyId,
                        userId,
                        InventoryAdjustmentKind.Gain,
                        warehouseId,
                        new DateOnly(2026, 5, 23),
                        "bad account root",
                        new[]
                        {
                            new InventoryAdjustmentLineInput(
                                1,
                                itemId,
                                "EA",
                                2m,
                                10m,
                                null,
                                null)
                        },
                        ClientRequestId: Guid.NewGuid()),
                    CancellationToken.None));

            Assert.Contains("root type", exception.Message);
            Assert.Equal(0, await CountRowsAsync(schemaConnectionString, "inventory_documents"));
            Assert.Equal(0, await CountRowsAsync(schemaConnectionString, "inventory_ledger_entries"));
            Assert.Equal(0, await CountRowsAsync(schemaConnectionString, "inventory_cost_layers"));
            Assert.Equal(0, await CountRowsAsync(schemaConnectionString, "journal_entries"));
            Assert.Equal(0, await CountRowsAsync(schemaConnectionString, "audit_logs"));
        }
        finally
        {
            await DropSchemaAsync(baseConnectionString, schemaName);
        }
    }

    [Fact]
    public async Task ApprovedWriteOff_WithRetries_DoesNotDuplicateInventoryOrGlTruth()
    {
        var baseConnectionString = GetPostgreSqlConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            return;
        }

        var schemaName = $"citus_inv_adj_{Guid.NewGuid():N}";
        await CreateSchemaAsync(baseConnectionString, schemaName);

        try
        {
            var schemaConnectionString = BuildSchemaConnectionString(baseConnectionString, schemaName);
            var connectionFactory = new PostgreSqlConnectionFactory(schemaConnectionString);
            var foundationStore = new PostgreSqlInventoryFoundationStore(connectionFactory);
            var adjustmentStore = new PostgreSqlInventoryAdjustmentStore(connectionFactory, foundationStore);

            var companyId = CompanyId.FromOrdinal(1);
            var userId = UserId.FromOrdinal(1);
            var inventoryAssetAccountId = Guid.NewGuid();
            var adjustmentAccountId = Guid.NewGuid();
            var writeOffAccountId = Guid.NewGuid();
            var openingClientRequestId = Guid.NewGuid();
            var writeOffClientRequestId = Guid.NewGuid();

            await SeedCompanyAsync(schemaConnectionString, companyId);
            await SeedPostingFoundationAsync(schemaConnectionString);
            await SeedAccountAsync(
                schemaConnectionString,
                companyId,
                inventoryAssetAccountId,
                "1200",
                "Inventory Asset",
                "asset",
                systemRole: "inventory_asset");
            await SeedAccountAsync(
                schemaConnectionString,
                companyId,
                adjustmentAccountId,
                "4910",
                "Inventory Adjustment",
                "revenue",
                systemRole: "inventory_adjustment");
            await SeedAccountAsync(
                schemaConnectionString,
                companyId,
                writeOffAccountId,
                "5920",
                "Inventory Write-Off",
                "expense",
                systemRole: "inventory_write_off");

            await foundationStore.EnsureSchemaAsync(CancellationToken.None);
            await adjustmentStore.EnsureSchemaAsync(CancellationToken.None);
            await foundationStore.EnsureCompanyFoundationAsync(
                new InventoryFoundationEnsureRequest(
                    companyId,
                    userId,
                    InventoryCostingMethod.Fifo,
                    NegativeStockAllowed: false,
                    RequireWriteOffApproval: true),
                CancellationToken.None);

            var warehouseId = await foundationStore.SaveWarehouseAsync(
                new InventoryWarehouseUpsertRequest(
                    companyId,
                    userId,
                    WarehouseId: null,
                    "MAIN",
                    "Main",
                    null),
                CancellationToken.None);
            var itemId = await foundationStore.SaveItemAsync(
                new InventoryItemUpsertRequest(
                    companyId,
                    userId,
                    ItemId: null,
                    "SKU-WO",
                    "Write-Off Item",
                    null,
                    InventoryItemKind.Stock,
                    "EA",
                    ManageInventoryMethod.ManageStock,
                    InventoryCostingMethod.Fifo,
                    InventoryBackorderMode.Disallow,
                    InventoryLowStockActivity.Nothing,
                    inventoryAssetAccountId,
                    DefaultCogsAccountId: null,
                    DefaultWriteOffAccountId: writeOffAccountId,
                    DefaultPurchaseVarianceAccountId: null,
                    DefaultSalesRevenueAccountId: null,
                    DefaultDropShipClearingAccountId: null,
                    DefaultSalesPrice: null,
                    DefaultPurchasePrice: null,
                    DefaultSalesTaxCodeId: null,
                    DefaultPurchaseTaxCodeId: null),
                CancellationToken.None);

            _ = await adjustmentStore.PostAsync(
                new InventoryAdjustmentPostRequest(
                    companyId,
                    userId,
                    InventoryAdjustmentKind.Gain,
                    warehouseId,
                    new DateOnly(2026, 5, 23),
                    "opening balance",
                    new[]
                    {
                        new InventoryAdjustmentLineInput(
                            1,
                            itemId,
                            "EA",
                            5m,
                            10m,
                            null,
                            null)
                    },
                    ClientRequestId: openingClientRequestId),
                CancellationToken.None);

            var request = new InventoryWriteOffRequestPostRequest(
                companyId,
                userId,
                warehouseId,
                new DateOnly(2026, 5, 24),
                "write-off retry test",
                new[]
                {
                    new InventoryAdjustmentLineInput(
                        1,
                        itemId,
                        "EA",
                        2m,
                        null,
                        null,
                        null)
                },
                ClientRequestId: writeOffClientRequestId);

            var requested = await adjustmentStore.RequestWriteOffAsync(request, CancellationToken.None);
            var requestedAgain = await adjustmentStore.RequestWriteOffAsync(request, CancellationToken.None);
            var conflictingWriteOffRequest = new InventoryWriteOffRequestPostRequest(
                companyId,
                userId,
                warehouseId,
                new DateOnly(2026, 5, 24),
                "write-off retry test changed",
                new[]
                {
                    new InventoryAdjustmentLineInput(
                        1,
                        itemId,
                        "EA",
                        1m,
                        null,
                        null,
                        null)
                },
                ClientRequestId: writeOffClientRequestId);
            var writeOffConflict = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                adjustmentStore.RequestWriteOffAsync(conflictingWriteOffRequest, CancellationToken.None));
            Assert.Contains("different inventory request payload", writeOffConflict.Message);

            var approveRequest = new InventoryWriteOffApprovePostRequest(companyId, userId, writeOffClientRequestId);
            var approved = await adjustmentStore.ApproveWriteOffAsync(approveRequest, CancellationToken.None);
            var approvedAgain = await adjustmentStore.ApproveWriteOffAsync(approveRequest, CancellationToken.None);
            var postedResults = await Task.WhenAll(
                adjustmentStore.PostApprovedWriteOffAsync(approveRequest, CancellationToken.None),
                adjustmentStore.PostApprovedWriteOffAsync(approveRequest, CancellationToken.None));

            Assert.Equal(writeOffClientRequestId, requested.DocumentId);
            Assert.Equal(requested.DocumentId, requestedAgain.DocumentId);
            Assert.Equal(requested.DocumentNumber, requestedAgain.DocumentNumber);
            Assert.Equal("approved", approved.Status);
            Assert.Equal("approved", approvedAgain.Status);
            Assert.Equal(writeOffClientRequestId, postedResults[0].DocumentId);
            Assert.Equal(postedResults[0].DocumentId, postedResults[1].DocumentId);
            Assert.Equal("posted", postedResults[0].Status);
            Assert.Equal(2m, postedResults[0].TotalQuantity);
            Assert.Equal(20m, postedResults[0].TotalCostBase);

            Assert.Equal(2, await CountRowsAsync(schemaConnectionString, "inventory_documents"));
            Assert.Equal(2, await CountRowsAsync(schemaConnectionString, "inventory_document_lines"));
            Assert.Equal(2, await CountRowsAsync(schemaConnectionString, "inventory_ledger_entries"));
            Assert.Equal(1, await CountRowsAsync(schemaConnectionString, "inventory_cost_layers"));
            Assert.Equal(1, await CountRowsAsync(schemaConnectionString, "inventory_layer_consumptions"));
            Assert.Equal(2, await CountRowsAsync(schemaConnectionString, "journal_entries"));
            Assert.Equal(4, await CountRowsAsync(schemaConnectionString, "journal_entry_lines"));
            Assert.Equal(4, await CountRowsAsync(schemaConnectionString, "ledger_entries"));
            Assert.Equal(2, await CountRowsAsync(schemaConnectionString, "audit_logs"));

            Assert.Equal(3m, await ReadScalarAsync<decimal>(
                schemaConnectionString,
                "select on_hand_qty from item_warehouse_balances where item_id = @id;",
                ("id", itemId)));
            Assert.Equal(3m, await ReadScalarAsync<decimal>(
                schemaConnectionString,
                "select remaining_qty from inventory_cost_layers where source_document_id = @id;",
                ("id", openingClientRequestId)));
            Assert.Equal(30m, await ReadScalarAsync<decimal>(
                schemaConnectionString,
                "select remaining_cost_base from inventory_cost_layers where source_document_id = @id;",
                ("id", openingClientRequestId)));
            Assert.Equal("posted", await ReadScalarAsync<string>(
                schemaConnectionString,
                "select status from inventory_documents where id = @id;",
                ("id", writeOffClientRequestId)));
            Assert.Equal("inventory_write_off", await ReadScalarAsync<string>(
                schemaConnectionString,
                "select source_type from journal_entries where source_id = @id;",
                ("id", writeOffClientRequestId)));
        }
        finally
        {
            await DropSchemaAsync(baseConnectionString, schemaName);
        }
    }

    private static string? GetPostgreSqlConnectionString() =>
        Environment.GetEnvironmentVariable("CITUS_POSTGRESQL_INTEGRATION_TEST_DB") ??
        Environment.GetEnvironmentVariable("CITUS_ACCOUNTING_DB");

    private static string BuildSchemaConnectionString(string baseConnectionString, string schemaName)
    {
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            SearchPath = schemaName
        };
        return builder.ConnectionString;
    }

    private static async Task CreateSchemaAsync(string connectionString, string schemaName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            create extension if not exists pgcrypto;
            create schema {schemaName};
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropSchemaAsync(string connectionString, string schemaName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"drop schema if exists {schemaName} cascade;";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedCompanyAsync(string connectionString, CompanyId companyId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            create table currency_catalog (
              code char(3) primary key,
              name text not null,
              minor_units integer not null default 2,
              is_active boolean not null default true
            );

            create table companies (
              id char(7) primary key,
              entity_number char(11) not null unique,
              legal_name text not null,
              base_currency_code char(3) not null references currency_catalog(code),
              multi_currency_enabled boolean not null default false,
              status text not null default 'active',
              inventory_module_locked_at timestamptz null,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now()
            );

            create table tax_codes (
              id uuid primary key default gen_random_uuid(),
              company_id char(7) not null references companies(id) on delete cascade,
              code text not null,
              name text not null,
              rate numeric(9, 6) not null default 0,
              is_active boolean not null default true
            );

            create table accounts (
              id uuid primary key,
              company_id char(7) not null references companies(id) on delete cascade,
              entity_number char(11) not null,
              code text not null,
              name text not null,
              root_type text not null,
              detail_type text null,
              currency_code char(3) null,
              allow_manual_posting boolean not null default true,
              is_active boolean not null default true,
              system_key text null,
              system_role text null,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now()
            );

            insert into currency_catalog (code, name)
            values ('USD', 'US Dollar');

            insert into companies (id, entity_number, legal_name, base_currency_code)
            values (@company_id, 'EN-INV-001', 'Inventory Adjustment Integration Company', 'USD');
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedPostingFoundationAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            create table company_numbering_sequences (
              company_id char(7) not null references companies(id) on delete cascade,
              scope_key text not null,
              prefix text not null,
              next_number bigint not null,
              padding smallint not null,
              suggestion_enabled boolean not null default true,
              updated_at timestamptz not null default now(),
              primary key (company_id, scope_key)
            );

            create table company_entity_number_sequences (
              company_id char(7) not null references companies(id) on delete cascade,
              entity_year integer not null,
              next_ordinal bigint not null,
              primary key (company_id, entity_year)
            );

            create table journal_entries (
              id uuid primary key,
              company_id char(7) not null references companies(id) on delete cascade,
              entity_number char(11) not null,
              display_number text not null,
              status text not null,
              source_type text not null,
              source_id uuid not null,
              transaction_currency_code char(3) not null,
              base_currency_code char(3) not null,
              exchange_rate numeric(20, 8) not null,
              exchange_rate_date date not null,
              exchange_rate_source text not null,
              fx_rate_snapshot_id uuid null,
              total_tx_debit numeric(20, 6) not null,
              total_tx_credit numeric(20, 6) not null,
              total_debit numeric(20, 6) not null,
              total_credit numeric(20, 6) not null,
              posting_run_id uuid not null,
              idempotency_key text not null,
              posted_at timestamptz not null,
              created_by_user_id char(7) not null,
              created_at timestamptz not null default now()
            );

            create unique index ux_journal_entries_idempotency
              on journal_entries (company_id, idempotency_key);

            create table journal_entry_lines (
              id uuid primary key,
              company_id char(7) not null references companies(id) on delete cascade,
              journal_entry_id uuid not null references journal_entries(id) on delete cascade,
              line_number integer not null,
              account_id uuid not null references accounts(id),
              description text null,
              party_type text null,
              party_id uuid null,
              tx_debit numeric(20, 6) not null,
              tx_credit numeric(20, 6) not null,
              debit numeric(20, 6) not null,
              credit numeric(20, 6) not null,
              tax_component_type text null,
              control_role text null,
              posting_role text null,
              source_line_number integer null,
              created_at timestamptz not null default now()
            );

            create table ledger_entries (
              id uuid primary key,
              company_id char(7) not null references companies(id) on delete cascade,
              journal_entry_id uuid not null references journal_entries(id) on delete cascade,
              journal_entry_line_id uuid not null references journal_entry_lines(id) on delete cascade,
              posting_date date not null,
              account_id uuid not null references accounts(id),
              debit numeric(20, 6) not null,
              credit numeric(20, 6) not null,
              transaction_currency_code char(3) not null,
              tx_debit numeric(20, 6) not null,
              tx_credit numeric(20, 6) not null,
              created_at timestamptz not null default now()
            );

            create table audit_logs (
              id uuid primary key default gen_random_uuid(),
              company_id char(7) not null references companies(id) on delete cascade,
              actor_type text not null,
              actor_id char(7) null,
              entity_type text not null,
              entity_id uuid not null,
              action text not null,
              payload jsonb not null,
              created_at timestamptz not null default now()
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedAccountAsync(
        string connectionString,
        CompanyId companyId,
        Guid accountId,
        string code,
        string name,
        string rootType,
        string systemRole)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into accounts (
              id,
              company_id,
              entity_number,
              code,
              name,
              root_type,
              detail_type,
              currency_code,
              allow_manual_posting,
              is_active,
              system_role,
              system_key
            )
            values (
              @account_id,
              @company_id,
              @entity_number,
              @code,
              @name,
              @root_type,
              @root_type || '_detail',
              'USD',
              true,
              true,
              @system_role,
              @system_role
            );
            """;
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("entity_number", $"EN-{code}");
        command.Parameters.AddWithValue("code", code);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("root_type", rootType);
        command.Parameters.AddWithValue("system_role", systemRole);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> CountRowsAsync(string connectionString, string tableName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"select count(*) from {tableName};";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<T> ReadScalarAsync<T>(
        string connectionString,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        var result = await command.ExecuteScalarAsync();
        return Assert.IsType<T>(result);
    }
}
