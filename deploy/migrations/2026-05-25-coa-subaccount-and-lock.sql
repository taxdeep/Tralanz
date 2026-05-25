-- Batch C + D: additive Chart-of-Accounts schema for sub-account
-- hierarchy and lock-account toggle.
--
-- Both columns are nullable so existing rows keep working without
-- backfill. The migration is idempotent — re-running it is a no-op.
--
-- DEPLOYMENT NOTE: the runtime `citus_app` user does NOT have DDL
-- privileges. Apply this migration with a superuser BEFORE restarting
-- the services on top of the new build, otherwise the app's account
-- read/write queries will fail with "column does not exist":
--
--     psql -U postgres -d citus_accounting \
--          -f deploy/migrations/2026-05-25-coa-subaccount-and-lock.sql
--
-- ========================================================================
-- C: parent_account_id  (sub-account hierarchy)
-- ========================================================================
--
-- Self-referencing FK. `ON DELETE SET NULL`: deleting a parent must NOT
-- cascade to children because their balances are real GL state — the
-- orphaned children become top-level rows, surfacing the mistake in the
-- CoA list so an operator can re-parent them.
--
-- No CHECK constraint forcing same root_type parent/child: in edge
-- cases a contra-asset child of an equity parent makes accounting
-- sense. UI prevents the common mistakes; the DB stays open.

alter table accounts
  add column if not exists parent_account_id uuid null
    references accounts (id) on delete set null;

create index if not exists idx_accounts_parent
  on accounts (parent_account_id)
  where parent_account_id is not null;

-- ========================================================================
-- D: locked_at + locked_by_user_id  (lock-account toggle)
-- ========================================================================
--
-- locked_at IS NULL   → account is editable (default)
-- locked_at IS NOT NULL → financial-truth fields are immutable until
--                        the operator un-locks. Enforced application-
--                        layer in PostgreSqlAccountStore.UpdateAsync.
--
-- The unlock action itself doesn't need a separate column — flipping
-- locked_at back to NULL is the unlock. audit_logs records both events.

alter table accounts
  add column if not exists locked_at timestamptz null;

alter table accounts
  add column if not exists locked_by_user_id char(7) null;

create index if not exists idx_accounts_locked
  on accounts (company_id)
  where locked_at is not null;
