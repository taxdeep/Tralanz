-- =====================================================================
-- H6-1: Task partial billing — schema foundation
-- =====================================================================
--
-- This migration is the schema half of H6 (Q4=B confirmed: partial
-- billing on the roadmap). It does NOT change behavior on its own;
-- the application logic that writes / reads the new columns ships in
-- H6-2 (forward path) and H6-3 (reverse path).
--
-- Three pieces:
--   1. `task_lines` gets four new columns to track per-line billing
--      via a polymorphic (source_type, source_id, source_line_id)
--      tuple — same pattern as `journal_entries.source_*`. The
--      polymorphic shape lets us bill task lines from either an AR
--      Invoice or a Sales Receipt (D8) without per-document columns.
--   2. Eight AR/AP line tables get `task_line_id uuid null` with a
--      FK back to `task_lines(id)`:
--        - The six H-4 tables (invoice/credit_note/bill/vendor_credit/
--          expense/ap_po) already have task_id pointing to the task
--          HEADER. The new task_line_id pins to the specific line so
--          partial billing knows which line was covered.
--        - `sales_receipt_lines` and `refund_receipt_lines` get both
--          task_id and task_line_id (they had neither before — these
--          two doc types weren't in the H-4 batch).
--   3. Backfill: every task currently in `billed` status gets a one-
--      shot UPDATE that copies the header `billed_invoice_id` to each
--      task_line as `(billed_source_type='invoice', billed_source_id=
--      tasks.billed_invoice_id, billed_source_line_id=NULL, billed_at=
--      tasks.billed_at)`. After H6-2 ships, the application will
--      always write line_id too; legacy backfilled rows leave it NULL,
--      which is fine because the only consumer that needs line-level
--      precision (credit-note partial rollback) won't apply to data
--      that pre-dates this migration.
-- =====================================================================

begin;

-- ---------------------------------------------------------------------
-- 1. Add the four new billing-state columns to task_lines.
--    Polymorphic source shape: source_type is the discriminator,
--    source_id is the parent doc, source_line_id is the leaf line.
-- ---------------------------------------------------------------------
alter table task_lines
  add column if not exists billed_source_type    varchar(32)  null,
  add column if not exists billed_source_id      uuid         null,
  add column if not exists billed_source_line_id uuid         null,
  add column if not exists billed_at             timestamptz  null;

do $$ begin
  if not exists (
    select 1 from pg_constraint where conname = 'task_lines_billed_source_type_chk'
  ) then
    alter table task_lines
      add constraint task_lines_billed_source_type_chk
        check (
          billed_source_type is null
          or billed_source_type in ('invoice', 'sales_receipt')
        );
  end if;
end$$;

-- Composite partial index so the H6-2 status recompute (`select count
-- where billed_source_id is null`) is a single index scan per task.
create index if not exists ix_task_lines_billing_state
  on task_lines (task_id)
  where billed_source_id is null;

-- ---------------------------------------------------------------------
-- 2a. The six H-4 line tables: add `task_line_id uuid null` plus a FK
--     to `task_lines(id)`. The pattern mirrors H-4 (deferrable +
--     initially deferred so void+delete in one tx works).
-- ---------------------------------------------------------------------
alter table invoice_lines           add column if not exists task_line_id uuid null;
alter table credit_note_lines       add column if not exists task_line_id uuid null;
alter table bill_lines              add column if not exists task_line_id uuid null;
alter table vendor_credit_lines     add column if not exists task_line_id uuid null;
alter table expense_lines           add column if not exists task_line_id uuid null;
alter table ap_purchase_order_lines add column if not exists task_line_id uuid null;

-- ---------------------------------------------------------------------
-- 2b. D8: sales_receipt_lines + refund_receipt_lines weren't in the
--     H-4 task_id batch. Add BOTH task_id (header) and task_line_id
--     (leaf) so Sales Receipt / Refund Receipt can mark task lines
--     billed the same way Invoice / Credit Note do.
-- ---------------------------------------------------------------------
alter table sales_receipt_lines
  add column if not exists task_id      uuid null,
  add column if not exists task_line_id uuid null;

alter table refund_receipt_lines
  add column if not exists task_id      uuid null,
  add column if not exists task_line_id uuid null;

-- ---------------------------------------------------------------------
-- 2c. Pre-flight: orphan check before we install task_line_id FKs.
-- ---------------------------------------------------------------------
do $$
declare
  bad text;
begin
  select string_agg(format('%s=%s', table_name, cnt), '; ')
    into bad
    from (
      select 'invoice_lines'           as table_name, count(*)::int as cnt
        from invoice_lines il
       where il.task_line_id is not null
         and not exists (select 1 from task_lines tl where tl.id = il.task_line_id)
      union all
      select 'credit_note_lines', count(*)::int
        from credit_note_lines cnl
       where cnl.task_line_id is not null
         and not exists (select 1 from task_lines tl where tl.id = cnl.task_line_id)
      union all
      select 'bill_lines', count(*)::int
        from bill_lines bl
       where bl.task_line_id is not null
         and not exists (select 1 from task_lines tl where tl.id = bl.task_line_id)
      union all
      select 'vendor_credit_lines', count(*)::int
        from vendor_credit_lines vcl
       where vcl.task_line_id is not null
         and not exists (select 1 from task_lines tl where tl.id = vcl.task_line_id)
      union all
      select 'expense_lines', count(*)::int
        from expense_lines el
       where el.task_line_id is not null
         and not exists (select 1 from task_lines tl where tl.id = el.task_line_id)
      union all
      select 'ap_purchase_order_lines', count(*)::int
        from ap_purchase_order_lines pol
       where pol.task_line_id is not null
         and not exists (select 1 from task_lines tl where tl.id = pol.task_line_id)
      union all
      select 'sales_receipt_lines', count(*)::int
        from sales_receipt_lines srl
       where srl.task_line_id is not null
         and not exists (select 1 from task_lines tl where tl.id = srl.task_line_id)
      union all
      select 'refund_receipt_lines', count(*)::int
        from refund_receipt_lines rrl
       where rrl.task_line_id is not null
         and not exists (select 1 from task_lines tl where tl.id = rrl.task_line_id)
    ) src
   where cnt > 0;

  if bad is not null then
    raise exception
      'Cannot add task_line_id foreign keys: orphan references found (%). Clean up before re-running.', bad;
  end if;
end$$;

-- ---------------------------------------------------------------------
-- 2d. Install the eight task_line_id FKs (idempotent via pg_constraint
--     probe). RESTRICT prevents hard-deleting a task line while live
--     invoice / receipt lines still reference it.
-- ---------------------------------------------------------------------
do $$ begin
  if not exists (select 1 from pg_constraint where conname = 'fk_invoice_lines_task_line_id') then
    alter table invoice_lines
      add constraint fk_invoice_lines_task_line_id
      foreign key (task_line_id) references task_lines(id)
      on delete restrict deferrable initially deferred;
  end if;
end$$;

do $$ begin
  if not exists (select 1 from pg_constraint where conname = 'fk_credit_note_lines_task_line_id') then
    alter table credit_note_lines
      add constraint fk_credit_note_lines_task_line_id
      foreign key (task_line_id) references task_lines(id)
      on delete restrict deferrable initially deferred;
  end if;
end$$;

do $$ begin
  if not exists (select 1 from pg_constraint where conname = 'fk_bill_lines_task_line_id') then
    alter table bill_lines
      add constraint fk_bill_lines_task_line_id
      foreign key (task_line_id) references task_lines(id)
      on delete restrict deferrable initially deferred;
  end if;
end$$;

do $$ begin
  if not exists (select 1 from pg_constraint where conname = 'fk_vendor_credit_lines_task_line_id') then
    alter table vendor_credit_lines
      add constraint fk_vendor_credit_lines_task_line_id
      foreign key (task_line_id) references task_lines(id)
      on delete restrict deferrable initially deferred;
  end if;
end$$;

do $$ begin
  if not exists (select 1 from pg_constraint where conname = 'fk_expense_lines_task_line_id') then
    alter table expense_lines
      add constraint fk_expense_lines_task_line_id
      foreign key (task_line_id) references task_lines(id)
      on delete restrict deferrable initially deferred;
  end if;
end$$;

do $$ begin
  if not exists (select 1 from pg_constraint where conname = 'fk_ap_purchase_order_lines_task_line_id') then
    alter table ap_purchase_order_lines
      add constraint fk_ap_purchase_order_lines_task_line_id
      foreign key (task_line_id) references task_lines(id)
      on delete restrict deferrable initially deferred;
  end if;
end$$;

do $$ begin
  if not exists (select 1 from pg_constraint where conname = 'fk_sales_receipt_lines_task_id') then
    alter table sales_receipt_lines
      add constraint fk_sales_receipt_lines_task_id
      foreign key (task_id) references tasks(id)
      on delete restrict deferrable initially deferred;
  end if;
end$$;

do $$ begin
  if not exists (select 1 from pg_constraint where conname = 'fk_sales_receipt_lines_task_line_id') then
    alter table sales_receipt_lines
      add constraint fk_sales_receipt_lines_task_line_id
      foreign key (task_line_id) references task_lines(id)
      on delete restrict deferrable initially deferred;
  end if;
end$$;

do $$ begin
  if not exists (select 1 from pg_constraint where conname = 'fk_refund_receipt_lines_task_id') then
    alter table refund_receipt_lines
      add constraint fk_refund_receipt_lines_task_id
      foreign key (task_id) references tasks(id)
      on delete restrict deferrable initially deferred;
  end if;
end$$;

do $$ begin
  if not exists (select 1 from pg_constraint where conname = 'fk_refund_receipt_lines_task_line_id') then
    alter table refund_receipt_lines
      add constraint fk_refund_receipt_lines_task_line_id
      foreign key (task_line_id) references task_lines(id)
      on delete restrict deferrable initially deferred;
  end if;
end$$;

-- ---------------------------------------------------------------------
-- 2e. Partial indexes on the new task_line_id columns so the margin
--     report's per-line aggregation (H6-4) stays index-served.
-- ---------------------------------------------------------------------
create index if not exists ix_invoice_lines_task_line_id
  on invoice_lines (task_line_id) where task_line_id is not null;
create index if not exists ix_credit_note_lines_task_line_id
  on credit_note_lines (task_line_id) where task_line_id is not null;
create index if not exists ix_sales_receipt_lines_task_line_id
  on sales_receipt_lines (task_line_id) where task_line_id is not null;
create index if not exists ix_refund_receipt_lines_task_line_id
  on refund_receipt_lines (task_line_id) where task_line_id is not null;

-- ---------------------------------------------------------------------
-- 3. Backfill (D6): every task currently in `billed` status had its
--    entire line set billed by tasks.billed_invoice_id. Stamp each
--    task_line so H6-2's "all lines billed?" check stays consistent
--    for legacy rows.
--
--    billed_source_line_id stays NULL — we don't know the precise
--    invoice_line each task_line maps to (1-N maps were collapsed
--    historically). The H6-2 status recompute uses billed_source_id
--    presence as its truth signal, not source_line_id, so NULL there
--    is fine.
-- ---------------------------------------------------------------------
update task_lines tl
   set billed_source_type = 'invoice',
       billed_source_id   = t.billed_invoice_id,
       billed_at          = t.billed_at
  from tasks t
 where tl.task_id = t.id
   and t.status = 'billed'
   and t.billed_invoice_id is not null
   and tl.billed_source_id is null;

commit;
