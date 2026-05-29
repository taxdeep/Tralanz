-- Sales Tax v2 — one-shot data migration: existing tax_codes → v2 shape.
--
-- For every row in tax_codes:
--   1. Insert a sales_tax_codes row (legacy_tax_code_id keeps audit link).
--   2. Insert one sales_tax_code_components row (jurisdiction inferred
--      by heuristic; ambiguous cases set needs_jurisdiction_review=true
--      per Decision 1).
--   3. Insert one sales_tax_code_component_rates row with the current
--      rate effective from 1900-01-01 so every historic posted document
--      can find a valid rate via the as-of-date lookup.
--   4. Insert the default box-mapping rows from the jurisdiction template
--      (sales_tax_jurisdiction_box_templates).
--   5. Insert / upsert a sales_tax_registrations row when the legacy
--      tax_codes row carries a registration_number, so the registration
--      number moves out of tax_codes into its rightful home (one row per
--      (company, jurisdiction)).
--
-- The original tax_codes table is left UNCHANGED. The legacy Tax Rates
-- flow keeps reading from it. S5 / S6 retire the legacy paths and only
-- then do we drop the old table.
--
-- Idempotent: every INSERT uses ON CONFLICT — re-applying is safe.
--
-- DEPLOYMENT NOTE: requires the schema and catalog-seed migrations to
-- have applied. Apply with postgres superuser:
--
--     psql -U postgres -d citus_accounting \
--          -f deploy/migrations/2026-05-29-sales-tax-v2-3-data-migrate.sql

-- ========================================================================
-- 1. Helper: infer jurisdiction from a legacy tax_codes row
-- ========================================================================
-- Returns NULL when the code is ambiguous (caller flags
-- needs_jurisdiction_review and defaults to CA federal GST).

create or replace function infer_sales_tax_jurisdiction_id(
    p_code text,
    p_name text,
    p_rate numeric
) returns uuid
language plpgsql
stable
as $$
declare
    j_id uuid;
    code_upper text := upper(coalesce(p_code, ''));
    name_upper text := upper(coalesce(p_name, ''));
    combined   text := code_upper || ' ' || name_upper;
begin
    -- Explicit code/name patterns win (most specific first).
    -- QC QST (Revenu Québec).
    if combined ~ '(QST|TVQ|QUEBEC|QU.BEC)' then
        select id into j_id from sales_tax_jurisdictions
         where country_code = 'CA' and region_code = 'CA-QC' and regime_type = 'qst';
        return j_id;
    end if;

    -- BC PST.
    if combined ~ '(PST_BC|BC_PST|PST-BC|BC-PST|PST.*BRITISH|BRITISH.*PST)'
       or (combined ~ '^PST\W' and combined ~ '\WBC\W') then
        select id into j_id from sales_tax_jurisdictions
         where country_code = 'CA' and region_code = 'CA-BC' and regime_type = 'pst';
        return j_id;
    end if;

    -- MB PST / RST.
    if combined ~ '(RST|MANITOBA|PST_MB|MB_PST|PST-MB|MB-PST)' then
        select id into j_id from sales_tax_jurisdictions
         where country_code = 'CA' and region_code = 'CA-MB' and regime_type = 'pst';
        return j_id;
    end if;

    -- SK PST.
    if combined ~ '(PST_SK|SK_PST|PST-SK|SK-PST|SASKATCHEWAN)' then
        select id into j_id from sales_tax_jurisdictions
         where country_code = 'CA' and region_code = 'CA-SK' and regime_type = 'pst';
        return j_id;
    end if;

    -- HST (any province).
    if combined ~ 'HST' then
        select id into j_id from sales_tax_jurisdictions
         where country_code = 'CA' and region_code is null and regime_type = 'hst';
        return j_id;
    end if;

    -- Plain GST (federal).
    if combined ~ 'GST' then
        select id into j_id from sales_tax_jurisdictions
         where country_code = 'CA' and region_code is null and regime_type = 'gst';
        return j_id;
    end if;

    -- Rate-only heuristic (last resort).
    if p_rate is not null then
        if abs(p_rate - 5.0) < 0.001 then
            -- 5% → federal GST.
            select id into j_id from sales_tax_jurisdictions
             where country_code = 'CA' and region_code is null and regime_type = 'gst';
            return j_id;
        elsif abs(p_rate - 13.0) < 0.001 or abs(p_rate - 15.0) < 0.001 then
            -- 13%/15% → HST.
            select id into j_id from sales_tax_jurisdictions
             where country_code = 'CA' and region_code is null and regime_type = 'hst';
            return j_id;
        elsif abs(p_rate - 9.975) < 0.001 then
            -- 9.975% → QC QST.
            select id into j_id from sales_tax_jurisdictions
             where country_code = 'CA' and region_code = 'CA-QC' and regime_type = 'qst';
            return j_id;
        end if;
        -- 6%/7% PST is ambiguous (BC=7, SK=6, MB=7) — leave NULL.
    end if;

    return null;  -- ambiguous; caller flags needs_jurisdiction_review
end;
$$;

-- ========================================================================
-- 2. Move registration_number → sales_tax_registrations
-- ========================================================================
-- One registration per (company, inferred-jurisdiction) — multiple
-- legacy tax_codes that share a registration_number collapse into a
-- single registration row (UNIQUE constraint enforces). For ambiguous
-- jurisdictions we default to CA federal GST so the registration_number
-- isn't lost; operator reconciles in S6 UI.

insert into sales_tax_registrations
    (company_id, jurisdiction_id, registration_number, effective_from,
     filing_frequency, reporting_calendar, base_currency_code, is_active)
select distinct
    t.company_id::char(7),
    coalesce(
        infer_sales_tax_jurisdiction_id(t.code, t.name, t.rate_percent),
        (select id from sales_tax_jurisdictions
          where country_code = 'CA' and region_code is null and regime_type = 'gst')
    ),
    trim(t.registration_number),
    '1900-01-01'::date,
    'quarterly',
    'calendar',
    coalesce(c.base_currency_code, 'CAD'),
    true
from tax_codes t
join companies c on c.id = t.company_id::char(7)
where t.registration_number is not null
  and length(trim(t.registration_number)) > 0
on conflict on constraint uq_sales_tax_registrations_natural_key do nothing;

-- ========================================================================
-- 3. Migrate tax_codes → sales_tax_codes (+ component + rate + boxes)
-- ========================================================================
-- Uses a CTE chain so each tax_codes row writes 1 sales_tax_codes
-- + 1 sales_tax_code_components + 1 sales_tax_code_component_rates.
-- legacy_tax_code_id holds the link so re-runs detect existing rows
-- and skip via ON CONFLICT.

with inferred as (
    select
        t.id              as legacy_id,
        t.company_id::char(7) as company_id,
        t.code,
        t.name,
        t.rate_percent,
        t.applies_to,
        t.is_recoverable_on_purchase,
        t.recoverability_mode,
        t.payable_account_id,
        t.recoverable_account_id,
        t.is_active,
        infer_sales_tax_jurisdiction_id(t.code, t.name, t.rate_percent) as inferred_jurisdiction_id
    from tax_codes t
),
migrated_codes as (
    insert into sales_tax_codes
        (company_id, code, name, treatment, applies_to, is_active,
         needs_jurisdiction_review, legacy_tax_code_id)
    select
        i.company_id,
        i.code,
        i.name,
        'taxable',
        coalesce(i.applies_to, 'both'),
        coalesce(i.is_active, true),
        (i.inferred_jurisdiction_id is null),  -- flag when heuristic failed
        i.legacy_id
    from inferred i
    on conflict on constraint uq_sales_tax_codes_natural_key do update
        set legacy_tax_code_id = excluded.legacy_tax_code_id
    returning id as v2_id, legacy_tax_code_id as legacy_id, company_id
),
fallback_jurisdiction as (
    select id from sales_tax_jurisdictions
     where country_code = 'CA' and region_code is null and regime_type = 'gst'
),
migrated_components as (
    insert into sales_tax_code_components
        (company_id, tax_code_id, jurisdiction_id, sequence, is_compound,
         recoverability_mode, recoverable_percent,
         payable_account_id, recoverable_account_id, non_recoverable_account_id)
    select
        mc.company_id,
        mc.v2_id,
        coalesce(i.inferred_jurisdiction_id, (select id from fallback_jurisdiction)),
        1,
        false,
        coalesce(i.recoverability_mode, 'full'),
        case when i.recoverability_mode = 'partial' then 50.00 else null end,
        i.payable_account_id,
        i.recoverable_account_id,
        null
    from migrated_codes mc
    join inferred i on i.legacy_id = mc.legacy_id
    on conflict on constraint uq_sales_tax_code_components_sequence do nothing
    returning id, tax_code_id
)
-- NOTE: bridge component → legacy via the migrated_codes CTE, NOT via the
-- sales_tax_codes TABLE. Data-modifying CTEs share one snapshot and cannot
-- see each other's table writes, so a `join sales_tax_codes` here would not
-- find the rows migrated_codes just inserted on a fresh run → 0 rates
-- inserted. (This bug shipped once; 2026-05-29-sales-tax-v2-5-rate-backfill
-- repairs already-migrated databases.)
insert into sales_tax_code_component_rates
    (component_id, rate_percent, effective_from)
select c.id, coalesce(i.rate_percent, 0), '1900-01-01'::date
from migrated_components c
join migrated_codes mc on mc.v2_id = c.tax_code_id
join inferred i on i.legacy_id = mc.legacy_id
on conflict on constraint uq_sales_tax_code_component_rates_natural_key do nothing;

-- ========================================================================
-- 4. Default box mappings per migrated component (from template)
-- ========================================================================
-- Each component picks up the box-mapping defaults for its (jurisdiction,
-- treatment, side). Treatment is always 'taxable' for migrated rows
-- (Decision 1: default; operator promotes to zero_rated / exempt later).
-- box_mapping_overridden stays false so the component editor knows it can
-- regenerate from the template on future template updates.

with component_sides as (
    select
        c.id as component_id,
        c.jurisdiction_id,
        c.recoverability_mode,
        stc.applies_to
    from sales_tax_code_components c
    join sales_tax_codes stc on stc.id = c.tax_code_id
    where stc.legacy_tax_code_id is not null  -- only migrated components
)
insert into sales_tax_code_component_box_mappings
    (component_id, box_id, side)
select cs.component_id, b.id, t.side
from component_sides cs
join sales_tax_jurisdiction_box_templates t
        on t.jurisdiction_id = cs.jurisdiction_id
       and t.treatment = 'taxable'
       -- emit collected mapping when the code applies to sales or both
       -- emit itc mapping when the code applies to purchase or both
       --   (and is at least partially recoverable)
       and (
            (t.side = 'collected' and cs.applies_to in ('sales','both'))
         or (t.side = 'itc'       and cs.applies_to in ('purchase','both')
                                  and cs.recoverability_mode <> 'none')
       )
join sales_tax_reporting_boxes b
        on b.jurisdiction_id = t.jurisdiction_id
       and b.box_code = any(t.default_box_codes)
on conflict on constraint uq_sales_tax_code_component_box_mappings_natural_key do nothing;

-- ========================================================================
-- 5. Verification (informational notice — does not fail the migration)
-- ========================================================================
-- Counts what landed. Operator can re-run the migration safely and re-
-- read this notice without side effects.

do $$
declare
    legacy_count            int;
    v2_codes_count          int;
    v2_components_count     int;
    v2_rates_count          int;
    v2_box_mappings_count   int;
    needs_review_count      int;
    registrations_count     int;
begin
    select count(*) into legacy_count from tax_codes;
    select count(*) into v2_codes_count from sales_tax_codes where legacy_tax_code_id is not null;
    select count(*) into v2_components_count from sales_tax_code_components c
        join sales_tax_codes s on s.id = c.tax_code_id where s.legacy_tax_code_id is not null;
    select count(*) into v2_rates_count from sales_tax_code_component_rates r
        join sales_tax_code_components c on c.id = r.component_id
        join sales_tax_codes s on s.id = c.tax_code_id where s.legacy_tax_code_id is not null;
    select count(*) into v2_box_mappings_count from sales_tax_code_component_box_mappings;
    select count(*) into needs_review_count from sales_tax_codes where needs_jurisdiction_review = true;
    select count(*) into registrations_count from sales_tax_registrations;

    raise notice 'Sales Tax v2 data migration counts:';
    raise notice '  legacy tax_codes rows         : %', legacy_count;
    raise notice '  migrated sales_tax_codes      : %', v2_codes_count;
    raise notice '  migrated components           : %', v2_components_count;
    raise notice '  migrated component rates      : %', v2_rates_count;
    raise notice '  default box mappings created  : %', v2_box_mappings_count;
    raise notice '  rows flagged needs_review     : %', needs_review_count;
    raise notice '  registrations rows created    : %', registrations_count;

    if v2_codes_count <> legacy_count then
        raise notice 'WARNING: migrated codes (%) != legacy count (%). Some rows may have collided on (company_id, code) UNIQUE.',
            v2_codes_count, legacy_count;
    end if;
end$$;
