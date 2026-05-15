-- Inventory foundation schema moved out of request-time store helpers.
-- Apply before receipt activation / valuation / GRIR migrations.
--
-- Guarded for partial pilot databases: if platform master tables are not
-- present yet, this migration no-ops and can be re-applied manually after the
-- platform baseline is installed.

do $migration$
begin
  if to_regclass('companies') is not null
     and to_regclass('accounts') is not null
     and to_regclass('tax_codes') is not null then

    create table if not exists company_inventory_policies (
      company_id char(7) primary key references companies(id) on delete cascade,
      default_costing_method text not null,
      negative_stock_allowed boolean not null default false,
      require_writeoff_approval boolean not null default true,
      created_by_user_id char(7) not null,
      created_at timestamptz not null default now(),
      updated_by_user_id char(7) null,
      updated_at timestamptz null,
      constraint ck_company_inventory_policies_costing_method
        check (default_costing_method in ('moving_average', 'fifo'))
    );

    create table if not exists inventory_items (
      id uuid primary key default gen_random_uuid(),
      company_id char(7) not null references companies(id) on delete cascade,
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
      default_sales_revenue_account_id uuid null references accounts(id),
      default_drop_ship_clearing_account_id uuid null references accounts(id),
      default_sales_price numeric(18, 4) null,
      default_purchase_price numeric(18, 4) null,
      default_sales_tax_code_id uuid null references tax_codes(id),
      default_purchase_tax_code_id uuid null references tax_codes(id),
      is_active boolean not null default true,
      created_at timestamptz not null default now(),
      updated_at timestamptz not null default now(),
      constraint ck_inventory_items_item_kind
        check (item_kind in ('stock', 'non_stock', 'service', 'drop_ship')),
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

    alter table inventory_items
      add column if not exists default_sales_price numeric(18, 4) null;

    alter table inventory_items
      add column if not exists default_purchase_price numeric(18, 4) null;

    alter table inventory_items
      add column if not exists default_sales_tax_code_id uuid null references tax_codes(id);

    alter table inventory_items
      add column if not exists default_purchase_tax_code_id uuid null references tax_codes(id);

    alter table inventory_items
      add column if not exists default_sales_revenue_account_id uuid null references accounts(id);

    alter table inventory_items
      add column if not exists default_drop_ship_clearing_account_id uuid null references accounts(id);

    alter table inventory_items drop constraint if exists ck_inventory_items_item_kind;
    alter table inventory_items add constraint ck_inventory_items_item_kind
      check (item_kind in ('stock', 'non_stock', 'service', 'drop_ship'));

    create table if not exists inventory_warehouses (
      id uuid primary key default gen_random_uuid(),
      company_id char(7) not null references companies(id) on delete cascade,
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
      company_id char(7) not null references companies(id) on delete cascade,
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
      company_id char(7) not null references companies(id) on delete cascade,
      document_type text not null,
      status text not null,
      movement_direction text not null,
      posting_date date not null,
      source_module text null,
      source_document_id uuid null,
      source_document_number text null,
      counterparty_id uuid null,
      customer_po_number text null,
      sales_order_id uuid null,
      memo text null,
      created_by_user_id char(7) not null,
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

    alter table inventory_documents add column if not exists customer_po_number text null;
    alter table inventory_documents add column if not exists sales_order_id uuid null;

    create index if not exists ix_inventory_documents_company_customer_po
      on inventory_documents (company_id, customer_po_number)
      where customer_po_number is not null;

    create index if not exists ix_inventory_documents_company_sales_order
      on inventory_documents (company_id, sales_order_id)
      where sales_order_id is not null;

    create table if not exists inventory_document_lines (
      id uuid primary key default gen_random_uuid(),
      company_id char(7) not null references companies(id) on delete cascade,
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
      company_id char(7) not null references companies(id) on delete cascade,
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
      company_id char(7) not null references companies(id) on delete cascade,
      item_id uuid not null references inventory_items(id),
      warehouse_id uuid null references inventory_warehouses(id),
      source_ledger_entry_id uuid null references inventory_ledger_entries(id),
      source_document_id uuid null references inventory_documents(id),
      source_document_line_id uuid null references inventory_document_lines(id),
      layer_date date not null,
      original_qty numeric(20, 6) not null,
      remaining_qty numeric(20, 6) not null,
      unit_cost_base numeric(20, 6) not null,
      remaining_cost_base numeric(20, 6) not null,
      created_at timestamptz not null default now()
    );

    alter table inventory_cost_layers
      add column if not exists source_document_line_id uuid null references inventory_document_lines(id);

    create index if not exists ix_inventory_cost_layers_company_item_date
      on inventory_cost_layers (company_id, item_id, layer_date asc, created_at asc);

    create table if not exists inventory_layer_consumptions (
      id uuid primary key default gen_random_uuid(),
      company_id char(7) not null references companies(id) on delete cascade,
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
      company_id char(7) not null references companies(id) on delete cascade,
      transfer_number text not null,
      status text not null,
      source_warehouse_id uuid not null references inventory_warehouses(id),
      destination_warehouse_id uuid not null references inventory_warehouses(id),
      requested_by_user_id char(7) not null,
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
      company_id char(7) not null references companies(id) on delete cascade,
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
      company_id char(7) not null references companies(id) on delete cascade,
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
      company_id char(7) not null references companies(id) on delete cascade,
      bom_id uuid not null references boms(id) on delete cascade,
      line_no integer not null,
      component_item_id uuid not null references inventory_items(id),
      quantity numeric(20, 6) not null,
      wastage_percent numeric(9, 4) not null default 0,
      memo text null
    );

    create unique index if not exists ux_bom_lines_bom_line_no
      on bom_lines (bom_id, line_no);

    create or replace function inventory_documents_stamp_module_lock()
      returns trigger as $fn$
    begin
      update companies
      set inventory_module_locked_at = now()
      where id = NEW.company_id
        and inventory_module_locked_at is null;
      return NEW;
    end;
    $fn$ language plpgsql;

    drop trigger if exists trg_inventory_documents_lock_module on inventory_documents;
    create trigger trg_inventory_documents_lock_module
      after insert on inventory_documents
      for each row
      execute function inventory_documents_stamp_module_lock();

    create or replace function promote_so_backorders_after_on_hand_change()
      returns trigger as $fn$
    declare
      remaining numeric(20, 6);
      total_promoted numeric(20, 6) := 0;
      v_line record;
      fill_qty numeric(20, 6);
    begin
      if to_regclass('sales_orders') is null
        or to_regclass('sales_order_lines') is null then
        return null;
      end if;

      if TG_OP = 'INSERT' then
        remaining := NEW.on_hand_qty;
      else
        remaining := NEW.on_hand_qty - OLD.on_hand_qty;
      end if;

      if remaining <= 0 then
        return null;
      end if;

      for v_line in
        select sol.id as line_id, sol.backorder_qty
          from sales_order_lines sol
          join sales_orders so on so.id = sol.sales_order_id
         where so.company_id = NEW.company_id
           and sol.item_id = NEW.item_id
           and sol.backorder_qty > 0
           and so.status = 'confirmed'
         order by so.confirmed_at asc nulls last, so.id asc
         for update
      loop
        exit when remaining <= 0;
        fill_qty := least(v_line.backorder_qty, remaining);
        update sales_order_lines
           set backorder_qty = backorder_qty - fill_qty,
               reserved_qty = reserved_qty + fill_qty
         where id = v_line.line_id;
        remaining := remaining - fill_qty;
        total_promoted := total_promoted + fill_qty;
      end loop;

      if total_promoted > 0 then
        update item_warehouse_balances
           set reserved_qty = reserved_qty + total_promoted,
               updated_at = now()
         where company_id = NEW.company_id
           and item_id = NEW.item_id
           and warehouse_id = NEW.warehouse_id;
      end if;

      return null;
    end;
    $fn$ language plpgsql;

    drop trigger if exists trg_promote_so_backorders_insert on item_warehouse_balances;
    create trigger trg_promote_so_backorders_insert
      after insert on item_warehouse_balances
      for each row
      when (NEW.on_hand_qty > 0)
      execute function promote_so_backorders_after_on_hand_change();

    drop trigger if exists trg_promote_so_backorders_update on item_warehouse_balances;
    create trigger trg_promote_so_backorders_update
      after update of on_hand_qty on item_warehouse_balances
      for each row
      when (NEW.on_hand_qty > OLD.on_hand_qty)
      execute function promote_so_backorders_after_on_hand_change();

    drop trigger if exists trg_promote_so_backorders on item_warehouse_balances;
  end if;
end
$migration$;
