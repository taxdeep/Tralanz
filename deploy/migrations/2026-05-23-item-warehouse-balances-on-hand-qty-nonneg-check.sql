-- =====================================================================
-- X-2.2 (AUDIT_2026-05-23 M-N2): CHECK (on_hand_qty >= 0) defense-in-depth
-- =====================================================================
--
-- Background. P0-4 (commit 27255f6) added `SELECT FOR UPDATE` on
-- item_warehouse_balances reads in PostgreSqlInventoryIssueStore /
-- PostgreSqlInventoryShipmentStore so two concurrent sales-issues
-- against the same (item, warehouse) can no longer both pass the
-- pre-write "do we have enough stock?" guard. That fix closed the race
-- at the application layer.
--
-- The audit re-verification on 2026-05-23 flagged the missing
-- database-side guard: any future code path that writes to
-- item_warehouse_balances without going through the issue / shipment
-- stores (a maintenance script, a future "stock import" feature, a
-- developer one-off, a botched migration) could still drive on_hand_qty
-- below zero. Once below zero, the FIFO cost layer accounting reads
-- garbage and the inventory subledger no longer matches the GL
-- Inventory Asset account — and the only signal is a manual
-- reconciliation report nobody runs.
--
-- This migration adds the missing constraint. It runs at every fresh
-- deploy and is idempotent (DO block checks pg_constraint before
-- adding). If the table already has rows that violate the constraint
-- the migration fails fast — that's the desired behavior; bad data
-- must be fixed before the constraint is allowed in.
--
-- We intentionally do NOT add CHECK constraints on reserved_qty /
-- in_transit_out_qty / in_transit_in_qty in this PR. The audit only
-- flagged on_hand_qty as a confirmed risk; those other columns would
-- be a separate analysis (reserved can transiently exceed on_hand in
-- some draft / unposted SO scenarios, depending on workflow). Keeping
-- this change minimal.
-- =====================================================================

begin;

do $$
begin
  if not exists (
    select 1
    from pg_constraint
    where conname = 'chk_item_warehouse_balances_on_hand_qty_nonneg'
      and conrelid = 'public.item_warehouse_balances'::regclass
  ) then
    alter table item_warehouse_balances
      add constraint chk_item_warehouse_balances_on_hand_qty_nonneg
      check (on_hand_qty >= 0);
  end if;
end$$;

commit;
