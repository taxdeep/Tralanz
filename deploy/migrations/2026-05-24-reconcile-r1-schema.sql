-- =====================================================================
-- R-1 (BANKING_RECONCILE_PLAN.md): Banking Reconcile V1 schema foundation
-- =====================================================================
--
-- This migration is the schema half of phase R-1 from
-- BANKING_RECONCILE_PLAN.md. It introduces:
--
--   1. Draft / completed / abandoned lifecycle on bank_reconciliations.
--      Until now the table only carried status='completed'; the new
--      draft state lets operators mark cleared lines, leave, and
--      resume later.
--
--   2. Line-level reconciliation tracking on ledger_entries via two
--      new FK columns (reconciliation_draft_id + reconciliation_id)
--      with an XOR CHECK so a row is either unreconciled, drafted,
--      or completed — never two at once. This is the Q1=A decision
--      (line-level state, not header-level), motivated by Bank
--      Transfer JEs whose two ledger lines belong to two different
--      bank accounts and therefore two different reconciliation
--      cycles.
--
--   3. An AFTER UPDATE trigger on ledger_entries that enforces
--      immutability of the financial fields (debit, credit, tx_debit,
--      tx_credit, posting_date, account_id) whenever
--      reconciliation_id IS NOT NULL. memo / ref_no / posting_role
--      stay editable. JE Void and JE Reverse emit a NEW compensating
--      JE rather than mutating the original, so this trigger does
--      not block those workflows.
--
--   4. A backfill that copies the existing
--      bank_reconciliation_lines.ledger_entry_id snapshots into the
--      new ledger_entries.reconciliation_id column. Existing
--      production data continues to "feel reconciled" with no
--      operator intervention.
--
-- All statements are idempotent (IF NOT EXISTS / DO blocks /
-- conditional CONSTRAINT drops). Re-running the migration is safe.
--
-- This migration LANDS the schema. The application layer that
-- consumes it (draft endpoints, store methods, undo) lands in the
-- same R-1 PR but in C# code.
-- =====================================================================

begin;

-- ---------------------------------------------------------------------
-- (1) bank_reconciliations: lifecycle states + nullable completion +
--     draft uniqueness + audit columns
-- ---------------------------------------------------------------------

-- Status enum: extend from {completed} to {in_progress, completed,
-- abandoned}. Drop the old constraint first; the new one accepts the
-- old value so existing rows survive.
alter table bank_reconciliations
  drop constraint if exists bank_reconciliations_status_chk;
alter table bank_reconciliations
  add constraint bank_reconciliations_status_chk
  check (status in ('in_progress', 'completed', 'abandoned'));

-- The existing line_count > 0 CHECK forces drafts (which start at
-- 0 lines) to fail. Make it status-conditional: only completed
-- reconciliations must have at least one line.
alter table bank_reconciliations
  drop constraint if exists bank_reconciliations_line_count_chk;
alter table bank_reconciliations
  add constraint bank_reconciliations_line_count_chk
  check (status <> 'completed' or line_count > 0);

-- Same for the zero-difference CHECK. Drafts can have non-zero
-- difference (that's why operators Save for later in the first
-- place). Completed must still be < 0.005.
alter table bank_reconciliations
  drop constraint if exists bank_reconciliations_zero_difference_chk;
alter table bank_reconciliations
  add constraint bank_reconciliations_zero_difference_chk
  check (status <> 'completed' or abs(difference) < 0.005);

-- The completion-only unique index over (company_id, bank_account_id,
-- statement_date) WHERE status='completed' was already present; we
-- keep it. It prevents two completed reconciliations for the same
-- account + same statement date.

-- completed_by_user_id and completed_at were NOT NULL. A draft has
-- no completed_by_user_id and no completed_at. Relax both to
-- nullable; we add a status-conditional CHECK to keep the invariant
-- "completed rows MUST have these fields set".
alter table bank_reconciliations
  alter column completed_by_user_id drop not null;
alter table bank_reconciliations
  alter column completed_at drop not null;
alter table bank_reconciliations
  drop constraint if exists bank_reconciliations_completed_fields_chk;
alter table bank_reconciliations
  add constraint bank_reconciliations_completed_fields_chk
  check (
    status <> 'completed'
    or (completed_by_user_id is not null and completed_at is not null)
  );

-- New columns: draft author + last touch + abandon audit.
alter table bank_reconciliations
  add column if not exists created_by_user_id char(7)
    references users(id) on delete restrict;
alter table bank_reconciliations
  add column if not exists last_modified_at timestamptz;
alter table bank_reconciliations
  add column if not exists abandoned_at timestamptz;
alter table bank_reconciliations
  add column if not exists abandoned_by_user_id char(7)
    references users(id) on delete restrict;

-- Backfill created_by_user_id from completed_by_user_id for existing
-- rows. Going forward the application sets created_by_user_id at
-- draft open. Make the column NOT NULL after backfill so it's
-- compulsory.
update bank_reconciliations
   set created_by_user_id = completed_by_user_id
 where created_by_user_id is null;
alter table bank_reconciliations
  alter column created_by_user_id set not null;

-- Backfill last_modified_at for existing rows so the column is
-- meaningful before R-3 starts writing to it. Use completed_at as a
-- reasonable proxy.
update bank_reconciliations
   set last_modified_at = completed_at
 where last_modified_at is null
   and completed_at is not null;
update bank_reconciliations
   set last_modified_at = created_at
 where last_modified_at is null;
alter table bank_reconciliations
  alter column last_modified_at set not null;
alter table bank_reconciliations
  alter column last_modified_at set default now();

-- Abandon audit: completed_fields_chk-style mirror. If status =
-- 'abandoned' then both fields must be set; otherwise either may be
-- null.
alter table bank_reconciliations
  drop constraint if exists bank_reconciliations_abandoned_fields_chk;
alter table bank_reconciliations
  add constraint bank_reconciliations_abandoned_fields_chk
  check (
    status <> 'abandoned'
    or (abandoned_by_user_id is not null and abandoned_at is not null)
  );

-- One draft per (company_id, bank_account_id). This is the DB-level
-- enforcement of "at most one in-flight draft per account" from
-- BANKING_RECONCILE_PLAN.md Section 6.2. Two operators racing to
-- open a draft both submit POST /draft; the loser gets a 23505
-- unique violation that the application surfaces as
-- "draft_already_open_for_account".
create unique index if not exists ux_bank_reconciliations_in_progress_per_account
  on bank_reconciliations (company_id, bank_account_id)
  where status = 'in_progress';

-- ---------------------------------------------------------------------
-- (2) ledger_entries: two new FK columns + XOR CHECK + partial indexes
-- ---------------------------------------------------------------------

-- These two columns are the heart of the Q1=A line-level model. A
-- ledger entry is in exactly one of three states at any time:
--   - unreconciled                  (both columns NULL)
--   - in a draft reconciliation     (reconciliation_draft_id NOT NULL)
--   - in a completed reconciliation (reconciliation_id NOT NULL)
-- The XOR CHECK rejects the impossible fourth state.

alter table ledger_entries
  add column if not exists reconciliation_draft_id uuid
    references bank_reconciliations(id) on delete set null;

alter table ledger_entries
  add column if not exists reconciliation_id uuid
    references bank_reconciliations(id) on delete restrict;

alter table ledger_entries
  drop constraint if exists chk_ledger_entries_reconciliation_xor;
alter table ledger_entries
  add constraint chk_ledger_entries_reconciliation_xor
  check (
    not (reconciliation_id is not null and reconciliation_draft_id is not null)
  );

-- Partial indexes. The first is the hot read path: "list
-- unreconciled ledger entries for account X on or before date D"
-- (Bank Register, reconcile candidate query). The current store
-- uses NOT EXISTS against bank_reconciliation_lines; R-1.3 switches
-- to the new column and this index.
create index if not exists ix_ledger_entries_company_account_unreconciled
  on ledger_entries (company_id, account_id, posting_date)
  where reconciliation_id is null;

-- Drives "show all entries in this draft" and "undo this draft"
-- (DELETE all draft-tagged rows).
create index if not exists ix_ledger_entries_reconciliation_draft
  on ledger_entries (reconciliation_draft_id)
  where reconciliation_draft_id is not null;

-- Drives "show all entries in this completed reconciliation" and
-- undo cleanup.
create index if not exists ix_ledger_entries_reconciliation_id
  on ledger_entries (reconciliation_id)
  where reconciliation_id is not null;

-- ---------------------------------------------------------------------
-- (3) AFTER UPDATE trigger: reconciled ledger entries are
--     financially immutable
-- ---------------------------------------------------------------------
--
-- Belt and suspenders to the application-layer lock predicate. The
-- application layer SHOULD refuse to UPDATE a reconciled ledger
-- entry's financial fields; this trigger guarantees the database
-- refuses too, in case a future code path forgets.
--
-- The lock fires only when reconciliation_id was already non-null
-- (i.e., the row was completed). Setting reconciliation_id (during
-- the completion workflow) and clearing it (during undo) are
-- legitimate transitions — those UPDATEs do not touch the financial
-- fields, so the trigger's inner check sees no diff and lets them
-- pass.
--
-- memo, ref_no, and posting_role live on journal_entry_lines (not
-- on ledger_entries), so this trigger does NOT block edits to
-- those. A future enhancement may add a parallel guard on
-- journal_entry_lines.
--
-- We do not block DELETE of a reconciled ledger entry at the
-- trigger level: ON DELETE RESTRICT on bank_reconciliation_lines
-- already prevents the underlying journal_entries / ledger_entries
-- row from being deleted while a completed reconciliation
-- references it. The trigger's job is to police IN-PLACE mutation.

create or replace function bank_recon_le_immutability_guard()
returns trigger as $$
begin
  -- Only fire when the row WAS reconciled before this update. If
  -- it wasn't reconciled, we don't care what fields change.
  if old.reconciliation_id is not null then
    if old.debit         <> new.debit         or
       old.credit        <> new.credit        or
       old.tx_debit      <> new.tx_debit      or
       old.tx_credit     <> new.tx_credit     or
       old.posting_date  <> new.posting_date  or
       old.account_id    <> new.account_id    then
      raise exception
        'ledger_entry % is reconciled in % and its financial fields '
        '(debit, credit, tx_debit, tx_credit, posting_date, '
        'account_id) are immutable. Undo the reconciliation first.',
        new.id, old.reconciliation_id
      using errcode = 'check_violation';
    end if;
  end if;
  return new;
end;
$$ language plpgsql;

drop trigger if exists trg_bank_recon_le_immutability on ledger_entries;
create trigger trg_bank_recon_le_immutability
  before update on ledger_entries
  for each row
  when (old.reconciliation_id is not null)
  execute function bank_recon_le_immutability_guard();

-- ---------------------------------------------------------------------
-- (4) Backfill: existing completed reconciliations populate
--     ledger_entries.reconciliation_id from the snapshot table.
-- ---------------------------------------------------------------------
--
-- Without this, a freshly-deployed R-1 would see every previously-
-- reconciled ledger entry as "unreconciled" (because the new column
-- is null). That would let an operator reconcile the same entries
-- a second time. Backfill closes that.
--
-- We only touch the completed rows. Drafts don't exist yet (the
-- column was just added). The backfill is idempotent: a re-run finds
-- nothing to update because reconciliation_id is already set.

update ledger_entries le
   set reconciliation_id = brl.reconciliation_id
  from bank_reconciliation_lines brl
 where brl.ledger_entry_id = le.id
   and le.reconciliation_id is null;

commit;
