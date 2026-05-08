-- Stage-1.4 batch 3: extract PostgresReceiptGrIrApSettlementControlStore.EnsureSchemaAsync.
-- Four tables (settlement_lines, settlement_batches, settlement_batch_lines,
-- purchase_variance_lines) plus 17 ALTERs that add late open-item-clearing
-- + reversal audit columns. The inline helper still exists (cached) for
-- fresh test databases.
--
-- Wrapped in a guard because the FK target receipt_grir_bridge_lines
-- (and ap_open_items) are created on-demand by other helpers — pilot
-- deployments that haven't used GR/IR yet won't have them. The helper
-- still creates this surface lazily on first use.

do $$
begin
  if to_regclass('receipt_grir_bridge_lines') is not null
     and to_regclass('ap_open_items') is not null then
  create table if not exists receipt_grir_ap_settlement_lines (
    id uuid primary key default gen_random_uuid(),
    company_id char(7) not null references companies(id) on delete cascade,
    receipt_id uuid not null,
    receipt_line_number integer not null,
    bridge_line_id uuid not null references receipt_grir_bridge_lines(id) on delete cascade,
    journal_entry_id uuid null references journal_entries(id) on delete set null,
    journal_entry_display_number text null,
    bill_id uuid not null,
    bill_line_number integer not null,
    ap_open_item_id uuid null references ap_open_items(id) on delete set null,
    item_id uuid not null,
    warehouse_id uuid not null,
    uom_code text not null,
    settlement_quantity numeric(20,6) not null,
    settlement_amount_base numeric(20,6) not null,
    settled_quantity numeric(20,6) not null default 0,
    settled_amount_base numeric(20,6) not null default 0,
    remaining_amount_base numeric(20,6) not null,
    settlement_status text not null,
    blocked_reason_code text null,
    refreshed_by_user_id char(7) not null,
    refreshed_at timestamptz not null default now(),
    last_settled_at timestamptz null
  );

  create unique index if not exists ux_receipt_grir_ap_settlement_lines_bridge
    on receipt_grir_ap_settlement_lines (company_id, bridge_line_id);

  create index if not exists ix_receipt_grir_ap_settlement_lines_receipt
    on receipt_grir_ap_settlement_lines (company_id, receipt_id, settlement_status);

  create index if not exists ix_receipt_grir_ap_settlement_lines_bill
    on receipt_grir_ap_settlement_lines (company_id, bill_id, bill_line_number, settlement_status);

  create table if not exists receipt_grir_ap_settlement_batches (
    id uuid primary key,
    company_id char(7) not null references companies(id) on delete cascade,
    receipt_id uuid not null,
    idempotency_key text not null,
    status text not null,
    requested_amount_base numeric(20,6) not null,
    settled_quantity numeric(20,6) not null,
    settled_amount_base numeric(20,6) not null,
    line_count integer not null,
    created_by_user_id char(7) not null,
    created_at timestamptz not null default now()
  );

  alter table receipt_grir_ap_settlement_batches
    add column if not exists journal_status text not null default 'not_posted';

  alter table receipt_grir_ap_settlement_batches
    add column if not exists journal_entry_id uuid null references journal_entries(id) on delete set null;

  alter table receipt_grir_ap_settlement_batches
    add column if not exists journal_entry_display_number text null;

  alter table receipt_grir_ap_settlement_batches
    add column if not exists journal_posted_by_user_id char(7) null;

  alter table receipt_grir_ap_settlement_batches
    add column if not exists journal_posted_at timestamptz null;

  alter table receipt_grir_ap_settlement_batches
    add column if not exists journal_refreshed_at timestamptz null;

  alter table receipt_grir_ap_settlement_batches
    add column if not exists journal_blocked_reason_code text null;

  alter table receipt_grir_ap_settlement_batches
    add column if not exists open_item_clearing_status text not null default 'not_cleared';

  alter table receipt_grir_ap_settlement_batches
    add column if not exists open_item_clearing_blocked_reason_code text null;

  alter table receipt_grir_ap_settlement_batches
    add column if not exists open_item_cleared_by_user_id char(7) null;

  alter table receipt_grir_ap_settlement_batches
    add column if not exists open_item_cleared_at timestamptz null;

  alter table receipt_grir_ap_settlement_batches
    add column if not exists open_item_clearing_refreshed_at timestamptz null;

  alter table receipt_grir_ap_settlement_batches
    add column if not exists open_item_reversed_by_user_id char(7) null;

  alter table receipt_grir_ap_settlement_batches
    add column if not exists open_item_reversed_at timestamptz null;

  alter table receipt_grir_ap_settlement_batches
    add column if not exists open_item_reversed_application_count integer not null default 0;

  alter table receipt_grir_ap_settlement_batches
    add column if not exists open_item_reversed_amount_tx numeric(20,6) not null default 0;

  alter table receipt_grir_ap_settlement_batches
    add column if not exists open_item_reversed_amount_base numeric(20,6) not null default 0;

  create unique index if not exists ux_receipt_grir_ap_settlement_batches_key
    on receipt_grir_ap_settlement_batches (company_id, idempotency_key);

  create index if not exists ix_receipt_grir_ap_settlement_batches_receipt
    on receipt_grir_ap_settlement_batches (company_id, receipt_id, created_at desc);

  create table if not exists receipt_grir_ap_settlement_batch_lines (
    id uuid primary key default gen_random_uuid(),
    company_id char(7) not null references companies(id) on delete cascade,
    settlement_batch_id uuid not null references receipt_grir_ap_settlement_batches(id) on delete cascade,
    settlement_line_id uuid not null references receipt_grir_ap_settlement_lines(id) on delete restrict,
    bridge_line_id uuid not null references receipt_grir_bridge_lines(id) on delete restrict,
    bill_id uuid not null,
    ap_open_item_id uuid null references ap_open_items(id) on delete set null,
    settled_quantity numeric(20,6) not null,
    settled_amount_base numeric(20,6) not null,
    created_at timestamptz not null default now()
  );

  create unique index if not exists ux_receipt_grir_ap_settlement_batch_lines_line
    on receipt_grir_ap_settlement_batch_lines (company_id, settlement_batch_id, settlement_line_id);

  create index if not exists ix_receipt_grir_ap_settlement_batch_lines_bridge
    on receipt_grir_ap_settlement_batch_lines (company_id, bridge_line_id);

  create table if not exists receipt_grir_ap_purchase_variance_lines (
    id uuid primary key default gen_random_uuid(),
    company_id char(7) not null references companies(id) on delete cascade,
    receipt_id uuid not null,
    receipt_line_number integer not null,
    settlement_batch_id uuid not null references receipt_grir_ap_settlement_batches(id) on delete cascade,
    settlement_batch_line_id uuid not null references receipt_grir_ap_settlement_batch_lines(id) on delete cascade,
    settlement_line_id uuid not null references receipt_grir_ap_settlement_lines(id) on delete cascade,
    bridge_line_id uuid not null references receipt_grir_bridge_lines(id) on delete cascade,
    bill_id uuid not null,
    bill_line_number integer not null,
    item_id uuid not null,
    warehouse_id uuid not null,
    uom_code text not null,
    settled_quantity numeric(20,6) not null,
    grir_amount_base numeric(20,6) not null,
    bill_amount_base numeric(20,6) not null,
    variance_amount_base numeric(20,6) not null,
    variance_status text not null,
    blocked_reason_code text null,
    refreshed_by_user_id char(7) not null,
    refreshed_at timestamptz not null default now()
  );

  create unique index if not exists ux_receipt_grir_ap_purchase_variance_batch_line
    on receipt_grir_ap_purchase_variance_lines (company_id, settlement_batch_line_id);

  create index if not exists ix_receipt_grir_ap_purchase_variance_receipt
    on receipt_grir_ap_purchase_variance_lines (company_id, receipt_id, variance_status);

  create index if not exists ix_receipt_grir_ap_purchase_variance_bill
    on receipt_grir_ap_purchase_variance_lines (company_id, bill_id, bill_line_number, variance_status);
  end if;
end $$;
