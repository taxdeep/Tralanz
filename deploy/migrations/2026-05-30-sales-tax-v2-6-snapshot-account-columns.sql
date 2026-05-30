-- Sales Tax v2 — S5 (decision A): snapshot stores its GL accounts.
--
-- The posting fragment builder (S5.2) routes each tax leg to a GL account.
-- For posted documents to be IMMUTABLE in their routing (a later edit to a
-- tax code's / component's account must NOT change where a re-post or void
-- of a historic document lands), the account is snapshotted at write time
-- alongside the rate/code/amount that S1/S2 already snapshot.
--
-- Three nullable columns (one per leg). A component may have no account
-- configured yet; the fragment builder requires a non-null account only
-- for a leg that actually carries tax. FK is simple (accounts.id), matching
-- the existing simple FKs on this table (tax_code_id, component_id).
--
-- Backfill: not needed — document_line_sales_tax_snapshots holds only
-- draft-stage rows (they are replaced on every Save Draft) and the live DB
-- currently has 0 rows. New snapshots written by the S2 engine/persister
-- (after this migration + the matching code deploy) carry the accounts.
--
-- Idempotent (ADD COLUMN IF NOT EXISTS). Apply with postgres superuser.

alter table document_line_sales_tax_snapshots
    add column if not exists payable_account_id         uuid null references accounts(id),
    add column if not exists recoverable_account_id     uuid null references accounts(id),
    add column if not exists non_recoverable_account_id uuid null references accounts(id);
