-- Move FX cache and invoice delivery/template schema out of app
-- startup/runtime DDL. Apply with a migration/admin role before running
-- app services without DDL privileges.

create table if not exists fx_rates_daily (
  id uuid primary key default gen_random_uuid(),
  rate_date date not null,
  base_code char(3) not null,
  quote_code char(3) not null,
  rate numeric(20, 10) not null,
  source text not null,
  fetched_at timestamptz not null default now(),
  value_basis text not null default 'frankfurter',
  constraint fx_rates_daily_unique unique (rate_date, base_code, quote_code)
);

alter table fx_rates_daily
  add column if not exists value_basis text not null default 'frankfurter';

create index if not exists idx_fx_rates_daily_pair_date
  on fx_rates_daily (base_code, quote_code, rate_date desc);

create table if not exists invoice_send_history (
  id uuid primary key default gen_random_uuid(),
  company_id char(7) not null,
  invoice_id uuid not null,
  sent_at timestamptz not null default now(),
  sent_by_user_id char(7) not null,
  to_email text not null,
  cc_emails text not null default '',
  bcc_emails text not null default '',
  subject text not null,
  status text not null,
  error_message text,
  constraint invoice_send_history_status_chk check (status in ('sent', 'failed'))
);

create index if not exists invoice_send_history_invoice_idx
  on invoice_send_history (invoice_id, sent_at desc);

create table if not exists invoice_templates (
  id uuid primary key default gen_random_uuid(),
  company_id char(7) not null,
  name text not null,
  is_default boolean not null default false,
  config jsonb not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create index if not exists invoice_templates_company_idx
  on invoice_templates (company_id);

create unique index if not exists invoice_templates_company_default_idx
  on invoice_templates (company_id)
  where is_default = true;
