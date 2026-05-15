-- Move request-path schema creation for GR/IR policy and platform
-- provisioning sequences into deployment-time migrations.
-- Apply with a migration/admin role before running app services without
-- DDL privileges.

create table if not exists receipt_grir_clearing_account_policies (
  company_id char(7) primary key references companies(id) on delete cascade,
  grir_clearing_account_id uuid not null references accounts(id),
  updated_by_user_id char(7) not null,
  updated_at timestamptz not null default now()
);

create table if not exists platform_entity_number_sequences (
  entity_year integer primary key,
  next_number bigint not null
);
