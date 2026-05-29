-- Sales Tax Module v2 — foundational schema.
--
-- See SALES_TAX_MODULE_DESIGN.md (repo root) for the full architecture
-- behind these tables. This migration creates the rails ONLY: no read
-- path changes, no engine wiring, no UI changes. Existing tax_codes and
-- the legacy `Tax Rates` flow continue to work unchanged after this
-- migration applies.
--
-- Naming convention: every new table uses the `sales_tax_` prefix so
-- nothing collides with the existing `tax_codes` / `tax_returns` tables
-- (which stay in place until S5/S6 retire them). The Domain.Shared
-- records in code use the matching `SalesTax*` type names.
--
-- Multi-tenant guard: cross-tenant CATALOGS (jurisdictions, reporting
-- boxes, jurisdiction box templates) carry NO company_id; per-company
-- tables all carry char(7) company_id NOT NULL matching the rest of
-- the codebase (Tasks, Accounts, Items, Customers, Vendors). Existing
-- tax_codes' use of UUID company_id is intentionally not propagated.
--
-- Idempotent — every CREATE / ALTER uses IF NOT EXISTS / IF NOT EXISTS.
--
-- DEPLOYMENT NOTE: citus_app lacks DDL privileges. Apply with postgres
-- superuser BEFORE restarting accounting-api on the new build:
--
--     psql -U postgres -d citus_accounting \
--          -f deploy/migrations/2026-05-29-sales-tax-v2-schema.sql

-- ========================================================================
-- 1. Cross-tenant catalog: jurisdictions
-- ========================================================================
-- (country_code, region_code, city_code, regime_type) is the natural key.
-- Region uses ISO 3166-2 subdivision codes ("CA-BC", "US-CA", "IE").
-- city_code is optional (used for US local sales tax). For pseudo-regimes
-- like EU OSS / IOSS that span jurisdictions, we use country_code='EU'.

create table if not exists sales_tax_jurisdictions (
    id              uuid          primary key default gen_random_uuid(),
    country_code    char(2)       not null,
    region_code     text          null,
    city_code       text          null,
    display_name    text          not null,
    authority_name  text          not null,
    regime_type     text          not null,
    is_active       boolean       not null default true,
    created_at      timestamptz   not null default now(),
    updated_at      timestamptz   not null default now(),
    constraint uq_sales_tax_jurisdictions_natural_key
        unique (country_code, region_code, city_code, regime_type),
    constraint chk_sales_tax_jurisdictions_regime check (
        regime_type in (
            'gst', 'hst', 'pst', 'qst',
            'vat', 'us_sales', 'local_sales',
            'oss', 'ioss', 'reverse_charge'
        )
    )
);

create index if not exists idx_sales_tax_jurisdictions_active
    on sales_tax_jurisdictions (regime_type, country_code)
    where is_active = true;

-- ========================================================================
-- 2. Cross-tenant catalog: reporting boxes (per jurisdiction)
-- ========================================================================
-- One row per filing-form line. CRA GST34 form lines 101/103/105/106/108/109
-- live here; same shape for state forms and EU VAT returns.

create table if not exists sales_tax_reporting_boxes (
    id              uuid          primary key default gen_random_uuid(),
    jurisdiction_id uuid          not null references sales_tax_jurisdictions(id) on delete cascade,
    box_code        text          not null,
    box_description text          not null,
    side            text          not null,
    sort_order      int           not null default 0,
    is_active       boolean       not null default true,
    created_at      timestamptz   not null default now(),
    constraint uq_sales_tax_reporting_boxes_natural_key
        unique (jurisdiction_id, box_code),
    constraint chk_sales_tax_reporting_boxes_side check (
        side in ('taxable_supplies','collected','itc','adjustment','net','instalment','balance')
    )
);

-- ========================================================================
-- 3. Cross-tenant catalog: default box-mapping templates per jurisdiction
-- ========================================================================
-- Per Decision 4: SysAdmin defines a per-(jurisdiction, treatment, side)
-- default set of box codes a tax_code_component lands in. Component-level
-- override flag lets specialists deviate; default UI sticks with the
-- template so new companies don't have to wire box mappings manually.

create table if not exists sales_tax_jurisdiction_box_templates (
    id                uuid        primary key default gen_random_uuid(),
    jurisdiction_id   uuid        not null references sales_tax_jurisdictions(id) on delete cascade,
    treatment         text        not null,
    side              text        not null,
    default_box_codes text[]      not null default '{}'::text[],
    created_at        timestamptz not null default now(),
    constraint uq_sales_tax_jurisdiction_box_templates_natural_key
        unique (jurisdiction_id, treatment, side),
    constraint chk_sales_tax_jurisdiction_box_templates_treatment check (
        treatment in ('taxable','zero_rated','exempt','out_of_scope','reverse_charge','import_tax')
    ),
    constraint chk_sales_tax_jurisdiction_box_templates_side check (
        side in ('collected','itc')
    )
);

-- ========================================================================
-- 4. Per-company: tax registrations (one row per company × jurisdiction)
-- ========================================================================
-- Per-jurisdiction GL routing quintet replaces the hardcoded chart
-- codes (25000/13700/25001/25002/13701) used by today's tax_returns
-- posting flow. Each registration carries its own quintet so a
-- multi-regime company (GST + QST + VAT) keeps each return on its
-- own GL accounts.

create table if not exists sales_tax_registrations (
    id                                uuid        primary key default gen_random_uuid(),
    company_id                        char(7)     not null references companies(id) on delete cascade,
    jurisdiction_id                   uuid        not null references sales_tax_jurisdictions(id),
    registration_number               text        not null,
    effective_from                    date        not null,
    effective_to                      date        null,
    filing_frequency                  text        not null,
    reporting_calendar                text        not null default 'calendar',
    base_currency_code                char(3)     not null,
    collected_clearing_account_id     uuid        null references accounts(id),
    recoverable_clearing_account_id   uuid        null references accounts(id),
    adjustment_account_id             uuid        null references accounts(id),
    return_liability_account_id       uuid        null references accounts(id),
    return_receivable_account_id      uuid        null references accounts(id),
    is_active                         boolean     not null default true,
    created_at                        timestamptz not null default now(),
    updated_at                        timestamptz not null default now(),
    constraint uq_sales_tax_registrations_natural_key
        unique (company_id, jurisdiction_id, effective_from),
    constraint chk_sales_tax_registrations_filing_frequency check (
        filing_frequency in ('monthly','quarterly','annual')
    ),
    constraint chk_sales_tax_registrations_effective_range check (
        effective_to is null or effective_to > effective_from
    )
);

create index if not exists idx_sales_tax_registrations_company_active
    on sales_tax_registrations (company_id, is_active)
    where is_active = true;

-- ========================================================================
-- 5. Per-company: tax codes (V2)
-- ========================================================================
-- The user-facing selectable. needs_jurisdiction_review flag (Decision 1)
-- lights up on rows whose jurisdiction was inferred by the data-migration
-- heuristic and needs operator confirmation in SalesTaxesPage.

create table if not exists sales_tax_codes (
    id                          uuid        primary key default gen_random_uuid(),
    company_id                  char(7)     not null references companies(id) on delete cascade,
    code                        text        not null,
    name                        text        not null,
    treatment                   text        not null default 'taxable',
    applies_to                  text        not null default 'both',
    is_active                   boolean     not null default true,
    needs_jurisdiction_review   boolean     not null default false,
    legacy_tax_code_id          uuid        null,    -- back-reference to source tax_codes row (data migration audit)
    created_at                  timestamptz not null default now(),
    updated_at                  timestamptz not null default now(),
    constraint uq_sales_tax_codes_natural_key
        unique (company_id, code),
    constraint chk_sales_tax_codes_treatment check (
        treatment in ('taxable','zero_rated','exempt','out_of_scope','reverse_charge','import_tax')
    ),
    constraint chk_sales_tax_codes_applies_to check (
        applies_to in ('sales','purchase','both')
    )
);

create index if not exists idx_sales_tax_codes_company_active
    on sales_tax_codes (company_id, is_active)
    where is_active = true;

-- ========================================================================
-- 6. Per-company: tax code components (1..N per code)
-- ========================================================================
-- Each component links to one jurisdiction, carries per-leg recoverability,
-- and per-leg GL routing. is_compound triggers piggyback math when true.

create table if not exists sales_tax_code_components (
    id                          uuid        primary key default gen_random_uuid(),
    company_id                  char(7)     not null references companies(id) on delete cascade,
    tax_code_id                 uuid        not null references sales_tax_codes(id) on delete cascade,
    jurisdiction_id             uuid        not null references sales_tax_jurisdictions(id),
    sequence                    int         not null,
    is_compound                 boolean     not null default false,
    recoverability_mode         text        not null default 'full',
    recoverable_percent         numeric(5,2) null,
    payable_account_id          uuid        null references accounts(id),
    recoverable_account_id      uuid        null references accounts(id),
    non_recoverable_account_id  uuid        null references accounts(id),
    box_mapping_overridden      boolean     not null default false,
    created_at                  timestamptz not null default now(),
    updated_at                  timestamptz not null default now(),
    constraint uq_sales_tax_code_components_sequence
        unique (tax_code_id, sequence),
    constraint chk_sales_tax_code_components_recoverability check (
        recoverability_mode in ('full','partial','none')
    ),
    constraint chk_sales_tax_code_components_partial_percent check (
        recoverability_mode <> 'partial' or (recoverable_percent is not null and recoverable_percent between 0 and 100)
    )
);

create index if not exists idx_sales_tax_code_components_code
    on sales_tax_code_components (tax_code_id, sequence);

-- ========================================================================
-- 7. Per-company: effective-dated rates per component
-- ========================================================================
-- Rate history. Lookup: WHERE component_id = ? AND effective_from <= :date
-- AND (effective_to IS NULL OR effective_to > :date). Rate changes never
-- UPDATE rate_percent on an active row — they INSERT a new row with the
-- new effective_from. The earlier row's effective_to gets set on insert.

create table if not exists sales_tax_code_component_rates (
    id                  uuid        primary key default gen_random_uuid(),
    component_id        uuid        not null references sales_tax_code_components(id) on delete cascade,
    rate_percent        numeric(9,6) not null,
    effective_from      date        not null,
    effective_to        date        null,
    created_at          timestamptz not null default now(),
    constraint uq_sales_tax_code_component_rates_natural_key
        unique (component_id, effective_from),
    constraint chk_sales_tax_code_component_rates_range check (
        effective_to is null or effective_to > effective_from
    ),
    constraint chk_sales_tax_code_component_rates_rate check (
        rate_percent >= 0 and rate_percent <= 100
    )
);

-- ========================================================================
-- 8. Per-component box mapping rows
-- ========================================================================
-- Sourced from jurisdiction_box_templates by default; component editor
-- can override (then box_mapping_overridden=true on the component).

create table if not exists sales_tax_code_component_box_mappings (
    id              uuid        primary key default gen_random_uuid(),
    component_id    uuid        not null references sales_tax_code_components(id) on delete cascade,
    box_id          uuid        not null references sales_tax_reporting_boxes(id) on delete restrict,
    side            text        not null,
    created_at      timestamptz not null default now(),
    constraint uq_sales_tax_code_component_box_mappings_natural_key
        unique (component_id, box_id, side),
    constraint chk_sales_tax_code_component_box_mappings_side check (
        side in ('collected','itc')
    )
);

-- ========================================================================
-- 9. Per-company: item × region default tax code (Decision 3)
-- ========================================================================
-- Lookup chain on a sales line:
--   (1) operator override on the line
--   (2) item_default_sales_tax_codes WHERE item_id=? AND region_code=customer.region
--   (3) items.default_tax_code_id (existing global fallback)

create table if not exists item_default_sales_tax_codes (
    id              uuid        primary key default gen_random_uuid(),
    company_id      char(7)     not null references companies(id) on delete cascade,
    item_id         uuid        not null references inventory_items(id) on delete cascade,
    region_code     text        not null,
    tax_code_id     uuid        not null references sales_tax_codes(id) on delete restrict,
    created_at      timestamptz not null default now(),
    updated_at      timestamptz not null default now(),
    constraint uq_item_default_sales_tax_codes_natural_key
        unique (company_id, item_id, region_code)
);

create index if not exists idx_item_default_sales_tax_codes_item
    on item_default_sales_tax_codes (company_id, item_id);

-- ========================================================================
-- 10. Immutable document-line tax snapshots (the source of truth)
-- ========================================================================
-- One row per (document, line, sequence, leg). leg defaults to 'primary';
-- reverse_charge emits two rows ('self_assessed_payable',
-- 'self_assessed_recoverable') sharing component_id but with opposite-
-- signed amounts.
--
-- Once the parent document.status flips to 'posted', a trigger (S5)
-- enforces immutability of these rows. For S1 the table is created
-- empty; backfill (S1.4) populates rows derived from existing posted
-- *_lines.tax_amount.

create table if not exists document_line_sales_tax_snapshots (
    id                          uuid        primary key default gen_random_uuid(),
    company_id                  char(7)     not null references companies(id) on delete cascade,
    document_type               text        not null,
    document_id                 uuid        not null,
    line_id                     uuid        not null,
    sequence                    int         not null default 1,
    leg                         text        not null default 'primary',
    tax_code_id                 uuid        not null references sales_tax_codes(id) on delete restrict,
    component_id                uuid        not null references sales_tax_code_components(id) on delete restrict,
    jurisdiction_id             uuid        not null references sales_tax_jurisdictions(id),
    -- Denormalized snapshot fields (immutable once parent.status='posted')
    code_snapshot               text        not null,
    name_snapshot               text        not null,
    regime_type_snapshot        text        not null,
    treatment_snapshot          text        not null,
    rate_percent_snapshot       numeric(9,6) not null,
    is_compound_snapshot        boolean     not null default false,
    reporting_box_codes         text[]      not null default '{}'::text[],
    -- Amounts in document currency
    taxable_amount              numeric(20,6) not null default 0,
    tax_amount                  numeric(20,6) not null default 0,
    recoverable_amount          numeric(20,6) not null default 0,
    non_recoverable_amount      numeric(20,6) not null default 0,
    -- Multi-currency
    document_currency_code      char(3)     not null,
    tax_amount_base             numeric(20,6) not null default 0,
    fx_rate_snapshot            numeric(20,8) not null default 1,
    computed_at                 timestamptz not null default now(),
    constraint uq_document_line_sales_tax_snapshots_natural_key
        unique (document_type, document_id, line_id, sequence, leg),
    constraint chk_document_line_sales_tax_snapshots_document_type check (
        document_type in (
            'invoice','credit_note','sales_receipt','refund_receipt',
            'bill','vendor_credit','expense','journal_entry'
        )
    ),
    constraint chk_document_line_sales_tax_snapshots_leg check (
        leg in ('primary','self_assessed_payable','self_assessed_recoverable')
    )
);

-- Hot path: reporting layer queries (company, jurisdiction, period).
create index if not exists idx_document_line_sales_tax_snapshots_query
    on document_line_sales_tax_snapshots (company_id, jurisdiction_id, computed_at);

-- Secondary path: drill-down from a document detail page.
create index if not exists idx_document_line_sales_tax_snapshots_document
    on document_line_sales_tax_snapshots (document_type, document_id);

-- ========================================================================
-- 11. Per-company: tax return adjustments (per-box edits to a draft return)
-- ========================================================================
-- The four scalar amounts on tax_returns (collected/itcs/adjustments/net)
-- become preview-derived totals. Operator-entered tweaks live in this
-- per-box adjustments table so the return form lines up with the
-- jurisdiction's actual filing schema.

create table if not exists sales_tax_return_adjustments (
    id                  uuid        primary key default gen_random_uuid(),
    company_id          char(7)     not null references companies(id) on delete cascade,
    tax_return_id       uuid        not null references tax_returns(id) on delete cascade,
    box_id              uuid        not null references sales_tax_reporting_boxes(id) on delete restrict,
    amount              numeric(20,6) not null default 0,
    note                text        not null,
    created_at          timestamptz not null default now(),
    created_by_user_id  text        not null
);

create index if not exists idx_sales_tax_return_adjustments_return
    on sales_tax_return_adjustments (tax_return_id);

-- ========================================================================
-- 12. ALTER document parent tables: tax_pricing_mode (Decision 5)
-- ========================================================================
-- Default 'exclusive' so existing documents and the existing UI flow
-- continue to render identically. EU / retail flows toggle to 'inclusive'
-- per-document via the header dropdown (S3 SalesTaxesPage scope).

alter table invoices         add column if not exists tax_pricing_mode text not null default 'exclusive';
alter table bills            add column if not exists tax_pricing_mode text not null default 'exclusive';
alter table credit_notes     add column if not exists tax_pricing_mode text not null default 'exclusive';
alter table vendor_credits   add column if not exists tax_pricing_mode text not null default 'exclusive';
alter table sales_receipts   add column if not exists tax_pricing_mode text not null default 'exclusive';
alter table refund_receipts  add column if not exists tax_pricing_mode text not null default 'exclusive';

-- CHECK constraints (idempotent: drop-and-recreate via DO block).
do $$
declare
    tbl text;
begin
    for tbl in select unnest(array[
        'invoices','bills','credit_notes','vendor_credits','sales_receipts','refund_receipts'
    ])
    loop
        execute format('alter table %I drop constraint if exists chk_%I_tax_pricing_mode', tbl, tbl);
        execute format(
            'alter table %I add constraint chk_%I_tax_pricing_mode '
            'check (tax_pricing_mode in (''exclusive'',''inclusive''))',
            tbl, tbl);
    end loop;
end$$;

-- ========================================================================
-- 13. ALTER tax_returns: link to v2 jurisdiction + registration
-- ========================================================================
-- Existing rows keep their free-text tax_regime column; new rows posted
-- via the v2 flow populate jurisdiction_id + registration_id. S6
-- migrates the legacy column into FKs.

alter table tax_returns
    add column if not exists jurisdiction_id  uuid null references sales_tax_jurisdictions(id),
    add column if not exists registration_id  uuid null references sales_tax_registrations(id);

create index if not exists idx_tax_returns_v2_jurisdiction
    on tax_returns (jurisdiction_id)
    where jurisdiction_id is not null;
