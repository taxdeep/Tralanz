-- Move CompanyAccess/SystemSetup runtime DDL into deploy-time migration.
-- Production app roles should not need CREATE/ALTER privileges for permission,
-- role governance, or user preference requests.

alter table company_memberships
  add column if not exists permissions jsonb not null default '[]'::jsonb;

create table if not exists audit_logs (
  id uuid primary key default gen_random_uuid(),
  company_id char(7) null,
  actor_type text not null,
  actor_id char(7) null,
  entity_type text not null,
  entity_id text not null,
  action text not null,
  payload jsonb not null default '{}'::jsonb,
  created_at timestamptz not null default now()
);

do $$
begin
  if exists (
    select 1
    from information_schema.columns
    where table_schema = 'public'
      and table_name = 'audit_logs'
      and column_name = 'actor_id'
      and udt_name = 'uuid'
  ) then
    if exists (select 1 from audit_logs where actor_id is not null limit 1) then
      raise exception 'audit_logs.actor_id is uuid and contains data; migrate actor IDs to char(7) manually before applying company-access runtime schema';
    end if;

    alter table audit_logs
      alter column actor_id type char(7) using null;
  end if;

  if exists (
    select 1
    from information_schema.columns
    where table_schema = 'public'
      and table_name = 'audit_logs'
      and column_name = 'entity_id'
      and udt_name = 'uuid'
  ) then
    alter table audit_logs
      alter column entity_id type text using entity_id::text;
  end if;
end $$;

create index if not exists idx_audit_logs_action_created_at
  on audit_logs (action, created_at desc);

create index if not exists idx_audit_logs_company_entity_action
  on audit_logs (company_id, entity_type, entity_id, action, created_at desc);

create table if not exists user_preferences (
  user_id char(7) primary key,
  number_display_mode text not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);
