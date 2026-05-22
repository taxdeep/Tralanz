-- =====================================================================
-- M3: inventory_documents.status — add `voided` + `reversed` terminal states
-- =====================================================================
--
-- AUDIT_2026-05-20 M3 flagged that the inventory_documents status
-- machine has no terminal void / reverse states, even though the table
-- already carries a `reversed_at timestamptz` column (added in
-- P0-2 / C2 for the invoice-reverse compensation idempotency marker).
-- The reverse path stamps `reversed_at` but cannot transition status
-- because the existing CHECK constraint forbids it.
--
-- This migration extends the CHECK to allow the two new states. No
-- behavior change here — no current code writes 'voided' or 'reversed'
-- to this table. The schema readiness lets a future PR change the
-- inventory reverse / void paths to also transition status (today
-- callers infer "reversed" from `reversed_at IS NOT NULL`).
--
-- Safety: the new value list is a strict SUPERSET of the previous one,
-- so no existing data can violate it. The CHECK is re-installed via a
-- drop-and-recreate dance so re-runs of this migration are idempotent.
-- =====================================================================

begin;

do $$
begin
  if exists (
    select 1 from pg_constraint where conname = 'ck_inventory_documents_status'
  ) then
    alter table inventory_documents
      drop constraint ck_inventory_documents_status;
  end if;
end$$;

alter table inventory_documents
  add constraint ck_inventory_documents_status
    check (status in (
      'draft',
      'submitted',
      'posted',
      'cancelled',
      'shipped',
      'received',
      'voided',
      'reversed'
    ));

commit;
