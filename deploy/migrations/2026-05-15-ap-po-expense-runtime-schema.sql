-- Move AP purchase order and expense schema out of app startup/runtime
-- DDL. Apply with a migration/admin role before running app services
-- without DDL privileges.

create table if not exists ap_purchase_orders (
  id uuid primary key default gen_random_uuid(),
  company_id char(7) not null,
  purchase_order_number text not null,
  status text not null default 'draft',
  vendor_id uuid not null,
  order_date date not null,
  expected_delivery_date date null,
  transaction_currency_code char(3) not null,
  fx_rate numeric(18,8) null,
  billing_address_line text null,
  billing_city text null,
  billing_province_state text null,
  billing_postal_code text null,
  billing_country text null,
  shipping_address_line text null,
  shipping_city text null,
  shipping_province_state text null,
  shipping_postal_code text null,
  shipping_country text null,
  ship_via text null,
  shipping_date date null,
  tracking_no text null,
  tax_mode text not null default 'exclusive',
  discount_kind text null,
  discount_value numeric(18,4) null,
  shipping_amount numeric(18,4) null,
  shipping_tax_code_id uuid null,
  subtotal_amount numeric(18,4) not null default 0,
  discount_amount numeric(18,4) not null default 0,
  tax_amount numeric(18,4) not null default 0,
  total_amount numeric(18,4) not null default 0,
  memo_to_supplier text null,
  internal_note text null,
  payment_term_id uuid null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create unique index if not exists uq_ap_purchase_orders_company_po_number
  on ap_purchase_orders (company_id, purchase_order_number);
create index if not exists idx_ap_purchase_orders_company_status
  on ap_purchase_orders (company_id, status);
create index if not exists idx_ap_purchase_orders_company_vendor
  on ap_purchase_orders (company_id, vendor_id);
create index if not exists idx_ap_purchase_orders_company_order_date
  on ap_purchase_orders (company_id, order_date desc);

create table if not exists ap_purchase_order_lines (
  id uuid primary key default gen_random_uuid(),
  purchase_order_id uuid not null references ap_purchase_orders(id) on delete cascade,
  sequence integer not null,
  service_date date null,
  item_id uuid null,
  expense_account_id uuid null,
  description text not null default '',
  quantity numeric(18,4) not null default 0,
  unit_price numeric(18,4) not null default 0,
  tax_code_id uuid null,
  line_total numeric(18,4) not null default 0
);

create index if not exists idx_ap_purchase_order_lines_po
  on ap_purchase_order_lines (purchase_order_id, sequence);

create table if not exists expenses (
  id uuid primary key default gen_random_uuid(),
  company_id char(7) not null,
  expense_number text not null,
  status text not null default 'posted',
  payee_kind text not null,
  payee_id uuid null,
  payee_name_freeform text not null default '',
  payment_account_id uuid not null,
  payment_method text not null,
  cheque_number text null,
  ref_no text null,
  transaction_currency_code char(3) not null,
  base_currency_code char(3) not null,
  fx_rate numeric(18,8) not null default 1,
  fx_source text not null default 'identity',
  payment_date date not null,
  source_purchase_order_id uuid null,
  source_purchase_order_number text null,
  tax_mode text not null default 'exclusive',
  discount_kind text null,
  discount_value numeric(18,4) null,
  subtotal_amount numeric(18,4) not null default 0,
  discount_amount numeric(18,4) not null default 0,
  tax_amount numeric(18,4) not null default 0,
  total_amount numeric(18,4) not null default 0,
  memo text null,
  internal_note text null,
  posted_journal_entry_id uuid null,
  voided_at timestamptz null,
  created_by_user_id char(7) not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create unique index if not exists uq_expenses_company_expense_number
  on expenses (company_id, expense_number);
create index if not exists idx_expenses_company_status_date
  on expenses (company_id, status, payment_date desc);
create index if not exists idx_expenses_company_payee
  on expenses (company_id, payee_id);
create index if not exists idx_expenses_source_po
  on expenses (source_purchase_order_id)
  where source_purchase_order_id is not null;

create table if not exists expense_lines (
  id uuid primary key default gen_random_uuid(),
  expense_id uuid not null references expenses(id) on delete cascade,
  sequence integer not null,
  service_date date null,
  item_id uuid null,
  expense_account_id uuid not null,
  description text not null default '',
  quantity numeric(18,4) not null default 0,
  unit_price numeric(18,4) not null default 0,
  tax_code_id uuid null,
  line_total numeric(18,4) not null default 0
);

create index if not exists idx_expense_lines_expense
  on expense_lines (expense_id, sequence);

-- One-time idempotent data migration: legacy seed data put "Cash on
-- Hand" under detail_type='bank'. The Payment Account picker groups by
-- detail_type, so move it under 'cash' when it still has the old label.
update accounts
   set detail_type = 'cash'
 where name = 'Cash on Hand'
   and detail_type = 'bank';
