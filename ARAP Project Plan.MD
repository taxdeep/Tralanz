# AR/AP Project Plan

This file records the current working plan so the AR/AP stream can resume cleanly after interruptions.

## Current baseline

- `.NET 11` is now the framework baseline.
- `Remeasurement Engine` and company book governance are already substantially closed on the accounting side.
- `AR/AP` backend already supports:
  - `Invoice / Bill / CreditNote / VendorCredit` posting
  - `Receive Payment / Pay Bill` draft preparation and posting
  - `Credit Application / Vendor Credit Application` draft preparation and posting
  - `AR/AP open items`
  - settlement applications
  - realized FX for settlement/application flows
  - aging reports
- `Web.Shell` already has:
  - real shell session hydration from `CompanyAccess` membership truth
  - business-session headers for outgoing API probes
  - company-governance pages

## Current execution strategy

The repo is not at a clean "Phase 1" state for the external 7-phase master prompt. We already have parts of later accounting phases implemented. So execution should follow repo reality, not restart from a theoretical earlier phase.

## Planning anchor

- `Inventory` must be planned as a `multi-warehouse` capability from the start.
- Do not design inventory as a single-location shortcut that would later need structural rework.
- Future inventory support must assume:
  - one company can operate multiple warehouses / locations
  - item availability, movements, reservations, receipts, issues, transfers, and costing traces must remain warehouse-aware
  - warehouse truth must stay company-scoped and must not collapse company isolation
  - AR / AP document flows that later touch inventory must be compatible with warehouse-aware fulfillment / receipt truth
- Because of that, any future `Inventory / Costing` step in this plan should be interpreted as:
  - `inventory with multi-warehouse support`
  - not `single warehouse now, fix later`

## Recommended closure order

### Step 1
De-demoize user/session usage in existing accessible pages.

- remove `DemoUserId / DemoCompanyId` from live shell-facing pages
- use shell session / active company context instead

Status:
- `System Setup` completed
- `Journal Entry` still pending

### Step 2
Create minimal real pages for existing AR/AP draft-preparation flows.

- `Receive Payment`
- `Pay Bill`
- `Credit Application`
- `Vendor Credit Application`

Rules:
- use current shell session
- allow manual GUID entry for party / bank account first
- support candidate loading and draft preparation only
- do not expand to posting/review in the same slice

Status:
- in progress

### Step 3
Connect those pages to richer lookup/navigation.

- replace raw GUID entry with governed lookup surfaces
- link prepared drafts into detail/review surfaces

### Step 4
De-demoize remaining GL review/editor pages.

- `JournalEntryEditorPage`
- `JournalEntryReviewPage`
- `ManualJournalSourceReviewPage`

### Step 5
Promote AR/AP source documents from posting-only to real business-entry flows.

- create/update/read flows for `Invoice`
- create/update/read flows for `Bill`
- create/update/read flows for `CreditNote`
- create/update/read flows for `VendorCredit`

### Step 6
Tighten AR/AP control layer.

- application trails
- document detail alignment
- aging drill-down consistency
- write-off governance skeleton

### Step 7
Then reassess Inventory / Costing entry.

- do not start full inventory/costing until AR/AP source and settlement pages are no longer demo-driven
- before opening that stream, reconfirm authority/module naming boundary
- treat `multi-warehouse inventory support` as mandatory scope, not an optional add-on
- design future inventory slices around:
  - warehouse master / location governance
  - warehouse-aware stock movement truth
  - warehouse-aware receipt / issue / transfer lifecycle
  - costing that can trace by item + warehouse without breaking accounting truth

## Inventory note

When the plan reaches `Inventory / Costing`, the target is not just "inventory exists".

The target is:

- inventory with `multi-warehouse` support
- warehouse-aware operational truth
- warehouse-aware costing and auditability
- AR/AP compatibility with later warehouse-linked receiving / fulfillment flows

## Immediate next slice

Build the first minimal `Web.Shell` pages for:

- `Receive Payment`
- `Pay Bill`
- `Credit Application`
- `Vendor Credit Application`

using existing draft-preparation workflows and current shell session truth.
