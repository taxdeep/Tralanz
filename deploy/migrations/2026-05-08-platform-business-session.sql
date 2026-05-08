-- Stage-1.4 batch 3: extract PostgresPlatformBusinessSessionRepository.EnsureSchemaAsync.
-- Creates business_sessions + business_session_mfa_challenges (the
-- former is unique to this helper) plus the security_stamp_snapshot
-- backfill for legacy rows. The inline helper still exists (cached)
-- for fresh test databases.

create extension if not exists pgcrypto;

alter table users
  add column if not exists status text not null default 'active';

alter table users
  add column if not exists locked_until timestamptz;

alter table users
  add column if not exists mfa_mode text not null default 'none';

create table if not exists business_sessions (
  id uuid primary key default gen_random_uuid(),
  token_hash text not null unique,
  user_id char(7) not null references users(id) on delete cascade,
  active_company_id char(7) not null references companies(id) on delete restrict,
  membership_id uuid not null references company_memberships(id) on delete cascade,
  role text not null,
  permissions jsonb not null default '[]'::jsonb,
  company_status text not null,
  permission_version text,
  security_stamp_snapshot text not null default '',
  expires_at timestamptz not null,
  created_at timestamptz not null default now(),
  constraint business_sessions_role_chk check (role in ('owner', 'user')),
  constraint business_sessions_permissions_array_chk check (jsonb_typeof(permissions) = 'array'),
  constraint business_sessions_company_status_chk check (company_status in ('active', 'inactive', 'suspended', 'archived'))
);

alter table business_sessions
  add column if not exists security_stamp_snapshot text not null default '';

update business_sessions s
set security_stamp_snapshot = u.security_stamp
from users u
where s.user_id = u.id
  and coalesce(s.security_stamp_snapshot, '') = '';

create index if not exists idx_business_sessions_user_company_expiry
  on business_sessions (user_id, active_company_id, expires_at desc);

create index if not exists idx_business_sessions_token_expiry
  on business_sessions (token_hash, expires_at desc);

create table if not exists business_session_mfa_challenges (
  id uuid primary key default gen_random_uuid(),
  user_id char(7) not null references users(id) on delete cascade,
  active_company_id char(7) not null references companies(id) on delete restrict,
  membership_id uuid not null references company_memberships(id) on delete cascade,
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

update business_session_mfa_challenges c
set security_stamp_snapshot = u.security_stamp
from users u
where c.user_id = u.id
  and coalesce(c.security_stamp_snapshot, '') = '';

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

create index if not exists idx_business_session_mfa_challenges_active
  on business_session_mfa_challenges (user_id, factor, expires_at desc)
  where consumed_at is null;

create index if not exists idx_account_mfa_totp_enrollments_active
  on account_mfa_totp_enrollments (user_id, status, created_at desc)
  where status in ('pending', 'active');
