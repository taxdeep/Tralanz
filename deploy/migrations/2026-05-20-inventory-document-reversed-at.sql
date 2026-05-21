-- =============================================================================
-- P0-2 (C2): track per-sales-issue reverse state on inventory_documents
--
-- The Invoice Reverse path (PostgresAccountingDocumentReviewRepository.
-- CompleteReverseRequestExecutionAsync) automatically emits an inventory
-- subledger compensation + a compensating COGS journal entry for every
-- posted sales-issue linked to the invoice (per business rule Q1=A).
-- `reversed_at` is the idempotency marker that lets the reverse runner
-- short-circuit on retry: if non-null, the sales-issue has already been
-- compensated and the runner skips it. Status stays `posted` because the
-- inventory document itself was never invalidated — only its outbound
-- effects were unwound for the linked invoice reverse.
--
-- Pre-flight: none. Adding a nullable column with no default is a
-- metadata-only change in Postgres and does not rewrite the table.
--
-- Idempotent: ADD COLUMN IF NOT EXISTS.
-- =============================================================================

alter table inventory_documents
  add column if not exists reversed_at timestamptz null;

create index if not exists ix_inventory_documents_company_reversed_at
  on inventory_documents (company_id, reversed_at)
  where reversed_at is not null;

-- VERSION bumped by pre-push hook.
