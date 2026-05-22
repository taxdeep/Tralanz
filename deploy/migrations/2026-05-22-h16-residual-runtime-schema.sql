-- =====================================================================
-- H16: Residual runtime schema → migrations catch-up
-- =====================================================================
--
-- Background: AUDIT_2026-05-20.md H16 flagged that several
-- `EnsureSchemaAsync` paths still emit CREATE TABLE / ALTER TABLE at
-- application startup. In production the `SchemaManagement:
-- ApplyOnStartup` flag (Citus.Accounting.Api/Program.cs:1104,
-- Citus.SysAdmin.Api/Program.cs:1819) gates that block off, so the
-- live risk is zero today — but a greenfield deploy with the gate on
-- (dev / CI / a future deployment where someone forgets to set the
-- flag) still relies on the C# bootstrap. The five `EnsureSchemaAsync`
-- sites covered here lack an authoritative migration counterpart;
-- this file is the catch-up.
--
-- Sites covered:
--   1. PostgresV1WriteFlowSchemaBootstrap  (8 tables + ALTERs)
--   2. PostgresCustomerDepositSchemaBootstrap  (1 table + 2 ALTERs)
--   3. PostgreSqlQuoteStore                    (2 tables)
--   4. PostgreSqlSalesOrderStore               (2 tables)
--
-- Sites already covered by prior migrations (no-op here):
--   * PostgreSqlInventoryItemPriceStore →
--     2026-05-19-task-module-and-permission-catalog.sql
--
-- Every statement below is `IF NOT EXISTS`-guarded and re-runnable.
-- The SQL is ported verbatim from the C# bootstraps so the two
-- definitions never drift; if a future schema change lands in the
-- bootstrap it must also land in a follow-up migration.
-- =====================================================================

begin;

-- ---------------------------------------------------------------------
-- Site 1: V1 write-flow document tables (sales_receipts, refund_receipts,
-- bank_transfers, bank_deposits, tax_returns) + cross-table customer-PO /
-- sales-order columns on invoices + credit_notes.
-- ---------------------------------------------------------------------

-- Sales Receipts ------------------------------------------------------
create table if not exists sales_receipts (
  id uuid primary key default gen_random_uuid(),
  company_id char(7) not null references companies(id) on delete cascade,
  entity_number char(11) not null unique,
  receipt_number text not null,
  customer_id uuid not null references customers(id) on delete restrict,
  status text not null default 'draft',
  receipt_date date not null,
  document_currency_code char(3) not null references currency_catalog(code) on delete restrict,
  base_currency_code char(3) not null references currency_catalog(code) on delete restrict,
  fx_rate_snapshot_id uuid references company_fx_rate_snapshots(id) on delete restrict,
  fx_rate numeric(20,10) not null default 1,
  fx_requested_date date not null,
  fx_effective_date date not null,
  fx_source text not null default 'identity',
  deposit_to_account_id uuid not null references accounts(id) on delete restrict,
  payment_method text not null default 'cash',
  reference_no text,
  subtotal_amount numeric(20,6) not null default 0,
  tax_amount numeric(20,6) not null default 0,
  total_amount numeric(20,6) not null default 0,
  memo text,
  customer_po_number text,
  posted_at timestamptz,
  created_by_user_id char(7) not null references users(id) on delete restrict,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint sales_receipts_status_chk check (status in ('draft', 'posted', 'voided', 'reversed')),
  constraint sales_receipts_payment_method_chk check (payment_method in ('cash', 'cheque', 'credit_card', 'wire', 'direct_deposit', 'eft', 'other')),
  constraint sales_receipts_fx_rate_positive_chk check (fx_rate > 0),
  constraint sales_receipts_unique_company_receipt_number unique (company_id, receipt_number)
);

create table if not exists sales_receipt_lines (
  id uuid primary key default gen_random_uuid(),
  company_id char(7) not null references companies(id) on delete cascade,
  sales_receipt_id uuid not null references sales_receipts(id) on delete cascade,
  line_number integer not null,
  revenue_account_id uuid not null references accounts(id) on delete restrict,
  description text not null,
  quantity numeric(20,6) not null,
  unit_price numeric(20,6) not null,
  line_amount numeric(20,6) not null,
  tax_code_id uuid references tax_codes(id) on delete set null,
  tax_amount numeric(20,6) not null default 0,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint sales_receipt_lines_quantity_nonnegative_chk check (quantity >= 0),
  constraint sales_receipt_lines_unit_price_nonnegative_chk check (unit_price >= 0),
  constraint sales_receipt_lines_unique_line unique (sales_receipt_id, line_number)
);

-- Backfill for legacy rows created before the column was added to the
-- inline CREATE TABLE above. No-op when the column is already present.
alter table sales_receipts add column if not exists customer_po_number text;

create index if not exists ix_sales_receipts_company_status on sales_receipts (company_id, status);
create index if not exists ix_sales_receipts_company_customer on sales_receipts (company_id, customer_id);
create index if not exists ix_sales_receipts_company_customer_po
  on sales_receipts (company_id, customer_po_number)
  where customer_po_number is not null;
create index if not exists ix_sales_receipt_lines_sales_receipt on sales_receipt_lines (sales_receipt_id, line_number);

-- Refund Receipts -----------------------------------------------------
create table if not exists refund_receipts (
  id uuid primary key default gen_random_uuid(),
  company_id char(7) not null references companies(id) on delete cascade,
  entity_number char(11) not null unique,
  refund_number text not null,
  customer_id uuid not null references customers(id) on delete restrict,
  status text not null default 'draft',
  refund_date date not null,
  document_currency_code char(3) not null references currency_catalog(code) on delete restrict,
  base_currency_code char(3) not null references currency_catalog(code) on delete restrict,
  fx_rate_snapshot_id uuid references company_fx_rate_snapshots(id) on delete restrict,
  fx_rate numeric(20,10) not null default 1,
  fx_requested_date date not null,
  fx_effective_date date not null,
  fx_source text not null default 'identity',
  refund_from_account_id uuid not null references accounts(id) on delete restrict,
  payment_method text not null default 'cash',
  reference_no text,
  reason text,
  subtotal_amount numeric(20,6) not null default 0,
  tax_amount numeric(20,6) not null default 0,
  total_amount numeric(20,6) not null default 0,
  memo text,
  customer_po_number text,
  posted_at timestamptz,
  created_by_user_id char(7) not null references users(id) on delete restrict,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint refund_receipts_status_chk check (status in ('draft', 'posted', 'voided', 'reversed')),
  constraint refund_receipts_payment_method_chk check (payment_method in ('cash', 'cheque', 'credit_card', 'wire', 'direct_deposit', 'eft', 'other')),
  constraint refund_receipts_fx_rate_positive_chk check (fx_rate > 0),
  constraint refund_receipts_unique_company_refund_number unique (company_id, refund_number)
);

create table if not exists refund_receipt_lines (
  id uuid primary key default gen_random_uuid(),
  company_id char(7) not null references companies(id) on delete cascade,
  refund_receipt_id uuid not null references refund_receipts(id) on delete cascade,
  line_number integer not null,
  revenue_account_id uuid not null references accounts(id) on delete restrict,
  description text not null,
  quantity numeric(20,6) not null,
  unit_price numeric(20,6) not null,
  line_amount numeric(20,6) not null,
  tax_code_id uuid references tax_codes(id) on delete set null,
  tax_amount numeric(20,6) not null default 0,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint refund_receipt_lines_quantity_nonnegative_chk check (quantity >= 0),
  constraint refund_receipt_lines_unit_price_nonnegative_chk check (unit_price >= 0),
  constraint refund_receipt_lines_unique_line unique (refund_receipt_id, line_number)
);

alter table refund_receipts add column if not exists customer_po_number text;

create index if not exists ix_refund_receipts_company_status on refund_receipts (company_id, status);
create index if not exists ix_refund_receipts_company_customer on refund_receipts (company_id, customer_id);
create index if not exists ix_refund_receipts_company_customer_po
  on refund_receipts (company_id, customer_po_number)
  where customer_po_number is not null;
create index if not exists ix_refund_receipt_lines_refund_receipt on refund_receipt_lines (refund_receipt_id, line_number);

-- Bank Transfers ------------------------------------------------------
create table if not exists bank_transfers (
  id uuid primary key default gen_random_uuid(),
  company_id char(7) not null references companies(id) on delete cascade,
  entity_number char(11) not null unique,
  transfer_number text not null,
  status text not null default 'draft',
  transfer_date date not null,
  from_account_id uuid not null references accounts(id) on delete restrict,
  from_currency_code char(3) not null references currency_catalog(code) on delete restrict,
  to_account_id uuid not null references accounts(id) on delete restrict,
  to_currency_code char(3) not null references currency_catalog(code) on delete restrict,
  amount numeric(20,6) not null,
  fx_rate numeric(20,10),
  reference_no text,
  memo text,
  posted_at timestamptz,
  created_by_user_id char(7) not null references users(id) on delete restrict,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint bank_transfers_status_chk check (status in ('draft', 'posted', 'voided', 'reversed')),
  constraint bank_transfers_amount_positive_chk check (amount > 0),
  constraint bank_transfers_distinct_accounts_chk check (from_account_id <> to_account_id),
  constraint bank_transfers_fx_rate_positive_chk check (fx_rate is null or fx_rate > 0),
  constraint bank_transfers_fx_rate_polarity_chk check (
    (from_currency_code = to_currency_code and fx_rate is null) or
    (from_currency_code <> to_currency_code and fx_rate is not null)
  ),
  constraint bank_transfers_unique_company_transfer_number unique (company_id, transfer_number)
);

create index if not exists ix_bank_transfers_company_status on bank_transfers (company_id, status);
create index if not exists ix_bank_transfers_company_from on bank_transfers (company_id, from_account_id);
create index if not exists ix_bank_transfers_company_to on bank_transfers (company_id, to_account_id);

-- Bank Deposits -------------------------------------------------------
create table if not exists bank_deposits (
  id uuid primary key default gen_random_uuid(),
  company_id char(7) not null references companies(id) on delete cascade,
  entity_number char(11) not null unique,
  deposit_number text not null,
  status text not null default 'draft',
  deposit_date date not null,
  deposit_to_account_id uuid not null references accounts(id) on delete restrict,
  document_currency_code char(3) not null references currency_catalog(code) on delete restrict,
  total_amount numeric(20,6) not null default 0,
  reference_no text,
  memo text,
  posted_at timestamptz,
  created_by_user_id char(7) not null references users(id) on delete restrict,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint bank_deposits_status_chk check (status in ('draft', 'posted', 'voided', 'reversed')),
  constraint bank_deposits_total_nonnegative_chk check (total_amount >= 0),
  constraint bank_deposits_unique_company_deposit_number unique (company_id, deposit_number)
);

create table if not exists bank_deposit_items (
  id uuid primary key default gen_random_uuid(),
  company_id char(7) not null references companies(id) on delete cascade,
  bank_deposit_id uuid not null references bank_deposits(id) on delete cascade,
  line_number integer not null,
  source_item_kind text not null,
  source_item_id uuid,
  source_item_display_number text not null,
  payer_name text,
  payment_method text,
  reference_no text,
  amount numeric(20,6) not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint bank_deposit_items_amount_positive_chk check (amount > 0),
  constraint bank_deposit_items_kind_chk check (source_item_kind in ('sales_receipt', 'receive_payment', 'journal_entry', 'manual')),
  constraint bank_deposit_items_payment_method_chk check (payment_method is null or payment_method in ('cash', 'cheque', 'credit_card', 'wire', 'direct_deposit', 'eft', 'other')),
  constraint bank_deposit_items_unique_line unique (bank_deposit_id, line_number)
);

create index if not exists ix_bank_deposits_company_status on bank_deposits (company_id, status);
create index if not exists ix_bank_deposits_company_deposit_to on bank_deposits (company_id, deposit_to_account_id);
create index if not exists ix_bank_deposit_items_bank_deposit on bank_deposit_items (bank_deposit_id, line_number);
create index if not exists ix_bank_deposit_items_source on bank_deposit_items (company_id, source_item_kind, source_item_id);

-- Tax Returns ---------------------------------------------------------
create table if not exists tax_returns (
  id uuid primary key default gen_random_uuid(),
  company_id char(7) not null references companies(id) on delete cascade,
  entity_number char(11) not null unique,
  return_number text not null,
  status text not null default 'draft',
  tax_regime text not null,
  filing_frequency text not null,
  period_start date not null,
  period_end date not null,
  base_currency_code char(3) not null references currency_catalog(code) on delete restrict,
  collected_amount numeric(20,6) not null default 0,
  input_credits_amount numeric(20,6) not null default 0,
  adjustments_amount numeric(20,6) not null default 0,
  adjustments_note text,
  net_amount numeric(20,6) not null default 0,
  regulator_reference_no text,
  memo text,
  posted_at timestamptz,
  created_by_user_id char(7) not null references users(id) on delete restrict,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint tax_returns_status_chk check (status in ('draft', 'posted', 'voided', 'amended')),
  constraint tax_returns_filing_frequency_chk check (filing_frequency in ('monthly', 'quarterly', 'annual')),
  constraint tax_returns_period_chk check (period_end >= period_start),
  constraint tax_returns_unique_company_return_number unique (company_id, return_number),
  constraint tax_returns_unique_posted_period unique (company_id, tax_regime, period_end, status) deferrable initially deferred
);

create index if not exists ix_tax_returns_company_status on tax_returns (company_id, status);
create index if not exists ix_tax_returns_company_period on tax_returns (company_id, tax_regime, period_end);

-- updated_at triggers (DROP+CREATE so re-runs replace any prior wiring) -
drop trigger if exists trg_sales_receipts_set_updated_at on sales_receipts;
create trigger trg_sales_receipts_set_updated_at before update on sales_receipts for each row execute function citus_set_updated_at();

drop trigger if exists trg_sales_receipt_lines_set_updated_at on sales_receipt_lines;
create trigger trg_sales_receipt_lines_set_updated_at before update on sales_receipt_lines for each row execute function citus_set_updated_at();

drop trigger if exists trg_refund_receipts_set_updated_at on refund_receipts;
create trigger trg_refund_receipts_set_updated_at before update on refund_receipts for each row execute function citus_set_updated_at();

drop trigger if exists trg_refund_receipt_lines_set_updated_at on refund_receipt_lines;
create trigger trg_refund_receipt_lines_set_updated_at before update on refund_receipt_lines for each row execute function citus_set_updated_at();

drop trigger if exists trg_bank_transfers_set_updated_at on bank_transfers;
create trigger trg_bank_transfers_set_updated_at before update on bank_transfers for each row execute function citus_set_updated_at();

drop trigger if exists trg_bank_deposits_set_updated_at on bank_deposits;
create trigger trg_bank_deposits_set_updated_at before update on bank_deposits for each row execute function citus_set_updated_at();

drop trigger if exists trg_bank_deposit_items_set_updated_at on bank_deposit_items;
create trigger trg_bank_deposit_items_set_updated_at before update on bank_deposit_items for each row execute function citus_set_updated_at();

drop trigger if exists trg_tax_returns_set_updated_at on tax_returns;
create trigger trg_tax_returns_set_updated_at before update on tax_returns for each row execute function citus_set_updated_at();

-- Customer-PO + sales-order link columns on invoices / credit_notes ----
-- These columns live on tables whose CREATE TABLE belongs to other
-- migrations; this catch-up only adds the missing ADD COLUMN + partial
-- index lines that the bootstrap was emitting.
alter table invoices             add column if not exists customer_po_number text;
alter table invoices             add column if not exists sales_order_id     uuid;
alter table credit_notes         add column if not exists customer_po_number text;

create index if not exists ix_invoices_company_customer_po
  on invoices (company_id, customer_po_number)
  where customer_po_number is not null;
create index if not exists ix_invoices_company_sales_order
  on invoices (company_id, sales_order_id)
  where sales_order_id is not null;
create index if not exists ix_credit_notes_company_customer_po
  on credit_notes (company_id, customer_po_number)
  where customer_po_number is not null;

-- ---------------------------------------------------------------------
-- Site 2: customer_deposits + receive_payments.extra_deposit_amount
-- ---------------------------------------------------------------------
do $$
begin
  if not exists (
    select 1
    from information_schema.columns
    where table_name = 'receive_payments'
      and column_name = 'extra_deposit_amount'
  ) then
    alter table receive_payments
      add column extra_deposit_amount numeric(20,6) not null default 0;
    alter table receive_payments
      add constraint receive_payments_extra_deposit_nonnegative_chk
      check (extra_deposit_amount >= 0);
  end if;
end $$;

create table if not exists customer_deposits (
  id uuid primary key default gen_random_uuid(),
  company_id char(7) not null,
  customer_id uuid not null,
  entity_number char(11) not null unique,
  display_number text not null,
  status text not null default 'open',
  deposit_date date not null,
  transaction_currency_code char(3) not null,
  base_currency_code char(3) not null,
  fx_rate_snapshot_id uuid null,
  fx_rate numeric(20,10) not null default 1,
  fx_requested_date date not null,
  fx_effective_date date not null,
  fx_source text not null default 'identity',
  original_amount_tx numeric(20,6) not null,
  original_amount_base numeric(20,6) not null,
  source_receive_payment_id uuid null,
  memo text null,
  posted_at timestamptz null,
  created_by_user_id char(7) not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint customer_deposits_status_chk
    check (status in ('open', 'partially_applied', 'closed', 'voided')),
  constraint customer_deposits_fx_rate_positive_chk
    check (fx_rate > 0),
  constraint customer_deposits_amount_positive_chk
    check (original_amount_tx > 0 and original_amount_base > 0),
  constraint customer_deposits_unique_company_display_number
    unique (company_id, display_number)
);

create index if not exists ix_customer_deposits_customer_open
  on customer_deposits (company_id, customer_id, status);

create index if not exists ix_customer_deposits_source_receive_payment
  on customer_deposits (source_receive_payment_id)
  where source_receive_payment_id is not null;

alter table customer_deposits
  add column if not exists source_sales_order_id uuid null;

create index if not exists ix_customer_deposits_source_sales_order
  on customer_deposits (source_sales_order_id)
  where source_sales_order_id is not null;

-- ---------------------------------------------------------------------
-- Site 3: quotes + quote_lines
-- ---------------------------------------------------------------------
create table if not exists quotes (
  id                          uuid primary key default gen_random_uuid(),
  company_id                  char(7) not null,
  quote_number                text not null,
  status                      text not null default 'draft',
  customer_id                 uuid not null,
  document_date               date not null,
  expiration_date             date null,
  transaction_currency_code   char(3) not null,
  fx_rate                     numeric(18,8) null,
  billing_address_line        text null,
  billing_city                text null,
  billing_province_state      text null,
  billing_postal_code         text null,
  billing_country             text null,
  shipping_address_line       text null,
  shipping_city               text null,
  shipping_province_state     text null,
  shipping_postal_code        text null,
  shipping_country            text null,
  ship_via                    text null,
  shipping_date               date null,
  tracking_no                 text null,
  tax_mode                    text not null default 'exclusive',
  discount_kind               text null,
  discount_value              numeric(18,4) null,
  shipping_amount             numeric(18,4) null,
  shipping_tax_code_id        uuid null,
  subtotal_amount             numeric(18,4) not null default 0,
  discount_amount             numeric(18,4) not null default 0,
  tax_amount                  numeric(18,4) not null default 0,
  total_amount                numeric(18,4) not null default 0,
  memo_to_customer            text null,
  internal_note               text null,
  converted_sales_order_id    uuid null,
  customer_po_number          text null,
  created_at                  timestamptz not null default now(),
  updated_at                  timestamptz not null default now()
);

alter table quotes add column if not exists customer_po_number text null;

create unique index if not exists uq_quotes_company_quote_number
  on quotes (company_id, quote_number);
create index if not exists idx_quotes_company_status
  on quotes (company_id, status);
create index if not exists idx_quotes_company_customer
  on quotes (company_id, customer_id);
create index if not exists idx_quotes_company_document_date
  on quotes (company_id, document_date desc);
create index if not exists idx_quotes_company_customer_po
  on quotes (company_id, customer_po_number)
  where customer_po_number is not null;

create table if not exists quote_lines (
  id              uuid primary key default gen_random_uuid(),
  quote_id        uuid not null references quotes(id) on delete cascade,
  sequence        integer not null,
  service_date    date null,
  item_id         uuid null,
  description     text not null default '',
  quantity        numeric(18,4) not null default 0,
  unit_price      numeric(18,4) not null default 0,
  tax_code_id     uuid null,
  account_code    text null,
  line_total      numeric(18,4) not null default 0
);
create index if not exists idx_quote_lines_quote
  on quote_lines (quote_id, sequence);

-- ---------------------------------------------------------------------
-- Site 4: sales_orders + sales_order_lines
-- ---------------------------------------------------------------------
create table if not exists sales_orders (
  id                          uuid primary key default gen_random_uuid(),
  company_id                  char(7) not null,
  sales_order_number          text not null,
  status                      text not null default 'open',
  customer_id                 uuid not null,
  document_date               date not null,
  transaction_currency_code   char(3) not null,
  fx_rate                     numeric(18,8) null,
  billing_address_line        text null,
  billing_city                text null,
  billing_province_state      text null,
  billing_postal_code         text null,
  billing_country             text null,
  shipping_address_line       text null,
  shipping_city               text null,
  shipping_province_state     text null,
  shipping_postal_code        text null,
  shipping_country            text null,
  ship_via                    text null,
  shipping_date               date null,
  tracking_no                 text null,
  tax_mode                    text not null default 'exclusive',
  discount_kind               text null,
  discount_value              numeric(18,4) null,
  shipping_amount             numeric(18,4) null,
  shipping_tax_code_id        uuid null,
  subtotal_amount             numeric(18,4) not null default 0,
  discount_amount             numeric(18,4) not null default 0,
  tax_amount                  numeric(18,4) not null default 0,
  total_amount                numeric(18,4) not null default 0,
  memo_to_customer            text null,
  internal_note               text null,
  source_quote_id             uuid null,
  invoice_number              text null,
  customer_po_number          text null,
  confirmed_at                timestamptz null,
  created_at                  timestamptz not null default now(),
  updated_at                  timestamptz not null default now()
);

alter table sales_orders add column if not exists customer_po_number text null;
alter table sales_orders add column if not exists confirmed_at timestamptz null;

create unique index if not exists uq_sales_orders_company_so_number
  on sales_orders (company_id, sales_order_number);
create index if not exists idx_sales_orders_company_status
  on sales_orders (company_id, status);
create index if not exists idx_sales_orders_company_customer
  on sales_orders (company_id, customer_id);
create index if not exists idx_sales_orders_company_document_date
  on sales_orders (company_id, document_date desc);
create index if not exists idx_sales_orders_company_customer_po
  on sales_orders (company_id, customer_po_number)
  where customer_po_number is not null;

create table if not exists sales_order_lines (
  id              uuid primary key default gen_random_uuid(),
  sales_order_id  uuid not null references sales_orders(id) on delete cascade,
  sequence        integer not null,
  service_date    date null,
  item_id         uuid null,
  description     text not null default '',
  quantity        numeric(18,4) not null default 0,
  unit_price      numeric(18,4) not null default 0,
  tax_code_id     uuid null,
  account_code    text null,
  line_total      numeric(18,4) not null default 0,
  reserved_qty    numeric(18,4) not null default 0,
  backorder_qty   numeric(18,4) not null default 0,
  shipped_qty     numeric(18,4) not null default 0
);

alter table sales_order_lines add column if not exists reserved_qty  numeric(18,4) not null default 0;
alter table sales_order_lines add column if not exists backorder_qty numeric(18,4) not null default 0;
alter table sales_order_lines add column if not exists shipped_qty   numeric(18,4) not null default 0;

create index if not exists idx_sales_order_lines_so
  on sales_order_lines (sales_order_id, sequence);

commit;
