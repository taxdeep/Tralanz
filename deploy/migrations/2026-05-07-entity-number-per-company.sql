-- Per-company entity_number scope migration.
--
-- Before this migration: every audit-numbered table had UNIQUE(entity_number)
-- as a global constraint, conflicting with the design memo's
-- "entity_number is per-company audit-only" rule. The constraint forced
-- a single platform-wide numbering namespace and made it impossible for
-- two companies to each issue their own EN+YYYY+5base36 series.
--
-- This migration widens UNIQUE(entity_number) to UNIQUE(company_id,
-- entity_number) on every per-company-scoped table. The companies table
-- itself stays globally unique because it has no company_id (it IS the
-- scope root).
--
-- Idempotent: re-running drops constraints conditionally and creates
-- indexes with IF NOT EXISTS, so the script can be applied to a partially
-- migrated database without fallout.

BEGIN;

-- accounts (had two redundant indexes)
ALTER TABLE accounts DROP CONSTRAINT IF EXISTS accounts_entity_number_key;
DROP INDEX IF EXISTS accounts_entity_number_key;
DROP INDEX IF EXISTS uq_accounts_entity_number;
CREATE UNIQUE INDEX IF NOT EXISTS uq_accounts_company_entity_number
  ON accounts (company_id, entity_number);

-- customers (had two redundant indexes)
ALTER TABLE customers DROP CONSTRAINT IF EXISTS customers_entity_number_key;
DROP INDEX IF EXISTS customers_entity_number_key;
DROP INDEX IF EXISTS uq_customers_entity_number;
CREATE UNIQUE INDEX IF NOT EXISTS uq_customers_company_entity_number
  ON customers (company_id, entity_number);

-- vendors (had two redundant indexes)
ALTER TABLE vendors DROP CONSTRAINT IF EXISTS vendors_entity_number_key;
DROP INDEX IF EXISTS vendors_entity_number_key;
DROP INDEX IF EXISTS uq_vendors_entity_number;
CREATE UNIQUE INDEX IF NOT EXISTS uq_vendors_company_entity_number
  ON vendors (company_id, entity_number);

-- tax_codes (had two redundant indexes)
ALTER TABLE tax_codes DROP CONSTRAINT IF EXISTS tax_codes_entity_number_key;
DROP INDEX IF EXISTS tax_codes_entity_number_key;
DROP INDEX IF EXISTS uq_tax_codes_entity_number;
CREATE UNIQUE INDEX IF NOT EXISTS uq_tax_codes_company_entity_number
  ON tax_codes (company_id, entity_number);

-- Single-index tables (the standard pattern: column-inline UNIQUE)
ALTER TABLE bank_deposits DROP CONSTRAINT IF EXISTS bank_deposits_entity_number_key;
CREATE UNIQUE INDEX IF NOT EXISTS uq_bank_deposits_company_entity_number
  ON bank_deposits (company_id, entity_number);

ALTER TABLE bank_transfers DROP CONSTRAINT IF EXISTS bank_transfers_entity_number_key;
CREATE UNIQUE INDEX IF NOT EXISTS uq_bank_transfers_company_entity_number
  ON bank_transfers (company_id, entity_number);

ALTER TABLE bills DROP CONSTRAINT IF EXISTS bills_entity_number_key;
CREATE UNIQUE INDEX IF NOT EXISTS uq_bills_company_entity_number
  ON bills (company_id, entity_number);

ALTER TABLE credit_applications DROP CONSTRAINT IF EXISTS credit_applications_entity_number_key;
CREATE UNIQUE INDEX IF NOT EXISTS uq_credit_applications_company_entity_number
  ON credit_applications (company_id, entity_number);

ALTER TABLE credit_notes DROP CONSTRAINT IF EXISTS credit_notes_entity_number_key;
CREATE UNIQUE INDEX IF NOT EXISTS uq_credit_notes_company_entity_number
  ON credit_notes (company_id, entity_number);

ALTER TABLE customer_deposits DROP CONSTRAINT IF EXISTS customer_deposits_entity_number_key;
CREATE UNIQUE INDEX IF NOT EXISTS uq_customer_deposits_company_entity_number
  ON customer_deposits (company_id, entity_number);

ALTER TABLE fx_revaluation_batches DROP CONSTRAINT IF EXISTS fx_revaluation_batches_entity_number_key;
CREATE UNIQUE INDEX IF NOT EXISTS uq_fx_revaluation_batches_company_entity_number
  ON fx_revaluation_batches (company_id, entity_number);

ALTER TABLE invoices DROP CONSTRAINT IF EXISTS invoices_entity_number_key;
CREATE UNIQUE INDEX IF NOT EXISTS uq_invoices_company_entity_number
  ON invoices (company_id, entity_number);

ALTER TABLE journal_entries DROP CONSTRAINT IF EXISTS journal_entries_entity_number_key;
CREATE UNIQUE INDEX IF NOT EXISTS uq_journal_entries_company_entity_number
  ON journal_entries (company_id, entity_number);

ALTER TABLE manual_journal_documents DROP CONSTRAINT IF EXISTS manual_journal_documents_entity_number_key;
CREATE UNIQUE INDEX IF NOT EXISTS uq_manual_journal_documents_company_entity_number
  ON manual_journal_documents (company_id, entity_number);

ALTER TABLE pay_bills DROP CONSTRAINT IF EXISTS pay_bills_entity_number_key;
CREATE UNIQUE INDEX IF NOT EXISTS uq_pay_bills_company_entity_number
  ON pay_bills (company_id, entity_number);

ALTER TABLE receive_payments DROP CONSTRAINT IF EXISTS receive_payments_entity_number_key;
CREATE UNIQUE INDEX IF NOT EXISTS uq_receive_payments_company_entity_number
  ON receive_payments (company_id, entity_number);

ALTER TABLE refund_receipts DROP CONSTRAINT IF EXISTS refund_receipts_entity_number_key;
CREATE UNIQUE INDEX IF NOT EXISTS uq_refund_receipts_company_entity_number
  ON refund_receipts (company_id, entity_number);

ALTER TABLE sales_receipts DROP CONSTRAINT IF EXISTS sales_receipts_entity_number_key;
CREATE UNIQUE INDEX IF NOT EXISTS uq_sales_receipts_company_entity_number
  ON sales_receipts (company_id, entity_number);

ALTER TABLE tax_returns DROP CONSTRAINT IF EXISTS tax_returns_entity_number_key;
CREATE UNIQUE INDEX IF NOT EXISTS uq_tax_returns_company_entity_number
  ON tax_returns (company_id, entity_number);

ALTER TABLE vendor_credit_applications DROP CONSTRAINT IF EXISTS vendor_credit_applications_entity_number_key;
CREATE UNIQUE INDEX IF NOT EXISTS uq_vendor_credit_applications_company_entity_number
  ON vendor_credit_applications (company_id, entity_number);

ALTER TABLE vendor_credits DROP CONSTRAINT IF EXISTS vendor_credits_entity_number_key;
CREATE UNIQUE INDEX IF NOT EXISTS uq_vendor_credits_company_entity_number
  ON vendor_credits (company_id, entity_number);

-- companies.entity_number stays globally unique by design (no company_id
-- column to scope by; the table is the scope root). Intentionally not
-- touched by this migration.

-- Ensure the per-company sequence table the new allocator targets exists.
-- The PostgreSqlIdentitySchemaBootstrap helper also auto-creates it on
-- first use, but defining it here keeps fresh-install paths consistent
-- regardless of which bootstrap path runs first.
CREATE TABLE IF NOT EXISTS company_entity_number_sequences (
  company_id char(7) NOT NULL,
  entity_year integer NOT NULL,
  next_ordinal bigint NOT NULL,
  PRIMARY KEY (company_id, entity_year)
);

COMMIT;
