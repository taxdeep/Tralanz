-- =====================================================================
-- X-2.3 (AUDIT_2026-05-23 M-N1): UnitySearch isolation indexes catch-up
-- =====================================================================
--
-- Background. H16 (commit 2128e1e, 2026-05-22) flagged that several
-- EnsureSchemaAsync call sites still emit DDL at startup. It shipped a
-- catch-up migration for FOUR sites (V1 write-flow, customer_deposits,
-- quotes, sales_orders) but inadvertently OMITTED two indexes that
-- PostgreSqlUnitySearchProjectionStore.EnsureSchemaAsync creates:
--
--   1. ix_search_documents_company_module   on (company_id, module_key)
--   2. ix_search_documents_company_owner    on (company_id, owner_user_id)
--      WHERE owner_user_id is not null
--
-- The base UnitySearch table (search_documents) and its non-isolation
-- indexes are in 2026-05-14-unity-search-runtime-schema.sql, but the
-- two Batch-2 isolation columns + indexes (module_key / owner_user_id)
-- were added later in C# inside EnsureSchemaAsync and never made it
-- to a migration.
--
-- Risk before this catch-up: a production deploy that disables runtime
-- schema management (recommended; ASPNETCORE_ENVIRONMENT=Production
-- + ShouldApplyRuntimeSchemaManagement=false) AND skips the
-- isolation-index migration leaves search_documents without the two
-- module / owner indexes. Module-permission-filtered searches still
-- function but degrade to a sequential scan on every query — the
-- problem surfaces as query latency under load, not as an error.
--
-- This migration brings the schema in line with what EnsureSchemaAsync
-- would have created. Follows the H16 pattern: migration is the
-- authoritative source, the C# bootstrap stays in place as the dev /
-- CI provisioning path (re-runnable, IF NOT EXISTS-guarded). The two
-- definitions are intentionally identical; if a future schema change
-- lands in EnsureSchemaAsync it must also land in a follow-up
-- migration.
--
-- Includes the two Batch-2 columns the bootstrap also adds, because
-- the indexes below reference them. If the table was created from the
-- 2026-05-14 baseline only, the columns may be missing on this DB.
-- The DO block plus IF NOT EXISTS make the file safe to re-run.
-- =====================================================================

begin;

-- Batch-2 columns (idempotent). The 2026-05-14 baseline migration
-- defines search_documents without these; EnsureSchemaAsync adds them
-- at startup. Bring them in here so the indexes below can reference
-- them on a fresh DB that runs migrations without the C# bootstrap.
alter table search_documents
  add column if not exists module_key text not null default 'core';
alter table search_documents
  add column if not exists required_permissions text[] not null default array[]::text[];
alter table search_documents
  add column if not exists owner_user_id char(7) null;
alter table search_documents
  add column if not exists visibility_scope text not null default 'company';
alter table search_documents
  add column if not exists visibility_override_permission text null;

-- Isolation indexes.
create index if not exists ix_search_documents_company_module
  on search_documents (company_id, module_key);

create index if not exists ix_search_documents_company_owner
  on search_documents (company_id, owner_user_id)
  where owner_user_id is not null;

commit;
