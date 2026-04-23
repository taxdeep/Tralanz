# PostgreSQL Core Schema Spec

## 1. Purpose

This document defines the target PostgreSQL schema direction for Citus core accounting.

It covers:

- platform and company identity
- company isolation
- multi-currency and FX persistence
- chart of accounts and tax
- source documents
- posted accounting truth
- AP/AR open-item control
- audit and migration guidance

Primary SQL draft:

- [CITUS_POSTGRESQL_MIGRATION_DRAFT.sql](./CITUS_POSTGRESQL_MIGRATION_DRAFT.sql)

Authority order:

`CITUS_PRODUCT_ENGINEERING_AUTHORITY.md > this document > POSTING_TAX_FX_ENGINE_EXECUTION_SPEC.md > POSTING_ENGINE_MULTICURRENCY_DESIGN.md > task notes > temporary implementation habits`

## 2. Platform Database Principles

- Database engine: `PostgreSQL`
- Core IDs: use `uuid`
- Business identity: use immutable backend-generated `entity_number`
- All core business/accounting rows are company-scoped unless explicitly declared as system-infrastructure
- Monetary columns use `NUMERIC`, never `float/double`
- Timestamps use `timestamptz`

Recommended numeric strategy:

- FX rate: `NUMERIC(20,10)`
- Generic money storage: `NUMERIC(20,6)`

Reason:

- current common currencies can still post/display at `minor_unit = 2`
- future 0-decimal and 3-decimal currencies remain possible without schema rewrite
- posting/output rounding remains governed by currency `minor_unit`

## 3. Identity And Numbering

Every formal business/accounting entity should distinguish:

- internal PK: `id uuid`
- immutable system identity: `entity_number text`
- human display number where applicable

Examples:

- `EN202600000123`
- `invoice_number`
- `bill_number`
- `journal_display_number`

## 4. Company And Security Core

Boundary rule:

- platform account storage is identity infrastructure, not a Business App root module
- company access truth belongs to `company_memberships`
- columns named `user_id` or `created_by_user_id` in older drafts should be understood as platform account actor references
- future migrations may rename these to `account_id` / `created_by_account_id` when migration risk is acceptable

### 4.1 Platform account storage (`users` draft table)

Meaning:

- stores platform account identity for login and actor references
- does not own company membership truth
- does not own company-scoped permission truth
- must not become a Business App `Users` / `UserManagement` root module

Physical table name in the current SQL draft:

- `users`

Preferred semantic name in application design:

- `platform_accounts`

Core columns:

- `id uuid pk`
- `email text unique not null`
- `username text unique null`
- `display_name text null`
- `password_hash text not null`
- `status text not null`
- `email_verified_at timestamptz null`
- `locked_until timestamptz null`
- `security_stamp text not null`
- `created_at timestamptz not null`
- `updated_at timestamptz not null`

Recommended `status` values:

- `active`
- `disabled`
- `locked`
- `pending_verification`

Rules:

- account status gates login
- platform account status does not grant company access by itself
- an account may have zero, one, or many company memberships
- deleting accounts with business history should be avoided; disable/lock is preferred
- Platform Identity may later be backed by ABP Identity / Account, but CompanyAccess remains Citus-owned

### 4.1.1 `account_verification_codes`

Meaning:

- secure verification workflow for email change, password change, reset password, and email verification
- verification is account infrastructure, not CompanyAccess truth

Core columns:

- `id uuid pk`
- `account_id uuid not null`
- `purpose text not null`
- `destination text null`
- `code_hash text not null`
- `expires_at timestamptz not null`
- `consumed_at timestamptz null`
- `failed_attempts integer not null default 0`
- `created_at timestamptz not null`

Recommended `purpose` values:

- `email_verification`
- `email_change`
- `password_change`
- `password_reset`

Rules:

- generated code is 6 characters
- validation is case-insensitive
- store hash only, never plaintext code
- code is single-use
- code is time-limited
- verification issuance requires notification readiness where the flow depends on email delivery

### 4.1.2 SysAdmin account storage

Meaning:

- independent PlatformOps / SysAdmin identity realm
- not a company member
- not valid as a business posting actor

Recommended physical direction if Citus owns SysAdmin credentials directly:

- `sysadmin_accounts`

Core columns:

- `id uuid pk`
- `email text unique not null`
- `password_hash text not null`
- `status text not null`
- `last_login_at timestamptz null`
- `created_at timestamptz not null`
- `updated_at timestamptz not null`

Rules:

- SysAdmin auth must not reuse Business App membership
- SysAdmin sessions must not carry `active_company_id` for business operations
- SysAdmin governance commands route to the boundary that owns the truth

### 4.1.3 Platform runtime state

Meaning:

- system-level runtime controls such as maintenance mode
- available to SysAdmin
- enforced by Business App backend guards

Recommended table:

- `platform_runtime_state`

Core columns:

- `state_key text pk`
- `json jsonb not null`
- `updated_by_sysadmin_account_id uuid null`
- `updated_at timestamptz not null`

Required state keys:

- `maintenance`

Rules:

- maintenance mode blocks normal Business App login and writes
- SysAdmin remains available during maintenance
- state changes must be audited

### 4.1.4 Platform billing / subscription storage

Meaning:

- commercial entitlement truth
- controls plan, trial, subscription, seats, features, and usage limits
- does not own company membership or business legality

Recommended later tables:

- `platform_billing_accounts`
- `platform_subscriptions`
- `platform_entitlements`

Rules:

- entitlements may enable or disable capabilities
- entitlements must not rewrite posted accounting truth
- entitlements must not replace CompanyAccess membership or domain legality

### 4.2 `companies`

Core columns:

- `id uuid pk`
- `entity_number text unique not null`
- `legal_name text not null`
- `base_currency_code char(3) not null`
- `multi_currency_enabled boolean not null default false`
- `status text not null`
- `created_at timestamptz not null`
- `updated_at timestamptz not null`

### 4.3 `company_memberships`

Meaning:

- authoritative CompanyAccess relationship between a platform account and a company
- owns owner/user role and company-scoped permission tokens
- not a credential table

Core columns:

- `id uuid pk`
- `company_id uuid not null`
- `account_id uuid not null`
- `role text not null`
- `is_active boolean not null default true`
- `permissions jsonb not null default '[]'::jsonb`
- `created_at timestamptz not null`
- `updated_at timestamptz not null`

Constraints:

- unique `(company_id, account_id)`
- at least one active owner per company enforced in application/service layer plus DB-safe guard flow

Rules:

- `role` values are `owner` and `user`
- `permissions` is a JSON array of company-scoped permission tokens
- owner assignment and permission changes are CompanyAccess operations
- SysAdmin may trigger membership actions, but CompanyAccess remains authoritative
- active-company selection must be validated against active membership and company status

### 4.4 `business_sessions`

Meaning:

- resolved Business App session context
- ties authenticated account to active company and membership-derived permissions

Core columns:

- `id uuid pk`
- `token_hash text unique not null`
- `account_id uuid not null`
- `active_company_id uuid not null`
- `membership_id uuid not null`
- `role text not null`
- `permissions jsonb not null`
- `company_status text not null`
- `permission_version text null`
- `expires_at timestamptz not null`
- `created_at timestamptz not null`

Rules:

- no Business App read/write may execute without active company context
- session data is a resolved context, not the source of membership truth
- backend APIs must reject stale or invalidated membership/company state
- inactive company allows read-only behavior only

### 4.5 `platform_modules`

Meaning:

- system-level module registry
- makes bounded contexts explicit instead of leaving them implied by API shape
- adapted from the plugin/kernel direction used in `WebVella.Erp`

Core columns:

- `id uuid pk`
- `module_key text unique not null`
- `json jsonb not null`
- `created_at timestamptz not null`
- `updated_at timestamptz not null`

### 4.6 `platform_entities`

Meaning:

- system-level entity metadata registry
- describes which module owns an entity and which storage table is authoritative
- gives `Citus.SysAdmin.Api` a durable control surface for platform governance

Core columns:

- `id uuid pk`
- `entity_name text unique not null`
- `module_key text not null`
- `storage_table text not null`
- `json jsonb not null`
- `created_at timestamptz not null`
- `updated_at timestamptz not null`

## 5. Currency And FX Structure

## 5.1 Design Split

To preserve company isolation while still allowing provider ingestion:

- `currency_catalog` is a system reference table
- `system_fx_market_rates` is provider-ingestion cache
- `company_fx_rate_snapshots` is company-approved accounting lookup table

Only company snapshots are valid posting inputs.

### 5.2 `currency_catalog`

Core columns:

- `code char(3) pk`
- `name text not null`
- `minor_unit smallint not null`
- `is_active boolean not null default true`

### 5.3 `company_currencies`

Core columns:

- `id uuid pk`
- `company_id uuid not null`
- `currency_code char(3) not null`
- `is_enabled boolean not null default true`
- `created_at timestamptz not null`

Constraints:

- unique `(company_id, currency_code)`

### 5.4 `system_fx_market_rates`

Meaning:

- raw provider ingestion cache
- not accounting truth
- may be shared infrastructure data

Core columns:

- `id uuid pk`
- `provider_key text not null`
- `base_currency_code char(3) not null`
- `quote_currency_code char(3) not null`
- `market_date date not null`
- `rate numeric(20,10) not null`
- `fetched_at timestamptz not null`
- `payload jsonb null`

Constraints:

- unique `(provider_key, base_currency_code, quote_currency_code, market_date)`

### 5.5 `company_fx_rate_snapshots`

Meaning:

- company-scoped locally accepted FX snapshots
- valid input to save/post logic
- may come from provider fetch, company override, or manual entry

Core columns:

- `id uuid pk`
- `company_id uuid not null`
- `base_currency_code char(3) not null`
- `quote_currency_code char(3) not null`
- `requested_date date not null`
- `effective_date date not null`
- `rate numeric(20,10) not null`
- `provider_key text null`
- `row_origin text not null`
- `snapshot_semantics text not null`
- `system_market_rate_id uuid null`
- `notes text null`
- `created_by_user_id uuid null`
- `created_at timestamptz not null`

Recommended `row_origin` values:

- `manual`
- `provider_fetched`
- `legacy_unknown`

Recommended `snapshot_semantics` values:

- `identity`
- `manual`
- `company_override`
- `system_stored`
- `provider_fetched`

Constraints:

- unique `(company_id, base_currency_code, quote_currency_code, requested_date, snapshot_semantics, coalesce(provider_key, ''))`

## 6. Numbering And Company Settings

### 6.1 `company_numbering_sequences`

Core columns:

- `id uuid pk`
- `company_id uuid not null`
- `scope_key text not null`
- `prefix text null`
- `next_number bigint not null`
- `padding smallint not null`
- `suggestion_enabled boolean not null default true`
- `updated_at timestamptz not null`

Constraints:

- unique `(company_id, scope_key)`

### 6.2 `company_settings`

Core columns:

- `company_id uuid pk`
- `profile jsonb not null default '{}'::jsonb`
- `security jsonb not null default '{}'::jsonb`
- `notification jsonb not null default '{}'::jsonb`
- `currency jsonb not null default '{}'::jsonb`
- `updated_at timestamptz not null`

## 7. Chart Of Accounts And Tax

### 7.1 `accounts`

Core columns:

- `id uuid pk`
- `company_id uuid not null`
- `entity_number text unique not null`
- `code text not null`
- `name text not null`
- `root_type text not null`
- `detail_type text not null`
- `is_active boolean not null default true`
- `is_system boolean not null default false`
- `is_system_default boolean not null default false`
- `system_key text null`
- `system_role text null`
- `currency_code char(3) null`
- `allow_manual_posting boolean not null default true`
- `created_at timestamptz not null`
- `updated_at timestamptz not null`

Constraints:

- unique `(company_id, code)`

### 7.2 `tax_codes`

Core columns:

- `id uuid pk`
- `company_id uuid not null`
- `entity_number text unique not null`
- `code text not null`
- `name text not null`
- `rate_percent numeric(9,6) not null`
- `applies_to text not null`
- `is_recoverable_on_purchase boolean not null default false`
- `recoverability_mode text not null default 'full'`
- `payable_account_id uuid null`
- `recoverable_account_id uuid null`
- `is_active boolean not null default true`
- `created_at timestamptz not null`
- `updated_at timestamptz not null`

Constraints:

- unique `(company_id, code)`

## 8. Parties

### 8.1 `customers`

Core columns:

- `id uuid pk`
- `company_id uuid not null`
- `entity_number text unique not null`
- `display_name text not null`
- `default_currency_code char(3) not null`
- `email text null`
- `phone text null`
- `is_active boolean not null default true`
- `currency_locked boolean not null default false`
- `created_at timestamptz not null`
- `updated_at timestamptz not null`

### 8.2 `vendors`

Core columns mirror `customers`.

## 9. Source Documents

Important principle:

- source documents are business truth
- posted JE is accounting result
- manual journal should also be treated as a source-document family

### 9.1 `invoices`

Core columns:

- `id uuid pk`
- `company_id uuid not null`
- `entity_number text unique not null`
- `invoice_number text not null`
- `customer_id uuid not null`
- `status text not null`
- `invoice_date date not null`
- `due_date date not null`
- `document_currency_code char(3) not null`
- `base_currency_code char(3) not null`
- `fx_rate_snapshot_id uuid null`
- `fx_rate numeric(20,10) not null default 1`
- `fx_requested_date date not null`
- `fx_effective_date date not null`
- `fx_source text not null`
- `subtotal_amount numeric(20,6) not null`
- `tax_amount numeric(20,6) not null`
- `total_amount numeric(20,6) not null`
- `memo text null`
- `posted_at timestamptz null`
- `created_by_user_id uuid not null`
- `created_at timestamptz not null`
- `updated_at timestamptz not null`

Constraints:

- unique `(company_id, invoice_number)`

### 9.2 `invoice_lines`

Core columns:

- `id uuid pk`
- `invoice_id uuid not null`
- `line_number integer not null`
- `revenue_account_id uuid not null`
- `description text not null`
- `quantity numeric(20,6) not null`
- `unit_price numeric(20,6) not null`
- `line_amount numeric(20,6) not null`
- `tax_code_id uuid null`
- `tax_amount numeric(20,6) not null default 0`

Constraints:

- unique `(invoice_id, line_number)`

### 9.3 `credit_notes`

Structure mirrors invoices but creates a credit-balance AR open item after posting.

Core columns:

- `id uuid pk`
- `company_id uuid not null`
- `entity_number text unique not null`
- `credit_note_number text not null`
- `customer_id uuid not null`
- `status text not null`
- `credit_note_date date not null`
- `due_date date not null`
- `document_currency_code char(3) not null`
- `base_currency_code char(3) not null`
- `fx_rate_snapshot_id uuid null`
- `fx_rate numeric(20,10) not null default 1`
- `fx_requested_date date not null`
- `fx_effective_date date not null`
- `fx_source text not null`
- `subtotal_amount numeric(20,6) not null`
- `tax_amount numeric(20,6) not null`
- `total_amount numeric(20,6) not null`
- `memo text null`
- `posted_at timestamptz null`
- `created_by_user_id uuid not null`
- `created_at timestamptz not null`
- `updated_at timestamptz not null`

Constraints:

- unique `(company_id, credit_note_number)`

### 9.4 `credit_note_lines`

Core columns:

- `id uuid pk`
- `credit_note_id uuid not null`
- `line_number integer not null`
- `revenue_account_id uuid not null`
- `description text not null`
- `quantity numeric(20,6) not null`
- `unit_price numeric(20,6) not null`
- `line_amount numeric(20,6) not null`
- `tax_code_id uuid null`
- `tax_amount numeric(20,6) not null default 0`

Constraints:

- unique `(credit_note_id, line_number)`

### 9.5 `bills`

Structure mirrors invoices but routes to vendor/AP behavior.

Core columns:

- `id uuid pk`
- `company_id uuid not null`
- `entity_number text unique not null`
- `bill_number text not null`
- `vendor_id uuid not null`
- `status text not null`
- `bill_date date not null`
- `due_date date not null`
- `document_currency_code char(3) not null`
- `base_currency_code char(3) not null`
- `fx_rate_snapshot_id uuid null`
- `fx_rate numeric(20,10) not null default 1`
- `fx_requested_date date not null`
- `fx_effective_date date not null`
- `fx_source text not null`
- `subtotal_amount numeric(20,6) not null`
- `tax_amount numeric(20,6) not null`
- `total_amount numeric(20,6) not null`
- `memo text null`
- `posted_at timestamptz null`
- `created_by_user_id uuid not null`
- `created_at timestamptz not null`
- `updated_at timestamptz not null`

### 9.6 `bill_lines`

Core columns:

- `id uuid pk`
- `bill_id uuid not null`
- `line_number integer not null`
- `expense_account_id uuid not null`
- `description text not null`
- `line_amount numeric(20,6) not null`
- `tax_code_id uuid null`
- `tax_amount numeric(20,6) not null default 0`
- `is_tax_recoverable boolean not null default false`

### 9.7 `vendor_credits`

Structure mirrors bills but creates a debit-balance AP open item after posting.

Core columns:

- `id uuid pk`
- `company_id uuid not null`
- `entity_number text unique not null`
- `vendor_credit_number text not null`
- `vendor_id uuid not null`
- `status text not null`
- `vendor_credit_date date not null`
- `due_date date not null`
- `document_currency_code char(3) not null`
- `base_currency_code char(3) not null`
- `fx_rate_snapshot_id uuid null`
- `fx_rate numeric(20,10) not null default 1`
- `fx_requested_date date not null`
- `fx_effective_date date not null`
- `fx_source text not null`
- `subtotal_amount numeric(20,6) not null`
- `tax_amount numeric(20,6) not null`
- `total_amount numeric(20,6) not null`
- `memo text null`
- `posted_at timestamptz null`
- `created_by_user_id uuid not null`
- `created_at timestamptz not null`
- `updated_at timestamptz not null`

Constraints:

- unique `(company_id, vendor_credit_number)`

### 9.8 `vendor_credit_lines`

Core columns:

- `id uuid pk`
- `vendor_credit_id uuid not null`
- `line_number integer not null`
- `expense_account_id uuid not null`
- `description text not null`
- `line_amount numeric(20,6) not null`
- `tax_code_id uuid null`
- `tax_amount numeric(20,6) not null default 0`
- `is_tax_recoverable boolean not null default false`

### 9.9 `receive_payments`

Core columns:

- `id uuid pk`
- `company_id uuid not null`
- `entity_number text unique not null`
- `payment_number text not null`
- `customer_id uuid not null`
- `status text not null`
- `payment_date date not null`
- `bank_account_id uuid not null`
- `document_currency_code char(3) not null`
- `base_currency_code char(3) not null`
- `fx_rate_snapshot_id uuid null`
- `fx_rate numeric(20,10) not null default 1`
- `fx_requested_date date not null`
- `fx_effective_date date not null`
- `fx_source text not null`
- `total_amount numeric(20,6) not null`
- `memo text null`
- `posted_at timestamptz null`
- `created_by_user_id uuid not null`
- `created_at timestamptz not null`
- `updated_at timestamptz not null`

Constraints:

- unique `(company_id, payment_number)`

### 9.10 `receive_payment_lines`

Core columns:

- `id uuid pk`
- `receive_payment_id uuid not null`
- `line_number integer not null`
- `target_ar_open_item_id uuid not null`
- `applied_amount_tx numeric(20,6) not null`

Constraints:

- unique `(receive_payment_id, line_number)`

### 9.11 `pay_bills`

Core columns:

- `id uuid pk`
- `company_id uuid not null`
- `entity_number text unique not null`
- `payment_number text not null`
- `vendor_id uuid not null`
- `status text not null`
- `payment_date date not null`
- `bank_account_id uuid not null`
- `document_currency_code char(3) not null`
- `base_currency_code char(3) not null`
- `fx_rate_snapshot_id uuid null`
- `fx_rate numeric(20,10) not null default 1`
- `fx_requested_date date not null`
- `fx_effective_date date not null`
- `fx_source text not null`
- `total_amount numeric(20,6) not null`
- `memo text null`
- `posted_at timestamptz null`
- `created_by_user_id uuid not null`
- `created_at timestamptz not null`
- `updated_at timestamptz not null`

Constraints:

- unique `(company_id, payment_number)`

### 9.12 `pay_bill_lines`

Core columns:

- `id uuid pk`
- `pay_bill_id uuid not null`
- `line_number integer not null`
- `target_ap_open_item_id uuid not null`
- `applied_amount_tx numeric(20,6) not null`

Constraints:

- unique `(pay_bill_id, line_number)`

### 9.13 `credit_applications`

Formal source document for applying customer credit-note AR open items against invoice AR open items.

Core columns:

- `id uuid pk`
- `company_id uuid not null`
- `entity_number text unique not null`
- `application_number text not null`
- `customer_id uuid not null`
- `status text not null`
- `application_date date not null`
- `document_currency_code char(3) not null`
- `base_currency_code char(3) not null`
- `total_amount numeric(20,6) not null`
- `memo text null`
- `posted_at timestamptz null`
- `created_by_user_id uuid not null`
- `created_at timestamptz not null`
- `updated_at timestamptz not null`

Constraints:

- unique `(company_id, application_number)`

### 9.14 `credit_application_lines`

Core columns:

- `id uuid pk`
- `credit_application_id uuid not null`
- `line_number integer not null`
- `source_credit_ar_open_item_id uuid not null`
- `target_invoice_ar_open_item_id uuid not null`
- `applied_amount_tx numeric(20,6) not null`

Constraints:

- unique `(credit_application_id, line_number)`

### 9.15 `vendor_credit_applications`

Formal source document for applying vendor-credit AP open items against bill AP open items.

Core columns:

- `id uuid pk`
- `company_id uuid not null`
- `entity_number text unique not null`
- `application_number text not null`
- `vendor_id uuid not null`
- `status text not null`
- `application_date date not null`
- `document_currency_code char(3) not null`
- `base_currency_code char(3) not null`
- `total_amount numeric(20,6) not null`
- `memo text null`
- `posted_at timestamptz null`
- `created_by_user_id uuid not null`
- `created_at timestamptz not null`
- `updated_at timestamptz not null`

Constraints:

- unique `(company_id, application_number)`

### 9.16 `vendor_credit_application_lines`

Core columns:

- `id uuid pk`
- `vendor_credit_application_id uuid not null`
- `line_number integer not null`
- `source_vendor_credit_ap_open_item_id uuid not null`
- `target_bill_ap_open_item_id uuid not null`
- `applied_amount_tx numeric(20,6) not null`

Constraints:

- unique `(vendor_credit_application_id, line_number)`

### 9.17 `manual_journal_documents`

Core columns:

- `id uuid pk`
- `company_id uuid not null`
- `entity_number text unique not null`
- `display_number text not null`
- `status text not null`
- `entry_date date not null`
- `transaction_currency_code char(3) not null`
- `base_currency_code char(3) not null`
- `fx_rate_snapshot_id uuid null`
- `fx_rate numeric(20,10) not null default 1`
- `fx_requested_date date not null`
- `fx_effective_date date not null`
- `fx_source text not null`
- `memo text null`
- `posted_at timestamptz null`
- `created_by_user_id uuid not null`
- `created_at timestamptz not null`
- `updated_at timestamptz not null`

### 9.18 `manual_journal_document_lines`

Core columns:

- `id uuid pk`
- `manual_journal_document_id uuid not null`
- `line_number integer not null`
- `account_id uuid not null`
- `description text null`
- `tx_debit numeric(20,6) not null default 0`
- `tx_credit numeric(20,6) not null default 0`

## 10. Posted Accounting Truth

### 10.1 `journal_entries`

This is the official accounting JE produced by Posting Engine.

Core columns:

- `id uuid pk`
- `company_id uuid not null`
- `entity_number text unique not null`
- `display_number text not null`
- `status text not null`
- `source_type text not null`
- `source_id uuid not null`
- `transaction_currency_code char(3) not null`
- `base_currency_code char(3) not null`
- `exchange_rate numeric(20,10) not null`
- `exchange_rate_date date not null`
- `exchange_rate_source text not null`
- `fx_rate_snapshot_id uuid null`
- `total_tx_debit numeric(20,6) not null`
- `total_tx_credit numeric(20,6) not null`
- `total_debit numeric(20,6) not null`
- `total_credit numeric(20,6) not null`
- `posting_run_id uuid not null`
- `idempotency_key text not null`
- `posted_at timestamptz null`
- `voided_at timestamptz null`
- `reversed_at timestamptz null`
- `created_by_user_id uuid not null`
- `created_at timestamptz not null`

Constraints:

- unique `(company_id, source_type, source_id, status)` filtered in implementation as appropriate
- unique `(company_id, idempotency_key)`

### 10.2 `journal_entry_lines`

Core columns:

- `id uuid pk`
- `journal_entry_id uuid not null`
- `line_number integer not null`
- `account_id uuid not null`
- `description text null`
- `party_type text null`
- `party_id uuid null`
- `tx_debit numeric(20,6) not null default 0`
- `tx_credit numeric(20,6) not null default 0`
- `debit numeric(20,6) not null default 0`
- `credit numeric(20,6) not null default 0`
- `tax_component_type text null`
- `control_role text null`
- `posting_role text null`
- `source_line_number integer null`

### 10.3 `ledger_entries`

Core columns:

- `id uuid pk`
- `company_id uuid not null`
- `journal_entry_id uuid not null`
- `journal_entry_line_id uuid not null`
- `posting_date date not null`
- `account_id uuid not null`
- `debit numeric(20,6) not null default 0`
- `credit numeric(20,6) not null default 0`
- `transaction_currency_code char(3) not null`
- `tx_debit numeric(20,6) not null default 0`
- `tx_credit numeric(20,6) not null default 0`
- `created_at timestamptz not null`

## 11. AP And AR Open-Item Control

### 11.1 `ar_open_items`

Core columns:

- `id uuid pk`
- `company_id uuid not null`
- `customer_id uuid not null`
- `source_type text not null`
- `source_id uuid not null`
- `balance_side text not null`
- `document_currency_code char(3) not null`
- `base_currency_code char(3) not null`
- `original_amount_tx numeric(20,6) not null`
- `original_amount_base numeric(20,6) not null`
- `open_amount_tx numeric(20,6) not null`
- `open_amount_base numeric(20,6) not null`
- `status text not null`
- `due_date date null`
- `created_at timestamptz not null`
- `updated_at timestamptz not null`

### 11.2 `ap_open_items`

Structure mirrors `ar_open_items`.

### 11.3 `settlement_applications`

Core columns:

- `id uuid pk`
- `company_id uuid not null`
- `application_type text not null`
- `source_type text not null`
- `source_id uuid not null`
- `target_open_item_type text not null`
- `target_open_item_id uuid not null`
- `applied_amount_tx numeric(20,6) not null`
- `applied_amount_base numeric(20,6) not null`
- `settlement_fx_rate numeric(20,10) null`
- `realized_fx_amount numeric(20,6) null`
- `created_at timestamptz not null`
- `created_by_user_id uuid not null`

Recommended semantics:

- `balance_side`: `debit` for asset-like open items such as invoices and vendor credits, `credit` for liability-like open items such as bills and customer credit notes
- `applied_amount_tx`: settlement amount in the document/open-item transaction currency
- `applied_amount_base`: settlement amount translated at the payment document's settlement FX rate
- `settlement_fx_rate`: the payment document FX rate used for the settlement
- `realized_fx_amount`: signed realized gain/loss in base currency
  - AR receive payment: positive = gain, negative = loss
  - AP pay bill: positive = gain, negative = loss
- credit-application and vendor-credit-application flows should write one settlement row per affected open item, so both the source credit item and the target invoice/bill item participate in later FX unwind replay

### 11.4 `fx_revaluation_batches`

Core columns:

- `id uuid pk`
- `company_id uuid not null`
- `entity_number text unique not null`
- `display_number text not null`
- `status text not null`
- `batch_kind text not null`
- `reversal_of_fx_revaluation_batch_id uuid null`
- `revaluation_date date not null`
- `transaction_currency_code char(3) not null`
- `base_currency_code char(3) not null`
- `fx_rate_snapshot_id uuid null`
- `fx_rate numeric(20,10) not null`
- `fx_requested_date date not null`
- `fx_effective_date date not null`
- `fx_source text not null`
- `memo text null`
- `posted_at timestamptz null`
- `created_by_user_id uuid not null`
- `created_at timestamptz not null`
- `updated_at timestamptz not null`

Recommended semantics:

- one batch revalues one foreign transaction currency against one company base currency
- batch remains a formal source document and must post through the Posting Engine
- batch uses a locally accepted FX snapshot captured on the batch header
- `batch_kind`: `revaluation` or `next_period_unwind`
- `reversal_of_fx_revaluation_batch_id`: points at the posted batch being unwound when `batch_kind = next_period_unwind`
- only one active unwind draft/posting should exist per posted source revaluation batch

### 11.5 `fx_revaluation_batch_lines`

Core columns:

- `id uuid pk`
- `company_id uuid not null`
- `fx_revaluation_batch_id uuid not null`
- `line_number integer not null`
- `target_open_item_type text not null`
- `target_open_item_id uuid not null`
- `target_balance_side text not null`
- `target_control_account_id uuid not null`
- `offset_account_id uuid not null`
- `party_id uuid not null`
- `description text not null`
- `open_amount_tx numeric(20,6) not null`
- `carrying_amount_base numeric(20,6) not null`
- `revalued_amount_base numeric(20,6) not null`
- `unrealized_fx_amount numeric(20,6) not null`
- `applied_at timestamptz null`
- `created_at timestamptz not null`
- `updated_at timestamptz not null`

Recommended semantics:

- `carrying_amount_base`: open-item base carrying amount at draft-prepare time
- `revalued_amount_base`: new carrying amount derived from the period-end FX rate
- `target_balance_side`: preserves whether the open item should behave like a debit balance or credit balance during unrealized FX posting and unwind
- `unrealized_fx_amount`: signed revaluation delta in base currency
  - debit-balance positive = unrealized gain, negative = unrealized loss
  - credit-balance positive = unrealized loss, negative = unrealized gain
- `offset_account_id`: the exact unrealized FX P&L account the line must hit, so next-period unwind can reverse the original account rather than re-deriving it from the new delta sign
- posting should fail if a draft batch line no longer matches the current open-item carrying/open balances
- next-period unwind lines should swap `carrying_amount_base` and `revalued_amount_base`, invert `unrealized_fx_amount`, and preserve `offset_account_id`
- when post-revaluation settlements exist, next-period unwind should replay those settlement amounts against both source carrying and source revalued balances so only the still-open unrealized portion is reversed
- when a later posted FX revaluation still remains active on the same open item, the older batch is no longer the chain tail and should not prepare a direct unwind; newer revaluation descendants must unwind first
- a cascade unwind helper may compute the active revaluation chain for a requested batch and prepare the latest active descendant as the next unwind step, but each unwind still posts as its own formal source batch
- an auto-post cascade unwind helper may iterate that chain tail-first in a single service request, but each generated unwind batch must still be persisted and posted through the normal Posting Engine path

## 12. Audit And Observability

### 12.1 `audit_logs`

Core columns:

- `id uuid pk`
- `company_id uuid null`
- `actor_type text not null`
- `actor_id uuid null`
- `entity_type text not null`
- `entity_id uuid not null`
- `action text not null`
- `payload jsonb not null default '{}'::jsonb`
- `created_at timestamptz not null`

### 12.2 Optional Later Tables

- `provider_lookup_logs`
- `report_cache_entries`
- `smartpicker_usage_signals`
- `runtime_error_events`

## 13. Recommended Index Strategy

Important indexes include:

- all core tables on `(company_id, created_at desc)`
- document tables on `(company_id, status, date)`
- JE tables on `(company_id, source_type, source_id)`
- ledger on `(company_id, account_id, posting_date)`
- open items on `(company_id, party_id, status, due_date)`
- FX snapshots on `(company_id, base_currency_code, quote_currency_code, requested_date desc)`

## 14. Migration Direction From Current Prisma/SQLite

Current state in [prisma/schema.prisma](./prisma/schema.prisma):

- SQLite provider
- `userId` used where `company_id` should become formal tenant boundary
- monetary fields use `Float`
- source documents and posted accounting truth are still too tightly coupled

Recommended migration sequence:

1. Introduce PostgreSQL as target environment.
2. Add `companies`, `company_memberships`, and active company context.
3. Replace `Float` monetary columns with `NUMERIC`.
4. Split document truth from posted JE truth where needed.
5. Add currency catalog, company currencies, and company FX snapshots.
6. Add open-item tables for AP/AR.
7. Move Prisma schema toward PostgreSQL-compatible naming and constraints, or migrate core write path to C# + EF Core/Dapper for the accounting core.

## 15. Notes For Implementation

- Do not let provider-fetch cache become posted accounting truth directly.
- Do not let human display numbers replace immutable internal identity.
- Do not hard-delete posted accounting truth.
- Do not skip `company_id` on core business/accounting tables.
