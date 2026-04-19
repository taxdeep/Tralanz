using Citus.Modules.Inventory.Application.Contracts;
using Citus.Modules.Inventory.Domain.Shared;
using Npgsql;

namespace Infrastructure.PostgreSQL.Inventory;

public sealed class PostgreSqlInventoryFoundationStore : IInventoryFoundationStore
{
    private readonly PostgreSqlConnectionFactory _connections;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private volatile bool _schemaEnsured;

    public PostgreSqlInventoryFoundationStore(PostgreSqlConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    public async Task<InventoryFoundationSummary> GetSummaryAsync(
        Guid companyId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await EnsureCompanyExistsAsync(connection, transaction: null, companyId, cancellationToken);
        return await LoadSummaryAsync(connection, transaction: null, companyId, cancellationToken);
    }

    public async Task<InventoryFoundationSummary> EnsureCompanyFoundationAsync(
        InventoryFoundationEnsureRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await EnsureCompanyExistsAsync(connection, transaction, request.CompanyId, cancellationToken);
        await EnsureCompanyPolicyAsync(connection, transaction, request, cancellationToken);

        var summary = await LoadSummaryAsync(connection, transaction, request.CompanyId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return summary;
    }

    public async Task<InventoryFoundationDashboard> GetDashboardAsync(
        Guid companyId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await EnsureCompanyExistsAsync(connection, transaction: null, companyId, cancellationToken);

        var summary = await LoadSummaryAsync(connection, transaction: null, companyId, cancellationToken);
        var items = await LoadItemsAsync(connection, transaction: null, companyId, cancellationToken);
        var warehouses = await LoadWarehousesAsync(connection, transaction: null, companyId, cancellationToken);
        var accountOptions = await LoadAccountOptionsAsync(connection, transaction: null, companyId, cancellationToken);
        return new InventoryFoundationDashboard(
            summary,
            items,
            warehouses,
            accountOptions.InventoryAssetAccountOptions,
            accountOptions.ExpenseAccountOptions);
    }

    public async Task<InventoryCostingPolicyRecord> SavePolicyAsync(
        InventoryCostingPolicyUpdateRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await EnsureCompanyExistsAsync(connection, transaction, request.CompanyId, cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                insert into company_inventory_policies (
                  company_id,
                  default_costing_method,
                  negative_stock_allowed,
                  require_writeoff_approval,
                  created_by_user_id,
                  created_at,
                  updated_by_user_id,
                  updated_at
                )
                values (
                  @company_id,
                  @default_costing_method,
                  @negative_stock_allowed,
                  @require_writeoff_approval,
                  @user_id,
                  now(),
                  @user_id,
                  now()
                )
                on conflict (company_id)
                do update
                  set default_costing_method = excluded.default_costing_method,
                      negative_stock_allowed = excluded.negative_stock_allowed,
                      require_writeoff_approval = excluded.require_writeoff_approval,
                      updated_by_user_id = excluded.updated_by_user_id,
                      updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("company_id", request.CompanyId);
            command.Parameters.AddWithValue("default_costing_method", FormatCostingMethod(request.DefaultCostingMethod));
            command.Parameters.AddWithValue("negative_stock_allowed", request.NegativeStockAllowed);
            command.Parameters.AddWithValue("require_writeoff_approval", request.RequireWriteOffApproval);
            command.Parameters.AddWithValue("user_id", request.UserId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var policy = await LoadPolicyAsync(connection, transaction, request.CompanyId, cancellationToken)
            ?? throw new InvalidOperationException("Inventory costing policy could not be loaded after saving.");
        await transaction.CommitAsync(cancellationToken);
        return policy;
    }

    public async Task<Guid> SaveItemAsync(
        InventoryItemUpsertRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await EnsureCompanyExistsAsync(connection, transaction, request.CompanyId, cancellationToken);

        var normalizedCode = request.ItemCode.Trim().ToUpperInvariant();
        var normalizedName = request.Name.Trim();
        var normalizedDescription = string.IsNullOrWhiteSpace(request.Description)
            ? DBNull.Value
            : (object)request.Description.Trim();
        var normalizedStockUomCode = string.IsNullOrWhiteSpace(request.StockUomCode)
            ? DBNull.Value
            : (object)request.StockUomCode.Trim().ToUpperInvariant();

        try
        {
            if (request.ItemId.HasValue)
            {
                await using var updateCommand = connection.CreateCommand();
                updateCommand.Transaction = transaction;
                updateCommand.CommandText =
                    """
                    update inventory_items
                    set item_code = @item_code,
                        name = @name,
                        description = @description,
                        item_kind = @item_kind,
                        stock_uom_code = @stock_uom_code,
                        manage_inventory_method = @manage_inventory_method,
                        default_costing_method = @default_costing_method,
                        backorder_mode = @backorder_mode,
                        low_stock_activity = @low_stock_activity,
                        default_inventory_asset_account_id = @default_inventory_asset_account_id,
                        default_cogs_account_id = @default_cogs_account_id,
                        default_writeoff_account_id = @default_writeoff_account_id,
                        default_purchase_variance_account_id = @default_purchase_variance_account_id,
                        updated_at = now()
                    where id = @item_id
                      and company_id = @company_id;
                    """;
                updateCommand.Parameters.AddWithValue("item_id", request.ItemId.Value);
                updateCommand.Parameters.AddWithValue("company_id", request.CompanyId);
                updateCommand.Parameters.AddWithValue("item_code", normalizedCode);
                updateCommand.Parameters.AddWithValue("name", normalizedName);
                updateCommand.Parameters.AddWithValue("description", normalizedDescription);
                updateCommand.Parameters.AddWithValue("item_kind", FormatItemKind(request.ItemKind));
                updateCommand.Parameters.AddWithValue("stock_uom_code", normalizedStockUomCode);
                updateCommand.Parameters.AddWithValue("manage_inventory_method", FormatManageInventoryMethod(request.ManageInventoryMethod));
                updateCommand.Parameters.AddWithValue("default_costing_method", FormatCostingMethod(request.DefaultCostingMethod));
                updateCommand.Parameters.AddWithValue("backorder_mode", FormatBackorderMode(request.BackorderMode));
                updateCommand.Parameters.AddWithValue("low_stock_activity", FormatLowStockActivity(request.LowStockActivity));
                updateCommand.Parameters.AddWithValue(
                    "default_inventory_asset_account_id",
                    request.DefaultInventoryAssetAccountId.HasValue ? (object)request.DefaultInventoryAssetAccountId.Value : DBNull.Value);
                updateCommand.Parameters.AddWithValue(
                    "default_cogs_account_id",
                    request.DefaultCogsAccountId.HasValue ? (object)request.DefaultCogsAccountId.Value : DBNull.Value);
                updateCommand.Parameters.AddWithValue(
                    "default_writeoff_account_id",
                    request.DefaultWriteOffAccountId.HasValue ? (object)request.DefaultWriteOffAccountId.Value : DBNull.Value);
                updateCommand.Parameters.AddWithValue(
                    "default_purchase_variance_account_id",
                    request.DefaultPurchaseVarianceAccountId.HasValue ? (object)request.DefaultPurchaseVarianceAccountId.Value : DBNull.Value);

                if (await updateCommand.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    throw new InvalidOperationException("The selected inventory item could not be found for this company.");
                }

                await transaction.CommitAsync(cancellationToken);
                return request.ItemId.Value;
            }

            var itemId = Guid.NewGuid();
            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText =
                """
                insert into inventory_items (
                  id,
                  company_id,
                  item_code,
                  name,
                  description,
                  item_kind,
                  stock_uom_code,
                  manage_inventory_method,
                  default_costing_method,
                  backorder_mode,
                  low_stock_activity,
                  default_inventory_asset_account_id,
                  default_cogs_account_id,
                  default_writeoff_account_id,
                  default_purchase_variance_account_id,
                  is_active,
                  created_at,
                  updated_at
                )
                values (
                  @id,
                  @company_id,
                  @item_code,
                  @name,
                  @description,
                  @item_kind,
                  @stock_uom_code,
                  @manage_inventory_method,
                  @default_costing_method,
                  @backorder_mode,
                  @low_stock_activity,
                  @default_inventory_asset_account_id,
                  @default_cogs_account_id,
                  @default_writeoff_account_id,
                  @default_purchase_variance_account_id,
                  true,
                  now(),
                  now()
                );
                """;
            insertCommand.Parameters.AddWithValue("id", itemId);
            insertCommand.Parameters.AddWithValue("company_id", request.CompanyId);
            insertCommand.Parameters.AddWithValue("item_code", normalizedCode);
            insertCommand.Parameters.AddWithValue("name", normalizedName);
            insertCommand.Parameters.AddWithValue("description", normalizedDescription);
            insertCommand.Parameters.AddWithValue("item_kind", FormatItemKind(request.ItemKind));
            insertCommand.Parameters.AddWithValue("stock_uom_code", normalizedStockUomCode);
            insertCommand.Parameters.AddWithValue("manage_inventory_method", FormatManageInventoryMethod(request.ManageInventoryMethod));
            insertCommand.Parameters.AddWithValue("default_costing_method", FormatCostingMethod(request.DefaultCostingMethod));
            insertCommand.Parameters.AddWithValue("backorder_mode", FormatBackorderMode(request.BackorderMode));
            insertCommand.Parameters.AddWithValue("low_stock_activity", FormatLowStockActivity(request.LowStockActivity));
            insertCommand.Parameters.AddWithValue(
                "default_inventory_asset_account_id",
                request.DefaultInventoryAssetAccountId.HasValue ? (object)request.DefaultInventoryAssetAccountId.Value : DBNull.Value);
            insertCommand.Parameters.AddWithValue(
                "default_cogs_account_id",
                request.DefaultCogsAccountId.HasValue ? (object)request.DefaultCogsAccountId.Value : DBNull.Value);
            insertCommand.Parameters.AddWithValue(
                "default_writeoff_account_id",
                request.DefaultWriteOffAccountId.HasValue ? (object)request.DefaultWriteOffAccountId.Value : DBNull.Value);
            insertCommand.Parameters.AddWithValue(
                "default_purchase_variance_account_id",
                request.DefaultPurchaseVarianceAccountId.HasValue ? (object)request.DefaultPurchaseVarianceAccountId.Value : DBNull.Value);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return itemId;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException("Another inventory item already uses the same company-scoped code or name.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task SetItemActiveAsync(
        Guid companyId,
        Guid itemId,
        bool isActive,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await EnsureCompanyExistsAsync(connection, transaction: null, companyId, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            update inventory_items
            set is_active = @is_active,
                updated_at = now()
            where id = @item_id
              and company_id = @company_id;
            """;
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("is_active", isActive);

        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            throw new InvalidOperationException("The selected inventory item could not be found for this company.");
        }
    }

    public async Task<Guid> SaveWarehouseAsync(
        InventoryWarehouseUpsertRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await EnsureCompanyExistsAsync(connection, transaction, request.CompanyId, cancellationToken);

        var normalizedCode = request.WarehouseCode.Trim().ToUpperInvariant();
        var normalizedName = request.Name.Trim();
        var normalizedDescription = string.IsNullOrWhiteSpace(request.Description)
            ? DBNull.Value
            : (object)request.Description.Trim();

        try
        {
            if (request.WarehouseId.HasValue)
            {
                await using var updateCommand = connection.CreateCommand();
                updateCommand.Transaction = transaction;
                updateCommand.CommandText =
                    """
                    update inventory_warehouses
                    set warehouse_code = @warehouse_code,
                        name = @name,
                        description = @description,
                        updated_at = now()
                    where id = @warehouse_id
                      and company_id = @company_id;
                    """;
                updateCommand.Parameters.AddWithValue("warehouse_id", request.WarehouseId.Value);
                updateCommand.Parameters.AddWithValue("company_id", request.CompanyId);
                updateCommand.Parameters.AddWithValue("warehouse_code", normalizedCode);
                updateCommand.Parameters.AddWithValue("name", normalizedName);
                updateCommand.Parameters.AddWithValue("description", normalizedDescription);

                if (await updateCommand.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    throw new InvalidOperationException("The selected warehouse could not be found for this company.");
                }

                await transaction.CommitAsync(cancellationToken);
                return request.WarehouseId.Value;
            }

            var warehouseId = Guid.NewGuid();
            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText =
                """
                insert into inventory_warehouses (
                  id,
                  company_id,
                  warehouse_code,
                  name,
                  description,
                  is_active,
                  created_at,
                  updated_at
                )
                values (
                  @id,
                  @company_id,
                  @warehouse_code,
                  @name,
                  @description,
                  true,
                  now(),
                  now()
                );
                """;
            insertCommand.Parameters.AddWithValue("id", warehouseId);
            insertCommand.Parameters.AddWithValue("company_id", request.CompanyId);
            insertCommand.Parameters.AddWithValue("warehouse_code", normalizedCode);
            insertCommand.Parameters.AddWithValue("name", normalizedName);
            insertCommand.Parameters.AddWithValue("description", normalizedDescription);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return warehouseId;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException("Another warehouse already uses the same company-scoped code or name.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task SetWarehouseActiveAsync(
        Guid companyId,
        Guid warehouseId,
        bool isActive,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await EnsureCompanyExistsAsync(connection, transaction: null, companyId, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            update inventory_warehouses
            set is_active = @is_active,
                updated_at = now()
            where id = @warehouse_id
              and company_id = @company_id;
            """;
        command.Parameters.AddWithValue("warehouse_id", warehouseId);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("is_active", isActive);

        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            throw new InvalidOperationException("The selected warehouse could not be found for this company.");
        }
    }

    private async Task EnsureSchemaAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        if (_schemaEnsured)
        {
            return;
        }

        await _schemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_schemaEnsured)
            {
                return;
            }

            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                create table if not exists company_inventory_policies (
                  company_id uuid primary key references companies(id) on delete cascade,
                  default_costing_method text not null,
                  negative_stock_allowed boolean not null default false,
                  require_writeoff_approval boolean not null default true,
                  created_by_user_id uuid not null,
                  created_at timestamptz not null default now(),
                  updated_by_user_id uuid null,
                  updated_at timestamptz null,
                  constraint ck_company_inventory_policies_costing_method
                    check (default_costing_method in ('moving_average', 'fifo'))
                );

                create table if not exists inventory_items (
                  id uuid primary key default gen_random_uuid(),
                  company_id uuid not null references companies(id) on delete cascade,
                  item_code text not null,
                  name text not null,
                  description text null,
                  item_kind text not null,
                  stock_uom_code text null,
                  manage_inventory_method text not null,
                  default_costing_method text not null,
                  backorder_mode text not null,
                  low_stock_activity text not null,
                  default_inventory_asset_account_id uuid null references accounts(id),
                  default_cogs_account_id uuid null references accounts(id),
                  default_writeoff_account_id uuid null references accounts(id),
                  default_purchase_variance_account_id uuid null references accounts(id),
                  is_active boolean not null default true,
                  created_at timestamptz not null default now(),
                  updated_at timestamptz not null default now(),
                  constraint ck_inventory_items_item_kind
                    check (item_kind in ('stock', 'non_stock', 'service')),
                  constraint ck_inventory_items_manage_inventory_method
                    check (manage_inventory_method in ('dont_manage_stock', 'manage_stock', 'manage_stock_by_sku')),
                  constraint ck_inventory_items_default_costing_method
                    check (default_costing_method in ('moving_average', 'fifo')),
                  constraint ck_inventory_items_backorder_mode
                    check (backorder_mode in ('disallow', 'allow_negative', 'allow_negative_with_warning')),
                  constraint ck_inventory_items_low_stock_activity
                    check (low_stock_activity in ('nothing', 'warn', 'block_outbound'))
                );

                create unique index if not exists ux_inventory_items_company_item_code
                  on inventory_items (company_id, lower(item_code));

                create unique index if not exists ux_inventory_items_company_name
                  on inventory_items (company_id, lower(name));

                alter table inventory_items
                  add column if not exists stock_uom_code text null;

                alter table inventory_items
                  add column if not exists default_inventory_asset_account_id uuid null references accounts(id);

                alter table inventory_items
                  add column if not exists default_cogs_account_id uuid null references accounts(id);

                alter table inventory_items
                  add column if not exists default_writeoff_account_id uuid null references accounts(id);

                alter table inventory_items
                  add column if not exists default_purchase_variance_account_id uuid null references accounts(id);

                create table if not exists inventory_warehouses (
                  id uuid primary key default gen_random_uuid(),
                  company_id uuid not null references companies(id) on delete cascade,
                  warehouse_code text not null,
                  name text not null,
                  description text null,
                  is_active boolean not null default true,
                  created_at timestamptz not null default now(),
                  updated_at timestamptz not null default now()
                );

                create unique index if not exists ux_inventory_warehouses_company_code
                  on inventory_warehouses (company_id, lower(warehouse_code));

                create unique index if not exists ux_inventory_warehouses_company_name
                  on inventory_warehouses (company_id, lower(name));

                create table if not exists item_warehouse_balances (
                  id uuid primary key default gen_random_uuid(),
                  company_id uuid not null references companies(id) on delete cascade,
                  item_id uuid not null references inventory_items(id) on delete cascade,
                  warehouse_id uuid not null references inventory_warehouses(id) on delete cascade,
                  on_hand_qty numeric(20, 6) not null default 0,
                  reserved_qty numeric(20, 6) not null default 0,
                  in_transit_out_qty numeric(20, 6) not null default 0,
                  in_transit_in_qty numeric(20, 6) not null default 0,
                  updated_at timestamptz not null default now()
                );

                create unique index if not exists ux_item_warehouse_balances_company_item_warehouse
                  on item_warehouse_balances (company_id, item_id, warehouse_id);

                create table if not exists inventory_documents (
                  id uuid primary key default gen_random_uuid(),
                  company_id uuid not null references companies(id) on delete cascade,
                  document_type text not null,
                  status text not null,
                  movement_direction text not null,
                  posting_date date not null,
                  source_module text null,
                  source_document_id uuid null,
                  source_document_number text null,
                  counterparty_id uuid null,
                  memo text null,
                  created_by_user_id uuid not null,
                  created_at timestamptz not null default now(),
                  posted_at timestamptz null,
                  constraint ck_inventory_documents_document_type
                    check (document_type in (
                      'purchase_receipt',
                      'customer_return_receipt',
                      'transfer_receive',
                      'manufacturing_receipt',
                      'opening_balance_receipt',
                      'inventory_adjustment_gain',
                      'sales_issue',
                      'shipment',
                      'vendor_return_issue',
                      'transfer_ship',
                      'manufacturing_issue',
                      'inventory_write_off',
                      'inventory_adjustment_loss'
                    )),
                  constraint ck_inventory_documents_status
                    check (status in ('draft', 'submitted', 'posted', 'cancelled', 'shipped', 'received')),
                  constraint ck_inventory_documents_movement_direction
                    check (movement_direction in ('inbound', 'outbound', 'internal', 'neutral'))
                );

                create index if not exists ix_inventory_documents_company_posting_date
                  on inventory_documents (company_id, posting_date desc, created_at desc);

                create table if not exists inventory_document_lines (
                  id uuid primary key default gen_random_uuid(),
                  company_id uuid not null references companies(id) on delete cascade,
                  document_id uuid not null references inventory_documents(id) on delete cascade,
                  line_no integer not null,
                  item_id uuid not null references inventory_items(id),
                  warehouse_id uuid null references inventory_warehouses(id),
                  uom_code text not null,
                  quantity numeric(20, 6) not null,
                  base_quantity numeric(20, 6) not null,
                  currency_code text null,
                  fx_rate_to_base numeric(20, 10) null,
                  unit_cost_tx numeric(20, 6) null,
                  unit_cost_base numeric(20, 6) null,
                  extended_cost_base numeric(20, 6) null,
                  reason_code text null,
                  memo text null
                );

                create unique index if not exists ux_inventory_document_lines_document_line_no
                  on inventory_document_lines (document_id, line_no);

                create table if not exists inventory_ledger_entries (
                  id uuid primary key default gen_random_uuid(),
                  company_id uuid not null references companies(id) on delete cascade,
                  item_id uuid not null references inventory_items(id),
                  warehouse_id uuid null references inventory_warehouses(id),
                  document_id uuid null references inventory_documents(id),
                  document_line_id uuid null references inventory_document_lines(id),
                  movement_direction text not null,
                  movement_type text not null,
                  posting_date date not null,
                  quantity_delta numeric(20, 6) not null,
                  quantity_after numeric(20, 6) not null,
                  cost_amount_delta_base numeric(20, 6) not null default 0,
                  cost_amount_after_base numeric(20, 6) not null default 0,
                  memo text null,
                  created_at timestamptz not null default now(),
                  constraint ck_inventory_ledger_entries_movement_direction
                    check (movement_direction in ('inbound', 'outbound', 'internal', 'neutral')),
                  constraint ck_inventory_ledger_entries_movement_type
                    check (movement_type in (
                      'purchase_receipt',
                      'customer_return_receipt',
                      'transfer_receive',
                      'manufacturing_receipt',
                      'opening_balance_receipt',
                      'inventory_adjustment_gain',
                      'sales_issue',
                      'vendor_return_issue',
                      'transfer_ship',
                      'manufacturing_issue',
                      'inventory_write_off',
                      'inventory_adjustment_loss',
                      'reservation',
                      'reservation_release'
                    ))
                );

                create index if not exists ix_inventory_ledger_entries_company_item_posting_date
                  on inventory_ledger_entries (company_id, item_id, posting_date desc, created_at desc);

                create table if not exists inventory_cost_layers (
                  id uuid primary key default gen_random_uuid(),
                  company_id uuid not null references companies(id) on delete cascade,
                  item_id uuid not null references inventory_items(id),
                  warehouse_id uuid null references inventory_warehouses(id),
                  source_ledger_entry_id uuid null references inventory_ledger_entries(id),
                  source_document_id uuid null references inventory_documents(id),
                  layer_date date not null,
                  original_qty numeric(20, 6) not null,
                  remaining_qty numeric(20, 6) not null,
                  unit_cost_base numeric(20, 6) not null,
                  remaining_cost_base numeric(20, 6) not null,
                  created_at timestamptz not null default now()
                );

                create index if not exists ix_inventory_cost_layers_company_item_date
                  on inventory_cost_layers (company_id, item_id, layer_date asc, created_at asc);

                create table if not exists inventory_layer_consumptions (
                  id uuid primary key default gen_random_uuid(),
                  company_id uuid not null references companies(id) on delete cascade,
                  issue_ledger_entry_id uuid not null references inventory_ledger_entries(id) on delete cascade,
                  cost_layer_id uuid not null references inventory_cost_layers(id) on delete cascade,
                  consumed_qty numeric(20, 6) not null,
                  consumed_cost_base numeric(20, 6) not null,
                  created_at timestamptz not null default now()
                );

                create index if not exists ix_inventory_layer_consumptions_issue_ledger_entry
                  on inventory_layer_consumptions (issue_ledger_entry_id);

                create table if not exists warehouse_transfers (
                  id uuid primary key default gen_random_uuid(),
                  company_id uuid not null references companies(id) on delete cascade,
                  transfer_number text not null,
                  status text not null,
                  source_warehouse_id uuid not null references inventory_warehouses(id),
                  destination_warehouse_id uuid not null references inventory_warehouses(id),
                  requested_by_user_id uuid not null,
                  memo text null,
                  created_at timestamptz not null default now(),
                  shipped_at timestamptz null,
                  received_at timestamptz null,
                  constraint ck_warehouse_transfers_status
                    check (status in ('draft', 'submitted', 'shipped', 'received', 'cancelled'))
                );

                create unique index if not exists ux_warehouse_transfers_company_transfer_number
                  on warehouse_transfers (company_id, lower(transfer_number));

                create table if not exists warehouse_transfer_lines (
                  id uuid primary key default gen_random_uuid(),
                  company_id uuid not null references companies(id) on delete cascade,
                  transfer_id uuid not null references warehouse_transfers(id) on delete cascade,
                  line_no integer not null,
                  item_id uuid not null references inventory_items(id),
                  quantity numeric(20, 6) not null,
                  base_quantity numeric(20, 6) not null,
                  memo text null
                );

                create unique index if not exists ux_warehouse_transfer_lines_transfer_line_no
                  on warehouse_transfer_lines (transfer_id, line_no);

                create table if not exists boms (
                  id uuid primary key default gen_random_uuid(),
                  company_id uuid not null references companies(id) on delete cascade,
                  bom_code text not null,
                  output_item_id uuid not null references inventory_items(id),
                  output_qty numeric(20, 6) not null,
                  is_active boolean not null default true,
                  created_at timestamptz not null default now(),
                  updated_at timestamptz not null default now()
                );

                create unique index if not exists ux_boms_company_bom_code
                  on boms (company_id, lower(bom_code));

                create table if not exists bom_lines (
                  id uuid primary key default gen_random_uuid(),
                  company_id uuid not null references companies(id) on delete cascade,
                  bom_id uuid not null references boms(id) on delete cascade,
                  line_no integer not null,
                  component_item_id uuid not null references inventory_items(id),
                  quantity numeric(20, 6) not null,
                  wastage_percent numeric(9, 4) not null default 0,
                  memo text null
                );

                create unique index if not exists ux_bom_lines_bom_line_no
                  on bom_lines (bom_id, line_no);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            _schemaEnsured = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private static async Task EnsureCompanyExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select count(*)
            from companies
            where id = @company_id;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        var count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
        if (count == 0)
        {
            throw new InvalidOperationException($"Company {companyId:D} could not be found.");
        }
    }

    private static async Task EnsureCompanyPolicyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        InventoryFoundationEnsureRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into company_inventory_policies (
              company_id,
              default_costing_method,
              negative_stock_allowed,
              require_writeoff_approval,
              created_by_user_id,
              created_at
            )
            values (
              @company_id,
              @default_costing_method,
              @negative_stock_allowed,
              @require_writeoff_approval,
              @created_by_user_id,
              now()
            )
            on conflict (company_id)
            do nothing;
            """;
        command.Parameters.AddWithValue("company_id", request.CompanyId);
        command.Parameters.AddWithValue("default_costing_method", FormatCostingMethod(request.DefaultCostingMethod));
        command.Parameters.AddWithValue("negative_stock_allowed", request.NegativeStockAllowed);
        command.Parameters.AddWithValue("require_writeoff_approval", request.RequireWriteOffApproval);
        command.Parameters.AddWithValue("created_by_user_id", request.UserId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<InventoryFoundationSummary> LoadSummaryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid companyId,
        CancellationToken cancellationToken)
    {
        var policy = await LoadPolicyAsync(connection, transaction, companyId, cancellationToken);
        var itemCount = await CountAsync(connection, transaction, "inventory_items", companyId, cancellationToken);
        var warehouseCount = await CountAsync(connection, transaction, "inventory_warehouses", companyId, cancellationToken);
        var activeWarehouseCount = await CountAsync(connection, transaction, "inventory_warehouses", companyId, cancellationToken, "and is_active = true");
        var balanceCount = await CountAsync(connection, transaction, "item_warehouse_balances", companyId, cancellationToken);
        var ledgerEntryCount = await CountAsync(connection, transaction, "inventory_ledger_entries", companyId, cancellationToken);
        var costLayerCount = await CountAsync(connection, transaction, "inventory_cost_layers", companyId, cancellationToken);

        return new InventoryFoundationSummary(
            companyId,
            policy,
            itemCount,
            warehouseCount,
            activeWarehouseCount,
            balanceCount,
            ledgerEntryCount,
            costLayerCount);
    }

    private static async Task<IReadOnlyList<InventoryManagedItemSummary>> LoadItemsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              item.id,
              item.company_id,
              item.item_code,
              item.name,
              item.description,
              item.item_kind,
              item.stock_uom_code,
              item.manage_inventory_method,
              item.default_costing_method,
              item.backorder_mode,
              item.low_stock_activity,
              item.default_inventory_asset_account_id,
              inventory_asset_account.code as default_inventory_asset_account_code,
              inventory_asset_account.name as default_inventory_asset_account_name,
              item.default_cogs_account_id,
              cogs_account.code as default_cogs_account_code,
              cogs_account.name as default_cogs_account_name,
              item.default_writeoff_account_id,
              writeoff_account.code as default_writeoff_account_code,
              writeoff_account.name as default_writeoff_account_name,
              item.default_purchase_variance_account_id,
              purchase_variance_account.code as default_purchase_variance_account_code,
              purchase_variance_account.name as default_purchase_variance_account_name,
              item.is_active,
              item.updated_at
            from inventory_items item
            left join accounts inventory_asset_account
              on inventory_asset_account.id = item.default_inventory_asset_account_id
            left join accounts cogs_account
              on cogs_account.id = item.default_cogs_account_id
            left join accounts writeoff_account
              on writeoff_account.id = item.default_writeoff_account_id
            left join accounts purchase_variance_account
              on purchase_variance_account.id = item.default_purchase_variance_account_id
            where item.company_id = @company_id
            order by
              case when item.is_active then 0 else 1 end,
              item.item_code asc,
              item.name asc;
            """;
        command.Parameters.AddWithValue("company_id", companyId);

        var items = new List<InventoryManagedItemSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new InventoryManagedItemSummary(
                reader.GetGuid(reader.GetOrdinal("id")),
                reader.GetGuid(reader.GetOrdinal("company_id")),
                reader.GetString(reader.GetOrdinal("item_code")),
                reader.GetString(reader.GetOrdinal("name")),
                reader.IsDBNull(reader.GetOrdinal("description"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("description")),
                ParseItemKind(reader.GetString(reader.GetOrdinal("item_kind"))),
                reader.IsDBNull(reader.GetOrdinal("stock_uom_code"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("stock_uom_code")),
                ParseManageInventoryMethod(reader.GetString(reader.GetOrdinal("manage_inventory_method"))),
                ParseCostingMethod(reader.GetString(reader.GetOrdinal("default_costing_method"))),
                ParseBackorderMode(reader.GetString(reader.GetOrdinal("backorder_mode"))),
                ParseLowStockActivity(reader.GetString(reader.GetOrdinal("low_stock_activity"))),
                reader.IsDBNull(reader.GetOrdinal("default_inventory_asset_account_id"))
                    ? null
                    : reader.GetGuid(reader.GetOrdinal("default_inventory_asset_account_id")),
                BuildAccountLabel(reader, "default_inventory_asset_account_code", "default_inventory_asset_account_name"),
                reader.IsDBNull(reader.GetOrdinal("default_cogs_account_id"))
                    ? null
                    : reader.GetGuid(reader.GetOrdinal("default_cogs_account_id")),
                BuildAccountLabel(reader, "default_cogs_account_code", "default_cogs_account_name"),
                reader.IsDBNull(reader.GetOrdinal("default_writeoff_account_id"))
                    ? null
                    : reader.GetGuid(reader.GetOrdinal("default_writeoff_account_id")),
                BuildAccountLabel(reader, "default_writeoff_account_code", "default_writeoff_account_name"),
                reader.IsDBNull(reader.GetOrdinal("default_purchase_variance_account_id"))
                    ? null
                    : reader.GetGuid(reader.GetOrdinal("default_purchase_variance_account_id")),
                BuildAccountLabel(reader, "default_purchase_variance_account_code", "default_purchase_variance_account_name"),
                reader.GetBoolean(reader.GetOrdinal("is_active")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at"))));
        }

        return items;
    }

    private static async Task<InventoryFoundationAccountCatalog> LoadAccountOptionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              id,
              code,
              name,
              root_type,
              coalesce(detail_type, root_type) as detail_type,
              coalesce(currency_code, '') as currency_code
            from accounts
            where company_id = @company_id
              and is_active = true
              and allow_manual_posting = true
              and root_type in ('asset', 'expense', 'cost_of_sales')
            order by code asc;
            """;
        command.Parameters.AddWithValue("company_id", companyId);

        var inventoryAssetAccountOptions = new List<InventoryFoundationAccountOption>();
        var expenseAccountOptions = new List<InventoryFoundationAccountOption>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var option = new InventoryFoundationAccountOption(
                reader.GetGuid(reader.GetOrdinal("id")),
                reader.GetString(reader.GetOrdinal("code")),
                reader.GetString(reader.GetOrdinal("name")),
                reader.GetString(reader.GetOrdinal("root_type")),
                reader.GetString(reader.GetOrdinal("detail_type")),
                reader.GetString(reader.GetOrdinal("currency_code")));

            if (string.Equals(option.RootType, "asset", StringComparison.OrdinalIgnoreCase))
            {
                inventoryAssetAccountOptions.Add(option);
                continue;
            }

            expenseAccountOptions.Add(option);
        }

        return new InventoryFoundationAccountCatalog
        {
            InventoryAssetAccountOptions = inventoryAssetAccountOptions,
            ExpenseAccountOptions = expenseAccountOptions
        };
    }

    private static async Task<IReadOnlyList<InventoryManagedWarehouseSummary>> LoadWarehousesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              id,
              company_id,
              warehouse_code,
              name,
              description,
              is_active,
              updated_at
            from inventory_warehouses
            where company_id = @company_id
            order by
              case when is_active then 0 else 1 end,
              warehouse_code asc,
              name asc;
            """;
        command.Parameters.AddWithValue("company_id", companyId);

        var warehouses = new List<InventoryManagedWarehouseSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            warehouses.Add(new InventoryManagedWarehouseSummary(
                reader.GetGuid(reader.GetOrdinal("id")),
                reader.GetGuid(reader.GetOrdinal("company_id")),
                reader.GetString(reader.GetOrdinal("warehouse_code")),
                reader.GetString(reader.GetOrdinal("name")),
                reader.IsDBNull(reader.GetOrdinal("description"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("description")),
                reader.GetBoolean(reader.GetOrdinal("is_active")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at"))));
        }

        return warehouses;
    }

    private static async Task<InventoryCostingPolicyRecord?> LoadPolicyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              default_costing_method,
              negative_stock_allowed,
              require_writeoff_approval,
              created_by_user_id,
              created_at,
              updated_by_user_id,
              coalesce(updated_at, created_at) as effective_updated_at
            from company_inventory_policies
            where company_id = @company_id
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new InventoryCostingPolicyRecord(
            companyId,
            ParseCostingMethod(reader.GetString(reader.GetOrdinal("default_costing_method"))),
            reader.GetBoolean(reader.GetOrdinal("negative_stock_allowed")),
            reader.GetBoolean(reader.GetOrdinal("require_writeoff_approval")),
            reader.GetGuid(reader.GetOrdinal("created_by_user_id")),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
            reader.IsDBNull(reader.GetOrdinal("updated_by_user_id"))
                ? null
                : reader.GetGuid(reader.GetOrdinal("updated_by_user_id")),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("effective_updated_at")));
    }

    private static async Task<int> CountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string tableName,
        Guid companyId,
        CancellationToken cancellationToken,
        string extraPredicate = "")
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            select count(*)
            from {tableName}
            where company_id = @company_id
            {extraPredicate};
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
    }

    private static string? BuildAccountLabel(NpgsqlDataReader reader, string codeColumnName, string nameColumnName)
    {
        var codeOrdinal = reader.GetOrdinal(codeColumnName);
        if (reader.IsDBNull(codeOrdinal))
        {
            return null;
        }

        return $"{reader.GetString(codeOrdinal)} - {reader.GetString(reader.GetOrdinal(nameColumnName))}";
    }

    private static string FormatCostingMethod(InventoryCostingMethod method) =>
        method switch
        {
            InventoryCostingMethod.MovingAverage => "moving_average",
            InventoryCostingMethod.Fifo => "fifo",
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unsupported inventory costing method.")
        };

    private static string FormatItemKind(InventoryItemKind kind) =>
        kind switch
        {
            InventoryItemKind.Stock => "stock",
            InventoryItemKind.NonStock => "non_stock",
            InventoryItemKind.Service => "service",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported inventory item kind.")
        };

    private static string FormatManageInventoryMethod(ManageInventoryMethod method) =>
        method switch
        {
            ManageInventoryMethod.DontManageStock => "dont_manage_stock",
            ManageInventoryMethod.ManageStock => "manage_stock",
            ManageInventoryMethod.ManageStockBySku => "manage_stock_by_sku",
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unsupported inventory management method.")
        };

    private static string FormatBackorderMode(InventoryBackorderMode mode) =>
        mode switch
        {
            InventoryBackorderMode.Disallow => "disallow",
            InventoryBackorderMode.AllowNegative => "allow_negative",
            InventoryBackorderMode.AllowNegativeWithWarning => "allow_negative_with_warning",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported inventory backorder mode.")
        };

    private static string FormatLowStockActivity(InventoryLowStockActivity activity) =>
        activity switch
        {
            InventoryLowStockActivity.Nothing => "nothing",
            InventoryLowStockActivity.Warn => "warn",
            InventoryLowStockActivity.BlockOutbound => "block_outbound",
            _ => throw new ArgumentOutOfRangeException(nameof(activity), activity, "Unsupported inventory low-stock activity.")
        };

    private static InventoryCostingMethod ParseCostingMethod(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "moving_average" => InventoryCostingMethod.MovingAverage,
            "fifo" => InventoryCostingMethod.Fifo,
            _ => throw new InvalidOperationException($"Unsupported inventory costing method '{value}'.")
        };

    private static InventoryItemKind ParseItemKind(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "stock" => InventoryItemKind.Stock,
            "non_stock" => InventoryItemKind.NonStock,
            "service" => InventoryItemKind.Service,
            _ => throw new InvalidOperationException($"Unsupported inventory item kind '{value}'.")
        };

    private static ManageInventoryMethod ParseManageInventoryMethod(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "dont_manage_stock" => ManageInventoryMethod.DontManageStock,
            "manage_stock" => ManageInventoryMethod.ManageStock,
            "manage_stock_by_sku" => ManageInventoryMethod.ManageStockBySku,
            _ => throw new InvalidOperationException($"Unsupported inventory management method '{value}'.")
        };

    private static InventoryBackorderMode ParseBackorderMode(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "disallow" => InventoryBackorderMode.Disallow,
            "allow_negative" => InventoryBackorderMode.AllowNegative,
            "allow_negative_with_warning" => InventoryBackorderMode.AllowNegativeWithWarning,
            _ => throw new InvalidOperationException($"Unsupported inventory backorder mode '{value}'.")
        };

    private static InventoryLowStockActivity ParseLowStockActivity(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "nothing" => InventoryLowStockActivity.Nothing,
            "warn" => InventoryLowStockActivity.Warn,
            "block_outbound" => InventoryLowStockActivity.BlockOutbound,
            _ => throw new InvalidOperationException($"Unsupported inventory low-stock activity '{value}'.")
        };

    private sealed record InventoryFoundationAccountCatalog
    {
        public IReadOnlyList<InventoryFoundationAccountOption> InventoryAssetAccountOptions { get; init; } = Array.Empty<InventoryFoundationAccountOption>();

        public IReadOnlyList<InventoryFoundationAccountOption> ExpenseAccountOptions { get; init; } = Array.Empty<InventoryFoundationAccountOption>();
    }
}
