-- Unit-of-Measure (UOM) foundation: per-company master table that
-- drives qty input rules on Task / Invoice / Bill line grids.
--
-- Decisions captured 2026-05-25 with the operator:
--   * Per-company isolation (matches accounts / customers / items pattern)
--   * Code stays the operator-facing PK part; (company_id, code) composite PK
--   * decimal_precision int 0-6 — drives the qty input step (10^-precision)
--   * Seed 8 service-oriented UOMs for every existing + future company
--
-- DEPLOYMENT NOTE: citus_app lacks DDL privileges. Apply with postgres
-- superuser BEFORE restarting services on the new build:
--
--     psql -U postgres -d citus_accounting \
--          -f deploy/migrations/2026-05-25-uom-foundation.sql
--
-- Migration is idempotent — every CREATE / INSERT uses IF NOT EXISTS /
-- ON CONFLICT.

-- ========================================================================
-- 1. Master table
-- ========================================================================

create table if not exists units_of_measure (
    id                 uuid          primary key default gen_random_uuid(),
    company_id         char(7)       not null references companies(id) on delete cascade,
    code               varchar(16)   not null,
    name               varchar(64)   not null,
    -- 0 = integer-only (each / case / box). 2 = hours / days / kg / m / L.
    -- 4-6 reserved for high-precision use (chemistry, FX position).
    decimal_precision  int           not null default 0,
    -- Operator-facing grouping in the picker dropdown. Free-text but
    -- the seed sticks to {'time','count','weight','volume','length'}.
    category           varchar(16)   null,
    is_active          boolean       not null default true,
    created_at         timestamptz   not null default now(),
    updated_at         timestamptz   not null default now(),
    constraint uq_uom_company_code   unique (company_id, code),
    constraint chk_uom_precision     check (decimal_precision between 0 and 6)
);

create index if not exists idx_uom_company_active
    on units_of_measure (company_id, is_active)
    where is_active = true;

-- ========================================================================
-- 2. Seed default UOMs for every existing company
-- ========================================================================
-- Idempotent: on conflict (company_id, code) do nothing — re-runs are safe.

insert into units_of_measure (company_id, code, name, decimal_precision, category, is_active)
select c.id, u.code, u.name, u.precision, u.category, true
from companies c
cross join (values
    ('each',   'Each',    0, 'count'),
    ('hour',   'Hour',    2, 'time'),
    ('day',    'Day',     2, 'time'),
    ('minute', 'Minute',  2, 'time'),
    ('month',  'Month',   2, 'time'),
    ('kg',     'Kilogram',3, 'weight'),
    ('m',      'Meter',   2, 'length'),
    ('L',      'Liter',   2, 'volume')
) as u(code, name, precision, category)
on conflict on constraint uq_uom_company_code do nothing;

-- ========================================================================
-- 3. Seed-on-new-company trigger
-- ========================================================================
-- So companies created after this migration get the same 8 default UOMs
-- without touching the platform provisioning code path. Same idempotent
-- on-conflict guard as above so a re-applied migration / a backfill
-- script can run before the trigger fires without conflict.

create or replace function seed_default_uoms_for_company()
returns trigger
language plpgsql
as $$
begin
    insert into units_of_measure (company_id, code, name, decimal_precision, category, is_active)
    select NEW.id, u.code, u.name, u.precision, u.category, true
    from (values
        ('each',   'Each',    0, 'count'),
        ('hour',   'Hour',    2, 'time'),
        ('day',    'Day',     2, 'time'),
        ('minute', 'Minute',  2, 'time'),
        ('month',  'Month',   2, 'time'),
        ('kg',     'Kilogram',3, 'weight'),
        ('m',      'Meter',   2, 'length'),
        ('L',      'Liter',   2, 'volume')
    ) as u(code, name, precision, category)
    on conflict on constraint uq_uom_company_code do nothing;
    return NEW;
end;
$$;

drop trigger if exists trg_seed_default_uoms on companies;

create trigger trg_seed_default_uoms
    after insert on companies
    for each row execute function seed_default_uoms_for_company();

-- ========================================================================
-- 4. FK from inventory_items.stock_uom_code -> units_of_measure.code
-- ========================================================================
-- inventory_items.stock_uom_code is nullable and stays nullable: items
-- created before this migration may not have a UOM yet. PostgreSQL
-- MATCH SIMPLE (default) accepts NULL on the FK side, so existing NULL
-- rows survive.
--
-- For rows with a non-null stock_uom_code that doesn't match any seeded
-- UOM, the FK ADD would fail — defend with a backfill that lower-cases
-- and normalises common operator inputs to a seeded code before the FK
-- guard takes effect. Anything still unmatchable lands on 'each'
-- (sensible default for service-heavy ledgers) rather than blowing up
-- the migration.

update inventory_items i
   set stock_uom_code = lower(trim(stock_uom_code))
 where stock_uom_code is not null
   and lower(trim(stock_uom_code)) <> stock_uom_code;

-- Normalise common shorthand → seed code.
update inventory_items
   set stock_uom_code = case lower(trim(stock_uom_code))
     when 'ea'    then 'each'
     when 'pc'    then 'each'
     when 'pcs'   then 'each'
     when 'hr'    then 'hour'
     when 'hrs'   then 'hour'
     when 'd'     then 'day'
     when 'min'   then 'minute'
     when 'mo'    then 'month'
     when 'mos'   then 'month'
     when 'kgs'   then 'kg'
     when 'meter' then 'm'
     when 'litre' then 'L'
     when 'liter' then 'L'
     else stock_uom_code
   end
 where stock_uom_code is not null;

-- Default any still-unmatchable code to 'each' so the FK can be added.
update inventory_items i
   set stock_uom_code = 'each'
 where stock_uom_code is not null
   and not exists (
     select 1 from units_of_measure u
      where u.company_id = i.company_id and u.code = i.stock_uom_code);

alter table inventory_items
    drop constraint if exists fk_items_uom;

alter table inventory_items
    add constraint fk_items_uom
    foreign key (company_id, stock_uom_code)
    references units_of_measure(company_id, code)
    on delete restrict;
