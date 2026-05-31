-- Sales Tax redesign (Rule / Code) — R1: Tax Code bundle schema.
--
-- See SALES_TAX_REDESIGN.md (repo root) for the full model. Confirmed
-- 2026-05-31:
--   * Tax Rule = a single tax = the existing `tax_codes` row (e.g. G = GST 5%).
--                UNCHANGED by this migration.
--   * Tax Code = a user-defined, ORDERED bundle of Tax Rules (e.g. BC =
--                GST 5% + PST 7%). NEW — the two tables below.
--
-- Rails ONLY: no engine wiring, no UI, no read-path changes. The existing
-- tax_codes / sales_tax_* flows keep working unchanged after this applies.
--
-- RLS: matches the v2 sales_tax_* config tables (RLS OFF — company isolation
-- is enforced by app-level `where company_id = @company_id` in every query,
-- exactly as those peers do). The legacy `tax_codes` keeps its own M13 RLS.
--
-- Idempotent — every CREATE uses IF NOT EXISTS.
--
-- DEPLOYMENT: apply with postgres superuser before restarting accounting-api
-- (citus_app lacks DDL). upgrade.sh's apply_pending_migrations does this.

-- ========================================================================
-- 1. Tax Code  (the bundle the user selects on a document line)
-- ========================================================================
-- A "single tax" is just a Tax Code with one member Rule, so document lines
-- can keep referencing a Rule id directly (polymorphic) — the engine treats
-- a bare Rule id as a one-member set. No existing line data is rewritten.

create table if not exists tax_code_sets (
    id          uuid        primary key default gen_random_uuid(),
    company_id  char(7)     not null references companies(id) on delete cascade,
    code        text        not null,
    name        text        not null,
    applies_to  text        not null default 'both',
    is_active   boolean     not null default true,
    created_at  timestamptz not null default now(),
    updated_at  timestamptz not null default now(),
    constraint uq_tax_code_sets_natural_key unique (company_id, code),
    constraint chk_tax_code_sets_applies_to check (applies_to in ('sales','purchase','both'))
);

create index if not exists idx_tax_code_sets_company_active
    on tax_code_sets (company_id, is_active)
    where is_active = true;

-- ========================================================================
-- 2. Tax Code -> Tax Rule membership  (ordered, many-to-many, reusable)
-- ========================================================================
-- A Rule may belong to many Codes (shared/reusable — decision 2026-05-31 #1).
-- `sequence` orders the tax legs on the resulting journal entry.
-- `is_compound` = tax-on-tax (default off; rarely needed — supported for
-- legacy QST-style piggyback math; decision #5). on delete restrict prevents
-- deleting a Rule still referenced by a Code.

create table if not exists tax_code_set_rules (
    id              uuid        primary key default gen_random_uuid(),
    company_id      char(7)     not null references companies(id) on delete cascade,
    tax_code_set_id uuid        not null references tax_code_sets(id) on delete cascade,
    tax_rule_id     uuid        not null references tax_codes(id) on delete restrict,
    sequence        int         not null,
    is_compound     boolean     not null default false,
    created_at      timestamptz not null default now(),
    updated_at      timestamptz not null default now(),
    constraint uq_tax_code_set_rules_sequence unique (tax_code_set_id, sequence),
    constraint uq_tax_code_set_rules_member   unique (tax_code_set_id, tax_rule_id)
);

create index if not exists idx_tax_code_set_rules_set
    on tax_code_set_rules (tax_code_set_id, sequence);

create index if not exists idx_tax_code_set_rules_rule
    on tax_code_set_rules (tax_rule_id);
