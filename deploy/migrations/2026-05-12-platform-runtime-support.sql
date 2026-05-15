-- Extract remaining platform runtime/support schema from runtime EnsureSchemaAsync paths.
-- Production app roles should not need DDL privileges; apply this with the
-- migration/admin role before starting Accounting.Api or SysAdmin.Api with
-- SchemaManagement:ApplyOnStartup disabled.

create extension if not exists pgcrypto;

create table if not exists platform_runtime_state (
  state_key text primary key,
  json jsonb not null,
  updated_at timestamptz not null default now()
);

create table if not exists account_login_attempts (
  id uuid primary key default gen_random_uuid(),
  realm text not null,
  account_id char(7),
  email_hash text not null,
  remote_ip text,
  user_agent text,
  succeeded boolean not null,
  attempted_at timestamptz not null default now(),
  constraint account_login_attempts_realm_chk
    check (realm in ('sysadmin','business'))
);

create index if not exists ix_login_attempts_lookup
  on account_login_attempts (realm, email_hash, attempted_at desc);

create table if not exists account_lockouts (
  id uuid primary key default gen_random_uuid(),
  realm text not null,
  email_hash text not null,
  account_id char(7),
  lockout_kind text not null,
  locked_at timestamptz not null default now(),
  locked_until timestamptz,
  lifted_at timestamptz,
  lifted_by_sysadmin_id uuid,
  lifted_reason text,
  constraint account_lockouts_realm_chk
    check (realm in ('sysadmin','business')),
  constraint account_lockouts_kind_chk
    check (lockout_kind in ('temporary_15min','permanent'))
);

create index if not exists ix_lockouts_active
  on account_lockouts (realm, email_hash, locked_at desc)
  where lifted_at is null;

alter table sysadmin_accounts
  add column if not exists display_name text not null default '';

alter table sysadmin_accounts
  drop constraint if exists sysadmin_accounts_status_chk;

alter table sysadmin_accounts
  add constraint sysadmin_accounts_status_chk
  check (status in ('active', 'disabled', 'locked'));

create table if not exists sysadmin_sessions (
  id uuid primary key default gen_random_uuid(),
  sysadmin_account_id char(7) not null references sysadmin_accounts(id) on delete cascade,
  session_token_hash text not null unique,
  expires_at timestamptz not null,
  last_seen_at timestamptz not null default now(),
  revoked_at timestamptz,
  remote_ip text,
  user_agent text,
  created_at timestamptz not null default now()
);

create index if not exists idx_sysadmin_accounts_status_email
  on sysadmin_accounts (status, email);

create index if not exists idx_sysadmin_sessions_active
  on sysadmin_sessions (sysadmin_account_id, expires_at desc)
  where revoked_at is null;

create table if not exists platform_smtp_config (
  id uuid primary key,
  provider text not null default 'disabled',
  from_email text not null default '',
  from_display_name text not null default 'Tralanz Books',
  host text not null default '',
  port int not null default 587,
  use_ssl boolean not null default true,
  username text not null default '',
  password_protected text,
  updated_at timestamptz not null default now(),
  updated_by_user_id char(7),
  constraint platform_smtp_config_provider_chk
    check (provider in ('disabled','smtp'))
);

create table if not exists platform_ai_provider_config (
  id uuid primary key,
  provider text not null default 'disabled',
  base_url text,
  model text not null default '',
  max_tokens int not null default 1024,
  temperature numeric(4,2) not null default 0.7,
  api_key_protected text,
  updated_at timestamptz not null default now(),
  updated_by_user_id char(7),
  constraint platform_ai_provider_config_provider_chk
    check (provider in ('disabled','openai','anthropic','azure_openai')),
  constraint platform_ai_provider_config_max_tokens_chk
    check (max_tokens > 0 and max_tokens <= 200000),
  constraint platform_ai_provider_config_temperature_chk
    check (temperature >= 0 and temperature <= 2)
);

create table if not exists platform_database_backups (
  id uuid primary key default gen_random_uuid(),
  started_at timestamptz not null default now(),
  completed_at timestamptz,
  status text not null,
  file_path text,
  size_bytes bigint,
  triggered_by_user_id char(7) not null,
  error_message text,
  constraint platform_database_backups_status_chk
    check (status in ('running','succeeded','failed'))
);

create index if not exists ix_platform_database_backups_started_at
  on platform_database_backups (started_at desc);

create table if not exists platform_database_maintenance_runs (
  id uuid primary key default gen_random_uuid(),
  operation text not null,
  started_at timestamptz not null default now(),
  completed_at timestamptz,
  status text not null,
  duration_ms bigint,
  triggered_by_user_id char(7) not null,
  error_message text,
  constraint platform_database_maintenance_runs_status_chk
    check (status in ('running','succeeded','failed'))
);

create index if not exists ix_platform_database_maintenance_runs_started_at
  on platform_database_maintenance_runs (started_at desc);

create table if not exists platform_modules (
  id uuid primary key,
  module_key text not null unique,
  json jsonb not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create table if not exists platform_entities (
  id uuid primary key,
  entity_name text not null unique,
  module_key text not null,
  storage_table text not null,
  json jsonb not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create index if not exists idx_platform_entities_module_key
  on platform_entities (module_key);

create index if not exists idx_platform_entities_storage_table
  on platform_entities (storage_table);
