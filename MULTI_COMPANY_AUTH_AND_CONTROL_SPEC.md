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

### 2.0 User Permission System Boundary

Citus uses four cooperating boundaries for identity, entitlement, company access, and business authority.

These boundaries must remain separate:

- Platform Identity / Account
- Platform Billing / Subscription
- CompanyAccess
- Business App Domains

#### Platform Identity / Account

Platform Identity / Account owns account-level identity truth.

It is responsible for:

- registration
- login
- password
- email verification
- reset password
- platform account status
- account lock / disable state

It is not responsible for company membership, active company, company-scoped permissions, owner/user assignment, or accounting authority.

#### Platform Billing / Subscription

Platform Billing / Subscription owns commercial entitlement truth.

It is responsible for:

- plan
- trial
- subscription
- renewal
- cancellation
- payment failure state
- seat entitlement
- feature entitlement
- usage limits

Feature and subscription gates may enable, disable, or limit capabilities.

They must not rewrite posted accounting truth or replace domain legality.

#### CompanyAccess

The Business App does not own an independent user-management business domain.

The Business App owns `CompanyAccess` only.

`CompanyAccess` resolves an already-authenticated account into:

- active company
- company membership
- company role
- company-scoped permissions
- company status gate
- business access legality in the current company context
- at-least-one-owner invariant

The Business App may reference platform account identifiers as actor references.

The Business App must not create business root modules centered on:

- `Users`
- `UserManagement`
- `Identity`

#### Business App Domains

Business App Domains own business truth.

Examples:

- AP
- AR
- GL
- Inventory
- Tax
- Reports
- Reconciliation
- Tasks

Business domains consume identity actor references, CompanyAccess context, and entitlement allow/deny signals.

Business domains decide domain legality and must remain authoritative for posting, approval, accounting, tax, FX, inventory, reporting, and reconciliation truth.

#### SysAdmin

SysAdmin is an independent PlatformOps / SysAdmin boundary with its own entry and session.

SysAdmin is a governance entry point.

SysAdmin may trigger user lock, password reset, membership adjustment, and owner/user assignment, but the authoritative truth remains with the proper owner:

- account and credential truth belongs to Account / Identity infrastructure
- company membership truth belongs to `CompanyAccess`
- posting and approval truth belongs to Citus accounting modules and engines

SysAdmin must not bypass business membership and become a business posting user.

#### Identity Infrastructure

Account / Identity infrastructure may carry login, password, account UI, session, and basic permission infrastructure.

It may not become the source of business authorization truth.

Citus must own and enforce:

- company membership
- active company
- company-scoped permissions
- at-least-one-owner rule
- company inactive write restrictions
- maintenance-mode business restrictions
- business action legality
- posting / approval / accounting authority

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

### 2.4 Business Login Flow

Business login is not complete after credential validation.

Required flow:

1. Platform Identity / Account validates account credentials.
2. Account status is checked: active, not locked, not disabled.
3. Maintenance mode is checked for normal Business App access.
4. CompanyAccess resolves available active memberships.
5. User selects or resumes an active company.
6. CompanyAccess resolves role, company-scoped permissions, company status, and permission version.
7. Business session is established with active company context.
8. Business APIs validate server-side context before reads and writes.

No business read or write may use a global user scope as a fallback.

If no active membership exists, the Business App may show a no-company / invitation-required state, but business data access is blocked.

### 2.5 SysAdmin Login Flow

SysAdmin login must use a separate PlatformOps / SysAdmin session.

SysAdmin sessions must not carry:

- active company for business operations
- company membership role
- company-scoped business permissions
- posting authority

SysAdmin may trigger governance actions, but must route truth-changing work to the owning boundary:

- account lock / disable / password reset -> Platform Identity / Account
- plan / entitlement changes -> Platform Billing / Subscription
- owner / user assignment -> CompanyAccess
- company lifecycle state -> company lifecycle governance
- accounting outcomes -> business modules and engines

SysAdmin must not become a super-owner inside Business App posting flows.

### 2.6 Business Authorization Decision Order

Every business action must be evaluated in this order:

1. Platform account is authenticated.
2. Account is active and not locked / disabled.
3. Maintenance mode allows the requested business access.
4. Active company is resolved.
5. Active membership exists for account + active company.
6. Company status allows the requested operation.
7. Subscription / feature entitlement allows the capability where applicable.
8. Company-scoped permission allows the attempt.
9. Business domain rules decide whether the action is legal.
10. Posting / Tax / FX / Reconciliation engines decide formal accounting truth where applicable.

Rules:

- permission allows an attempt
- entitlement enables a capability
- company status gates mutation
- business domains decide lifecycle legality
- accounting engines decide accounting truth

### 2.7 Company Status Gate

Company status must be enforced on the backend.

Recommended behavior:

- `active`: reads and writes may proceed subject to authorization and domain rules
- `inactive`: reads may proceed, writes are blocked
- `suspended`: access is restricted by platform policy
- `archived`: normal writes are blocked; read/export behavior must be explicit

Frontend disabled controls are not sufficient enforcement.

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
