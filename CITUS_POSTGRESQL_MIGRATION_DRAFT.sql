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
-- Platform account, CompanyAccess, and SysAdmin core
-- ---------------------------------------------------------------------------

-- Physical table name remains `users` in this draft to avoid touching every
-- actor-reference FK in the baseline. Semantically, this is Platform
-- Identity / Account storage, not a Business App Users module.
CREATE TABLE users (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  email text NOT NULL UNIQUE,
  username text UNIQUE,
  display_name text,
  password_hash text NOT NULL,
  status text NOT NULL DEFAULT 'active',
  email_verified_at timestamptz,
  locked_until timestamptz,
  mfa_mode text NOT NULL DEFAULT 'none',
  security_stamp text NOT NULL DEFAULT gen_random_uuid()::text,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT users_status_chk CHECK (status IN ('active', 'disabled', 'locked', 'pending_verification')),
  CONSTRAINT users_mfa_mode_chk CHECK (mfa_mode IN ('none', 'email_code', 'totp_app'))
);

CREATE TABLE account_verification_codes (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  purpose text NOT NULL,
  destination text,
  code_hash text NOT NULL,
  expires_at timestamptz NOT NULL,
  consumed_at timestamptz,
  failed_attempts integer NOT NULL DEFAULT 0,
  payload jsonb NOT NULL DEFAULT '{}'::jsonb,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT account_verification_codes_purpose_chk CHECK (purpose IN ('email_verification', 'email_change', 'password_change', 'password_reset')),
  CONSTRAINT account_verification_codes_failed_attempts_chk CHECK (failed_attempts >= 0)
);

-- SysAdmin is an independent PlatformOps identity realm. SysAdmin accounts are
-- not company members and must never become business posting actors.
CREATE TABLE sysadmin_accounts (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  email text NOT NULL UNIQUE,
  display_name text NOT NULL DEFAULT '',
  password_hash text NOT NULL,
  status text NOT NULL DEFAULT 'active',
  last_login_at timestamptz,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT sysadmin_accounts_status_chk CHECK (status IN ('active', 'disabled', 'locked'))
);

CREATE TABLE sysadmin_sessions (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  sysadmin_account_id uuid NOT NULL REFERENCES sysadmin_accounts(id) ON DELETE CASCADE,
  session_token_hash text NOT NULL UNIQUE,
  expires_at timestamptz NOT NULL,
  last_seen_at timestamptz NOT NULL DEFAULT NOW(),
  revoked_at timestamptz,
  remote_ip text,
  user_agent text,
  created_at timestamptz NOT NULL DEFAULT NOW()
);

CREATE TABLE platform_notification_dispatches (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  notification_type text NOT NULL,
  destination text NOT NULL,
  status text NOT NULL DEFAULT 'queued',
  provider_key text,
  attempt_count integer NOT NULL DEFAULT 0,
  sent_at timestamptz,
  failed_at timestamptz,
  last_error text,
  payload jsonb NOT NULL DEFAULT '{}'::jsonb,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW()
);

CREATE TABLE platform_runtime_state (
  state_key text PRIMARY KEY,
  json jsonb NOT NULL,
  updated_by_sysadmin_account_id uuid REFERENCES sysadmin_accounts(id) ON DELETE SET NULL,
  updated_at timestamptz NOT NULL DEFAULT NOW()
);

INSERT INTO platform_runtime_state (state_key, json)
VALUES ('maintenance', '{"enabled": false, "message": "Maintenance mode is off.", "scheduledUntilUtc": null}'::jsonb)
ON CONFLICT (state_key) DO NOTHING;

INSERT INTO platform_runtime_state (state_key, json)
VALUES ('notification_readiness', '{"configPresent": false, "testStatus": "untested", "lastTestedAtUtc": null, "verificationReady": false}'::jsonb)
ON CONFLICT (state_key) DO NOTHING;

CREATE TABLE companies (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  entity_number text NOT NULL UNIQUE,
  legal_name text NOT NULL,
  entity_type text NOT NULL DEFAULT 'corporation',
  industry text NOT NULL DEFAULT 'general_services',
  incorporated_on date,
  fiscal_year_end_month smallint NOT NULL DEFAULT 12,
  fiscal_year_end_day smallint NOT NULL DEFAULT 31,
  business_number text,
  phone text,
  email text,
  address_line text,
  city text,
  province_state text,
  postal_code text,
  country text NOT NULL DEFAULT 'Canada',
  account_code_length smallint NOT NULL DEFAULT 4,
  base_currency_code char(3) NOT NULL,
  multi_currency_enabled boolean NOT NULL DEFAULT false,
  status text NOT NULL DEFAULT 'active',
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT companies_entity_number_format_chk CHECK (entity_number ~ '^EN[0-9]{4}[0-9]{8}$'),
  CONSTRAINT companies_account_code_length_chk CHECK (account_code_length BETWEEN 4 AND 6),
  CONSTRAINT companies_status_chk CHECK (status IN ('active', 'inactive', 'suspended', 'archived'))
);

CREATE TABLE company_memberships (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  role text NOT NULL,
  is_active boolean NOT NULL DEFAULT true,
  permissions jsonb NOT NULL DEFAULT '[]'::jsonb,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT company_memberships_role_chk CHECK (role IN ('owner', 'user')),
  CONSTRAINT company_memberships_permissions_array_chk CHECK (jsonb_typeof(permissions) = 'array'),
  CONSTRAINT company_memberships_unique_member UNIQUE (company_id, user_id),
  CONSTRAINT company_memberships_session_context_unique UNIQUE (id, company_id, user_id)
);

CREATE TABLE business_sessions (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  token_hash text NOT NULL UNIQUE,
  user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  active_company_id uuid NOT NULL REFERENCES companies(id) ON DELETE RESTRICT,
  membership_id uuid NOT NULL REFERENCES company_memberships(id) ON DELETE CASCADE,
  role text NOT NULL,
  permissions jsonb NOT NULL DEFAULT '[]'::jsonb,
  company_status text NOT NULL,
  permission_version text,
  security_stamp_snapshot text NOT NULL,
  expires_at timestamptz NOT NULL,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT business_sessions_role_chk CHECK (role IN ('owner', 'user')),
  CONSTRAINT business_sessions_permissions_array_chk CHECK (jsonb_typeof(permissions) = 'array'),
  CONSTRAINT business_sessions_company_status_chk CHECK (company_status IN ('active', 'inactive', 'suspended', 'archived')),
  CONSTRAINT business_sessions_membership_context_fk
    FOREIGN KEY (membership_id, active_company_id, user_id)
    REFERENCES company_memberships(id, company_id, user_id)
    ON DELETE CASCADE
);

CREATE TABLE business_session_mfa_challenges (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  active_company_id uuid NOT NULL REFERENCES companies(id) ON DELETE RESTRICT,
  membership_id uuid NOT NULL REFERENCES company_memberships(id) ON DELETE CASCADE,
  role text NOT NULL,
  permissions jsonb NOT NULL DEFAULT '[]'::jsonb,
  company_status text NOT NULL,
  factor text NOT NULL,
  destination text NOT NULL,
  code_hash text NOT NULL,
  security_stamp_snapshot text NOT NULL,
  expires_at timestamptz NOT NULL,
  consumed_at timestamptz,
  failed_attempts integer NOT NULL DEFAULT 0,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT business_session_mfa_challenges_role_chk CHECK (role IN ('owner', 'user')),
  CONSTRAINT business_session_mfa_challenges_permissions_array_chk CHECK (jsonb_typeof(permissions) = 'array'),
  CONSTRAINT business_session_mfa_challenges_company_status_chk CHECK (company_status IN ('active', 'inactive', 'suspended', 'archived')),
  CONSTRAINT business_session_mfa_challenges_factor_chk CHECK (factor IN ('email_code', 'totp_app'))
);

CREATE TABLE account_mfa_recovery_requests (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  requested_by_user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  current_mfa_mode text NOT NULL,
  status text NOT NULL DEFAULT 'requested',
  request_reason text NOT NULL,
  requested_at timestamptz NOT NULL DEFAULT NOW(),
  review_reason text,
  reviewed_at timestamptz,
  reviewed_by_sysadmin_account_id uuid REFERENCES sysadmin_accounts(id) ON DELETE SET NULL,
  execution_reason text,
  executed_at timestamptz,
  executed_by_sysadmin_account_id uuid REFERENCES sysadmin_accounts(id) ON DELETE SET NULL,
  CONSTRAINT account_mfa_recovery_requests_current_mode_chk CHECK (current_mfa_mode IN ('none', 'email_code', 'totp_app')),
  CONSTRAINT account_mfa_recovery_requests_status_chk CHECK (status IN ('requested', 'approved', 'rejected', 'executed'))
);

CREATE TABLE account_mfa_totp_enrollments (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  status text NOT NULL,
  secret_base32 text NOT NULL, -- protected ciphertext for new rows; legacy plaintext tolerated during migration
  created_at timestamptz NOT NULL DEFAULT NOW(),
  expires_at timestamptz,
  confirmed_at timestamptz,
  revoked_at timestamptz,
  last_used_at timestamptz,
  CONSTRAINT account_mfa_totp_enrollments_status_chk CHECK (status IN ('pending', 'active', 'revoked'))
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

CREATE TABLE company_books (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  book_code text NOT NULL,
  book_name text NOT NULL,
  book_role text NOT NULL,
  accounting_standard text NOT NULL,
  book_base_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  functional_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  presentation_currency_code char(3) REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  is_primary boolean NOT NULL DEFAULT false,
  is_adjustment_only boolean NOT NULL DEFAULT false,
  effective_from date NOT NULL,
  is_active boolean NOT NULL DEFAULT true,
  created_by_user_id uuid REFERENCES users(id) ON DELETE RESTRICT,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT company_books_role_chk CHECK (
    book_role IN ('primary', 'secondary', 'adjustment', 'tax', 'management')
  ),
  CONSTRAINT company_books_standard_chk CHECK (
    accounting_standard IN ('ASPE', 'IFRS', 'US_GAAP', 'TAX', 'MANAGEMENT')
  ),
  CONSTRAINT company_books_primary_adjustment_chk CHECK (
    NOT (is_primary = true AND is_adjustment_only = true)
  ),
  CONSTRAINT company_books_unique UNIQUE (company_id, book_code)
);

CREATE TABLE company_book_remeasurement_policies (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  company_book_id uuid NOT NULL REFERENCES company_books(id) ON DELETE CASCADE,
  rate_type text NOT NULL DEFAULT 'closing',
  quote_basis text NOT NULL DEFAULT 'direct',
  rate_use_case text NOT NULL DEFAULT 'remeasurement',
  posting_reason text NOT NULL DEFAULT 'revaluation',
  revaluation_profile text NOT NULL DEFAULT 'monetary_open_item_closing',
  fx_rounding_policy text NOT NULL DEFAULT 'currency_precision',
  effective_from date NOT NULL,
  is_active boolean NOT NULL DEFAULT true,
  created_by_user_id uuid REFERENCES users(id) ON DELETE RESTRICT,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT company_book_remeasurement_policies_rate_type_chk CHECK (
    rate_type IN ('spot', 'closing', 'average', 'historical', 'custom')
  ),
  CONSTRAINT company_book_remeasurement_policies_quote_basis_chk CHECK (
    quote_basis IN ('direct', 'inverse')
  ),
  CONSTRAINT company_book_remeasurement_policies_rate_use_case_chk CHECK (
    rate_use_case IN ('general', 'settlement', 'remeasurement', 'translation')
  ),
  CONSTRAINT company_book_remeasurement_policies_posting_reason_chk CHECK (
    posting_reason IN ('normal', 'settlement', 'revaluation', 'translation', 'adjustment')
  ),
  CONSTRAINT company_book_remeasurement_policies_profile_chk CHECK (
    revaluation_profile IN ('monetary_open_item_closing')
  ),
  CONSTRAINT company_book_remeasurement_policies_rounding_chk CHECK (
    fx_rounding_policy IN ('currency_precision')
  )
);

CREATE TABLE company_chart_template_bindings (
  company_id uuid PRIMARY KEY REFERENCES companies(id) ON DELETE CASCADE,
  template_key text NOT NULL,
  template_version text NOT NULL,
  account_code_length smallint NOT NULL,
  base_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  country text NOT NULL,
  entity_type text NOT NULL,
  industry text NOT NULL,
  reserved_ranges jsonb NOT NULL DEFAULT '[]'::jsonb,
  mandatory_system_roles jsonb NOT NULL DEFAULT '[]'::jsonb,
  applied_by_sysadmin_account_id uuid REFERENCES sysadmin_accounts(id) ON DELETE SET NULL,
  applied_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT company_chart_template_bindings_reserved_ranges_array_chk CHECK (jsonb_typeof(reserved_ranges) = 'array'),
  CONSTRAINT company_chart_template_bindings_mandatory_roles_array_chk CHECK (jsonb_typeof(mandatory_system_roles) = 'array')
);

CREATE TABLE platform_entity_number_sequences (
  entity_year integer PRIMARY KEY,
  next_number bigint NOT NULL,
  CONSTRAINT platform_entity_number_sequences_next_number_chk CHECK (next_number > 0)
);

CREATE TABLE company_book_governed_change_requests (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  company_book_id uuid NOT NULL REFERENCES company_books(id) ON DELETE RESTRICT,
  status text NOT NULL DEFAULT 'draft',
  requested_action text NOT NULL,
  evaluation_basis text NOT NULL,
  as_of_date date NOT NULL,
  effective_from date NOT NULL,
  has_company_posted_history boolean NOT NULL DEFAULT false,
  has_book_specific_revaluation_history boolean NOT NULL DEFAULT false,
  current_book_code text NOT NULL,
  current_book_name text NOT NULL,
  current_book_role text NOT NULL,
  current_is_primary boolean NOT NULL,
  current_is_adjustment_only boolean NOT NULL,
  current_accounting_standard text NOT NULL,
  current_book_base_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  current_functional_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  current_presentation_currency_code char(3) REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  current_book_effective_from date NOT NULL,
  current_rate_type text,
  current_quote_basis text,
  current_rate_use_case text,
  current_posting_reason text,
  current_revaluation_profile text,
  current_fx_rounding_policy text,
  current_policy_effective_from date,
  proposed_is_primary boolean,
  proposed_accounting_standard text,
  proposed_book_base_currency_code char(3) REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  proposed_functional_currency_code char(3) REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  proposed_presentation_currency_code char(3) REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  proposed_rate_type text,
  proposed_quote_basis text,
  proposed_rate_use_case text,
  proposed_posting_reason text,
  proposed_revaluation_profile text,
  proposed_fx_rounding_policy text,
  changed_fields text[] NOT NULL DEFAULT '{}',
  change_categories text[] NOT NULL DEFAULT '{}',
  reason text NOT NULL,
  created_by_user_id uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
  submitted_by_user_id uuid REFERENCES users(id) ON DELETE RESTRICT,
  submitted_at timestamptz,
  cancelled_by_user_id uuid REFERENCES users(id) ON DELETE RESTRICT,
  cancelled_at timestamptz,
  applied_at timestamptz,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT company_book_governed_change_requests_status_chk CHECK (
    status IN ('draft', 'submitted', 'cancelled', 'applied')
  ),
  CONSTRAINT company_book_governed_change_requests_action_chk CHECK (
    requested_action IN ('direct_update_in_place', 'future_dated_cutover_or_new_book', 'new_secondary_or_adjustment_book')
  ),
  CONSTRAINT company_book_governed_change_requests_basis_chk CHECK (
    evaluation_basis IN ('current_book_truth', 'company_posted_history', 'company_posted_history_and_book_remeasurement_history', 'formal_governance_signals')
  ),
  CONSTRAINT company_book_governed_change_requests_effective_chk CHECK (
    effective_from >= as_of_date
  ),
  CONSTRAINT company_book_governed_change_requests_role_chk CHECK (
    current_book_role IN ('primary', 'secondary', 'adjustment', 'tax', 'management')
  ),
  CONSTRAINT company_book_governed_change_requests_current_standard_chk CHECK (
    current_accounting_standard IN ('ASPE', 'IFRS', 'US_GAAP', 'TAX', 'MANAGEMENT')
  ),
  CONSTRAINT company_book_governed_change_requests_proposed_standard_chk CHECK (
    proposed_accounting_standard IS NULL OR proposed_accounting_standard IN ('ASPE', 'IFRS', 'US_GAAP', 'TAX', 'MANAGEMENT')
  ),
  CONSTRAINT company_book_governed_change_requests_current_rate_type_chk CHECK (
    current_rate_type IS NULL OR current_rate_type IN ('spot', 'closing', 'average', 'historical', 'custom')
  ),
  CONSTRAINT company_book_governed_change_requests_proposed_rate_type_chk CHECK (
    proposed_rate_type IS NULL OR proposed_rate_type IN ('spot', 'closing', 'average', 'historical', 'custom')
  ),
  CONSTRAINT company_book_governed_change_requests_current_quote_basis_chk CHECK (
    current_quote_basis IS NULL OR current_quote_basis IN ('direct', 'inverse')
  ),
  CONSTRAINT company_book_governed_change_requests_proposed_quote_basis_chk CHECK (
    proposed_quote_basis IS NULL OR proposed_quote_basis IN ('direct', 'inverse')
  ),
  CONSTRAINT company_book_governed_change_requests_current_rate_use_case_chk CHECK (
    current_rate_use_case IS NULL OR current_rate_use_case IN ('general', 'settlement', 'remeasurement', 'translation')
  ),
  CONSTRAINT company_book_governed_change_requests_proposed_rate_use_case_chk CHECK (
    proposed_rate_use_case IS NULL OR proposed_rate_use_case IN ('general', 'settlement', 'remeasurement', 'translation')
  ),
  CONSTRAINT company_book_governed_change_requests_current_posting_reason_chk CHECK (
    current_posting_reason IS NULL OR current_posting_reason IN ('normal', 'settlement', 'revaluation', 'translation', 'adjustment')
  ),
  CONSTRAINT company_book_governed_change_requests_proposed_posting_reason_chk CHECK (
    proposed_posting_reason IS NULL OR proposed_posting_reason IN ('normal', 'settlement', 'revaluation', 'translation', 'adjustment')
  ),
  CONSTRAINT company_book_governed_change_requests_current_profile_chk CHECK (
    current_revaluation_profile IS NULL OR current_revaluation_profile IN ('monetary_open_item_closing')
  ),
  CONSTRAINT company_book_governed_change_requests_proposed_profile_chk CHECK (
    proposed_revaluation_profile IS NULL OR proposed_revaluation_profile IN ('monetary_open_item_closing')
  ),
  CONSTRAINT company_book_governed_change_requests_current_rounding_chk CHECK (
    current_fx_rounding_policy IS NULL OR current_fx_rounding_policy IN ('currency_precision')
  ),
  CONSTRAINT company_book_governed_change_requests_proposed_rounding_chk CHECK (
    proposed_fx_rounding_policy IS NULL OR proposed_fx_rounding_policy IN ('currency_precision')
  )
);

CREATE TABLE company_book_governance_signals (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  company_book_id uuid NOT NULL REFERENCES company_books(id) ON DELETE CASCADE,
  signal_type text NOT NULL,
  signal_date date NOT NULL,
  reference_label text,
  notes text,
  created_by_user_id uuid REFERENCES users(id) ON DELETE RESTRICT,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT company_book_governance_signals_type_chk CHECK (
    signal_type IN ('closed_period', 'reported_statement', 'filed_tax')
  )
);

CREATE TABLE system_fx_market_rates (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  provider_key text NOT NULL,
  base_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  quote_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  market_date date NOT NULL,
  rate numeric(20,10) NOT NULL,
  rate_type text NOT NULL DEFAULT 'spot',
  quote_basis text NOT NULL DEFAULT 'direct',
  fetched_at timestamptz NOT NULL DEFAULT NOW(),
  payload jsonb,
  CONSTRAINT system_fx_market_rates_positive_rate_chk CHECK (rate > 0),
  CONSTRAINT system_fx_market_rates_rate_type_chk CHECK (
    rate_type IN ('spot', 'closing', 'average', 'historical', 'custom')
  ),
  CONSTRAINT system_fx_market_rates_quote_basis_chk CHECK (
    quote_basis IN ('direct', 'inverse')
  ),
  CONSTRAINT system_fx_market_rates_unique UNIQUE (
    provider_key,
    base_currency_code,
    quote_currency_code,
    market_date,
    rate_type,
    quote_basis
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
  rate_type text NOT NULL DEFAULT 'spot',
  quote_basis text NOT NULL DEFAULT 'direct',
  rate_use_case text NOT NULL DEFAULT 'general',
  posting_reason text NOT NULL DEFAULT 'normal',
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
  CONSTRAINT company_fx_rate_snapshots_rate_type_chk CHECK (
    rate_type IN ('spot', 'closing', 'average', 'historical', 'custom')
  ),
  CONSTRAINT company_fx_rate_snapshots_quote_basis_chk CHECK (
    quote_basis IN ('direct', 'inverse')
  ),
  CONSTRAINT company_fx_rate_snapshots_rate_use_case_chk CHECK (
    rate_use_case IN ('general', 'settlement', 'remeasurement', 'translation')
  ),
  CONSTRAINT company_fx_rate_snapshots_posting_reason_chk CHECK (
    posting_reason IN ('normal', 'settlement', 'revaluation', 'translation', 'adjustment')
  ),
  CONSTRAINT company_fx_rate_snapshots_date_order_chk CHECK (effective_date <= requested_date)
);

CREATE UNIQUE INDEX uq_company_fx_rate_snapshots_identity
  ON company_fx_rate_snapshots (
    company_id,
    base_currency_code,
    quote_currency_code,
    requested_date,
    rate_type,
    quote_basis,
    rate_use_case,
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
  customer_po_number text,
  sales_order_id uuid,
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
  customer_po_number text,
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
  -- Slice of total_amount that was *not* applied to AR open items and is
  -- being parked as a Customer Deposit. Populated when the New Receive
  -- Payment form has Cash > Applied. The CR side of the deposit hits
  -- account 24700 (Customer Deposits); the matching customer_deposits
  -- row + ar_open_items row are inserted in the same transaction.
  extra_deposit_amount numeric(20,6) NOT NULL DEFAULT 0,
  memo text,
  posted_at timestamptz,
  created_by_user_id uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT receive_payments_entity_number_format_chk CHECK (entity_number ~ '^EN[0-9]{4}[0-9]{8}$'),
  CONSTRAINT receive_payments_status_chk CHECK (status IN ('draft', 'posted', 'voided', 'reversed')),
  CONSTRAINT receive_payments_fx_rate_positive_chk CHECK (fx_rate > 0),
  CONSTRAINT receive_payments_total_amount_nonnegative_chk CHECK (total_amount >= 0),
  CONSTRAINT receive_payments_extra_deposit_nonnegative_chk CHECK (extra_deposit_amount >= 0),
  CONSTRAINT receive_payments_unique_company_payment_number UNIQUE (company_id, payment_number)
);

-- ---------------------------------------------------------------------------
-- Customer Deposits — overpayment parked as a future credit. One row is
-- inserted per receive_payment that has extra_deposit_amount > 0; that row
-- spawns a matching ar_open_items row with source_type='customer_deposit'
-- and balance_side='credit' so the deposit can later be consumed against
-- new invoices via a Receive Payment / Credit Application flow.
--
-- Lifecycle: deposits are immutable once created — they're always tied to
-- a receive_payment and any modification would unwind that posting. The
-- only edit/void path is for "orphan" deposits (source_receive_payment_id
-- is NULL), reserved for a future "manual deposit entry" flow that doesn't
-- exist today.
-- ---------------------------------------------------------------------------
CREATE TABLE customer_deposits (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  customer_id uuid NOT NULL REFERENCES customers(id) ON DELETE RESTRICT,
  entity_number text NOT NULL UNIQUE,
  display_number text NOT NULL,
  status text NOT NULL DEFAULT 'open',
  deposit_date date NOT NULL,
  transaction_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  base_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  fx_rate_snapshot_id uuid REFERENCES company_fx_rate_snapshots(id) ON DELETE RESTRICT,
  fx_rate numeric(20,10) NOT NULL DEFAULT 1,
  fx_requested_date date NOT NULL,
  fx_effective_date date NOT NULL,
  fx_source text NOT NULL DEFAULT 'identity',
  original_amount_tx numeric(20,6) NOT NULL,
  original_amount_base numeric(20,6) NOT NULL,
  source_receive_payment_id uuid REFERENCES receive_payments(id) ON DELETE RESTRICT,
  memo text,
  posted_at timestamptz,
  created_by_user_id uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT customer_deposits_entity_number_format_chk CHECK (entity_number ~ '^EN[0-9]{4}[0-9]{8}$'),
  CONSTRAINT customer_deposits_status_chk CHECK (status IN ('open', 'partially_applied', 'closed', 'voided')),
  CONSTRAINT customer_deposits_fx_rate_positive_chk CHECK (fx_rate > 0),
  CONSTRAINT customer_deposits_amount_positive_chk CHECK (original_amount_tx > 0 AND original_amount_base > 0),
  CONSTRAINT customer_deposits_unique_company_display_number UNIQUE (company_id, display_number)
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
  posting_role text,
  source_line_number integer,
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
  company_book_id uuid REFERENCES company_books(id) ON DELETE RESTRICT,
  entity_number text NOT NULL UNIQUE,
  display_number text NOT NULL,
  book_code text,
  accounting_standard text,
  revaluation_profile text,
  fx_rounding_policy text,
  status text NOT NULL,
  batch_kind text NOT NULL DEFAULT 'revaluation',
  reversal_of_fx_revaluation_batch_id uuid REFERENCES fx_revaluation_batches(id) ON DELETE RESTRICT,
  revaluation_date date NOT NULL,
  transaction_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  base_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  fx_rate_snapshot_id uuid REFERENCES company_fx_rate_snapshots(id) ON DELETE RESTRICT,
  fx_rate numeric(20,10) NOT NULL,
  rate_type text NOT NULL DEFAULT 'spot',
  quote_basis text NOT NULL DEFAULT 'direct',
  rate_use_case text NOT NULL DEFAULT 'remeasurement',
  posting_reason text NOT NULL DEFAULT 'revaluation',
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
  CONSTRAINT fx_revaluation_batches_standard_chk CHECK (
    accounting_standard IS NULL OR accounting_standard IN ('ASPE', 'IFRS', 'US_GAAP', 'TAX', 'MANAGEMENT')
  ),
  CONSTRAINT fx_revaluation_batches_rate_type_chk CHECK (
    rate_type IN ('spot', 'closing', 'average', 'historical', 'custom')
  ),
  CONSTRAINT fx_revaluation_batches_quote_basis_chk CHECK (
    quote_basis IN ('direct', 'inverse')
  ),
  CONSTRAINT fx_revaluation_batches_rate_use_case_chk CHECK (
    rate_use_case IN ('general', 'settlement', 'remeasurement', 'translation')
  ),
  CONSTRAINT fx_revaluation_batches_posting_reason_chk CHECK (
    posting_reason IN ('normal', 'settlement', 'revaluation', 'translation', 'adjustment')
  ),
  CONSTRAINT fx_revaluation_batches_profile_chk CHECK (
    revaluation_profile IS NULL OR revaluation_profile IN ('monetary_open_item_closing')
  ),
  CONSTRAINT fx_revaluation_batches_rounding_chk CHECK (
    fx_rounding_policy IS NULL OR fx_rounding_policy IN ('currency_precision')
  ),
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

CREATE INDEX idx_users_status_email
  ON users (status, email);

CREATE INDEX idx_account_verification_codes_active
  ON account_verification_codes (user_id, purpose, expires_at DESC)
  WHERE consumed_at IS NULL;

CREATE INDEX idx_sysadmin_accounts_status_email
  ON sysadmin_accounts (status, email);

CREATE INDEX idx_sysadmin_sessions_active
  ON sysadmin_sessions (sysadmin_account_id, expires_at DESC)
  WHERE revoked_at IS NULL;

CREATE INDEX idx_platform_notification_dispatches_status
  ON platform_notification_dispatches (status, created_at DESC);

CREATE INDEX idx_company_memberships_company_active
  ON company_memberships (company_id, is_active);

CREATE INDEX idx_business_sessions_user_company_expiry
  ON business_sessions (user_id, active_company_id, expires_at DESC);

CREATE INDEX idx_business_session_mfa_challenges_active
  ON business_session_mfa_challenges (user_id, factor, expires_at DESC)
  WHERE consumed_at IS NULL;

CREATE INDEX idx_account_mfa_recovery_requests_open
  ON account_mfa_recovery_requests (user_id, status, requested_at DESC)
  WHERE status IN ('requested', 'approved');

CREATE INDEX idx_account_mfa_totp_enrollments_active
  ON account_mfa_totp_enrollments (user_id, status, created_at DESC)
  WHERE status IN ('pending', 'active');

CREATE INDEX idx_company_currencies_company_enabled
  ON company_currencies (company_id, is_enabled);

CREATE INDEX idx_system_fx_market_rates_lookup
  ON system_fx_market_rates (provider_key, base_currency_code, quote_currency_code, rate_type, quote_basis, market_date DESC);

CREATE INDEX idx_company_fx_rate_snapshots_lookup
  ON company_fx_rate_snapshots (company_id, base_currency_code, quote_currency_code, rate_type, quote_basis, rate_use_case, requested_date DESC);

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

CREATE INDEX idx_fx_revaluation_batches_company_book
  ON fx_revaluation_batches (company_id, company_book_id, revaluation_date DESC);

CREATE UNIQUE INDEX idx_fx_revaluation_batches_active_reversal
  ON fx_revaluation_batches (reversal_of_fx_revaluation_batch_id)
  WHERE reversal_of_fx_revaluation_batch_id IS NOT NULL
    AND status <> 'voided';

CREATE INDEX idx_fx_revaluation_batch_lines_company_batch
  ON fx_revaluation_batch_lines (company_id, fx_revaluation_batch_id, line_number);

CREATE INDEX idx_fx_revaluation_batch_lines_company_target
  ON fx_revaluation_batch_lines (company_id, target_open_item_type, target_open_item_id);

CREATE UNIQUE INDEX uq_company_books_active_primary
  ON company_books (company_id)
  WHERE is_primary = true
    AND is_active = true;

CREATE INDEX idx_company_books_company_effective
  ON company_books (company_id, effective_from DESC, is_active);

CREATE UNIQUE INDEX uq_company_book_remeasurement_policies_active_book
  ON company_book_remeasurement_policies (company_book_id)
  WHERE is_active = true;

CREATE INDEX idx_company_book_remeasurement_policies_company_effective
  ON company_book_remeasurement_policies (company_id, company_book_id, effective_from DESC, is_active);

CREATE INDEX idx_company_book_governed_change_requests_company_created
  ON company_book_governed_change_requests (company_id, created_at DESC, id DESC);

CREATE INDEX idx_company_book_governed_change_requests_company_book_status
  ON company_book_governed_change_requests (company_id, company_book_id, status, effective_from DESC);

CREATE INDEX idx_company_book_governance_signals_company_book_date
  ON company_book_governance_signals (company_id, company_book_id, signal_date DESC, signal_type);

CREATE UNIQUE INDEX uq_company_book_governance_signals_company_book_identity
  ON company_book_governance_signals (
    company_id,
    company_book_id,
    signal_type,
    signal_date,
    coalesce(reference_label, '')
  );

CREATE INDEX idx_audit_logs_company_entity_created
  ON audit_logs (company_id, entity_type, entity_id, created_at DESC);

CREATE INDEX idx_audit_logs_action_created_at
  ON audit_logs (action, created_at DESC);

-- ---------------------------------------------------------------------------
-- updated_at triggers
-- ---------------------------------------------------------------------------

CREATE TRIGGER trg_users_set_updated_at
BEFORE UPDATE ON users
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_sysadmin_accounts_set_updated_at
BEFORE UPDATE ON sysadmin_accounts
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_platform_runtime_state_set_updated_at
BEFORE UPDATE ON platform_runtime_state
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_platform_notification_dispatches_set_updated_at
BEFORE UPDATE ON platform_notification_dispatches
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

CREATE TRIGGER trg_company_books_set_updated_at
BEFORE UPDATE ON company_books
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_company_book_remeasurement_policies_set_updated_at
BEFORE UPDATE ON company_book_remeasurement_policies
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_company_book_governed_change_requests_set_updated_at
BEFORE UPDATE ON company_book_governed_change_requests
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_company_book_governance_signals_set_updated_at
BEFORE UPDATE ON company_book_governance_signals
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

-- ============================================================================
-- Sales Receipts — cash-in-hand sales (no AR open item).
-- Mirrors `invoices` shape with these deltas:
--   • no due_date (paid at point of sale)
--   • carries the deposit_to_account_id, payment_method, reference_no
--     so downstream bank-rec can match the bank-statement line
--   • status set is narrower — there's no "partially_paid" state
--     because the receipt settles in one shot
-- ============================================================================
CREATE TABLE sales_receipts (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  entity_number text NOT NULL UNIQUE,
  receipt_number text NOT NULL,
  customer_id uuid NOT NULL REFERENCES customers(id) ON DELETE RESTRICT,
  status text NOT NULL DEFAULT 'draft',
  receipt_date date NOT NULL,
  document_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  base_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  fx_rate_snapshot_id uuid REFERENCES company_fx_rate_snapshots(id) ON DELETE RESTRICT,
  fx_rate numeric(20,10) NOT NULL DEFAULT 1,
  fx_requested_date date NOT NULL,
  fx_effective_date date NOT NULL,
  fx_source text NOT NULL DEFAULT 'identity',
  -- Deposit destination — the asset account that absorbs the cash.
  -- Holding-account workflow (deposit lands in Undeposited Funds first
  -- and a later Bank Deposit moves it to the real bank) is the
  -- expected pattern; sales_receipts.deposit_to_account_id can point
  -- at either depending on the operator's choice.
  deposit_to_account_id uuid NOT NULL REFERENCES accounts(id) ON DELETE RESTRICT,
  payment_method text NOT NULL DEFAULT 'cash',
  reference_no text,
  subtotal_amount numeric(20,6) NOT NULL DEFAULT 0,
  tax_amount numeric(20,6) NOT NULL DEFAULT 0,
  total_amount numeric(20,6) NOT NULL DEFAULT 0,
  memo text,
  posted_at timestamptz,
  created_by_user_id uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT sales_receipts_entity_number_format_chk CHECK (entity_number ~ '^EN[0-9]{4}[0-9]{8}$'),
  CONSTRAINT sales_receipts_status_chk CHECK (
    status IN ('draft', 'posted', 'voided', 'reversed')
  ),
  CONSTRAINT sales_receipts_payment_method_chk CHECK (
    payment_method IN ('cash', 'cheque', 'credit_card', 'wire', 'direct_deposit', 'eft', 'other')
  ),
  CONSTRAINT sales_receipts_fx_rate_positive_chk CHECK (fx_rate > 0),
  CONSTRAINT sales_receipts_unique_company_receipt_number UNIQUE (company_id, receipt_number)
);

CREATE TABLE sales_receipt_lines (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  sales_receipt_id uuid NOT NULL REFERENCES sales_receipts(id) ON DELETE CASCADE,
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
  CONSTRAINT sales_receipt_lines_quantity_nonnegative_chk CHECK (quantity >= 0),
  CONSTRAINT sales_receipt_lines_unit_price_nonnegative_chk CHECK (unit_price >= 0),
  CONSTRAINT sales_receipt_lines_unique_line UNIQUE (sales_receipt_id, line_number)
);

CREATE INDEX ix_sales_receipts_company_status ON sales_receipts (company_id, status);
CREATE INDEX ix_sales_receipts_company_customer ON sales_receipts (company_id, customer_id);
CREATE INDEX ix_sales_receipt_lines_sales_receipt ON sales_receipt_lines (sales_receipt_id, line_number);

-- ============================================================================
-- Refund Receipts — cash-out customer refunds (no AR open item).
-- Mirror of sales_receipts: same shape, opposite GL polarity at post
-- time. We keep them as a separate table (rather than a signed
-- amount on sales_receipts) because they have a distinct lifecycle,
-- distinct numbering sequence, and reporting categorises them
-- separately on cash-flow statements.
-- ============================================================================
CREATE TABLE refund_receipts (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  entity_number text NOT NULL UNIQUE,
  refund_number text NOT NULL,
  customer_id uuid NOT NULL REFERENCES customers(id) ON DELETE RESTRICT,
  status text NOT NULL DEFAULT 'draft',
  refund_date date NOT NULL,
  document_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  base_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  fx_rate_snapshot_id uuid REFERENCES company_fx_rate_snapshots(id) ON DELETE RESTRICT,
  fx_rate numeric(20,10) NOT NULL DEFAULT 1,
  fx_requested_date date NOT NULL,
  fx_effective_date date NOT NULL,
  fx_source text NOT NULL DEFAULT 'identity',
  -- Source of funds — the asset account the money LEAVES.
  refund_from_account_id uuid NOT NULL REFERENCES accounts(id) ON DELETE RESTRICT,
  payment_method text NOT NULL DEFAULT 'cash',
  reference_no text,
  reason text,
  subtotal_amount numeric(20,6) NOT NULL DEFAULT 0,
  tax_amount numeric(20,6) NOT NULL DEFAULT 0,
  total_amount numeric(20,6) NOT NULL DEFAULT 0,
  memo text,
  posted_at timestamptz,
  created_by_user_id uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT refund_receipts_entity_number_format_chk CHECK (entity_number ~ '^EN[0-9]{4}[0-9]{8}$'),
  CONSTRAINT refund_receipts_status_chk CHECK (
    status IN ('draft', 'posted', 'voided', 'reversed')
  ),
  CONSTRAINT refund_receipts_payment_method_chk CHECK (
    payment_method IN ('cash', 'cheque', 'credit_card', 'wire', 'direct_deposit', 'eft', 'other')
  ),
  CONSTRAINT refund_receipts_fx_rate_positive_chk CHECK (fx_rate > 0),
  CONSTRAINT refund_receipts_unique_company_refund_number UNIQUE (company_id, refund_number)
);

CREATE TABLE refund_receipt_lines (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  refund_receipt_id uuid NOT NULL REFERENCES refund_receipts(id) ON DELETE CASCADE,
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
  CONSTRAINT refund_receipt_lines_quantity_nonnegative_chk CHECK (quantity >= 0),
  CONSTRAINT refund_receipt_lines_unit_price_nonnegative_chk CHECK (unit_price >= 0),
  CONSTRAINT refund_receipt_lines_unique_line UNIQUE (refund_receipt_id, line_number)
);

CREATE INDEX ix_refund_receipts_company_status ON refund_receipts (company_id, status);
CREATE INDEX ix_refund_receipts_company_customer ON refund_receipts (company_id, customer_id);
CREATE INDEX ix_refund_receipt_lines_refund_receipt ON refund_receipt_lines (refund_receipt_id, line_number);

-- ============================================================================
-- Bank Transfers — internal asset → asset movement (operating →
-- savings, USD wallet → CAD wallet, etc.). Single record per
-- transfer; no lines table because there's exactly one source and
-- one destination.
--
-- Cross-currency transfers carry the operator-supplied fx_rate; the
-- backend uses each account's currency snapshot rate for the
-- base-currency JE rows so the audit trail uses
-- fx_rates_daily-grade rates rather than the bank's rate.
-- ============================================================================
CREATE TABLE bank_transfers (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  entity_number text NOT NULL UNIQUE,
  transfer_number text NOT NULL,
  status text NOT NULL DEFAULT 'draft',
  transfer_date date NOT NULL,
  from_account_id uuid NOT NULL REFERENCES accounts(id) ON DELETE RESTRICT,
  from_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  to_account_id uuid NOT NULL REFERENCES accounts(id) ON DELETE RESTRICT,
  to_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  -- Amount is in from_currency. The backend computes
  -- to_currency_amount = amount * fx_rate when currencies differ.
  amount numeric(20,6) NOT NULL,
  fx_rate numeric(20,10),
  reference_no text,
  memo text,
  posted_at timestamptz,
  created_by_user_id uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT bank_transfers_entity_number_format_chk CHECK (entity_number ~ '^EN[0-9]{4}[0-9]{8}$'),
  CONSTRAINT bank_transfers_status_chk CHECK (
    status IN ('draft', 'posted', 'voided', 'reversed')
  ),
  CONSTRAINT bank_transfers_amount_positive_chk CHECK (amount > 0),
  CONSTRAINT bank_transfers_distinct_accounts_chk CHECK (from_account_id <> to_account_id),
  CONSTRAINT bank_transfers_fx_rate_positive_chk CHECK (fx_rate IS NULL OR fx_rate > 0),
  -- Same-currency transfers MUST have null fx_rate; cross-currency
  -- transfers MUST have a positive fx_rate. Enforced at the row
  -- level so a malformed write never makes it past INSERT.
  CONSTRAINT bank_transfers_fx_rate_polarity_chk CHECK (
    (from_currency_code = to_currency_code AND fx_rate IS NULL) OR
    (from_currency_code <> to_currency_code AND fx_rate IS NOT NULL)
  ),
  CONSTRAINT bank_transfers_unique_company_transfer_number UNIQUE (company_id, transfer_number)
);

CREATE INDEX ix_bank_transfers_company_status ON bank_transfers (company_id, status);
CREATE INDEX ix_bank_transfers_company_from ON bank_transfers (company_id, from_account_id);
CREATE INDEX ix_bank_transfers_company_to ON bank_transfers (company_id, to_account_id);

-- ============================================================================
-- Bank Deposits — group N Undeposited-Funds items into one bank
-- statement-shaped entry. Header row identifies the destination bank
-- account; the line rows reference the source receipts / payments
-- being grouped.
--
-- The source_item_id column is intentionally a free-form uuid + text
-- pair (no FK) because the source can come from any of:
--   sales_receipts, receive_payments, journal entries, ...
-- and a polymorphic FK in Postgres is more friction than value. The
-- backend resolves source_item_kind + source_item_id at post time.
-- ============================================================================
CREATE TABLE bank_deposits (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  entity_number text NOT NULL UNIQUE,
  deposit_number text NOT NULL,
  status text NOT NULL DEFAULT 'draft',
  deposit_date date NOT NULL,
  deposit_to_account_id uuid NOT NULL REFERENCES accounts(id) ON DELETE RESTRICT,
  document_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  total_amount numeric(20,6) NOT NULL DEFAULT 0,
  reference_no text,
  memo text,
  posted_at timestamptz,
  created_by_user_id uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT bank_deposits_entity_number_format_chk CHECK (entity_number ~ '^EN[0-9]{4}[0-9]{8}$'),
  CONSTRAINT bank_deposits_status_chk CHECK (
    status IN ('draft', 'posted', 'voided', 'reversed')
  ),
  CONSTRAINT bank_deposits_total_nonnegative_chk CHECK (total_amount >= 0),
  CONSTRAINT bank_deposits_unique_company_deposit_number UNIQUE (company_id, deposit_number)
);

CREATE TABLE bank_deposit_items (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  bank_deposit_id uuid NOT NULL REFERENCES bank_deposits(id) ON DELETE CASCADE,
  line_number integer NOT NULL,
  -- Loose pointer at the source document. backend resolves to the
  -- right table by source_item_kind. text key keeps the schema small;
  -- the (kind, id) pair is the canonical reference.
  source_item_kind text NOT NULL,
  source_item_id uuid,
  source_item_display_number text NOT NULL,
  payer_name text,
  payment_method text,
  reference_no text,
  amount numeric(20,6) NOT NULL,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT bank_deposit_items_amount_positive_chk CHECK (amount > 0),
  CONSTRAINT bank_deposit_items_kind_chk CHECK (
    source_item_kind IN ('sales_receipt', 'receive_payment', 'journal_entry', 'manual')
  ),
  CONSTRAINT bank_deposit_items_payment_method_chk CHECK (
    payment_method IS NULL OR payment_method IN
      ('cash', 'cheque', 'credit_card', 'wire', 'direct_deposit', 'eft', 'other')
  ),
  CONSTRAINT bank_deposit_items_unique_line UNIQUE (bank_deposit_id, line_number)
);

CREATE INDEX ix_bank_deposits_company_status ON bank_deposits (company_id, status);
CREATE INDEX ix_bank_deposits_company_deposit_to ON bank_deposits (company_id, deposit_to_account_id);
CREATE INDEX ix_bank_deposit_items_bank_deposit ON bank_deposit_items (bank_deposit_id, line_number);
CREATE INDEX ix_bank_deposit_items_source ON bank_deposit_items (company_id, source_item_kind, source_item_id);

-- ============================================================================
-- Tax Returns — period close for a sales-tax regime. One row per
-- (company, regime, period). Adjustments are a single signed amount
-- with a free-form note (CRA / Revenu Quebec corrections, recapture,
-- etc.); detailed line-by-line box mapping is out of scope for V1
-- and lands in a follow-on tax_return_adjustments table when the
-- regulator-form Engine ships.
-- ============================================================================
CREATE TABLE tax_returns (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
  entity_number text NOT NULL UNIQUE,
  return_number text NOT NULL,
  status text NOT NULL DEFAULT 'draft',
  tax_regime text NOT NULL,
  filing_frequency text NOT NULL,
  period_start date NOT NULL,
  period_end date NOT NULL,
  base_currency_code char(3) NOT NULL REFERENCES currency_catalog(code) ON DELETE RESTRICT,
  collected_amount numeric(20,6) NOT NULL DEFAULT 0,
  input_credits_amount numeric(20,6) NOT NULL DEFAULT 0,
  adjustments_amount numeric(20,6) NOT NULL DEFAULT 0,
  adjustments_note text,
  -- Net = collected - input_credits + adjustments (signed). Stored
  -- so reports can sum without recomputing; the GL contract in
  -- TaxReturnDraft.cs documents the binding arithmetic.
  net_amount numeric(20,6) NOT NULL DEFAULT 0,
  regulator_reference_no text,
  memo text,
  posted_at timestamptz,
  created_by_user_id uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
  created_at timestamptz NOT NULL DEFAULT NOW(),
  updated_at timestamptz NOT NULL DEFAULT NOW(),
  CONSTRAINT tax_returns_entity_number_format_chk CHECK (entity_number ~ '^EN[0-9]{4}[0-9]{8}$'),
  CONSTRAINT tax_returns_status_chk CHECK (
    status IN ('draft', 'posted', 'voided', 'amended')
  ),
  CONSTRAINT tax_returns_filing_frequency_chk CHECK (
    filing_frequency IN ('monthly', 'quarterly', 'annual')
  ),
  CONSTRAINT tax_returns_period_chk CHECK (period_end >= period_start),
  CONSTRAINT tax_returns_unique_company_return_number UNIQUE (company_id, return_number),
  -- Only one posted return per (company, regime, period_end) — once
  -- a period is filed we lock it. Drafts are unrestricted; amendments
  -- go through the 'amended' status with a follow-on row.
  CONSTRAINT tax_returns_unique_posted_period UNIQUE (company_id, tax_regime, period_end, status)
    DEFERRABLE INITIALLY DEFERRED
);

CREATE INDEX ix_tax_returns_company_status ON tax_returns (company_id, status);
CREATE INDEX ix_tax_returns_company_period ON tax_returns (company_id, tax_regime, period_end);

-- updated_at triggers for the new tables
CREATE TRIGGER trg_sales_receipts_set_updated_at
BEFORE UPDATE ON sales_receipts
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_sales_receipt_lines_set_updated_at
BEFORE UPDATE ON sales_receipt_lines
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_refund_receipts_set_updated_at
BEFORE UPDATE ON refund_receipts
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_refund_receipt_lines_set_updated_at
BEFORE UPDATE ON refund_receipt_lines
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_bank_transfers_set_updated_at
BEFORE UPDATE ON bank_transfers
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_bank_deposits_set_updated_at
BEFORE UPDATE ON bank_deposits
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_bank_deposit_items_set_updated_at
BEFORE UPDATE ON bank_deposit_items
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

CREATE TRIGGER trg_tax_returns_set_updated_at
BEFORE UPDATE ON tax_returns
FOR EACH ROW
EXECUTE FUNCTION citus_set_updated_at();

COMMIT;
