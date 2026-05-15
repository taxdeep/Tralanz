-- Move counterparty catalog schema out of app startup/runtime DDL.
-- Apply with a migration/admin role before running app services without
-- DDL privileges.

create table if not exists customers (
  id uuid primary key default gen_random_uuid(),
  company_id char(7) not null references companies(id) on delete cascade,
  entity_number char(11) not null,
  display_name text not null,
  default_currency_code char(3) not null references currency_catalog(code) on delete restrict,
  email text,
  phone text,
  address text,
  is_active boolean not null default true,
  currency_locked boolean not null default false,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint customers_entity_number_format_chk check (entity_number ~ '^EN[0-9]{4}[A-Z0-9]{5}$')
);

alter table customers add column if not exists address_line text null;
alter table customers add column if not exists city text null;
alter table customers add column if not exists province_state text null;
alter table customers add column if not exists postal_code text null;
alter table customers add column if not exists country text null;
alter table customers add column if not exists tax_id text null;
alter table customers add column if not exists notes text null;
alter table customers add column if not exists payment_term_id uuid null;
alter table customers add column if not exists customer_number text null;

alter table customers drop constraint if exists customers_entity_number_key;
drop index if exists uq_customers_entity_number;
create unique index if not exists uq_customers_company_entity_number
  on customers (company_id, entity_number);
create unique index if not exists uq_customers_company_customer_number
  on customers (company_id, customer_number)
  where customer_number is not null;
create index if not exists idx_customers_company_active
  on customers (company_id, is_active);
create index if not exists idx_customers_company_name
  on customers (company_id, display_name);

create table if not exists vendors (
  id uuid primary key default gen_random_uuid(),
  company_id char(7) not null references companies(id) on delete cascade,
  entity_number char(11) not null,
  display_name text not null,
  default_currency_code char(3) not null references currency_catalog(code) on delete restrict,
  email text,
  phone text,
  address text,
  is_active boolean not null default true,
  currency_locked boolean not null default false,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint vendors_entity_number_format_chk check (entity_number ~ '^EN[0-9]{4}[A-Z0-9]{5}$')
);

alter table vendors add column if not exists address_line text null;
alter table vendors add column if not exists city text null;
alter table vendors add column if not exists province_state text null;
alter table vendors add column if not exists postal_code text null;
alter table vendors add column if not exists country text null;
alter table vendors add column if not exists tax_id text null;
alter table vendors add column if not exists notes text null;
alter table vendors add column if not exists payment_term_id uuid null;
alter table vendors add column if not exists vendor_number text null;

alter table vendors drop constraint if exists vendors_entity_number_key;
drop index if exists uq_vendors_entity_number;
create unique index if not exists uq_vendors_company_entity_number
  on vendors (company_id, entity_number);
create unique index if not exists uq_vendors_company_vendor_number
  on vendors (company_id, vendor_number)
  where vendor_number is not null;
create index if not exists idx_vendors_company_active
  on vendors (company_id, is_active);
create index if not exists idx_vendors_company_name
  on vendors (company_id, display_name);

create table if not exists payment_terms (
  id uuid primary key default gen_random_uuid(),
  company_id char(7) not null references companies(id) on delete cascade,
  code text not null,
  name text not null,
  net_days integer not null default 0,
  is_active boolean not null default true,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create unique index if not exists uq_payment_terms_company_code
  on payment_terms (company_id, code);
create index if not exists idx_payment_terms_company_active
  on payment_terms (company_id, is_active);

create table if not exists customer_shipping_address_book (
  id uuid primary key default gen_random_uuid(),
  company_id char(7) not null references companies(id) on delete cascade,
  customer_id uuid not null references customers(id) on delete cascade,
  label text,
  address_line text not null default '',
  city text not null default '',
  province_state text not null default '',
  postal_code text not null default '',
  country text not null default '',
  is_default boolean not null default false,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create index if not exists ix_customer_shipping_address_book_customer
  on customer_shipping_address_book (company_id, customer_id);
create unique index if not exists uq_customer_shipping_address_book_default
  on customer_shipping_address_book (company_id, customer_id)
  where is_default;

create table if not exists vendor_shipping_address_book (
  id uuid primary key default gen_random_uuid(),
  company_id char(7) not null references companies(id) on delete cascade,
  vendor_id uuid not null references vendors(id) on delete cascade,
  label text,
  address_line text not null default '',
  city text not null default '',
  province_state text not null default '',
  postal_code text not null default '',
  country text not null default '',
  is_default boolean not null default false,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create index if not exists ix_vendor_shipping_address_book_vendor
  on vendor_shipping_address_book (company_id, vendor_id);
create unique index if not exists uq_vendor_shipping_address_book_default
  on vendor_shipping_address_book (company_id, vendor_id)
  where is_default;
