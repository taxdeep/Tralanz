# First-Time Setup And Initial Company Provisioning

This note records the approved first-run direction for Citus.

## Goal

The first run of Citus must initialize:

1. the independent SysAdmin identity
2. the optional first company bootstrap decision
3. the first Business App owner and company profile when the user chooses to continue
4. the initial chart-of-accounts template binding with multi-currency reserve families

This flow is intentionally platform-first. SysAdmin does not become a business owner automatically.

## Setup State Machine

- `uninitialized`
  - no SysAdmin account exists
  - only first SysAdmin setup is allowed
- `platform_ready`
  - at least one SysAdmin account exists
  - no business-ready company/owner bootstrap exists yet
  - user must decide whether to create the first company now
- `platform_ready_deferred`
  - SysAdmin exists
  - first company creation was explicitly deferred
  - SysAdmin remains available, Business App remains unavailable
- `business_initializing`
  - first company bootstrap has started structurally
  - company truth exists, but owner/business-ready bootstrap is incomplete
- `business_ready`
  - at least one company exists
  - at least one active owner membership exists
  - business login may proceed normally

## UX Flow

### 1. First SysAdmin Setup

- route: `/login`
- create the first SysAdmin account
- this account belongs only to the SysAdmin / PlatformOps boundary

### 2. Create First Company Decision

- route: `/setup/company-decision`
- question: `Do you want to create your first company now?`
- if `No`
  - persist a deferred first-company setup decision
  - stop inside SysAdmin
- if `Yes`
  - continue to the first company wizard

### 3. First Company Wizard

- route: `/setup/first-company`
- sections:
  - first business owner
  - company profile
  - chart template / provisioning preview
  - review and finish

This slice now provisions the first business-ready company end to end:

- create first business owner
- create company truth with structured profile fields
- create first owner membership
- enable base currency
- create a default primary book and remeasurement policy
- bind the selected chart template
- seed starter accounts with reserved multi-currency families recorded in template binding truth

## First Company Wizard Fields

### Business owner

- display name
- email
- password

### Company profile

- company name
- entity type
- industry
- incorporated date
- fiscal year end
- business number
- account code length
- address line
- city
- province / state
- postal code
- country
- phone
- email

### Chart template

- template key
- base currency
- template preview
- reserved multi-currency account families

## COA Template Direction

Chart-of-accounts provisioning must be backend-owned and versioned.

Each template should eventually bind:

- `template_key`
- `template_version`
- `country`
- `entity_type`
- `industry`
- `default_account_code_length`
- `accounts`
- `reserved_ranges`
- `mandatory_system_roles`

Canonical account codes should be stored independent of company-specific display length. The company `account_code_length` setting then formats the effective code.

## Multi-Currency Reserve Families

The template layer must reserve code families for future multi-currency activation.

Minimum reserve set:

- `1000-1099` cash and bank family
- `1200` base accounts receivable control
- `1210-1249` multi-currency AR reserve
- `3000` base accounts payable control
- `3010-3049` multi-currency AP reserve
- `5600-5699` FX gain/loss and translation reserve family

Minimum FX system roles:

- `accounts_receivable`
- `accounts_payable`
- `realized_fx_gain`
- `realized_fx_loss`
- `unrealized_fx_gain`
- `unrealized_fx_loss`
- `translation_adjustment`

## Current Slice

This slice implements:

- first-run setup state detection
- first-company decision persistence
- SysAdmin routing based on setup state
- first company wizard shell
- first business owner creation
- company profile persistence
- first owner membership creation
- chart template binding
- initial company/account/book provisioning
- first-run completion hand-off UX with explicit business-login next steps
- business-side company-ready onboarding summary and acknowledgement after the first login
- company-ready acknowledgement routes into intentional first workspaces:
  - book governance
  - first invoice draft
  - first bill draft
- first workspaces receive `origin=company-ready` context so the operator sees why that page opened first
- first workspaces also expose recommended next-workspace continuity instead of dead-ending on the first destination
- company-ready onboarding summary now carries a business-ready checklist:
  - starter chart truth
  - first bank / cash anchor
  - tax setup pending vs configured
- company-side review pages now exist for:
  - bank and cash readiness
  - tax operating setup
- tax operating setup has now moved from read-only review into a minimal governed flow:
  - create tax code
  - edit tax code
  - deactivate / reactivate tax code
  - enforce company-scoped direction, recoverability, and posting-account rules before save
- tax setup must now be treated as the first layer of a longer company-tax surface:
  - tax code governance
  - future sales tax report visibility
  - future governed `File Sales Tax` workflow
  - the current tax-code shape must therefore preserve reporting-safe semantics instead of acting like a rate-only picker
- bank and cash setup has now moved from read-only review into a minimal governed flow:
  - add bank-detail account
  - allocate the next company-scoped code from the reserved 1000-1099 family
  - reactivate or deactivate non-primary bank accounts
  - enforce enabled-currency and last-active-bank guardrails before save
- receive-payment and pay-bill now recognize `origin=company-ready` and expose first-company settlement continuity:
  - summarize bank, customer/vendor, and open-item readiness
  - explain whether the operator should go back to first invoice / first bill before attempting settlement
  - allow `Company Ready` to hand off directly into receipt and vendor-payment workbenches once a starter bank exists
- first invoice / first bill now continue the same hand-off after posting:
  - once the first source document is posted, the draft editor offers the next settlement workspace directly
  - invoice posting can hand off into `Receive Payment`
  - bill posting can hand off into `Pay Bill`
- first invoice / first bill now also state the missing-truth boundary honestly during first-company onboarding:
  - whether customer/vendor truth already exists
  - whether tax truth is available or the first document should stay tax-free
  - whether posting has happened yet, so settlement can legally begin
- a minimal customer/vendor onboarding surface now exists:
  - company name is mandatory
  - currency is mandatory only when the company has multi-currency enabled
  - single-currency companies automatically use the base currency
  - optional contact details include email, phone, and a simple address bundle
  - the surface is intentionally limited to company-scoped counterparty truth needed by first invoice and first bill continuity
  - the same surface now also supports minimum maintenance truth:
    - edit an existing customer or vendor
    - deactivate or reactivate counterparties without deleting history
    - keep first-company operators inside one company-scoped counterparty governance page instead of bouncing them to another admin surface
- first-company continuity now also carries the chosen counterparty forward:
  - after creating a new customer or vendor, the onboarding surface can open first invoice / first bill with that exact counterparty preselected
  - after posting the first invoice or bill, the draft editor can open receive-payment / pay-bill with the same customer or vendor already selected
  - settlement workbenches now explain when the selected counterparty was carried forward from the prior step instead of asking the operator to reconstruct context manually
  - when the selected counterparty still has no visible open items, settlement workbenches now give a concrete gap panel that links back to the matching source-document flow and filtered source-browser review
  - settlement workbenches now also classify the missing-candidate state more explicitly:
    - bank anchor missing
    - counterparty truth missing
    - counterparty not selected
    - likely waiting for first post
    - posted source needs review because the open item may already be consumed
  - settlement workbenches now also expose a dedicated `Source Follow-up` summary so the operator can distinguish “still draft”, “already consumed”, and “review matching source history” without first reading the longer gap panel
- source document detail now continues the same first-company explanation chain:
  - posted first invoice / bill can carry `company-ready` context into read-only detail
  - detail pages explain whether settlement should be expected yet
  - detail pages can open the next settlement workspace with the same counterparty already selected
- first invoice / first bill now have a minimal single-tax defaulting assist:
  - when setup truth contains exactly one active sales or purchase tax code, the draft editor exposes a `Default Tax From Setup` action
  - the line-level `Rate` action can also fall back to that single active tax code instead of forcing an extra picker step
  - when some lines still remain on `No tax`, the editor now shows both a line-area reminder and a per-row hint directly under the tax selector
  - this does not invent posting truth; it only reduces friction when company tax setup is intentionally simple
- the same default-tax behavior now applies consistently across all four source-document draft editors:
  - invoice
  - bill
  - credit note
  - vendor credit
- a minimal product/service catalog now exists for first-company operating continuity:
  - product/service truth is company-scoped and intentionally small
  - each item can carry default line description, sales revenue account, purchase expense account, and direction-specific default tax code
  - invoice, bill, credit note, and vendor credit editors can now apply those defaults directly onto line truth without inventing a new persisted `product_service_id` dependency in document lines yet
  - company-ready onboarding and navigation now expose a dedicated `Product & Service Setup` surface so the first operator can configure reusable defaults before heavy source-document entry begins
  - this surface should not be interpreted as inventory-only:
    - trading companies can let it evolve toward a stricter inventory item master
    - professional-service companies can let it evolve toward a lightweight service catalog and later task-management continuity
  - if first-time setup identifies a service-led company, inventory-facing expansion should stay disabled by default while the `Product / Service Setup` entry itself remains available

The next slice should implement:

- stronger template catalog breadth beyond the first two Canada templates
- governed reserved-family enforcement when manual accounts are introduced
- first-company operational continuity after the first counterparty is created, especially the first posted source document and first settlement chain
