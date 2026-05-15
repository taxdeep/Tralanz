-- Makes the Receipt inventory cost-layer emission and GR/IR bridge control
-- tables explicit deploy-time schema instead of relying on the first business
-- request to create receipt_grir_bridge_lines.
--
-- This migration expects the inventory foundation tables to already exist.
-- On older pilot databases that have not enabled Inventory yet, the guarded
-- block intentionally no-ops; the runtime startup hook can still bootstrap
-- dev/test schemas when RuntimeSchemaManagement is enabled.

do $$
begin
  if to_regclass('inventory_cost_layers') is not null
     and to_regclass('inventory_documents') is not null
     and to_regclass('inventory_document_lines') is not null
     and to_regclass('inventory_ledger_entries') is not null
     and to_regclass('inventory_items') is not null
     and to_regclass('inventory_warehouses') is not null then

    alter table inventory_cost_layers
      add column if not exists source_document_line_id uuid null references inventory_document_lines(id);

    create table if not exists receipt_inventory_cost_layer_emission_lines (
      id uuid primary key default gen_random_uuid(),
      company_id char(7) not null references companies(id) on delete cascade,
      receipt_id uuid not null,
      receipt_line_number integer not null,
      valuation_line_id uuid not null,
      cost_layer_id uuid not null references inventory_cost_layers(id) on delete cascade,
      inventory_document_id uuid not null references inventory_documents(id) on delete cascade,
      inventory_document_line_id uuid not null references inventory_document_lines(id) on delete cascade,
      source_ledger_entry_id uuid null references inventory_ledger_entries(id) on delete set null,
      bill_id uuid not null,
      bill_line_number integer not null,
      item_id uuid not null references inventory_items(id) on delete cascade,
      warehouse_id uuid not null references inventory_warehouses(id) on delete cascade,
      uom_code text not null,
      emitted_quantity numeric(20, 6) not null,
      emitted_cost_base numeric(20, 6) not null,
      emitted_by_user_id char(7) not null,
      emitted_at timestamptz not null default now()
    );

    create unique index if not exists ux_receipt_inventory_cost_layer_emission_lines_valuation
      on receipt_inventory_cost_layer_emission_lines (company_id, valuation_line_id);

    create index if not exists ix_receipt_inventory_cost_layer_emission_lines_receipt
      on receipt_inventory_cost_layer_emission_lines (company_id, receipt_id, emitted_at desc);

    create index if not exists ix_receipt_inventory_cost_layer_emission_lines_bill
      on receipt_inventory_cost_layer_emission_lines (company_id, bill_id, bill_line_number);

    create table if not exists receipt_grir_bridge_lines (
      id uuid primary key default gen_random_uuid(),
      company_id char(7) not null references companies(id) on delete cascade,
      receipt_id uuid not null,
      receipt_line_number integer not null,
      valuation_line_id uuid not null,
      cost_layer_emission_line_id uuid not null references receipt_inventory_cost_layer_emission_lines(id) on delete cascade,
      cost_layer_id uuid not null,
      bill_id uuid not null,
      bill_line_number integer not null,
      item_id uuid not null references inventory_items(id) on delete cascade,
      warehouse_id uuid not null references inventory_warehouses(id) on delete cascade,
      uom_code text not null,
      bridge_quantity numeric(20, 6) not null,
      bridge_amount_base numeric(20, 6) not null,
      bridge_status text not null,
      blocked_reason_code text null,
      journal_entry_id uuid null references journal_entries(id) on delete set null,
      journal_entry_display_number text null,
      posted_by_user_id char(7) null,
      posted_at timestamptz null,
      refreshed_by_user_id char(7) not null,
      refreshed_at timestamptz not null default now()
    );

    alter table receipt_grir_bridge_lines
      add column if not exists journal_entry_id uuid null;

    alter table receipt_grir_bridge_lines
      add column if not exists journal_entry_display_number text null;

    alter table receipt_grir_bridge_lines
      add column if not exists posted_by_user_id char(7) null;

    alter table receipt_grir_bridge_lines
      add column if not exists posted_at timestamptz null;

    create unique index if not exists ux_receipt_grir_bridge_lines_emission
      on receipt_grir_bridge_lines (company_id, cost_layer_emission_line_id);

    create index if not exists ix_receipt_grir_bridge_lines_receipt
      on receipt_grir_bridge_lines (company_id, receipt_id, refreshed_at desc);

    create index if not exists ix_receipt_grir_bridge_lines_bill
      on receipt_grir_bridge_lines (company_id, bill_id, bill_line_number);

    create index if not exists ix_receipt_grir_bridge_lines_status
      on receipt_grir_bridge_lines (company_id, bridge_status);
  end if;
end $$;
