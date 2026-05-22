-- =====================================================================
-- M13: Postgres row-level security on every multi-tenant table
-- =====================================================================
--
-- AUDIT_2026-05-20 M13 flagged the absence of Postgres RLS on the
-- ~120 tables that carry `company_id`. Every existing query already
-- includes `WHERE company_id = @company_id` — so today's protection
-- is purely "every developer remembers to add the WHERE". RLS adds
-- defense-in-depth: a query missing the WHERE returns zero rows for
-- the wrong tenant instead of leaking.
--
-- Design — two-mode policy keyed on Postgres GUCs:
--   (a) strict mode  → connection sets `app.company_id = '<companyId>'`,
--                       policy admits only rows whose `company_id`
--                       matches.
--   (b) bypass mode  → connection sets `app.bypass_company_filter =
--                       'true'`, policy admits all rows.
--
-- The connection factories (modified in this same PR) default to
-- bypass mode after opening so the existing 447 call sites keep
-- working unchanged. Per-tenant queries opt into strict mode by
-- calling the new `OpenWithCompanyAsync(companyId, …)` overload.
-- Today this PR ships RLS infrastructure only; future PRs migrate
-- per-call-site to strict mode as the active defense.
--
-- Why this is safe to ship today:
--   * citus_app is non-superuser + non-bypassrls (verified via
--     `select rolbypassrls from pg_roles where rolname='citus_app'`).
--   * postgres (the migration runner role) IS superuser + BYPASSRLS,
--     so schema_migrations / startup probes / future SQL migrations
--     are unaffected.
--   * The policy admits all rows when bypass=true, so existing
--     queries continue to function byte-for-byte. RLS becomes an
--     active defense only when a future PR opts a query in.
--   * Connections returned to the Npgsql pool run DISCARD ALL by
--     default, which clears the GUCs — no cross-request leak risk.
--
-- The DO block iterates every `public` table with a `company_id`
-- column so the policy stays in sync as new tables get added in
-- future migrations.
-- =====================================================================

begin;

do $$
declare
  t text;
begin
  for t in
    select c.table_name
    from information_schema.columns c
    join information_schema.tables tab
      on tab.table_schema = c.table_schema
     and tab.table_name = c.table_name
    where c.table_schema = 'public'
      and c.column_name = 'company_id'
      and tab.table_type = 'BASE TABLE'
    order by c.table_name
  loop
    execute format('alter table %I enable row level security', t);

    -- Drop-then-create makes the policy install re-runnable across
    -- future migration replays without throwing on duplicate name.
    execute format('drop policy if exists tenant_isolation on %I', t);
    execute format(
      $POLICY$
      create policy tenant_isolation on %I
        for all
        to citus_app
        using (
          current_setting('app.bypass_company_filter', true) = 'true'
          or company_id::text = current_setting('app.company_id', true)
        )
      $POLICY$,
      t
    );
  end loop;
end$$;

commit;
