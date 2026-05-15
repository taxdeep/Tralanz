-- Moves receipt inventory activation / valuation schema from first business
-- use into deploy-time schema. This migration is intentionally guarded so
-- older pilot databases that have not enabled Inventory yet can apply it
-- safely after the inventory foundation tables are installed.

do $$
begin
  if to_regclass('inventory_documents') is not null
     and to_regclass('inventory_document_lines') is not null
     and to_regclass('inventory_items') is not null
     and to_regclass('inventory_warehouses') is not null then

    alter table inventory_documents
      add column if not exists document_number text null;

    create unique index if not exists ux_inventory_documents_company_document_number
      on inventory_documents (company_id, lower(document_number))
      where document_number is not null;

    create table if not exists receipt_inventory_activation_lines (
      id uuid primary key default gen_random_uuid(),
      company_id char(7) not null references companies(id) on delete cascade,
      receipt_id uuid not null,
      receipt_line_number integer not null,
      inventory_document_id uuid not null references inventory_documents(id) on delete cascade,
      inventory_document_line_id uuid not null references inventory_document_lines(id) on delete cascade,
      item_id uuid not null references inventory_items(id) on delete cascade,
      warehouse_id uuid not null references inventory_warehouses(id) on delete cascade,
      uom_code text not null,
      activated_quantity numeric(20, 6) not null,
      activated_by_user_id char(7) not null,
      activated_at timestamptz not null default now()
    );

    create unique index if not exists ux_receipt_inventory_activation_lines_company_receipt_line
      on receipt_inventory_activation_lines (company_id, receipt_id, receipt_line_number);

    create index if not exists ix_receipt_inventory_activation_lines_company_receipt
      on receipt_inventory_activation_lines (company_id, receipt_id, activated_at desc);

    create table if not exists receipt_inventory_activation_failures (
      id uuid primary key default gen_random_uuid(),
      company_id char(7) not null references companies(id) on delete cascade,
      receipt_id uuid not null,
      failure_message text not null,
      recorded_by_user_id char(7) not null,
      recorded_at timestamptz not null default now()
    );

    create unique index if not exists ux_receipt_inventory_activation_failures_company_receipt
      on receipt_inventory_activation_failures (company_id, receipt_id);

    create table if not exists receipt_inventory_valuation_lines (
      id uuid primary key default gen_random_uuid(),
      company_id char(7) not null references companies(id) on delete cascade,
      receipt_id uuid not null,
      receipt_line_number integer not null,
      bill_id uuid not null,
      bill_line_number integer not null,
      item_id uuid not null references inventory_items(id) on delete cascade,
      warehouse_id uuid not null references inventory_warehouses(id) on delete cascade,
      uom_code text not null,
      valued_quantity numeric(20, 6) not null,
      document_currency_code text not null,
      base_currency_code text not null,
      fx_rate_to_base numeric(20, 8) not null,
      unit_cost_tx numeric(20, 6) not null,
      unit_cost_base numeric(20, 6) not null,
      extended_cost_base numeric(20, 6) not null,
      valuation_source text not null,
      valued_by_user_id char(7) not null,
      valued_at timestamptz not null default now()
    );

    create unique index if not exists ux_receipt_inventory_valuation_lines_natural
      on receipt_inventory_valuation_lines (company_id, receipt_id, receipt_line_number, bill_id, bill_line_number);

    create index if not exists ix_receipt_inventory_valuation_lines_receipt
      on receipt_inventory_valuation_lines (company_id, receipt_id, valued_at desc);

    create index if not exists ix_receipt_inventory_valuation_lines_bill
      on receipt_inventory_valuation_lines (company_id, bill_id, bill_line_number);
  end if;
end $$;
