-- =====================================================================
-- PR-4A: Permission foundation
-- =====================================================================
--
-- New permission model (Tralanz-wide):
--
--   Owner = unique transferable special status per company.
--           Implied-all-permissions inside the company.
--           Only Owner can perform 4 hard-coded actions:
--             - company.make_inactive
--             - owner.transfer
--             - permission_grant_authority.assign
--             - permission_grant_authority.revoke
--           These actions are NEVER assignable via permission grants.
--
--   User  = ordinary company member. NO implicit role/preset.
--           Permissions come from two orthogonal grants:
--             1) Business permissions (company_user_permissions)
--                   — what the user can DO
--             2) Grant authority (company_user_permission_grant_authorities)
--                   — which tokens the user can GRANT/REVOKE to other Users
--           Anti-recursion: grant authority cannot itself be re-delegated
--           by a non-Owner. Only Owner can assign/revoke grant authority.
--
-- This migration is schema-only + legacy permission_jsonb migration via
-- a safe allowlist. Endpoint gates / UI / preset application land in
-- PR-4B → PR-4E.
--
-- Pre-flight: aborts if any active company does not have exactly one
-- active owner — the operator must fix the data manually before
-- re-running. We do NOT silently choose a "winner" or promote a
-- random member.
-- =====================================================================

begin;

-- ---------------------------------------------------------------------
-- 1. is_owner flag on company_memberships
-- ---------------------------------------------------------------------
-- Backfill from the legacy `role` column (kept as deprecated for now;
-- a later sweep PR physically drops it once all callers stop reading
-- it). Only flips false → true so re-runs are idempotent.
alter table company_memberships
  add column if not exists is_owner boolean not null default false;

update company_memberships
   set is_owner = true
 where is_owner = false
   and role = 'owner';

comment on column company_memberships.role is
  'DEPRECATED — use is_owner. Will be dropped in a future sweep PR.';

-- ---------------------------------------------------------------------
-- 2. Pre-flight: every active company must have exactly one active owner
-- ---------------------------------------------------------------------
-- Includes companies with ZERO active owners (via LEFT JOIN). Aborts
-- the migration with a diagnostic listing offending company ids so the
-- operator can fix and re-run.
do $$
declare
  bad text;
begin
  select string_agg(c.id || ' (' || coalesce(o.cnt, 0)::text || ' active owners)', ', ')
    into bad
    from companies c
    left join (
      select company_id, count(*) as cnt
        from company_memberships
       where is_owner = true and status = 'active'
       group by company_id
    ) o on o.company_id = c.id
   where c.status = 'active'
     and coalesce(o.cnt, 0) <> 1;

  if bad is not null then
    raise exception
      'Permission foundation aborted: %. Fix company_memberships manually before re-running.', bad;
  end if;
end$$;

-- ---------------------------------------------------------------------
-- 3. Partial unique index enforcing one-active-owner per company
-- ---------------------------------------------------------------------
create unique index if not exists ux_company_memberships_active_owner
  on company_memberships(company_id)
  where is_owner = true and status = 'active';

-- ---------------------------------------------------------------------
-- 4. permission_registry — single source of truth for valid tokens
-- ---------------------------------------------------------------------
-- module_key  : top-level surface (ar, ap, gl, inventory, task, ...)
-- group_key   : resource family within module (invoice, bill, ...)
-- action_key  : verb (view, create, post, ...)
-- is_high_risk: post / void / reverse / export / delete / etc.
-- is_assignable: false for the 4 Owner-only hard-coded actions; they
--                live in the registry for catalog visibility but are
--                NEVER grantable to a User.
create table if not exists permission_registry (
  permission_token text primary key,
  module_key text not null,
  group_key text not null,
  action_key text not null,
  description text not null default '',
  is_high_risk boolean not null default false,
  is_assignable boolean not null default true,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

-- Seed: every fine-grained token from CompanyMembershipPermissionCatalog
-- + the 4 Owner-only actions. Keep this list in sync with the C#
-- catalog (CI parity check is a P3 follow-up).
--
-- Heuristic: action_key matching post|void|reverse|export|delete|
-- approve|convert|toggle|adjust|close|cancel|bill|edit|assign is
-- flagged is_high_risk. Used by the safe-allowlist legacy migration
-- below; also surfaced to the UI to badge dangerous toggles.
insert into permission_registry
  (permission_token, module_key, group_key, action_key, description,
   is_high_risk, is_assignable)
select
  token,
  split_part(token, '.', 1) as module_key,
  case when array_length(string_to_array(token, '.'), 1) >= 3
       then split_part(token, '.', 2) else split_part(token, '.', 1) end as group_key,
  case when array_length(string_to_array(token, '.'), 1) >= 3
       then split_part(token, '.', 3)
       when array_length(string_to_array(token, '.'), 1) = 2
       then split_part(token, '.', 2)
       else token end as action_key,
  '' as description,
  case when token ~ '\.(post|void|reverse|export|delete|approve|convert|toggle|adjust|close|cancel|bill|edit|assign)$'
       then true else false end as is_high_risk,
  true as is_assignable
from unnest(array[
  -- AR
  'ar.invoice.view', 'ar.invoice.create', 'ar.invoice.edit',
  'ar.invoice.post', 'ar.invoice.void', 'ar.invoice.export',
  'ar.receipt.view', 'ar.receipt.create', 'ar.receipt.apply',
  'ar.creditnote.view', 'ar.creditnote.create',
  'ar.customer.view', 'ar.customer.create', 'ar.customer.edit', 'ar.customer.export',
  'ar.aging.view',
  -- AP
  'ap.bill.view', 'ap.bill.create', 'ap.bill.edit',
  'ap.bill.post', 'ap.bill.void', 'ap.bill.export',
  'ap.payment.view', 'ap.payment.create', 'ap.payment.apply',
  'ap.vendorcredit.view', 'ap.vendorcredit.create',
  'ap.vendor.view', 'ap.vendor.create', 'ap.vendor.edit', 'ap.vendor.export',
  'ap.aging.view',
  -- Inventory / Products & Services
  'inventory.item.view', 'inventory.item.create', 'inventory.item.edit', 'inventory.item.export',
  'inventory.price.view', 'inventory.price.edit',
  'inventory.warehouse.view', 'inventory.warehouse.edit',
  'inventory.stock.view', 'inventory.stock.adjust',
  -- GL
  'gl.journal.view', 'gl.journal.create', 'gl.journal.post',
  'gl.account.view', 'gl.account.edit',
  'gl.period.close',
  -- Reports
  'reports.view', 'reports.export', 'reports.advanced.view',
  -- Task
  'task.view', 'task.view.all', 'task.create', 'task.edit',
  'task.complete', 'task.cancel', 'task.bill', 'task.export',
  'task.archive.read', 'task.report.margin',
  -- Settings
  'settings.company.view', 'settings.company.edit',
  'settings.permissions.view', 'settings.permissions.assign',
  'settings.modules.toggle',
  'settings.numbering.edit', 'settings.fx.edit', 'settings.tax.edit'
]) as t(token)
on conflict (permission_token) do update set
  is_high_risk = excluded.is_high_risk,
  is_assignable = excluded.is_assignable,
  module_key = excluded.module_key,
  group_key = excluded.group_key,
  action_key = excluded.action_key,
  updated_at = now();

-- The 4 Owner-only hard-coded actions. Catalogued for visibility but
-- is_assignable=false so the grant path can never include them. The
-- PermissionEvaluator checks these against is_owner directly; the
-- registry row exists so the UI can show "this exists, only Owner can
-- do it" rather than "no such permission".
insert into permission_registry
  (permission_token, module_key, group_key, action_key, description, is_high_risk, is_assignable)
values
  ('company.make_inactive', 'company', 'company', 'make_inactive',
   'Owner-only: set company to inactive state. Cannot be delegated.',
   true, false),
  ('owner.transfer', 'company', 'owner', 'transfer',
   'Owner-only: transfer ownership to another active company member. Cannot be delegated.',
   true, false),
  ('permission_grant_authority.assign', 'company', 'grant_authority', 'assign',
   'Owner-only: grant a User the authority to grant/revoke a specific token. Anti-recursion: even a User with grant authority cannot re-delegate that authority.',
   true, false),
  ('permission_grant_authority.revoke', 'company', 'grant_authority', 'revoke',
   'Owner-only: revoke a User''s grant authority for a specific token.',
   true, false)
on conflict (permission_token) do update set
  description = excluded.description,
  is_assignable = excluded.is_assignable,
  is_high_risk = excluded.is_high_risk,
  updated_at = now();

-- ---------------------------------------------------------------------
-- 5. company_user_permissions — business permissions a User has
-- ---------------------------------------------------------------------
-- One row per (company, user, token, is_active). Revocation flips
-- is_active=false rather than DELETEing so the audit trail is
-- recoverable. The partial unique index prevents two simultaneously-
-- active grants for the same triple.
--
-- Owner is excluded from this table — Owner is implied-all-permissions
-- via is_owner=true on company_memberships. Granting an Owner explicit
-- tokens is allowed (harmless) but unnecessary.
create table if not exists company_user_permissions (
  id uuid primary key default gen_random_uuid(),
  company_id char(7) not null references companies(id) on delete cascade,
  user_id char(7) not null references users(id) on delete cascade,
  permission_token text not null references permission_registry(permission_token),
  granted_by_user_id char(7) not null references users(id),
  granted_at timestamptz not null default now(),
  revoked_by_user_id char(7) references users(id),
  revoked_at timestamptz,
  is_active boolean not null default true
);

create unique index if not exists ux_company_user_permissions_active
  on company_user_permissions(company_id, user_id, permission_token)
  where is_active = true;

create index if not exists ix_company_user_permissions_lookup
  on company_user_permissions(company_id, user_id)
  where is_active = true;

-- ---------------------------------------------------------------------
-- 6. company_user_permission_grant_authorities — delegated grant power
-- ---------------------------------------------------------------------
-- A User with a row here can grant/revoke `grantable_permission_token`
-- to OTHER Users in the same company. Having grant authority for X
-- does NOT mean the User has business permission X themselves — that
-- requires a separate row in company_user_permissions.
--
-- can_grant and can_revoke are independent (UI may bind them today but
-- DB supports decoupling — e.g. "grant-only auditor" patterns).
--
-- granted_by_owner_user_id is non-null and is enforced at evaluator
-- level: only Owner can assign/revoke grant authority. The DB column
-- documents intent but is not an FK to a "is_owner=true" subset; the
-- PermissionEvaluator enforces the Owner check before the row is
-- written.
create table if not exists company_user_permission_grant_authorities (
  id uuid primary key default gen_random_uuid(),
  company_id char(7) not null references companies(id) on delete cascade,
  user_id char(7) not null references users(id) on delete cascade,
  grantable_permission_token text not null references permission_registry(permission_token),
  can_grant boolean not null default true,
  can_revoke boolean not null default true,
  granted_by_owner_user_id char(7) not null references users(id),
  granted_at timestamptz not null default now(),
  revoked_by_owner_user_id char(7) references users(id),
  revoked_at timestamptz,
  is_active boolean not null default true
);

create unique index if not exists ux_grant_authority_active
  on company_user_permission_grant_authorities(company_id, user_id, grantable_permission_token)
  where is_active = true;

create index if not exists ix_grant_authority_lookup
  on company_user_permission_grant_authorities(company_id, user_id)
  where is_active = true;

-- ---------------------------------------------------------------------
-- 7. permission_presets + items — templates only, not roles
-- ---------------------------------------------------------------------
-- Applying a preset writes individual rows to company_user_permissions.
-- The preset itself is not a stored role — once applied, the User's
-- access is exactly the union of their explicit grants, regardless of
-- which preset they came from. Future revocation of a preset item
-- requires individually revoking each token grant.
--
-- is_system=true presets ship with the platform (read-only catalog);
-- false presets are company-defined.
create table if not exists permission_presets (
  preset_key text primary key,
  preset_name text not null,
  description text not null default '',
  is_system boolean not null default false,
  created_at timestamptz not null default now()
);

create table if not exists permission_preset_items (
  preset_key text not null references permission_presets(preset_key) on delete cascade,
  permission_token text not null references permission_registry(permission_token),
  primary key (preset_key, permission_token)
);

-- ---------------------------------------------------------------------
-- 8. Safe-allowlist migration from legacy permissions jsonb
-- ---------------------------------------------------------------------
-- The pre-PR-4A model stored tokens in `company_memberships.permissions`
-- (jsonb array). The new model stores per-token rows in
-- `company_user_permissions`. To preserve existing low-risk grants
-- without leaking high-risk ones into the new system, we copy ONLY:
--
--   * tokens ending in `.view` or `.create` (low risk)
--   * AND that exist in `permission_registry` (no garbage tokens)
--   * AND `is_assignable = true` (excludes Owner-only actions)
--   * for non-Owner active members (Owner doesn't need explicit rows)
--
-- Everything else is skipped — Owners must explicitly re-grant any
-- post/void/reverse/export permissions. This is the deliberate
-- "tighten on migration" stance: when we don't know, deny.
--
-- granted_by_user_id is set to the company's active Owner — the new
-- model assigns Owner as the implicit grantor of all migrated rows.
--
-- ON CONFLICT DO NOTHING makes this idempotent against the partial
-- unique index — a second run is a no-op.
insert into company_user_permissions
  (company_id, user_id, permission_token, granted_by_user_id, granted_at, is_active)
select
  m.company_id,
  m.user_id,
  token,
  owner.user_id,
  now(),
  true
from company_memberships m
join company_memberships owner
  on owner.company_id = m.company_id
 and owner.is_owner = true
 and owner.status = 'active'
cross join lateral jsonb_array_elements_text(m.permissions) as p(token)
where m.status = 'active'
  and m.is_owner = false
  and token ~ '\.(view|create)$'
  and token in (select permission_token from permission_registry where is_assignable = true)
on conflict do nothing;

commit;
