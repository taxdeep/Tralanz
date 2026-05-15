-- Move chart-of-accounts and AP bill additive schema out of app
-- startup/runtime DDL. Apply with a migration/admin role before running
-- app services without DDL privileges.

create table if not exists accounts (
  id uuid primary key default gen_random_uuid(),
  company_id char(7) not null references companies(id) on delete cascade,
  entity_number char(11) not null,
  code text not null,
  name text not null,
  root_type text not null,
  detail_type text not null,
  is_active boolean not null default true,
  is_system boolean not null default false,
  is_system_default boolean not null default false,
  system_key text,
  system_role text,
  currency_code char(3) references currency_catalog(code) on delete restrict,
  allow_manual_posting boolean not null default true,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint accounts_entity_number_format_chk check (entity_number ~ '^EN[0-9]{4}[A-Z0-9]{5}$'),
  constraint accounts_root_type_chk check (
    root_type in ('asset', 'liability', 'equity', 'revenue', 'cost_of_sales', 'expense')
  ),
  constraint accounts_unique_company_code unique (company_id, code)
);

alter table accounts drop constraint if exists accounts_entity_number_key;
drop index if exists uq_accounts_entity_number;
create unique index if not exists uq_accounts_company_entity_number
  on accounts (company_id, entity_number);
create index if not exists idx_accounts_company_active
  on accounts (company_id, is_active);
create index if not exists idx_accounts_company_root
  on accounts (company_id, root_type);

alter table bills add column if not exists payment_term_id uuid null;
alter table bills add column if not exists source_purchase_order_id uuid null;
alter table bills add column if not exists source_purchase_order_number text null;

create index if not exists idx_bills_company_status_date
  on bills (company_id, status, bill_date desc);
create index if not exists idx_bills_company_vendor
  on bills (company_id, vendor_id);
create index if not exists idx_bills_source_po
  on bills (source_purchase_order_id)
  where source_purchase_order_id is not null;
