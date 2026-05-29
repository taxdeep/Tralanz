-- Sales Tax v2 — repair: backfill empty sales_tax_code_component_rates.
--
-- ROOT CAUSE. The S1.3 data-migrate (2026-05-29-sales-tax-v2-3-data-migrate)
-- inserted the per-component rate inside a single multi-CTE statement whose
-- final INSERT bridged component → legacy code via the sales_tax_codes
-- TABLE. PostgreSQL runs all CTEs of one statement against the same
-- snapshot, so the rows the migrated_codes CTE had just inserted into
-- sales_tax_codes were NOT visible to that table reference. On a fresh
-- migration the join matched nothing and ZERO rate rows were written —
-- even though sales_tax_codes + sales_tax_code_components populated fine.
--
-- IMPACT. The Sales Tax v2 engine resolves a component's rate via an
-- as-of-date lookup against this table; with no rows it falls back to
-- coalesce(..., 0) and computes $0 tax. So enabling SalesTaxV2 against a
-- database migrated by the buggy S1.3 would REGRESS every tax line to 0.
--
-- FIX. S1.3 is corrected for future fresh installs (it now bridges via the
-- migrated_codes CTE). This migration repairs databases already migrated
-- by the buggy version: for every migrated component that has NO rate row,
-- insert one from the legacy tax_codes.rate_percent, effective 1900-01-01
-- (same effective_from S1.3 intended) so historic + new documents resolve.
--
-- This is a plain INSERT ... SELECT (not a data-modifying CTE), so it reads
-- the already-committed sales_tax_codes / _components / tax_codes rows.
--
-- Idempotent: the NOT EXISTS guard + ON CONFLICT skip components that
-- already have a rate, so re-running is safe and a no-op on healthy DBs
-- (including fresh installs migrated by the fixed S1.3).
--
-- DEPLOYMENT NOTE: requires migrations 1–3 to have applied. Apply with the
-- postgres superuser:
--
--     psql -U postgres -d citus_accounting \
--          -f deploy/migrations/2026-05-29-sales-tax-v2-5-rate-backfill.sql

insert into sales_tax_code_component_rates
    (component_id, rate_percent, effective_from)
select
    c.id,
    coalesce(t.rate_percent, 0),
    '1900-01-01'::date
from sales_tax_code_components c
join sales_tax_codes stc on stc.id = c.tax_code_id
join tax_codes t         on t.id  = stc.legacy_tax_code_id
where stc.legacy_tax_code_id is not null
  and not exists (
      select 1
      from sales_tax_code_component_rates r
      where r.component_id = c.id
  )
on conflict on constraint uq_sales_tax_code_component_rates_natural_key do nothing;

-- ========================================================================
-- Verification (informational notice — does not fail the migration)
-- ========================================================================
do $$
declare
    components_total      int;
    components_with_rate  int;
    components_missing    int;
begin
    select count(*) into components_total
    from sales_tax_code_components c
    join sales_tax_codes s on s.id = c.tax_code_id
    where s.legacy_tax_code_id is not null;

    select count(*) into components_with_rate
    from sales_tax_code_components c
    join sales_tax_codes s on s.id = c.tax_code_id
    where s.legacy_tax_code_id is not null
      and exists (select 1 from sales_tax_code_component_rates r where r.component_id = c.id);

    components_missing := components_total - components_with_rate;

    raise notice 'Sales Tax v2 rate backfill:';
    raise notice '  migrated components            : %', components_total;
    raise notice '  components with >=1 rate        : %', components_with_rate;
    raise notice '  components still missing a rate : %', components_missing;

    if components_missing > 0 then
        raise notice 'WARNING: % migrated component(s) still have no rate (legacy tax_codes row missing or deleted?).', components_missing;
    end if;
end$$;
