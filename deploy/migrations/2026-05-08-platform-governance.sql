-- Stage-1.4 batch 3: extract PostgresPlatformGovernanceRepository.EnsureSchemaAsync.
-- The 17 ALTERs here add late audit / lifecycle columns to the same
-- platform tables as PostgresPlatformAccountProfileRepository. Every
-- statement is idempotent (IF NOT EXISTS); applied after platform-
-- account-profile.sql has run, every CREATE is a no-op and every
-- ALTER finds the column already present. The inline helper still
-- exists (cached) for fresh test databases.

create extension if not exists pgcrypto;

alter table users
  add column if not exists display_name text;

alter table users
  add column if not exists status text not null default 'active';

alter table users
  add column if not exists email_verified_at timestamptz;

alter table users
  add column if not exists locked_until timestamptz;

alter table users
  add column if not exists security_stamp text not null default gen_random_uuid()::text;

alter table users
  add column if not exists mfa_mode text not null default 'none';

create table if not exists sysadmin_accounts (
  id char(7) primary key,
  email text not null unique,
  display_name text not null default '',
  password_hash text not null,
  status text not null default 'active',
  last_login_at timestamptz,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create table if not exists account_mfa_recovery_requests (
  id uuid primary key default gen_random_uuid(),
  user_id char(7) not null references users(id) on delete cascade,
  requested_by_user_id char(7) not null references users(id) on delete cascade,
  current_mfa_mode text not null,
  status text not null default 'requested',
  request_reason text not null,
  requested_at timestamptz not null default now(),
  review_reason text,
  reviewed_at timestamptz,
  reviewed_by_sysadmin_account_id char(7),
  execution_reason text,
  executed_at timestamptz,
  executed_by_sysadmin_account_id char(7),
  constraint account_mfa_recovery_requests_status_chk check (status in ('requested', 'approved', 'rejected', 'executed'))
);

alter table account_mfa_recovery_requests
  drop constraint if exists account_mfa_recovery_requests_current_mode_chk;

alter table account_mfa_recovery_requests
  add constraint account_mfa_recovery_requests_current_mode_chk
  check (current_mfa_mode in ('none', 'email_code', 'totp_app'));

create table if not exists business_session_mfa_challenges (
  id uuid primary key default gen_random_uuid(),
  user_id char(7) not null references users(id) on delete cascade,
  active_company_id char(7) not null,
  membership_id uuid not null,
  role text not null,
  permissions jsonb not null default '[]'::jsonb,
  company_status text not null,
  factor text not null,
  destination text not null,
  code_hash text not null,
  security_stamp_snapshot text not null default '',
  expires_at timestamptz not null,
  consumed_at timestamptz,
  failed_attempts integer not null default 0,
  created_at timestamptz not null default now(),
  constraint business_session_mfa_challenges_role_chk check (role in ('owner', 'user')),
  constraint business_session_mfa_challenges_permissions_array_chk check (jsonb_typeof(permissions) = 'array'),
  constraint business_session_mfa_challenges_company_status_chk check (company_status in ('active', 'inactive', 'suspended', 'archived'))
);

alter table business_session_mfa_challenges
  drop constraint if exists business_session_mfa_challenges_factor_chk;

alter table business_session_mfa_challenges
  add constraint business_session_mfa_challenges_factor_chk
  check (factor in ('email_code', 'totp_app'));

alter table business_session_mfa_challenges
  add column if not exists security_stamp_snapshot text not null default '';

create table if not exists account_mfa_totp_enrollments (
  id uuid primary key default gen_random_uuid(),
  user_id char(7) not null references users(id) on delete cascade,
  status text not null,
  secret_base32 text not null,
  created_at timestamptz not null default now(),
  expires_at timestamptz,
  confirmed_at timestamptz,
  revoked_at timestamptz,
  last_used_at timestamptz,
  constraint account_mfa_totp_enrollments_status_chk
    check (status in ('pending', 'active', 'revoked'))
);

create table if not exists account_verification_codes (
  id uuid primary key default gen_random_uuid(),
  user_id char(7) not null references users(id) on delete cascade,
  purpose text not null,
  destination text,
  code_hash text not null,
  expires_at timestamptz not null,
  consumed_at timestamptz,
  failed_attempts integer not null default 0,
  created_at timestamptz not null default now(),
  payload jsonb not null default '{}'::jsonb
);

alter table account_verification_codes
  add column if not exists payload jsonb not null default '{}'::jsonb;

create table if not exists platform_notification_dispatches (
  id uuid primary key default gen_random_uuid(),
  notification_type text not null,
  destination text not null,
  status text not null default 'queued',
  provider_key text,
  attempt_count integer not null default 0,
  sent_at timestamptz,
  failed_at timestamptz,
  last_error text,
  payload jsonb not null default '{}'::jsonb,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

alter table platform_notification_dispatches
  add column if not exists provider_key text;

alter table platform_notification_dispatches
  add column if not exists attempt_count integer not null default 0;

alter table platform_notification_dispatches
  add column if not exists sent_at timestamptz;

alter table platform_notification_dispatches
  add column if not exists failed_at timestamptz;

alter table platform_notification_dispatches
  add column if not exists last_error text;

create table if not exists audit_logs (
  id uuid primary key default gen_random_uuid(),
  company_id char(7) null,
  actor_type text not null,
  actor_id char(7) null,
  entity_type text not null,
  entity_id uuid not null,
  action text not null,
  payload jsonb not null default '{}'::jsonb,
  created_at timestamptz not null default now()
);

create index if not exists idx_users_status_email
  on users (status, email);

create index if not exists idx_account_verification_codes_active
  on account_verification_codes (user_id, purpose, expires_at desc)
  where consumed_at is null;

create index if not exists idx_account_mfa_recovery_requests_open
  on account_mfa_recovery_requests (status, requested_at desc)
  where status in ('requested', 'approved');

create index if not exists idx_business_session_mfa_challenges_active
  on business_session_mfa_challenges (user_id, factor, expires_at desc)
  where consumed_at is null;

create index if not exists idx_platform_notification_dispatches_status
  on platform_notification_dispatches (status, created_at desc);

create index if not exists idx_audit_logs_action_created_at
  on audit_logs (action, created_at desc);
