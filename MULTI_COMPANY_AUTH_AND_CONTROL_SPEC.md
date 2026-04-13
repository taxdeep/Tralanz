# Multi-Company, Authorization, And Control Spec

## 1. Purpose

This document defines the executable implementation rules for:

- company isolation
- business authentication context
- authorization and role behavior
- SysAdmin separation
- maintenance mode behavior

Authority order:

`CITUS_PRODUCT_ENGINEERING_AUTHORITY.md > this document > task notes > temporary implementation habits`

## 2. Core Model

### 2.1 Business Session

Every authenticated business session must include:

- `user_id`
- `active_company_id`
- company membership context
- permission scope/version data where needed

No business read or write may execute without a resolved `active_company_id`.

### 2.2 Membership Model

- one user may belong to multiple companies
- one company may have multiple users
- each company must always have at least one `owner`

### 2.3 Minimum Business Roles

- `owner`
- `user`

Minimum permission domains:

- `ar`
- `ap`
- `approve`
- `reports`
- `settings`
- `reconciliation`

## 3. Mandatory Data Rules

All core business and accounting tables must include:

- `company_id NOT NULL`

This applies at minimum to:

- accounts
- journal entries
- journal entry lines
- ledger entries
- invoices
- bills
- customers
- vendors
- tax codes
- numbering configs
- templates
- reconciliations
- audit logs
- tasks
- products/services
- currencies
- exchange rates
- notification configs
- security configs

## 4. Mandatory Read And Write Rules

### 4.1 Read Rules

- every repository/service query must be company-scoped
- list/search/report/export/cache reads must filter by `active_company_id`
- AI context and picker context must be company-scoped

### 4.2 Write Validation

Every write path must validate:

- `session.active_company_id == target.company_id`
- all referenced master data belongs to the same company
- source and accounting artifacts remain company-consistent

Required examples:

- `document.company_id == session.active_company_id`
- `account.company_id == session.active_company_id`
- `tax.company_id == session.active_company_id`
- `customer.company_id == session.active_company_id`
- `vendor.company_id == session.active_company_id`
- `journal_entry.company_id == source.company_id`

Any cross-company reference must be rejected.

## 5. Forbidden By Default

- cross-company journal entries
- cross-company ledger entries
- shared chart of accounts
- shared customers
- shared vendors
- shared tax objects
- shared business documents
- business documents referencing another company's accounting objects

## 6. SysAdmin Separation

SysAdmin is a separate system identity and separate application surface.

Rules:

- SysAdmin auth must not be reused as a business-company write shortcut
- SysAdmin must not participate in day-to-day posting flows
- SysAdmin controls company lifecycle, platform health, maintenance mode, and system administration

SysAdmin capabilities include:

- company disable/delete/inactivate lifecycle actions
- user disable/reset/role management
- maintenance mode
- runtime/system visibility

## 7. Maintenance Mode

When maintenance mode is enabled:

- normal business users cannot log in for normal operations
- normal business writes are blocked
- maintenance state must be visible
- SysAdmin remains available

Recommended implementation:

- platform-level maintenance flag
- read-safe banner/status surface
- write middleware guard
- explicit allowlist for SysAdmin routes

## 8. UI Requirements

The business UI must always show:

- current company name
- company switcher
- a clear active-company context

When switching company:

- shell may remain stable
- all data, permissions, reports, settings, numbering, templates, currencies, caches, and FX context must switch

## 9. Implementation Requirements

### 9.1 Backend

- enforce company filters in repositories/services, not just controllers
- avoid optional company filters on core accounting reads
- add audit records for membership, role, and sensitive settings changes

### 9.2 Frontend

- never infer legal company context from route params alone
- treat session company context as authoritative
- clear stale company-scoped view state after company switch

### 9.3 Cache

- cache keys must be company-scoped
- invalidation must be company-safe
- global flush is not the default strategy

## 10. Test Matrix

Important tests must cover:

- same user switching between two companies
- cross-company reference rejection
- owner cannot remove the last owner
- company-scoped cache isolation
- report/export isolation
- maintenance mode blocks normal writes
- SysAdmin remains available during maintenance
