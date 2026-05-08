-- Stage-1.4 batch 3: extract PostgresReceiptGrIrPostingRepository.EnsureSchemaAsync.
-- Adds late lifecycle columns to receipt_grir_bridge_lines and
-- creates the two posting-batch tables. The inline helper still
-- exists (cached) for fresh test databases.
--
-- The whole block is wrapped in a `do $$ if to_regclass(...) is not
-- null` guard because the prerequisite table (receipt_grir_bridge_lines)
-- is created on-demand by PostgreSqlReceiptGrIrBridgeStore — pilot
-- deployments that haven't used GR/IR yet won't have it. When that
-- helper does run for the first time and creates the table, this
-- migration will already be marked applied; the helper handles the
-- column ADDs / table CREATEs in its own EnsureSchemaAsync, so we
-- don't lose anything by letting this one no-op on cold installs.

do $$
begin
  if to_regclass('receipt_grir_bridge_lines') is not null then
    alter table receipt_grir_bridge_lines
      add column if not exists journal_entry_id uuid null references journal_entries(id) on delete set null;

    alter table receipt_grir_bridge_lines
      add column if not exists journal_entry_display_number text null;

    alter table receipt_grir_bridge_lines
      add column if not exists posted_by_user_id char(7) null;

    alter table receipt_grir_bridge_lines
      add column if not exists posted_at timestamptz null;

    create table if not exists receipt_grir_bridge_posting_batches (
      id uuid primary key,
      company_id char(7) not null references companies(id) on delete cascade,
      receipt_id uuid not null,
      entity_number char(11) not null,
      display_number text not null,
      status text not null,
      document_date date not null,
      transaction_currency_code char(3) not null,
      base_currency_code char(3) not null,
      grir_clearing_account_id uuid not null references accounts(id),
      total_amount_base numeric(20, 6) not null,
      line_count integer not null,
      journal_entry_id uuid null references journal_entries(id) on delete set null,
      journal_entry_display_number text null,
      created_by_user_id char(7) not null,
      created_at timestamptz not null default now(),
      posted_by_user_id char(7) null,
      posted_at timestamptz null,
      updated_at timestamptz not null default now()
    );

    create unique index if not exists ux_receipt_grir_bridge_posting_batches_entity
      on receipt_grir_bridge_posting_batches (company_id, entity_number);

    create index if not exists ix_receipt_grir_bridge_posting_batches_receipt
      on receipt_grir_bridge_posting_batches (company_id, receipt_id, created_at desc);

    create table if not exists receipt_grir_bridge_posting_batch_lines (
      id uuid primary key default gen_random_uuid(),
      company_id char(7) not null references companies(id) on delete cascade,
      posting_batch_id uuid not null references receipt_grir_bridge_posting_batches(id) on delete cascade,
      bridge_line_id uuid not null references receipt_grir_bridge_lines(id) on delete cascade,
      inventory_asset_account_id uuid not null references accounts(id),
      amount_base numeric(20, 6) not null,
      created_at timestamptz not null default now()
    );

    create unique index if not exists ux_receipt_grir_bridge_posting_batch_lines_bridge
      on receipt_grir_bridge_posting_batch_lines (company_id, bridge_line_id);

    create index if not exists ix_receipt_grir_bridge_posting_batch_lines_batch
      on receipt_grir_bridge_posting_batch_lines (company_id, posting_batch_id);
  end if;
end $$;
