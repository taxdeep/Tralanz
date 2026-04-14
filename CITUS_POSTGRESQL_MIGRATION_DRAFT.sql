-- Citus PostgreSQL Core Migration Draft
-- Draft target schema for moving from the current Prisma/SQLite MVP
-- toward the backend-authoritative PostgreSQL accounting architecture.
--
-- Important:
-- 1. This is a draft migration baseline, not a final production migration.
-- 2. Data backfill from the current SQLite schema still needs a dedicated mapping step.
-- 3. Current Prisma `userId` ownership semantics must be migrated into formal
--    `company_id` tenancy semantics before core posting is considered authoritative.

BEGIN;

CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE OR REPLACE FUNCTION citus_set_updated_at()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
  NEW.updated_at = NOW();
  RETURN NEW;
END;
$$;

-- ---------------------------------------------------------------------------
-- Identity and security core
-- ---------------------------------------------------------------------------

CREATE TABLE users (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  email text NOT NULL UNIQUE,
  username text UNIQUE,
  password_hash text NOT NULL,
  is_active boolean NOT NULL DEFAULT true,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW()
);

CREATE TABLE companies (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  entity_number text NOT NULL UNIQUE,
  legal_name text NOT NULL,
  base_currency_code char(3) NOT NULL,
  multi_currency_enabled boolean NOT NULL DEFAULT false,
  status text NOT NULL DEFAULT 'active',
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT companies_entity_number_format_chk CHECK (entity_number ~ '^EN[0-9]{4}[0-9]{8}$'),
  CONSTRAINT companies_status_chk CHECK (status IN ('active', 'inactive', 'suspended', 'archived'))
);

CREATE TABLE company_memberships (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  role text NOT NULL,
  is_active boolean NOT NULL DEFAULT true,
  permissions jsonb NOT NULL DEFAULT '{}'::jsonb,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT company_memberships_role_chk CHECK (role IN ('owner', 'user')),
  CONSTRAINT company_memberships_unique_member UNIQUE (company_id, user_id)
);

CREATE TABLE business_sessions (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  token_hash text NOT NULL UNIQUE,
  user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  active_company_id uuid NOT NULL REFERENCES companies(id) ON DELETE RESTRICT,
  expires_at timestamptz NOT NULL,
  created_at timestamptz NOT NULL DEFAULT NOW()
);

CREATE TABLE platform_modules (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  module_key text NOT NULL UNIQUE,
  json jsonb NOT NULL,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW()
);

CREATE TABLE platform_entities (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  entity_name text NOT NULL UNIQUE,
  module_key text NOT NULL,
  storage_table text NOT NULL,
  json jsonb NOT NULL,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_platform_entities_module_key
  ON platform_entities (module_key);

CREATE INDEX idx_platform_entities_storage_table
  ON platform_entities (storage_table);

-- ---------------------------------------------------------------------------
-- Currency and FX
-- ---------------------------------------------------------------------------

CREATE TABLE currency_catalog (
  code char(3) PRIMARY KEY,
  name text NOT NULL,
  minor_unit smallint NOT NULL,
  is_active boolean NOT NULL DEFAULT true,
  CONSTRAINT currency_catalog_minor_unit_chk CHECK (minor_unit BETWEEN 0 AND 6)
);

INSERT INTO currency_catalog (code, name, minor_unit, is_active) VALUES
  ('CAD', 'Canadian Dollar', 2, true),
  ('USD', 'US Dollar', 2, true),
  ('EUR', 'Euro', 2, true),
  ('CNY', 'Chinese Yuan', 2, true),
  ('JPY', 'Japanese Yen', 0, true),
  ('KWD', 'Kuwaiti Dinar', 3, true)
ON CONFLICT (code) DO NOTHING;

ALTER TABLE companies
  ADD CONSTRAINT companies_base_currency_fk
  FOREIGN KEY (base_currency_code) REFERENCES currency_catalog(code) ON DELETE RESTRICT;

CREATE TABLE company_currencies (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  is_enabled boolean NOT NULL DEFAULT true,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT company_currencies_unique UNIQUE (company_id, currency_code)
);

CREATE TABLE system_fx_market_rates (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  provider_key text NOT NULL,
  base_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  quote_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  market_date date NOT NULL,
  rate numeric(20,10) NOT NULL,
  fetched_at timestamptz NOT NULL DEFAULT NOW(),
  payload jsonb,
  CONSTRAINT system_fx_market_rates_positive_rate_chk CHECK (rate > 0),
  CONSTRAINT system_fx_market_rates_unique UNIQUE (
    provider_key,
    base_currency_code,
    quote_currency_code,
    market_date
  )
);

CREATE TABLE company_fx_rate_snapshots (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  base_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  quote_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  requested_date date NOT NULL,
  effective_date date NOT NULL,
  rate numeric(20,10) NOT NULL,
  provider_key text,
  row_origin text NOT NULL,
  snapshot_semantics text NOT NULL,
  system_market_rate_id uuid REFERENCES system_fx_market_rates(id) ON DELETE SET NULL,
  notes text,
  created_by_user_id uuid REFERENCES users(id) ON DELETE SET NULL,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT company_fx_rate_snapshots_positive_rate_chk CHECK (rate > 0),
  CONSTRAINT company_fx_rate_snapshots_row_origin_chk CHECK (
    row_origin IN ('manual', 'provider_fetched', 'legacy_unknown')
  ),
  CONSTRAINT company_fx_rate_snapshots_semantics_chk CHECK (
    snapshot_semantics IN ('identity', 'manual', 'company_override', 'system_stored', 'provider_fetched')
  ),
  CONSTRAINT company_fx_rate_snapshots_date_order_chk CHECK (effective_date <= requested_date)
);

CREATE UNIQUE INDEX uq_company_fx_rate_snapshots_identity
  ON company_fx_rate_snapshots (
    company_id,
    base_currency_code,
    quote_currency_code,
    requested_date,
    snapshot_semantics,
    COALESCE(provider_key, '')
  );

-- ---------------------------------------------------------------------------
-- Company settings and numbering
-- ---------------------------------------------------------------------------

CREATE TABLE company_numbering_sequences (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  scope_key text NOT NULL,
  prefix text,
  next_number bigint NOT NULL,
  padding smallint NOT NULL DEFAULT 6,
  suggestion_enabled boolean NOT NULL DEFAULT true,
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT company_numbering_sequences_next_number_chk CHECK (next_number > 0),
  CONSTRAINT company_numbering_sequences_padding_chk CHECK (padding BETWEEN 1 AND 16),
  CONSTRAINT company_numbering_sequences_unique UNIQUE (company_id, scope_key)
);

CREATE TABLE company_settings (
  company_id uuid PRIMARY KEY REFERENCES companies(id) ON DELETE CASCADE,
  profile jsonb NOT NULL DEFAULT '{}'::jsonb,
  security jsonb NOT NULL DEFAULT '{}'::jsonb,
  notification jsonb NOT NULL DEFAULT '{}'::jsonb,
  currency jsonb NOT NULL DEFAULT '{}'::jsonb,
  updated_at timestamptz NOT NULL DEFAULT NOW()
);

-- ---------------------------------------------------------------------------
-- Chart of accounts and tax
-- ---------------------------------------------------------------------------

CREATE TABLE accounts (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  entity_number text NOT NULL UNIQUE,
  code text NOT NULL,
  name text NOT NULL,
  root_type text NOT NULL,
  detail_type text NOT NULL,
  is_active boolean NOT NULL DEFAULT true,
  is_system boolean NOT NULL DEFAULT false,
  is_system_default boolean NOT NULL DEFAULT false,
  system_key text,
  system_role text,
  currency_code char(3) REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  allow_manual_posting boolean NOT NULL DEFAULT true,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT accounts_entity_number_format_chk CHECK (entity_number ~ '^EN[0-9]{4}[0-9]{8}$'),
  CONSTRAINT accounts_root_type_chk CHECK (
    root_type IN ('asset', 'liability', 'equity', 'revenue', 'cost_of_sales', 'expense')
  ),
  CONSTRAINT accounts_unique_company_code UNIQUE (company_id, code)
);

CREATE TABLE tax_codes (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  entity_number text NOT NULL UNIQUE,
  code text NOT NULL,
  name text NOT NULL,
  rate_percent numeric(9,6) NOT NULL,
  applies_to text NOT NULL DEFAULT 'both',
  is_recoverable_on_purchase boolean NOT NULL DEFAULT false,
  recoverability_mode text NOT NULL DEFAULT 'full',
  payable_account_id uuid REFERENCES accounts(id) ON DELETE SET NULL,
  recoverable_account_id uuid REFERENCES accounts(id) ON DELETE SET NULL,
  is_active boolean NOT NULL DEFAULT true,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT tax_codes_entity_number_format_chk CHECK (entity_number ~ '^EN[0-9]{4}[0-9]{8}$'),
  CONSTRAINT tax_codes_rate_percent_chk CHECK (rate_percent >= 0),
  CONSTRAINT tax_codes_applies_to_chk CHECK (applies_to IN ('sales', 'purchase', 'both')),
  CONSTRAINT tax_codes_recoverability_mode_chk CHECK (
    recoverability_mode IN ('full', 'partial', 'none')
  ),
  CONSTRAINT tax_codes_unique_company_code UNIQUE (company_id, code)
);

-- ---------------------------------------------------------------------------
-- Parties
-- ---------------------------------------------------------------------------

CREATE TABLE customers (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  entity_number text NOT NULL UNIQUE,
  display_name text NOT NULL,
  default_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  email text,
  phone text,
  address text,
  is_active boolean NOT NULL DEFAULT true,
  currency_locked boolean NOT NULL DEFAULT false,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT customers_entity_number_format_chk CHECK (entity_number ~ '^EN[0-9]{4}[0-9]{8}$')
);

CREATE TABLE vendors (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  entity_number text NOT NULL UNIQUE,
  display_name text NOT NULL,
  default_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  email text,
  phone text,
  address text,
  is_active boolean NOT NULL DEFAULT true,
  currency_locked boolean NOT NULL DEFAULT false,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT vendors_entity_number_format_chk CHECK (entity_number ~ '^EN[0-9]{4}[0-9]{8}$')
);

-- ---------------------------------------------------------------------------
-- Source documents
-- ---------------------------------------------------------------------------

CREATE TABLE invoices (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  entity_number text NOT NULL UNIQUE,
  invoice_number text NOT NULL,
  customer_id uuid NOT NULL REFERENCES customers(id) ON DELETE RESTRICT,
  status text NOT NULL DEFAULT 'draft',
  invoice_date date NOT NULL,
  due_date date NOT NULL,
  document_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  base_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  fx_rate_snapshot_id uuid REFERENCES company_fx_rate_snapshots(id) ON DELETE RESTRICT,
  fx_rate numeric(20,10) NOT NULL DEFAULT 1,
  fx_requested_date date NOT NULL,
  fx_effective_date date NOT NULL,
  fx_source text NOT NULL DEFAULT 'identity',
  subtotal_amount numeric(20,6) NOT NULL DEFAULT 0,
  tax_amount numeric(20,6) NOT NULL DEFAULT 0,
  total_amount numeric(20,6) NOT NULL DEFAULT 0,
  memo text,
  posted_at timestamptz,
  created_by_user_id uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT invoices_entity_number_format_chk CHECK (entity_number ~ '^EN[0-9]{4}[0-9]{8}$'),
  CONSTRAINT invoices_status_chk CHECK (
    status IN ('draft', 'issued', 'posted', 'partially_paid', 'paid', 'voided', 'reversed')
  ),
  CONSTRAINT invoices_fx_rate_positive_chk CHECK (fx_rate > 0),
  CONSTRAINT invoices_unique_company_invoice_number UNIQUE (company_id, invoice_number)
);

CREATE TABLE invoice_lines (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  invoice_id uuid NOT NULL REFERENCES invoices(id) ON DELETE CASCADE,
  line_number integer NOT NULL,
  revenue_account_id uuid NOT NULL REFERENCES accounts(id) ON DELETE RESTRICT,
  description text NOT NULL,
  quantity numeric(20,6) NOT NULL,
  unit_price numeric(20,6) NOT NULL,
  line_amount numeric(20,6) NOT NULL,
  tax_code_id uuid REFERENCES tax_codes(id) ON DELETE SET NULL,
  tax_amount numeric(20,6) NOT NULL DEFAULT 0,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT invoice_lines_quantity_nonnegative_chk CHECK (quantity >= 0),
  CONSTRAINT invoice_lines_unit_price_nonnegative_chk CHECK (unit_price >= 0),
  CONSTRAINT invoice_lines_unique_line UNIQUE (invoice_id, line_number)
);

CREATE TABLE credit_notes (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  entity_number text NOT NULL UNIQUE,
  credit_note_number text NOT NULL,
  customer_id uuid NOT NULL REFERENCES customers(id) ON DELETE RESTRICT,
  status text NOT NULL DEFAULT 'draft',
  credit_note_date date NOT NULL,
  due_date date NOT NULL,
  document_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  base_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  fx_rate_snapshot_id uuid REFERENCES company_fx_rate_snapshots(id) ON DELETE RESTRICT,
  fx_rate numeric(20,10) NOT NULL DEFAULT 1,
  fx_requested_date date NOT NULL,
  fx_effective_date date NOT NULL,
  fx_source text NOT NULL DEFAULT 'identity',
  subtotal_amount numeric(20,6) NOT NULL DEFAULT 0,
  tax_amount numeric(20,6) NOT NULL DEFAULT 0,
  total_amount numeric(20,6) NOT NULL DEFAULT 0,
  memo text,
  posted_at timestamptz,
  created_by_user_id uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT credit_notes_entity_number_format_chk CHECK (entity_number ~ '^EN[0-9]{4}[0-9]{8}$'),
  CONSTRAINT credit_notes_status_chk CHECK (
    status IN ('draft', 'issued', 'posted', 'partially_applied', 'applied', 'voided', 'reversed')
  ),
  CONSTRAINT credit_notes_fx_rate_positive_chk CHECK (fx_rate > 0),
  CONSTRAINT credit_notes_unique_company_credit_note_number UNIQUE (company_id, credit_note_number)
);

CREATE TABLE credit_note_lines (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  credit_note_id uuid NOT NULL REFERENCES credit_notes(id) ON DELETE CASCADE,
  line_number integer NOT NULL,
  revenue_account_id uuid NOT NULL REFERENCES accounts(id) ON DELETE RESTRICT,
  description text NOT NULL,
  quantity numeric(20,6) NOT NULL,
  unit_price numeric(20,6) NOT NULL,
  line_amount numeric(20,6) NOT NULL,
  tax_code_id uuid REFERENCES tax_codes(id) ON DELETE SET NULL,
  tax_amount numeric(20,6) NOT NULL DEFAULT 0,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT credit_note_lines_quantity_nonnegative_chk CHECK (quantity >= 0),
  CONSTRAINT credit_note_lines_unit_price_nonnegative_chk CHECK (unit_price >= 0),
  CONSTRAINT credit_note_lines_unique_line UNIQUE (credit_note_id, line_number)
);

CREATE TABLE bills (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  entity_number text NOT NULL UNIQUE,
  bill_number text NOT NULL,
  vendor_id uuid NOT NULL REFERENCES vendors(id) ON DELETE RESTRICT,
  status text NOT NULL DEFAULT 'draft',
  bill_date date NOT NULL,
  due_date date NOT NULL,
  document_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  base_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  fx_rate_snapshot_id uuid REFERENCES company_fx_rate_snapshots(id) ON DELETE RESTRICT,
  fx_rate numeric(20,10) NOT NULL DEFAULT 1,
  fx_requested_date date NOT NULL,
  fx_effective_date date NOT NULL,
  fx_source text NOT NULL DEFAULT 'identity',
  subtotal_amount numeric(20,6) NOT NULL DEFAULT 0,
  tax_amount numeric(20,6) NOT NULL DEFAULT 0,
  total_amount numeric(20,6) NOT NULL DEFAULT 0,
  memo text,
  posted_at timestamptz,
  created_by_user_id uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT bills_entity_number_format_chk CHECK (entity_number ~ '^EN[0-9]{4}[0-9]{8}$'),
  CONSTRAINT bills_status_chk CHECK (
    status IN ('draft', 'posted', 'partially_paid', 'paid', 'voided', 'reversed')
  ),
  CONSTRAINT bills_fx_rate_positive_chk CHECK (fx_rate > 0),
  CONSTRAINT bills_unique_company_bill_number UNIQUE (company_id, bill_number)
);

CREATE TABLE bill_lines (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  bill_id uuid NOT NULL REFERENCES bills(id) ON DELETE CASCADE,
  line_number integer NOT NULL,
  expense_account_id uuid NOT NULL REFERENCES accounts(id) ON DELETE RESTRICT,
  description text NOT NULL,
  line_amount numeric(20,6) NOT NULL,
  tax_code_id uuid REFERENCES tax_codes(id) ON DELETE SET NULL,
  tax_amount numeric(20,6) NOT NULL DEFAULT 0,
  is_tax_recoverable boolean NOT NULL DEFAULT false,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT bill_lines_line_amount_nonnegative_chk CHECK (line_amount >= 0),
  CONSTRAINT bill_lines_unique_line UNIQUE (bill_id, line_number)
);

CREATE TABLE vendor_credits (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  entity_number text NOT NULL UNIQUE,
  vendor_credit_number text NOT NULL,
  vendor_id uuid NOT NULL REFERENCES vendors(id) ON DELETE RESTRICT,
  status text NOT NULL DEFAULT 'draft',
  vendor_credit_date date NOT NULL,
  due_date date NOT NULL,
  document_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  base_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  fx_rate_snapshot_id uuid REFERENCES company_fx_rate_snapshots(id) ON DELETE RESTRICT,
  fx_rate numeric(20,10) NOT NULL DEFAULT 1,
  fx_requested_date date NOT NULL,
  fx_effective_date date NOT NULL,
  fx_source text NOT NULL DEFAULT 'identity',
  subtotal_amount numeric(20,6) NOT NULL DEFAULT 0,
  tax_amount numeric(20,6) NOT NULL DEFAULT 0,
  total_amount numeric(20,6) NOT NULL DEFAULT 0,
  memo text,
  posted_at timestamptz,
  created_by_user_id uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT vendor_credits_entity_number_format_chk CHECK (entity_number ~ '^EN[0-9]{4}[0-9]{8}$'),
  CONSTRAINT vendor_credits_status_chk CHECK (
    status IN ('draft', 'posted', 'partially_applied', 'applied', 'voided', 'reversed')
  ),
  CONSTRAINT vendor_credits_fx_rate_positive_chk CHECK (fx_rate > 0),
  CONSTRAINT vendor_credits_unique_company_vendor_credit_number UNIQUE (company_id, vendor_credit_number)
);

CREATE TABLE vendor_credit_lines (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  vendor_credit_id uuid NOT NULL REFERENCES vendor_credits(id) ON DELETE CASCADE,
  line_number integer NOT NULL,
  expense_account_id uuid NOT NULL REFERENCES accounts(id) ON DELETE RESTRICT,
  description text NOT NULL,
  line_amount numeric(20,6) NOT NULL,
  tax_code_id uuid REFERENCES tax_codes(id) ON DELETE SET NULL,
  tax_amount numeric(20,6) NOT NULL DEFAULT 0,
  is_tax_recoverable boolean NOT NULL DEFAULT false,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT vendor_credit_lines_line_amount_nonnegative_chk CHECK (line_amount >= 0),
  CONSTRAINT vendor_credit_lines_unique_line UNIQUE (vendor_credit_id, line_number)
);

CREATE TABLE receive_payments (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  entity_number text NOT NULL UNIQUE,
  payment_number text NOT NULL,
  customer_id uuid NOT NULL REFERENCES customers(id) ON DELETE RESTRICT,
  status text NOT NULL DEFAULT 'draft',
  payment_date date NOT NULL,
  bank_account_id uuid NOT NULL REFERENCES accounts(id) ON DELETE RESTRICT,
  document_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  base_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  fx_rate_snapshot_id uuid REFERENCES company_fx_rate_snapshots(id) ON DELETE RESTRICT,
  fx_rate numeric(20,10) NOT NULL DEFAULT 1,
  fx_requested_date date NOT NULL,
  fx_effective_date date NOT NULL,
  fx_source text NOT NULL DEFAULT 'identity',
  total_amount numeric(20,6) NOT NULL DEFAULT 0,
  memo text,
  posted_at timestamptz,
  created_by_user_id uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT receive_payments_entity_number_format_chk CHECK (entity_number ~ '^EN[0-9]{4}[0-9]{8}$'),
  CONSTRAINT receive_payments_status_chk CHECK (status IN ('draft', 'posted', 'voided', 'reversed')),
  CONSTRAINT receive_payments_fx_rate_positive_chk CHECK (fx_rate > 0),
  CONSTRAINT receive_payments_total_amount_nonnegative_chk CHECK (total_amount >= 0),
  CONSTRAINT receive_payments_unique_company_payment_number UNIQUE (company_id, payment_number)
);

CREATE TABLE receive_payment_lines (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  receive_payment_id uuid NOT NULL REFERENCES receive_payments(id) ON DELETE CASCADE,
  line_number integer NOT NULL,
  target_ar_open_item_id uuid NOT NULL,
  applied_amount_tx numeric(20,6) NOT NULL,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT receive_payment_lines_amount_positive_chk CHECK (applied_amount_tx > 0),
  CONSTRAINT receive_payment_lines_unique_line UNIQUE (receive_payment_id, line_number)
);

CREATE TABLE pay_bills (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  entity_number text NOT NULL UNIQUE,
  payment_number text NOT NULL,
  vendor_id uuid NOT NULL REFERENCES vendors(id) ON DELETE RESTRICT,
  status text NOT NULL DEFAULT 'draft',
  payment_date date NOT NULL,
  bank_account_id uuid NOT NULL REFERENCES accounts(id) ON DELETE RESTRICT,
  document_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  base_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  fx_rate_snapshot_id uuid REFERENCES company_fx_rate_snapshots(id) ON DELETE RESTRICT,
  fx_rate numeric(20,10) NOT NULL DEFAULT 1,
  fx_requested_date date NOT NULL,
  fx_effective_date date NOT NULL,
  fx_source text NOT NULL DEFAULT 'identity',
  total_amount numeric(20,6) NOT NULL DEFAULT 0,
  memo text,
  posted_at timestamptz,
  created_by_user_id uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT pay_bills_entity_number_format_chk CHECK (entity_number ~ '^EN[0-9]{4}[0-9]{8}$'),
  CONSTRAINT pay_bills_status_chk CHECK (status IN ('draft', 'posted', 'voided', 'reversed')),
  CONSTRAINT pay_bills_fx_rate_positive_chk CHECK (fx_rate > 0),
  CONSTRAINT pay_bills_total_amount_nonnegative_chk CHECK (total_amount >= 0),
  CONSTRAINT pay_bills_unique_company_payment_number UNIQUE (company_id, payment_number)
);

CREATE TABLE pay_bill_lines (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  pay_bill_id uuid NOT NULL REFERENCES pay_bills(id) ON DELETE CASCADE,
  line_number integer NOT NULL,
  target_ap_open_item_id uuid NOT NULL,
  applied_amount_tx numeric(20,6) NOT NULL,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT pay_bill_lines_amount_positive_chk CHECK (applied_amount_tx > 0),
  CONSTRAINT pay_bill_lines_unique_line UNIQUE (pay_bill_id, line_number)
);

CREATE TABLE credit_applications (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  entity_number text NOT NULL UNIQUE,
  application_number text NOT NULL,
  customer_id uuid NOT NULL REFERENCES customers(id) ON DELETE RESTRICT,
  status text NOT NULL DEFAULT 'draft',
  application_date date NOT NULL,
  document_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  base_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  total_amount numeric(20,6) NOT NULL DEFAULT 0,
  memo text,
  posted_at timestamptz,
  created_by_user_id uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT credit_applications_entity_number_format_chk CHECK (entity_number ~ '^EN[0-9]{4}[0-9]{8}$'),
  CONSTRAINT credit_applications_status_chk CHECK (status IN ('draft', 'posted', 'voided', 'reversed')),
  CONSTRAINT credit_applications_total_amount_nonnegative_chk CHECK (total_amount >= 0),
  CONSTRAINT credit_applications_unique_company_application_number UNIQUE (company_id, application_number)
);

CREATE TABLE credit_application_lines (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  credit_application_id uuid NOT NULL REFERENCES credit_applications(id) ON DELETE CASCADE,
  line_number integer NOT NULL,
  source_credit_ar_open_item_id uuid NOT NULL,
  target_invoice_ar_open_item_id uuid NOT NULL,
  applied_amount_tx numeric(20,6) NOT NULL,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT credit_application_lines_amount_positive_chk CHECK (applied_amount_tx > 0),
  CONSTRAINT credit_application_lines_unique_line UNIQUE (credit_application_id, line_number)
);

CREATE TABLE vendor_credit_applications (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  entity_number text NOT NULL UNIQUE,
  application_number text NOT NULL,
  vendor_id uuid NOT NULL REFERENCES vendors(id) ON DELETE RESTRICT,
  status text NOT NULL DEFAULT 'draft',
  application_date date NOT NULL,
  document_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  base_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  total_amount numeric(20,6) NOT NULL DEFAULT 0,
  memo text,
  posted_at timestamptz,
  created_by_user_id uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT vendor_credit_applications_entity_number_format_chk CHECK (entity_number ~ '^EN[0-9]{4}[0-9]{8}$'),
  CONSTRAINT vendor_credit_applications_status_chk CHECK (status IN ('draft', 'posted', 'voided', 'reversed')),
  CONSTRAINT vendor_credit_applications_total_amount_nonnegative_chk CHECK (total_amount >= 0),
  CONSTRAINT vendor_credit_applications_unique_company_application_number UNIQUE (company_id, application_number)
);

CREATE TABLE vendor_credit_application_lines (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  vendor_credit_application_id uuid NOT NULL REFERENCES vendor_credit_applications(id) ON DELETE CASCADE,
  line_number integer NOT NULL,
  source_vendor_credit_ap_open_item_id uuid NOT NULL,
  target_bill_ap_open_item_id uuid NOT NULL,
  applied_amount_tx numeric(20,6) NOT NULL,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT vendor_credit_application_lines_amount_positive_chk CHECK (applied_amount_tx > 0),
  CONSTRAINT vendor_credit_application_lines_unique_line UNIQUE (vendor_credit_application_id, line_number)
);

CREATE TABLE manual_journal_documents (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  entity_number text NOT NULL UNIQUE,
  display_number text NOT NULL,
  status text NOT NULL DEFAULT 'draft',
  entry_date date NOT NULL,
  transaction_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  base_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  fx_rate_snapshot_id uuid REFERENCES company_fx_rate_snapshots(id) ON DELETE RESTRICT,
  fx_rate numeric(20,10) NOT NULL DEFAULT 1,
  fx_requested_date date NOT NULL,
  fx_effective_date date NOT NULL,
  fx_source text NOT NULL DEFAULT 'identity',
  memo text,
  posted_at timestamptz,
  created_by_user_id uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT manual_journal_documents_entity_number_format_chk CHECK (
    entity_number ~ '^EN[0-9]{4}[0-9]{8}$'
  ),
  CONSTRAINT manual_journal_documents_status_chk CHECK (
    status IN ('draft', 'posted', 'voided', 'reversed')
  ),
  CONSTRAINT manual_journal_documents_fx_rate_positive_chk CHECK (fx_rate > 0),
  CONSTRAINT manual_journal_documents_unique_display_number UNIQUE (company_id, display_number)
);

CREATE TABLE manual_journal_document_lines (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  manual_journal_document_id uuid NOT NULL REFERENCES manual_journal_documents(id) ON DELETE CASCADE,
  line_number integer NOT NULL,
  account_id uuid NOT NULL REFERENCES accounts(id) ON DELETE RESTRICT,
  description text,
  tx_debit numeric(20,6) NOT NULL DEFAULT 0,
  tx_credit numeric(20,6) NOT NULL DEFAULT 0,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT manual_journal_document_lines_nonnegative_chk CHECK (tx_debit >= 0 AND tx_credit >= 0),
  CONSTRAINT manual_journal_document_lines_one_sided_chk CHECK (
    (CASE WHEN tx_debit > 0 THEN 1 ELSE 0 END) +
    (CASE WHEN tx_credit > 0 THEN 1 ELSE 0 END) = 1
  ),
  CONSTRAINT manual_journal_document_lines_unique_line UNIQUE (manual_journal_document_id, line_number)
);

-- ---------------------------------------------------------------------------
-- Posted accounting truth
-- ---------------------------------------------------------------------------

CREATE TABLE journal_entries (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  entity_number text NOT NULL UNIQUE,
  display_number text NOT NULL,
  status text NOT NULL DEFAULT 'draft',
  source_type text NOT NULL,
  source_id uuid NOT NULL,
  transaction_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  base_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  exchange_rate numeric(20,10) NOT NULL,
  exchange_rate_date date NOT NULL,
  exchange_rate_source text NOT NULL,
  fx_rate_snapshot_id uuid REFERENCES company_fx_rate_snapshots(id) ON DELETE RESTRICT,
  total_tx_debit numeric(20,6) NOT NULL DEFAULT 0,
  total_tx_credit numeric(20,6) NOT NULL DEFAULT 0,
  total_debit numeric(20,6) NOT NULL DEFAULT 0,
  total_credit numeric(20,6) NOT NULL DEFAULT 0,
  posting_run_id uuid NOT NULL,
  idempotency_key text NOT NULL,
  posted_at timestamptz,
  voided_at timestamptz,
  reversed_at timestamptz,
  created_by_user_id uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT journal_entries_entity_number_format_chk CHECK (entity_number ~ '^EN[0-9]{4}[0-9]{8}$'),
  CONSTRAINT journal_entries_status_chk CHECK (status IN ('draft', 'posted', 'voided', 'reversed')),
  CONSTRAINT journal_entries_exchange_rate_positive_chk CHECK (exchange_rate > 0),
  CONSTRAINT journal_entries_unique_idempotency UNIQUE (company_id, idempotency_key),
  CONSTRAINT journal_entries_unique_display_number UNIQUE (company_id, display_number)
);

CREATE TABLE journal_entry_lines (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  journal_entry_id uuid NOT NULL REFERENCES journal_entries(id) ON DELETE CASCADE,
  line_number integer NOT NULL,
  account_id uuid NOT NULL REFERENCES accounts(id) ON DELETE RESTRICT,
  description text,
  party_type text,
  party_id uuid,
  tx_debit numeric(20,6) NOT NULL DEFAULT 0,
  tx_credit numeric(20,6) NOT NULL DEFAULT 0,
  debit numeric(20,6) NOT NULL DEFAULT 0,
  credit numeric(20,6) NOT NULL DEFAULT 0,
  tax_component_type text,
  control_role text,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT journal_entry_lines_nonnegative_chk CHECK (
    tx_debit >= 0 AND tx_credit >= 0 AND debit >= 0 AND credit >= 0
  ),
  CONSTRAINT journal_entry_lines_one_sided_base_chk CHECK (
    (CASE WHEN debit > 0 THEN 1 ELSE 0 END) +
    (CASE WHEN credit > 0 THEN 1 ELSE 0 END) = 1
  ),
  CONSTRAINT journal_entry_lines_unique_line UNIQUE (journal_entry_id, line_number)
);

CREATE TABLE ledger_entries (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  journal_entry_id uuid NOT NULL REFERENCES journal_entries(id) ON DELETE CASCADE,
  journal_entry_line_id uuid NOT NULL REFERENCES journal_entry_lines(id) ON DELETE CASCADE,
  posting_date date NOT NULL,
  account_id uuid NOT NULL REFERENCES accounts(id) ON DELETE RESTRICT,
  debit numeric(20,6) NOT NULL DEFAULT 0,
  credit numeric(20,6) NOT NULL DEFAULT 0,
  transaction_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  tx_debit numeric(20,6) NOT NULL DEFAULT 0,
  tx_credit numeric(20,6) NOT NULL DEFAULT 0,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT ledger_entries_nonnegative_chk CHECK (
    debit >= 0 AND credit >= 0 AND tx_debit >= 0 AND tx_credit >= 0
  ),
  CONSTRAINT ledger_entries_one_sided_base_chk CHECK (
    (CASE WHEN debit > 0 THEN 1 ELSE 0 END) +
    (CASE WHEN credit > 0 THEN 1 ELSE 0 END) = 1
  )
);

-- ---------------------------------------------------------------------------
-- AP / AR open-item control
-- ---------------------------------------------------------------------------

CREATE TABLE ar_open_items (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  customer_id uuid NOT NULL REFERENCES customers(id) ON DELETE RESTRICT,
  source_type text NOT NULL,
  source_id uuid NOT NULL,
  balance_side text NOT NULL,
  document_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  base_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  original_amount_tx numeric(20,6) NOT NULL,
  original_amount_base numeric(20,6) NOT NULL,
  open_amount_tx numeric(20,6) NOT NULL,
  open_amount_base numeric(20,6) NOT NULL,
  status text NOT NULL,
  due_date date,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT ar_open_items_nonnegative_chk CHECK (
    original_amount_tx >= 0 AND
    original_amount_base >= 0 AND
    open_amount_tx >= 0 AND
    open_amount_base >= 0
  ),
  CONSTRAINT ar_open_items_balance_side_chk CHECK (balance_side IN ('debit', 'credit')),
  CONSTRAINT ar_open_items_status_chk CHECK (status IN ('open', 'partially_applied', 'closed', 'voided'))
);

CREATE TABLE ap_open_items (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  vendor_id uuid NOT NULL REFERENCES vendors(id) ON DELETE RESTRICT,
  source_type text NOT NULL,
  source_id uuid NOT NULL,
  balance_side text NOT NULL,
  document_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  base_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  original_amount_tx numeric(20,6) NOT NULL,
  original_amount_base numeric(20,6) NOT NULL,
  open_amount_tx numeric(20,6) NOT NULL,
  open_amount_base numeric(20,6) NOT NULL,
  status text NOT NULL,
  due_date date,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT ap_open_items_nonnegative_chk CHECK (
    original_amount_tx >= 0 AND
    original_amount_base >= 0 AND
    open_amount_tx >= 0 AND
    open_amount_base >= 0
  ),
  CONSTRAINT ap_open_items_balance_side_chk CHECK (balance_side IN ('debit', 'credit')),
  CONSTRAINT ap_open_items_status_chk CHECK (status IN ('open', 'partially_applied', 'closed', 'voided'))
);

CREATE TABLE settlement_applications (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  application_type text NOT NULL,
  source_type text NOT NULL,
  source_id uuid NOT NULL,
  target_open_item_type text NOT NULL,
  target_open_item_id uuid NOT NULL,
  applied_amount_tx numeric(20,6) NOT NULL,
  applied_amount_base numeric(20,6) NOT NULL,
  settlement_fx_rate numeric(20,10),
  realized_fx_amount numeric(20,6),
  created_at timestamptz NOT NULL DEFAULT NOW(),
  created_by_user_id uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
  CONSTRAINT settlement_applications_application_type_chk CHECK (
    application_type IN ('receive_payment', 'pay_bill', 'credit_application', 'vendor_credit_application')
  ),
  CONSTRAINT settlement_applications_target_type_chk CHECK (
    target_open_item_type IN ('ar_open_item', 'ap_open_item')
  ),
  CONSTRAINT settlement_applications_amount_nonnegative_chk CHECK (
    applied_amount_tx >= 0 AND applied_amount_base >= 0
  ),
  CONSTRAINT settlement_applications_fx_rate_positive_chk CHECK (
    settlement_fx_rate IS NULL OR settlement_fx_rate > 0
  )
);

CREATE TABLE fx_revaluation_batches (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  entity_number text NOT NULL UNIQUE,
  display_number text NOT NULL,
  status text NOT NULL,
  batch_kind text NOT NULL DEFAULT 'revaluation',
  reversal_of_fx_revaluation_batch_id uuid REFERENCES fx_revaluation_batches(id) ON DELETE RESTRICT,
  revaluation_date date NOT NULL,
  transaction_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  base_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  fx_rate_snapshot_id uuid REFERENCES company_fx_rate_snapshots(id) ON DELETE RESTRICT,
  fx_rate numeric(20,10) NOT NULL,
  fx_requested_date date NOT NULL,
  fx_effective_date date NOT NULL,
  fx_source text NOT NULL,
  memo text,
  posted_at timestamptz,
  created_by_user_id uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT fx_revaluation_batches_status_chk CHECK (status IN ('draft', 'posted', 'voided')),
  CONSTRAINT fx_revaluation_batches_kind_chk CHECK (
    batch_kind IN ('revaluation', 'next_period_unwind')
  ),
  CONSTRAINT fx_revaluation_batches_reversal_link_chk CHECK (
    (batch_kind = 'revaluation' AND reversal_of_fx_revaluation_batch_id IS NULL) OR
    (batch_kind = 'next_period_unwind' AND reversal_of_fx_revaluation_batch_id IS NOT NULL)
  ),
  CONSTRAINT fx_revaluation_batches_foreign_currency_chk CHECK (
    transaction_currency_code <> base_currency_code
  ),
  CONSTRAINT fx_revaluation_batches_fx_positive_chk CHECK (fx_rate > 0),
  CONSTRAINT fx_revaluation_batches_company_display_unique UNIQUE (company_id, display_number)
);

CREATE TABLE fx_revaluation_batch_lines (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  fx_revaluation_batch_id uuid NOT NULL REFERENCES fx_revaluation_batches(id) ON DELETE CASCADE,
  line_number integer NOT NULL,
  target_open_item_type text NOT NULL,
  target_open_item_id uuid NOT NULL,
  target_balance_side text NOT NULL,
  target_control_account_id uuid NOT NULL REFERENCES accounts(id) ON DELETE RESTRICT,
  offset_account_id uuid NOT NULL REFERENCES accounts(id) ON DELETE RESTRICT,
  party_id uuid NOT NULL,
  description text NOT NULL,
  open_amount_tx numeric(20,6) NOT NULL,
  carrying_amount_base numeric(20,6) NOT NULL,
  revalued_amount_base numeric(20,6) NOT NULL,
  unrealized_fx_amount numeric(20,6) NOT NULL,
  applied_at timestamptz,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT fx_revaluation_batch_lines_target_type_chk CHECK (
    target_open_item_type IN ('ar_open_item', 'ap_open_item')
  ),
  CONSTRAINT fx_revaluation_batch_lines_balance_side_chk CHECK (
    target_balance_side IN ('debit', 'credit')
  ),
  CONSTRAINT fx_revaluation_batch_lines_amount_positive_chk CHECK (
    open_amount_tx > 0 AND carrying_amount_base > 0 AND revalued_amount_base > 0
  ),
  CONSTRAINT fx_revaluation_batch_lines_delta_nonzero_chk CHECK (
    unrealized_fx_amount <> 0
  ),
  CONSTRAINT fx_revaluation_batch_lines_batch_line_unique UNIQUE (fx_revaluation_batch_id, line_number)
);

-- ---------------------------------------------------------------------------
-- Audit
-- ---------------------------------------------------------------------------

CREATE TABLE audit_logs (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid REFERENCES companies(id) ON DELETE SET NULL,
  actor_type text NOT NULL,
  actor_id uuid,
  entity_type text NOT NULL,
  entity_id uuid NOT NULL,
  action text NOT NULL,
  payload jsonb NOT NULL DEFAULT '{}'::jsonb,
  created_at timestamptz NOT NULL DEFAULT NOW()
);

-- ---------------------------------------------------------------------------
-- Indexes
-- ---------------------------------------------------------------------------

CREATE INDEX idx_company_memberships_company_active
  ON company_memberships (company_id, is_active);

CREATE INDEX idx_company_currencies_company_enabled
  ON company_currencies (company_id, is_enabled);

CREATE INDEX idx_system_fx_market_rates_lookup
  ON system_fx_market_rates (provider_key, base_currency_code, quote_currency_code, market_date DESC);

CREATE INDEX idx_company_fx_rate_snapshots_lookup
  ON company_fx_rate_snapshots (company_id, base_currency_code, quote_currency_code, requested_date DESC);

CREATE INDEX idx_accounts_company_root_code
  ON accounts (company_id, root_type, code);

CREATE INDEX idx_tax_codes_company_active
  ON tax_codes (company_id, is_active, code);

CREATE INDEX idx_customers_company_name
  ON customers (company_id, display_name);

CREATE INDEX idx_vendors_company_name
  ON vendors (company_id, display_name);

CREATE INDEX idx_invoices_company_status_date
  ON invoices (company_id, status, invoice_date DESC);

CREATE INDEX idx_credit_notes_company_status_date
  ON credit_notes (company_id, status, credit_note_date DESC);

CREATE INDEX idx_bills_company_status_date
  ON bills (company_id, status, bill_date DESC);

CREATE INDEX idx_vendor_credits_company_status_date
  ON vendor_credits (company_id, status, vendor_credit_date DESC);

CREATE INDEX idx_receive_payments_company_status_date
  ON receive_payments (company_id, status, payment_date DESC);

CREATE INDEX idx_pay_bills_company_status_date
  ON pay_bills (company_id, status, payment_date DESC);

CREATE INDEX idx_credit_applications_company_status_date
  ON credit_applications (company_id, status, application_date DESC);

CREATE INDEX idx_vendor_credit_applications_company_status_date
  ON vendor_credit_applications (company_id, status, application_date DESC);

CREATE INDEX idx_manual_journal_documents_company_status_date
  ON manual_journal_documents (company_id, status, entry_date DESC);

CREATE INDEX idx_journal_entries_company_source
  ON journal_entries (company_id, source_type, source_id);

CREATE INDEX idx_ledger_entries_company_account_date
  ON ledger_entries (company_id, account_id, posting_date DESC);

CREATE INDEX idx_ar_open_items_company_customer_status_due
  ON ar_open_items (company_id, customer_id, status, due_date);

CREATE INDEX idx_ap_open_items_company_vendor_status_due
  ON ap_open_items (company_id, vendor_id, status, due_date);

CREATE INDEX idx_settlement_applications_company_target
  ON settlement_applications (company_id, target_open_item_type, target_open_item_id);

CREATE INDEX idx_fx_revaluation_batches_company_status_date
  ON fx_revaluation_batches (company_id, status, revaluation_date DESC);

CREATE UNIQUE INDEX idx_fx_revaluation_batches_active_reversal
  ON fx_revaluation_batches (reversal_of_fx_revaluation_batch_id)
  WHERE reversal_of_fx_revaluation_batch_id IS NOT NULL
    AND status <> 'voided';

CREATE INDEX idx_fx_revaluation_batch_lines_company_batch
  ON fx_revaluation_batch_lines (company_id, fx_revaluation_batch_id, line_number);

CREATE INDEX idx_fx_revaluation_batch_lines_company_target
  ON fx_revaluation_batch_lines (company_id, target_open_item_type, target_open_item_id);

CREATE INDEX idx_audit_logs_company_entity_created
  ON audit_logs (company_id, entity_type, entity_id, created_at DESC);

-- ---------------------------------------------------------------------------
-- updated_at triggers
-- ---------------------------------------------------------------------------

CREATE TRIGGER trg_users_set_updated_at
BEFORE UPDATE ON users
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_companies_set_updated_at
BEFORE UPDATE ON companies
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_company_memberships_set_updated_at
BEFORE UPDATE ON company_memberships
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_company_numbering_sequences_set_updated_at
BEFORE UPDATE ON company_numbering_sequences
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_company_settings_set_updated_at
BEFORE UPDATE ON company_settings
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_accounts_set_updated_at
BEFORE UPDATE ON accounts
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_tax_codes_set_updated_at
BEFORE UPDATE ON tax_codes
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_customers_set_updated_at
BEFORE UPDATE ON customers
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_vendors_set_updated_at
BEFORE UPDATE ON vendors
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_invoices_set_updated_at
BEFORE UPDATE ON invoices
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_invoice_lines_set_updated_at
BEFORE UPDATE ON invoice_lines
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_credit_notes_set_updated_at
BEFORE UPDATE ON credit_notes
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_credit_note_lines_set_updated_at
BEFORE UPDATE ON credit_note_lines
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_bills_set_updated_at
BEFORE UPDATE ON bills
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_bill_lines_set_updated_at
BEFORE UPDATE ON bill_lines
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_vendor_credits_set_updated_at
BEFORE UPDATE ON vendor_credits
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_vendor_credit_lines_set_updated_at
BEFORE UPDATE ON vendor_credit_lines
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_receive_payments_set_updated_at
BEFORE UPDATE ON receive_payments
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_receive_payment_lines_set_updated_at
BEFORE UPDATE ON receive_payment_lines
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_pay_bills_set_updated_at
BEFORE UPDATE ON pay_bills
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_pay_bill_lines_set_updated_at
BEFORE UPDATE ON pay_bill_lines
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_credit_applications_set_updated_at
BEFORE UPDATE ON credit_applications
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_credit_application_lines_set_updated_at
BEFORE UPDATE ON credit_application_lines
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_vendor_credit_applications_set_updated_at
BEFORE UPDATE ON vendor_credit_applications
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_vendor_credit_application_lines_set_updated_at
BEFORE UPDATE ON vendor_credit_application_lines
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_fx_revaluation_batches_set_updated_at
BEFORE UPDATE ON fx_revaluation_batches
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_fx_revaluation_batch_lines_set_updated_at
BEFORE UPDATE ON fx_revaluation_batch_lines
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_manual_journal_documents_set_updated_at
BEFORE UPDATE ON manual_journal_documents
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_manual_journal_document_lines_set_updated_at
BEFORE UPDATE ON manual_journal_document_lines
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_ar_open_items_set_updated_at
BEFORE UPDATE ON ar_open_items
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_ap_open_items_set_updated_at
BEFORE UPDATE ON ap_open_items
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

COMMIT;
