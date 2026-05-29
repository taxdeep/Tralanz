-- Sales Tax v2 — cross-tenant catalog seed for Canada (Phase 1 / MVP).
--
-- Seeds:
--   * 6 jurisdictions covering the four Canadian regimes (GST / HST / PST / QST)
--   * CRA GST34 reporting-box catalog (boxes 101 / 103-110 / 113)
--   * Default box-mapping templates for both GST and HST so a per-company
--     component editor lands on the right boxes without operator wiring
--
-- US states + EU VAT come later (Phase 2 / Phase 3). PST/QST reporting
-- boxes are deferred to S6 (return-routing) — for S1, the PST/QST
-- jurisdictions are seeded with treatment templates pointing at an
-- empty box set so the schema is exercised without a partial filing flow.
--
-- Idempotent — every INSERT uses ON CONFLICT.
--
-- DEPLOYMENT NOTE: requires the schema migration
-- (2026-05-29-sales-tax-v2-1-schema.sql) to have already applied. Apply
-- with postgres superuser:
--
--     psql -U postgres -d citus_accounting \
--          -f deploy/migrations/2026-05-29-sales-tax-v2-2-catalog-seed.sql

-- ========================================================================
-- 1. Jurisdictions
-- ========================================================================
-- One row per (country_code, region_code, city_code, regime_type). HST
-- is modeled as ONE jurisdiction across all five HST provinces because
-- a company files via the same CRA GST34 form regardless of which HST
-- province they operate in; rate differences (13% ON, 15% NB/NL/NS/PE)
-- live on the per-company tax_code_component_rates row.

insert into sales_tax_jurisdictions
    (country_code, region_code, city_code, display_name, authority_name, regime_type)
values
    ('CA', null,    null, 'Canada — GST (federal)',        'Canada Revenue Agency',         'gst'),
    ('CA', null,    null, 'Canada — HST (harmonized)',     'Canada Revenue Agency',         'hst'),
    ('CA', 'CA-BC', null, 'British Columbia — PST',        'BC Ministry of Finance',        'pst'),
    ('CA', 'CA-MB', null, 'Manitoba — RST (PST)',          'Manitoba Finance',              'pst'),
    ('CA', 'CA-SK', null, 'Saskatchewan — PST',            'Saskatchewan Ministry of Finance','pst'),
    ('CA', 'CA-QC', null, 'Québec — QST',                  'Revenu Québec',                 'qst')
on conflict on constraint uq_sales_tax_jurisdictions_natural_key do nothing;

-- ========================================================================
-- 2. CRA GST34 reporting boxes
-- ========================================================================
-- Owned by the federal GST jurisdiction (CRA collects both GST and HST
-- via the same GST34 form). HST jurisdictions cross-reference these boxes
-- via the box-mapping templates below. PST / QST returns have their own
-- forms; their box catalogs are deferred to S6.

with cra_gst as (
    select id from sales_tax_jurisdictions
     where country_code = 'CA'
       and region_code is null
       and regime_type = 'gst'
)
insert into sales_tax_reporting_boxes
    (jurisdiction_id, box_code, box_description, side, sort_order)
select cra_gst.id, b.box_code, b.box_description, b.side, b.sort_order
from cra_gst
cross join (values
    ('101', 'Sales and other revenue',                       'taxable_supplies',  1),
    ('103', 'GST/HST collected or collectible',              'collected',         2),
    ('104', 'Adjustments to be added',                       'adjustment',        3),
    ('105', 'Total GST/HST and adjustments (103+104)',       'collected',         4),
    ('106', 'Input tax credits (ITCs)',                      'itc',               5),
    ('107', 'Adjustments to be deducted',                    'adjustment',        6),
    ('108', 'Total ITCs and adjustments (106+107)',          'itc',               7),
    ('109', 'Net tax (105-108)',                             'net',               8),
    ('110', 'Instalment and other annual filer payments',    'instalment',        9),
    ('113', 'Balance (refund if negative)',                  'balance',          10)
) as b(box_code, box_description, side, sort_order)
on conflict on constraint uq_sales_tax_reporting_boxes_natural_key do nothing;

-- ========================================================================
-- 3. Jurisdiction box templates — defaults the component editor uses
-- ========================================================================
-- Per Decision 4: creating a new TaxCodeComponent for a given
-- (jurisdiction, treatment, side) auto-populates the box mapping from
-- this template. Component editor's "Override boxes" toggle lets
-- specialists deviate per-component.

-- GST + HST file via the same GST34 form, so they share the same template.
with sources as (
    select j.id as jurisdiction_id, j.regime_type
    from sales_tax_jurisdictions j
    where j.country_code = 'CA'
      and j.region_code is null
      and j.regime_type in ('gst','hst')
)
insert into sales_tax_jurisdiction_box_templates
    (jurisdiction_id, treatment, side, default_box_codes)
select sources.jurisdiction_id, t.treatment, t.side, t.boxes
from sources
cross join (values
    -- Sales: collected lands in Box 103 → flows into 105 (auto). Box 101
    -- tracks taxable supplies, which includes zero-rated sales (per the
    -- form). Exempt and out-of-scope do NOT bucket into 101.
    ('taxable',     'collected', array['101','103']),
    ('zero_rated',  'collected', array['101']),
    -- Purchases: recoverable ITC lands in Box 106 → flows into 108.
    ('taxable',     'itc',       array['106']),
    ('zero_rated',  'itc',       array['106']),
    -- Reverse-charge (Phase 1 scope per Decision 6): self-assessed
    -- payable bumps 103; self-assessed recoverable bumps 106.
    ('reverse_charge','collected', array['103']),
    ('reverse_charge','itc',       array['106'])
) as t(treatment, side, boxes)
on conflict on constraint uq_sales_tax_jurisdiction_box_templates_natural_key do nothing;

-- PST / QST: seed empty-box templates so the schema FK is exercised but
-- the operator-visible reporting wiring is deliberately deferred to S6.
-- This avoids surfacing half-built per-province filing flows that the
-- engine isn't yet aware of.
insert into sales_tax_jurisdiction_box_templates
    (jurisdiction_id, treatment, side, default_box_codes)
select j.id, t.treatment, t.side, '{}'::text[]
from sales_tax_jurisdictions j
cross join (values
    ('taxable',    'collected'),
    ('taxable',    'itc'),
    ('zero_rated', 'collected'),
    ('zero_rated', 'itc')
) as t(treatment, side)
where j.country_code = 'CA'
  and j.regime_type in ('pst','qst')
on conflict on constraint uq_sales_tax_jurisdiction_box_templates_natural_key do nothing;
