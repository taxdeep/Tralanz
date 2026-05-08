-- Stage-1.4 batch 3: extract PostgresReceiptGrIrSettlementPostingRepository.EnsureSchemaAsync.
-- Same 7 ALTERs applied at deploy time. The inline helper still
-- exists (cached) for fresh test databases.
--
-- Wrapped in a guard because the target table is created by
-- 2026-05-08-receipt-grir-ap-settlement-control.sql, which itself
-- only runs once GR/IR has been used. Pilot deployments without GR/IR
-- usage will see this no-op gracefully; the helper stays as the
-- on-demand fallback.

do $$
begin
  if to_regclass('receipt_grir_ap_settlement_batches') is not null then
    alter table receipt_grir_ap_settlement_batches
      add column if not exists journal_status text not null default 'not_posted';

  alter table receipt_grir_ap_settlement_batches
    add column if not exists journal_entry_id uuid null references journal_entries(id) on delete set null;

  alter table receipt_grir_ap_settlement_batches
    add column if not exists journal_entry_display_number text null;

  alter table receipt_grir_ap_settlement_batches
    add column if not exists journal_posted_by_user_id char(7) null;

  alter table receipt_grir_ap_settlement_batches
    add column if not exists journal_posted_at timestamptz null;

  alter table receipt_grir_ap_settlement_batches
    add column if not exists journal_refreshed_at timestamptz null;

    alter table receipt_grir_ap_settlement_batches
      add column if not exists journal_blocked_reason_code text null;
  end if;
end $$;
