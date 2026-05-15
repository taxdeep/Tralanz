-- Extracted from PostgresAccountingPeriodRepository.EnsureSchemaAsync.
-- Production deployments should apply this before disabling runtime
-- schema management in Accounting API.

create table if not exists accounting_periods (
  id uuid primary key default gen_random_uuid(),
  company_id char(7) not null references companies(id) on delete cascade,
  period_start date not null,
  period_end date not null,
  status text not null default 'open',
  closing_started_at timestamptz null,
  closed_at timestamptz null,
  locked_at timestamptz null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint ck_accounting_periods_status
    check (status in ('open', 'closing', 'closed', 'locked')),
  constraint ck_accounting_periods_range
    check (period_end >= period_start),
  constraint ux_accounting_periods_company_period
    unique (company_id, period_start, period_end)
);

create index if not exists ix_accounting_periods_company_status_start
  on accounting_periods (company_id, status, period_start);
