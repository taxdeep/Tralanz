-- =====================================================================
-- H-4: task_id foreign keys on AR / AP line tables
-- =====================================================================
--
-- PostgresTaskLinkSchemaInitializer (Batch 8) added the bare
-- `task_id uuid null` columns + partial indexes on six line tables.
-- It did NOT add the FK to tasks(id) because the initializer runs as
-- the citus_app user which has DML-only perms (no DDL on
-- postgres-owned tables).
--
-- Without the FK, a Task can be hard-deleted while live
-- invoice_lines / bill_lines / credit_note_lines / vendor_credit_lines
-- / expense_lines / ap_purchase_order_lines rows still hold its
-- GUID — silently orphaning the margin report and the per-task
-- related-documents rollup.
--
-- Spec called for `on delete restrict deferrable initially deferred`
-- so:
--   * ON DELETE RESTRICT: cannot delete a Task that any line still
--     references. Operators see a clear FK violation instead of
--     silent data drift.
--   * DEFERRABLE INITIALLY DEFERRED: check happens at COMMIT, so a
--     transaction that voids a line + soft-deletes a Task in
--     sequence can resolve mid-transaction without ordering issues.
--
-- Pre-flight: detect any existing orphan task_id values that would
-- block the FK creation, and abort loudly with a diagnostic so the
-- operator can clean up before re-running.
-- =====================================================================

begin;

-- ---------------------------------------------------------------------
-- Pre-flight: orphan detection
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
       where il.task_id is not null
         and not exists (select 1 from tasks t where t.id = il.task_id)
      union all
      select 'credit_note_lines',     count(*)::int
        from credit_note_lines cnl
       where cnl.task_id is not null
         and not exists (select 1 from tasks t where t.id = cnl.task_id)
      union all
      select 'bill_lines',            count(*)::int
        from bill_lines bl
       where bl.task_id is not null
         and not exists (select 1 from tasks t where t.id = bl.task_id)
      union all
      select 'vendor_credit_lines',   count(*)::int
        from vendor_credit_lines vcl
       where vcl.task_id is not null
         and not exists (select 1 from tasks t where t.id = vcl.task_id)
      union all
      select 'expense_lines',         count(*)::int
        from expense_lines el
       where el.task_id is not null
         and not exists (select 1 from tasks t where t.id = el.task_id)
      union all
      select 'ap_purchase_order_lines', count(*)::int
        from ap_purchase_order_lines pol
       where pol.task_id is not null
         and not exists (select 1 from tasks t where t.id = pol.task_id)
    ) src
   where cnt > 0;

  if bad is not null then
    raise exception
      'Cannot add task_id foreign keys: orphan references found (%). Clean up before re-running.', bad;
  end if;
end$$;

-- ---------------------------------------------------------------------
-- Add the six foreign keys (DO blocks check pg_constraint for
-- idempotency — Postgres ADD CONSTRAINT does not natively support
-- IF NOT EXISTS).
-- ---------------------------------------------------------------------

do $$ begin
  if not exists (select 1 from pg_constraint where conname = 'fk_invoice_lines_task_id') then
    alter table invoice_lines
      add constraint fk_invoice_lines_task_id
      foreign key (task_id) references tasks(id)
      on delete restrict deferrable initially deferred;
  end if;
end$$;

do $$ begin
  if not exists (select 1 from pg_constraint where conname = 'fk_credit_note_lines_task_id') then
    alter table credit_note_lines
      add constraint fk_credit_note_lines_task_id
      foreign key (task_id) references tasks(id)
      on delete restrict deferrable initially deferred;
  end if;
end$$;

do $$ begin
  if not exists (select 1 from pg_constraint where conname = 'fk_bill_lines_task_id') then
    alter table bill_lines
      add constraint fk_bill_lines_task_id
      foreign key (task_id) references tasks(id)
      on delete restrict deferrable initially deferred;
  end if;
end$$;

do $$ begin
  if not exists (select 1 from pg_constraint where conname = 'fk_vendor_credit_lines_task_id') then
    alter table vendor_credit_lines
      add constraint fk_vendor_credit_lines_task_id
      foreign key (task_id) references tasks(id)
      on delete restrict deferrable initially deferred;
  end if;
end$$;

do $$ begin
  if not exists (select 1 from pg_constraint where conname = 'fk_expense_lines_task_id') then
    alter table expense_lines
      add constraint fk_expense_lines_task_id
      foreign key (task_id) references tasks(id)
      on delete restrict deferrable initially deferred;
  end if;
end$$;

do $$ begin
  if not exists (select 1 from pg_constraint where conname = 'fk_ap_purchase_order_lines_task_id') then
    alter table ap_purchase_order_lines
      add constraint fk_ap_purchase_order_lines_task_id
      foreign key (task_id) references tasks(id)
      on delete restrict deferrable initially deferred;
  end if;
end$$;

commit;
