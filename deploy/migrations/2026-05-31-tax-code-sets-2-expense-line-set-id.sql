-- Sales Tax redesign R4: expense_lines may reference a Tax Code bundle.
--
-- A line can now carry tax_code_set_id (tax_code_sets.id) as an alternative
-- to the single legacy Rule in tax_code_id. When present, PostgreSqlExpenseStore
-- passes it to the engine as TaxCodeSetId, which expands the bundle to its
-- member Rules and emits one tax leg per Rule (GST recoverable + PST
-- non-recoverable, etc.). Nullable + no FK, matching the existing loose
-- tax_code_id reference; company isolation + validity are enforced in the app.
--
-- Idempotent. Apply with postgres superuser (citus_app lacks DDL) — upgrade.sh
-- apply_pending_migrations does this before restarting accounting-api.

alter table expense_lines add column if not exists tax_code_set_id uuid;
