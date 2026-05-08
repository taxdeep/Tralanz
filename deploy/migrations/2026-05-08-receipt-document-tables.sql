-- Stage-1.4 batch 3: extract PostgresReceiptDocumentRepository.EnsureSchemaAsync.
-- Same SQL applied at deploy time by the migration runner. The inline
-- helper still exists (cached via _schemaEnsured) so fresh test
-- databases keep working, but the production hot path no longer
-- takes any AccessExclusiveLock on receipt_lines / receipts.

create table if not exists receipts (
  id uuid primary key,
  company_id char(7) not null,
  entity_number char(11) not null,
  receipt_number text not null,
  vendor_id uuid not null,
  warehouse_id uuid not null,
  status text not null,
  receipt_date date not null,
  vendor_reference text null,
  source_reference text null,
  memo text null,
  created_by_user_id char(7) not null,
  created_at timestamptz not null default now(),
  updated_by_user_id char(7) null,
  updated_at timestamptz not null default now(),
  posted_by_user_id char(7) null,
  posted_at timestamptz null
);

create unique index if not exists ux_receipts_company_entity_number
  on receipts (company_id, entity_number);

create unique index if not exists ux_receipts_company_receipt_number
  on receipts (company_id, receipt_number);

create index if not exists ix_receipts_company_receipt_date
  on receipts (company_id, receipt_date desc, created_at desc);

create table if not exists receipt_lines (
  id uuid primary key,
  company_id char(7) not null,
  receipt_id uuid not null,
  line_number integer not null,
  item_id uuid not null,
  quantity numeric(18,6) not null,
  uom_code text not null,
  tracking_capture_home text null,
  purchase_order_id uuid null,
  purchase_order_line_number integer null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

alter table receipt_lines
  add column if not exists purchase_order_id uuid null;

alter table receipt_lines
  add column if not exists purchase_order_line_number integer null;

create unique index if not exists ux_receipt_lines_company_receipt_line
  on receipt_lines (company_id, receipt_id, line_number);

create index if not exists ix_receipt_lines_company_receipt
  on receipt_lines (company_id, receipt_id, line_number);

create index if not exists ix_receipt_lines_company_purchase_order_line
  on receipt_lines (company_id, purchase_order_id, purchase_order_line_number);
