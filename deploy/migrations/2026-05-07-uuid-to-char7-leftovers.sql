-- Convert leftover uuid columns to char(7) for the typed-id migration.
--
-- The original ID redesign migrated the high-traffic tables (companies,
-- users, journal_entries, invoices, bills, etc.) but missed a long tail
-- of auxiliary tables — payment_terms, quotes, sales_orders,
-- ap_purchase_orders, expenses, dashboard_*, action_center_*,
-- report_usage_*, unitysearch_*, ai_*, user_profile_overrides. The C#
-- code already passes CompanyId.Value (a char(7) string like "C000001")
-- and UserId.Value (a char(7) like "U000001"), so any query against
-- these tables raises 42883 "operator does not exist: uuid = text".
--
-- All affected tables have no foreign keys on these columns and the
-- DB is currently empty, so a plain ALTER COLUMN TYPE ... USING ... ::text
-- is safe. The USING clause covers the case where a stray uuid value
-- is present — it gets coerced to its 36-char text form, which is too
-- long for char(7) and would raise 22001; that's a deliberate fail-loud
-- so we surface any pre-migration row that snuck in. After the wipe
-- the count is zero, so the conversion is a no-op data-wise.
--
-- Idempotent: re-running on already-char(7) columns is a no-op since
-- the to_text-then-cast pipeline still applies. Postgres rebuilds the
-- relevant indexes automatically.

BEGIN;

-- Drop unique indexes whose expression embeds a uuid sentinel via COALESCE.
-- Postgres can't ALTER COLUMN TYPE while such expressions reference the
-- column with the old type. We re-create them at the bottom of this
-- migration with a char(7) sentinel ('0000000') that's clearly not a
-- valid UserId (real ones start with 'U').
DROP INDEX IF EXISTS uq_dashboard_user_widgets_scope;
DROP INDEX IF EXISTS uq_report_usage_stats_scope;
DROP INDEX IF EXISTS uq_unitysearch_pair_stats_scope;
DROP INDEX IF EXISTS uq_unitysearch_usage_stats_scope;

-- company_id columns
ALTER TABLE IF EXISTS action_center_task_events     ALTER COLUMN company_id TYPE char(7) USING company_id::text;
ALTER TABLE IF EXISTS action_center_tasks           ALTER COLUMN company_id TYPE char(7) USING company_id::text;
ALTER TABLE IF EXISTS ai_job_runs                   ALTER COLUMN company_id TYPE char(7) USING company_id::text;
ALTER TABLE IF EXISTS ai_request_logs               ALTER COLUMN company_id TYPE char(7) USING company_id::text;
ALTER TABLE IF EXISTS ap_purchase_orders            ALTER COLUMN company_id TYPE char(7) USING company_id::text;
ALTER TABLE IF EXISTS dashboard_user_widgets        ALTER COLUMN company_id TYPE char(7) USING company_id::text;
ALTER TABLE IF EXISTS dashboard_widget_suggestions  ALTER COLUMN company_id TYPE char(7) USING company_id::text;
ALTER TABLE IF EXISTS expenses                      ALTER COLUMN company_id TYPE char(7) USING company_id::text;
ALTER TABLE IF EXISTS payment_terms                 ALTER COLUMN company_id TYPE char(7) USING company_id::text;
ALTER TABLE IF EXISTS quotes                        ALTER COLUMN company_id TYPE char(7) USING company_id::text;
ALTER TABLE IF EXISTS report_usage_events           ALTER COLUMN company_id TYPE char(7) USING company_id::text;
ALTER TABLE IF EXISTS report_usage_stats            ALTER COLUMN company_id TYPE char(7) USING company_id::text;
ALTER TABLE IF EXISTS sales_orders                  ALTER COLUMN company_id TYPE char(7) USING company_id::text;
ALTER TABLE IF EXISTS unitysearch_alias_suggestions ALTER COLUMN company_id TYPE char(7) USING company_id::text;
ALTER TABLE IF EXISTS unitysearch_decision_traces   ALTER COLUMN company_id TYPE char(7) USING company_id::text;
ALTER TABLE IF EXISTS unitysearch_events            ALTER COLUMN company_id TYPE char(7) USING company_id::text;
ALTER TABLE IF EXISTS unitysearch_learning_profiles ALTER COLUMN company_id TYPE char(7) USING company_id::text;
ALTER TABLE IF EXISTS unitysearch_pair_stats        ALTER COLUMN company_id TYPE char(7) USING company_id::text;
ALTER TABLE IF EXISTS unitysearch_ranking_hints     ALTER COLUMN company_id TYPE char(7) USING company_id::text;
ALTER TABLE IF EXISTS unitysearch_recent_queries    ALTER COLUMN company_id TYPE char(7) USING company_id::text;
ALTER TABLE IF EXISTS unitysearch_usage_stats       ALTER COLUMN company_id TYPE char(7) USING company_id::text;

-- user_id / *_user_id columns
ALTER TABLE IF EXISTS action_center_task_events     ALTER COLUMN user_id              TYPE char(7) USING user_id::text;
ALTER TABLE IF EXISTS action_center_tasks           ALTER COLUMN assigned_user_id     TYPE char(7) USING assigned_user_id::text;
ALTER TABLE IF EXISTS ai_job_runs                   ALTER COLUMN triggered_by_user_id TYPE char(7) USING triggered_by_user_id::text;
ALTER TABLE IF EXISTS dashboard_user_widgets        ALTER COLUMN user_id              TYPE char(7) USING user_id::text;
ALTER TABLE IF EXISTS dashboard_widget_suggestions  ALTER COLUMN user_id              TYPE char(7) USING user_id::text;
ALTER TABLE IF EXISTS expenses                      ALTER COLUMN created_by_user_id   TYPE char(7) USING created_by_user_id::text;
ALTER TABLE IF EXISTS report_usage_events           ALTER COLUMN user_id              TYPE char(7) USING user_id::text;
ALTER TABLE IF EXISTS report_usage_stats            ALTER COLUMN user_id              TYPE char(7) USING user_id::text;
ALTER TABLE IF EXISTS unitysearch_alias_suggestions ALTER COLUMN approved_by_user_id  TYPE char(7) USING approved_by_user_id::text;
ALTER TABLE IF EXISTS unitysearch_alias_suggestions ALTER COLUMN rejected_by_user_id  TYPE char(7) USING rejected_by_user_id::text;
ALTER TABLE IF EXISTS unitysearch_decision_traces   ALTER COLUMN user_id              TYPE char(7) USING user_id::text;
ALTER TABLE IF EXISTS unitysearch_events            ALTER COLUMN user_id              TYPE char(7) USING user_id::text;
ALTER TABLE IF EXISTS unitysearch_learning_profiles ALTER COLUMN user_id              TYPE char(7) USING user_id::text;
ALTER TABLE IF EXISTS unitysearch_pair_stats        ALTER COLUMN user_id              TYPE char(7) USING user_id::text;
ALTER TABLE IF EXISTS unitysearch_ranking_hints     ALTER COLUMN activated_by_user_id TYPE char(7) USING activated_by_user_id::text;
ALTER TABLE IF EXISTS unitysearch_ranking_hints     ALTER COLUMN rejected_by_user_id  TYPE char(7) USING rejected_by_user_id::text;
ALTER TABLE IF EXISTS unitysearch_ranking_hints     ALTER COLUMN user_id              TYPE char(7) USING user_id::text;
ALTER TABLE IF EXISTS unitysearch_recent_queries    ALTER COLUMN user_id              TYPE char(7) USING user_id::text;
ALTER TABLE IF EXISTS unitysearch_usage_stats       ALTER COLUMN user_id              TYPE char(7) USING user_id::text;
ALTER TABLE IF EXISTS user_profile_overrides        ALTER COLUMN user_id              TYPE char(7) USING user_id::text;

-- Re-create the unique indexes with a char(7) sentinel for "no user".
-- '0000000' is intentionally not a valid UserId format (real ones start
-- with 'U') so it can never collide with an allocator-issued id.
DO $$
BEGIN
  IF to_regclass('public.dashboard_user_widgets') IS NOT NULL THEN
    CREATE UNIQUE INDEX uq_dashboard_user_widgets_scope
      ON dashboard_user_widgets (company_id, COALESCE(user_id, '0000000'::char(7)), widget_key);
  END IF;
END $$;

DO $$
BEGIN
  IF to_regclass('public.report_usage_stats') IS NOT NULL THEN
    CREATE UNIQUE INDEX uq_report_usage_stats_scope
      ON report_usage_stats (company_id, scope_type, COALESCE(user_id, '0000000'::char(7)), report_key);
  END IF;
END $$;

DO $$
BEGIN
  IF to_regclass('public.unitysearch_pair_stats') IS NOT NULL THEN
    CREATE UNIQUE INDEX uq_unitysearch_pair_stats_scope
      ON unitysearch_pair_stats (
        company_id, scope_type, COALESCE(user_id, '0000000'::char(7)),
        source_context, anchor_entity_type, anchor_entity_id,
        target_context, target_entity_type, target_entity_id);
  END IF;
END $$;

DO $$
BEGIN
  IF to_regclass('public.unitysearch_usage_stats') IS NOT NULL THEN
    CREATE UNIQUE INDEX uq_unitysearch_usage_stats_scope
      ON unitysearch_usage_stats (
        company_id, scope_type, COALESCE(user_id, '0000000'::char(7)),
        context, entity_type, entity_id);
  END IF;
END $$;

COMMIT;
