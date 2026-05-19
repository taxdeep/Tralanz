-- Task module + per-company module flags + fine-grained permission
-- catalog. Carries the schema the in-app EnsureSchemaAsync paths
-- create in Development (Batches 1, 3, 3.5, 4, 5, 8) over to a
-- migration that can be applied by an admin role on a Production
-- database where the app's runtime user has only DML privileges.
--
-- Every statement uses IF NOT EXISTS or a WHERE guard so re-running
-- is a no-op. Safe to apply on a fresh DB and on the May-15 baseline
-- alike.

-- ============================================================
-- Batch 1: per-company module flags
-- ============================================================

create table if not exists company_module_flags (
  company_id   char(7)      not null,
  module_key   varchar(64)  not null,
  enabled      boolean      not null,
  updated_at   timestamptz  not null default now(),
  updated_by   char(7)      null,
  primary key (company_id, module_key)
);

create index if not exists ix_company_module_flags_company
  on company_module_flags (company_id);

-- ============================================================
-- Batch 3.5: owner immutability + atomic transfer
-- ============================================================

alter table company_memberships
  add column if not exists is_owner boolean not null default false;

-- At-most-one-owner-per-company invariant. Partial unique index so
-- non-owner rows (the majority) don't conflict.
create unique index if not exists uq_company_memberships_one_owner
  on company_memberships (company_id)
  where is_owner = true;

-- Backfill: every membership currently flagged as role='owner'
-- becomes is_owner=true. The WHERE clause skips rows already
-- aligned so re-running this migration writes nothing.
update company_memberships
set is_owner = true,
    updated_at = now()
where role = 'owner'
  and is_owner = false;

-- ============================================================
-- Batch 3: legacy permission token expansion
--
-- Existing memberships that hold a coarse legacy token ('ar', 'ap',
-- etc.) need the fine-grained equivalents added so the new
-- [HasPermission] decorator path grants the same authorization.
-- Each block is idempotent: the NOT @> guard skips rows whose
-- expansion is already present.
--
-- IMPORTANT: jsonb_agg(distinct value order by value) preserves the
-- ordinal sort the in-app expansion produces, so downstream
-- equality comparisons stay stable.
-- ============================================================

-- 'ar' → 11 fine-grained AR tokens
update company_memberships
set permissions = (
        select coalesce(jsonb_agg(distinct elem order by elem), '[]'::jsonb)
        from jsonb_array_elements_text(
          permissions || jsonb_build_array(
            'ar.invoice.view','ar.invoice.create','ar.invoice.edit',
            'ar.receipt.view','ar.receipt.create',
            'ar.customer.view','ar.customer.create','ar.customer.edit',
            'ar.creditnote.view','ar.creditnote.create',
            'ar.aging.view'
          )
        ) as elem
    ),
    updated_at = now()
where permissions @> '"ar"'::jsonb
  and not permissions @> jsonb_build_array(
        'ar.invoice.view','ar.invoice.create','ar.invoice.edit',
        'ar.receipt.view','ar.receipt.create',
        'ar.customer.view','ar.customer.create','ar.customer.edit',
        'ar.creditnote.view','ar.creditnote.create',
        'ar.aging.view'
      );

-- 'ap' → 11 fine-grained AP tokens
update company_memberships
set permissions = (
        select coalesce(jsonb_agg(distinct elem order by elem), '[]'::jsonb)
        from jsonb_array_elements_text(
          permissions || jsonb_build_array(
            'ap.bill.view','ap.bill.create','ap.bill.edit',
            'ap.payment.view','ap.payment.create',
            'ap.vendor.view','ap.vendor.create','ap.vendor.edit',
            'ap.vendorcredit.view','ap.vendorcredit.create',
            'ap.aging.view'
          )
        ) as elem
    ),
    updated_at = now()
where permissions @> '"ap"'::jsonb
  and not permissions @> jsonb_build_array(
        'ap.bill.view','ap.bill.create','ap.bill.edit',
        'ap.payment.view','ap.payment.create',
        'ap.vendor.view','ap.vendor.create','ap.vendor.edit',
        'ap.vendorcredit.view','ap.vendorcredit.create',
        'ap.aging.view'
      );

-- 'approve' → 3 post tokens (AR invoice, AP bill, GL journal)
update company_memberships
set permissions = (
        select coalesce(jsonb_agg(distinct elem order by elem), '[]'::jsonb)
        from jsonb_array_elements_text(
          permissions || jsonb_build_array(
            'ar.invoice.post','ap.bill.post','gl.journal.post'
          )
        ) as elem
    ),
    updated_at = now()
where permissions @> '"approve"'::jsonb
  and not permissions @> jsonb_build_array(
        'ar.invoice.post','ap.bill.post','gl.journal.post'
      );

-- 'reports' → 2 standard report tokens
update company_memberships
set permissions = (
        select coalesce(jsonb_agg(distinct elem order by elem), '[]'::jsonb)
        from jsonb_array_elements_text(
          permissions || jsonb_build_array('reports.view','reports.export')
        ) as elem
    ),
    updated_at = now()
where permissions @> '"reports"'::jsonb
  and not permissions @> jsonb_build_array('reports.view','reports.export');

-- 'reconciliation' → 4 settlement / journal tokens
update company_memberships
set permissions = (
        select coalesce(jsonb_agg(distinct elem order by elem), '[]'::jsonb)
        from jsonb_array_elements_text(
          permissions || jsonb_build_array(
            'gl.journal.view','gl.journal.create',
            'ar.receipt.apply','ap.payment.apply'
          )
        ) as elem
    ),
    updated_at = now()
where permissions @> '"reconciliation"'::jsonb
  and not permissions @> jsonb_build_array(
        'gl.journal.view','gl.journal.create',
        'ar.receipt.apply','ap.payment.apply'
      );

-- 'settings_access' → settings.company.view
update company_memberships
set permissions = (
        select coalesce(jsonb_agg(distinct elem order by elem), '[]'::jsonb)
        from jsonb_array_elements_text(
          permissions || jsonb_build_array('settings.company.view')
        ) as elem
    ),
    updated_at = now()
where permissions @> '"settings_access"'::jsonb
  and not permissions @> jsonb_build_array('settings.company.view');

-- 'company_accounting_settings' → 4 settings edit tokens
update company_memberships
set permissions = (
        select coalesce(jsonb_agg(distinct elem order by elem), '[]'::jsonb)
        from jsonb_array_elements_text(
          permissions || jsonb_build_array(
            'settings.company.edit',
            'settings.numbering.edit',
            'settings.fx.edit',
            'settings.tax.edit'
          )
        ) as elem
    ),
    updated_at = now()
where permissions @> '"company_accounting_settings"'::jsonb
  and not permissions @> jsonb_build_array(
        'settings.company.edit',
        'settings.numbering.edit',
        'settings.fx.edit',
        'settings.tax.edit'
      );

-- 'company_book_governance' → 4 governance tokens
update company_memberships
set permissions = (
        select coalesce(jsonb_agg(distinct elem order by elem), '[]'::jsonb)
        from jsonb_array_elements_text(
          permissions || jsonb_build_array(
            'settings.permissions.view',
            'settings.permissions.assign',
            'settings.modules.toggle',
            'gl.period.close'
          )
        ) as elem
    ),
    updated_at = now()
where permissions @> '"company_book_governance"'::jsonb
  and not permissions @> jsonb_build_array(
        'settings.permissions.view',
        'settings.permissions.assign',
        'settings.modules.toggle',
        'gl.period.close'
      );

-- ============================================================
-- Batch 4: inventory item prices (Pricelist merged INTO Products)
-- ============================================================

create table if not exists inventory_item_prices (
  id                uuid           primary key default gen_random_uuid(),
  company_id        char(7)        not null,
  item_id           uuid           not null,
  currency_code     char(3)        not null,
  unit_price        numeric(20, 4) not null,
  min_quantity      numeric(20, 4) not null default 1,
  effective_from    date           not null,
  effective_to      date           null,
  price_list_code   varchar(32)    null,
  customer_id       uuid           null,
  is_active         boolean        not null default true,
  created_at        timestamptz    not null default now(),
  updated_at        timestamptz    not null default now()
);

create index if not exists ix_inventory_item_prices_company_item
  on inventory_item_prices (company_id, item_id);

-- Resolver hot path: every resolve query funnels through
-- (company_id, item_id, currency_code, is_active=true). Partial
-- index keeps it tight on multi-million-row catalogs.
create index if not exists ix_inventory_item_prices_resolve
  on inventory_item_prices (company_id, item_id, currency_code)
  where is_active = true;

-- ============================================================
-- Batch 5: Task module — domain tables + per-company sequence
-- ============================================================

create table if not exists tasks (
  id                    uuid           primary key default gen_random_uuid(),
  company_id            char(7)        not null,
  task_no               varchar(32)    not null,
  title                 varchar(200)   not null,
  description           text           null,
  customer_id           uuid           null,
  project_id            uuid           null,
  assigned_to_user_id   char(7)        null,
  status                varchar(16)    not null,
  service_date          date           null,
  ready_to_bill_at      timestamptz    null,
  billed_invoice_id     uuid           null,
  billed_at             timestamptz    null,
  total_billable_value  numeric(20, 4) not null default 0,
  total_direct_cost     numeric(20, 4) not null default 0,
  currency_code         char(3)        not null,
  is_voided             boolean        not null default false,
  created_at            timestamptz    not null default now(),
  created_by            char(7)        not null,
  updated_at            timestamptz    not null default now(),
  constraint uq_tasks_company_task_no unique (company_id, task_no)
);

create index if not exists ix_tasks_company_status
  on tasks (company_id, status);

create index if not exists ix_tasks_company_customer
  on tasks (company_id, customer_id);

create index if not exists ix_tasks_company_assignee
  on tasks (company_id, assigned_to_user_id)
  where assigned_to_user_id is not null;

create table if not exists task_lines (
  id              uuid           primary key default gen_random_uuid(),
  company_id      char(7)        not null,
  task_id         uuid           not null references tasks(id) on delete cascade,
  line_no         int            not null,
  item_id         uuid           not null,
  description     varchar(400)   null,
  quantity        numeric(20, 4) not null,
  unit_price      numeric(20, 4) not null,
  currency_code   char(3)        not null,
  line_amount     numeric(20, 4) not null,
  tax_code_id     uuid           null,
  constraint uq_task_lines_task_line_no unique (company_id, task_id, line_no)
);

create index if not exists ix_task_lines_company_task
  on task_lines (company_id, task_id, line_no);

create table if not exists task_state_transitions (
  id              bigserial      primary key,
  company_id      char(7)        not null,
  task_id         uuid           not null,
  from_status     varchar(16)    not null,
  to_status       varchar(16)    not null,
  reason          varchar(200)   null,
  actor_user_id   char(7)        not null,
  occurred_at     timestamptz    not null default now()
);

create index if not exists ix_task_state_transitions_task
  on task_state_transitions (company_id, task_id, occurred_at desc);

-- Per-company task-number sequence. Singleton row per company;
-- next_ordinal increments atomically inside the create flow.
create table if not exists tasks_company_sequence (
  company_id    char(7) primary key,
  next_ordinal  bigint  not null default 1
);

-- ============================================================
-- Batch 8: AR/AP line-table task_id columns
--
-- Per-table index strategy:
--   Tables with company_id on the line row → composite
--     (company_id, task_id) partial index.
--   Tables that isolate via parent row (expense_lines,
--     ap_purchase_order_lines) → single (task_id) partial index.
-- ============================================================

alter table if exists invoice_lines
  add column if not exists task_id uuid null;
create index if not exists ix_invoice_lines_company_task
  on invoice_lines (company_id, task_id)
  where task_id is not null;

alter table if exists credit_note_lines
  add column if not exists task_id uuid null;
create index if not exists ix_credit_note_lines_company_task
  on credit_note_lines (company_id, task_id)
  where task_id is not null;

alter table if exists bill_lines
  add column if not exists task_id uuid null;
create index if not exists ix_bill_lines_company_task
  on bill_lines (company_id, task_id)
  where task_id is not null;

alter table if exists vendor_credit_lines
  add column if not exists task_id uuid null;
create index if not exists ix_vendor_credit_lines_company_task
  on vendor_credit_lines (company_id, task_id)
  where task_id is not null;

-- expense_lines isolates via expenses.company_id (no company_id on
-- the line row) → single-column task_id index.
alter table if exists expense_lines
  add column if not exists task_id uuid null;
create index if not exists ix_expense_lines_task
  on expense_lines (task_id)
  where task_id is not null;

-- ap_purchase_order_lines isolates via ap_purchase_orders.company_id.
alter table if exists ap_purchase_order_lines
  add column if not exists task_id uuid null;
create index if not exists ix_ap_purchase_order_lines_task
  on ap_purchase_order_lines (task_id)
  where task_id is not null;
