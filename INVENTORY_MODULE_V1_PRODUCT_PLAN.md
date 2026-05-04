# Inventory Module V1 — Product Plan and Bridge Decisions

Status: Draft v2 — ready to start M0
Date: 2026-05-03

## Purpose

This document complements [INVENTORY_PHASE_D_DESIGN.md](./INVENTORY_PHASE_D_DESIGN.md). It does not replace any of the
inventory-truth engine design captured there. It adds the layers that
the Phase D doc deliberately leaves out:

1. **Cost-currency invariants** — the red lines every schema change
   and posting path must preserve.
2. **The commercial layer** — Tralanz Inventory as a paid module, not
   a bundled feature; the activation lifecycle that goes with that.
3. **Six V1 product decisions** that affect customer-visible flows
   (opening balance, backorder, GR/IR write-off, adjustment,
   drop-ship, period close and backdating).
4. **Robustness investments** — six cross-cutting concerns
   (smoke-test suite, concurrency locks, idempotency keys, real
   migration framework, declarative state machines, cross-cutting
   audit log) interleaved into the build order.
5. **The bridge roadmap** — how the inventory-truth engine that
   already exists (D1–D5, H.9–H.12) connects to AP, AR, and the GL
   Posting Engine, and the milestone sequence (M0 → M8) to get there.

The Phase D rule still holds: `Inventory` owns physical and cost truth;
`AP` and `AR` may feed it; `Accounting` may consume it; none may
replace it.

---

## 0. Core Cost-Currency Invariants (read first)

These are red lines. Every schema change, posting path, and FX
boundary in this module MUST preserve them. Violating any one of them
turns the module from "robust" to "bugs are inevitable".

1. **One base currency per company, locked for life.**
   `companies.base_currency_code` is set at company creation and never
   changes. Multi-base scenarios (subsidiary in another country) are
   modelled as multiple companies with inter-company AR/AP, not as one
   company with multiple bases.

2. **No per-warehouse base currency.**
   `inventory_warehouses` does NOT have a `base_currency_code` column.
   All warehouses inside one company share that company's single base.
   Cross-warehouse transfers move cost layers in base value with no FX
   step.

3. **Cost layers store base only.**
   `inventory_cost_layers.base_unit_cost` is the **only** field the
   posting engine reads when computing COGS. Transaction-currency
   fields (`source_tx_currency`, `source_tx_unit_cost`,
   `source_fx_rate`) live on the layer for audit / forensics; they
   MUST NOT be consulted in cost arithmetic. Once a layer is created
   the base unit cost is frozen — never re-flowed by FX movement.

4. **FX revaluation explicitly excludes inventory.**
   The existing FxRevaluation engine runs on monetary
   assets/liabilities (Cash, AR, AP, foreign-currency loans). It MUST
   NOT touch inventory accounts. Historical-cost principle (GAAP /
   IFRS) forbids upward inventory revaluation; downward is handled
   separately via NRV impairment, not FX.

5. **FX direction is normalised at the boundary.**
   Frankfurter publishes "1 base = X quote". The system stores and
   uses "1 quote = X base" (`baseAmount = txAmount × rate`). Inversion
   happens once, in `LocalFirstRecommendedFxRateService`, before any
   value enters the cache or the posting engine. Every consumer
   downstream assumes the system convention.

These invariants apply uniformly across Receipts, Shipments,
Adjustments, Transfers, BOM consumption/output, GR/IR clearing, PPV,
Drop-ship clearing, and any future inventory-touching flow.

---

## 1. Commercial Layer

### 1.1 Three tiers

```
┌─ Tralanz Books (base subscription) ─────────────────────┐
│  Service items, non-stock items, drop-ship items        │
│  Invoice / Bill posting flows (no inventory)            │
└─────────────────────────────────────────────────────────┘
                       ↓ paid add-on
┌─ Tralanz Inventory (per-company add-on) ────────────────┐
│  Stock items + Receipt / Shipment / Adjustment          │
│  Costing engine (Moving Average OR FIFO)                │
│  GR/IR Clearing, PPV, Inventory Adjustment accounts     │
│  Single warehouse (named, default "Main Warehouse")     │
│  Sales Order with reservation + backorder               │
│  Customer Deposit liability flow                        │
└─────────────────────────────────────────────────────────┘
                       ↓ paid add-on
┌─ Tralanz ERP (per-company add-on, later phase) ─────────┐
│  Multi-warehouse / Bin / Location                       │
│  Lot / Batch / Serial                                   │
│  BOM / Manufacturing / WIP account                      │
│  Per-item costing-method override                       │
│  Period close revaluation tooling                       │
└─────────────────────────────────────────────────────────┘
```

The Inventory tier is **independent of subscription level**, mirroring
QuickBooks Enterprise *Advanced Inventory* and NetSuite's per-module
SKUs. Customers on the base subscription can pay for Inventory only,
without being forced into a higher subscription tier they don't need.

### 1.2 Module flag

A single boolean at the company level gates Inventory:

```
companies.inventory_module_enabled        boolean not null default false
companies.inventory_module_enabled_at     timestamptz null
companies.inventory_module_locked_at      timestamptz null  -- set on first inventory transaction
```

When `inventory_module_enabled = false`:

- The Items page does not allow `stock` kind. The Kind dropdown shows
  Stock greyed out with an "Upgrade to Tralanz Inventory" affordance.
- Receipts / Shipments / Adjustments / Transfers / BOM workbenches
  must not be reachable from the navigation, even via direct URL.
- Bill / Invoice line item pickers continue to work; they just don't
  surface stock items (the existing `BillItemPicker` policy that
  filters to `inventory_stock_item` already enforces this naturally
  since stock items can't exist in this state).

When `inventory_module_locked_at` is set, the company cannot disable
the module without an admin-tool path (data preservation rule below).

### 1.3 Downgrade / cancellation

If a customer stops paying the Inventory add-on after they have stock
data:

- The flag does not flip back to `false` automatically.
- Existing stock items, receipts, shipments, cost layers, and ledger
  rows remain in the database — they belong to the customer.
- New posting is blocked: `inventory_documents` writes return a
  payment-required error.
- The UI shifts to a read-only mode banner: "Your Inventory add-on is
  inactive. You can view past stock activity but cannot post new
  inventory transactions until the add-on is reactivated."

Re-activation reopens posting in the same database state. No data
migration is required.

---

## 2. Activation Wizard

When an authorised user toggles `inventory_module_enabled`, the system
must walk them through a five-step wizard before the flag commits.
This is not optional — the choices made here affect every subsequent
inventory transaction.

### Step 1 — Profile (analytics + nav defaults)

Single multi-select question: "Which describes your inventory?"
- Retail / point-of-sale
- Wholesale / distribution
- Light manufacturing
- Service business with consumables
- Other

Stored on `companies.inventory_profile_tag`. Drives nav defaults
(e.g. service profile keeps BOM hidden until they explicitly enable it
later) and analytics. Does not affect posting.

### Step 2 — Costing method

Two cards, side by side:

| Moving Average (recommended) | FIFO |
|---|---|
| Easier to explain | Closer to true cost flow |
| No layer tracking | Per-receipt cost layer |
| Cost smooths over time | Cost reflects acquisition order |
| Fits most retail/service | Required for shelf-life, tax-regulated, or high-cost-volatility goods |

Footer: "This locks after your first inventory transaction. Changing
later requires an inventory revaluation by an administrator."

Persisted to `company_inventory_policies.default_costing_method`.

### Step 3 — Standard accounts

Tralanz auto-creates these accounts in the Chart of Accounts if they
don't already exist. The user can change codes/names but cannot skip
the bind:

| Code | Name | Type | Used for |
|---|---|---|---|
| 1310 | Inventory Asset | Asset | On-hand inventory value |
| 2150 | GR/IR Clearing | Liability | Goods received, bill not yet matched |
| 2160 | Drop-ship Clearing | Liability | Drop-ship vendor bills awaiting customer invoice match |
| 2210 | Customer Deposit | Liability | Customer prepayment before shipment |
| 5000 | Cost of Goods Sold | Expense | COGS on shipment |
| 5910 | Purchase Price Variance | Expense | Bill price ≠ PO/receipt price |
| 5920 | Inventory Adjustment | Expense | Cycle count / damage / loss |
| 4910 | Other Income | Revenue | GR/IR write-off as gain (un-billed receipts) |

Codes follow the existing canonical CoA ranges. Each binds to a
`company.system_role` so the posting engine can resolve them without
asking the user per transaction.

### Step 4 — Default warehouse

Auto-creates a single warehouse named `Main Warehouse` (editable).
Future ERP-tier subscribers can add more; V1 Inventory subscribers
have exactly one.

### Step 5 — Existing items + opening balance

Lists all current items where `item_kind = 'stock'`. For each, ask:

- Quantity on hand today
- Total inventory value at this moment (in base currency)

System creates one `OpeningBalanceReceipt` document per item × warehouse
combination (Phase D doc already specifies this entity), valued as a
**single cost layer at the average unit cost** = `total_value / qty`.
This is the V1 simplification — see decision #1 below.

Items the user does not fill in stay flagged "awaiting opening balance"
and cannot be sold or shipped until they are. The Items page shows a
yellow banner on those rows.

### After completion

- `inventory_module_enabled = true`
- Module locks once any inventory document posts (D2/D3/D4 documents
  in Phase D terms; sets `inventory_module_locked_at`).
- Nav reveals Receipts, Shipments, Adjustments, Transfers (BOM stays
  hidden unless profile = manufacturing).

---

## 3. Six V1 Product Decisions

These six are the customer-facing rules nailed down in the V1
discussion. Each maps to specific schema and workflow obligations on
top of the inventory-truth engine.

### Decision #1 — Opening Balance: average cost, not user-managed FIFO layers

V1 does **not** ask SMB customers to enter multiple cost layers when
they migrate. Even if their costing method is FIFO, the opening
balance enters as a single cost layer with the weighted-average unit
cost the operator provides.

Rationale: 90% of SMB operators don't have layer-level history at
migration time. Forcing it inflates abandonment. The single-layer
opening balance is correct for the first sale (drains it like any
other layer); only when later receipts arrive does FIFO start
producing real layer ordering.

The opening cost layer is tagged `source = 'opening_balance'` so
revaluation tooling (later phase) can split it into real layers if
better history becomes available.

### Decision #2 — Backorder + Customer Deposit (no negative stock, ever)

V1 enforces hard non-negative inventory. Outbound documents must not
post if they would drive `on_hand - reserved < 0`. The cost reasons:

- Negative stock with no cost layer breaks COGS calculation.
- Cost-correction back-fills when the next receipt arrives are an
  audit-trail nightmare.
- Most ERPs (SAP, NetSuite, BC) support negative stock only behind an
  admin-toggled override; default is hard block.

#### 2a. Two distinct customer-funds liability accounts

V1 distinguishes two cases and seeds two accounts (not one):

| Code | Name | Use | Refundability |
|---|---|---|---|
| 2210 | Customer Deposit | 100% prepayment for stock-out SO | Generally refundable |
| 2220 | Customer Advance Payment | Partial deposit (e.g. 30% on big-ticket order) | Contract-bound, generally non-refundable |

The activation wizard auto-creates both. Defer "Deferred Revenue
(2230)" — that's a subscription-product concern outside V1.

The Receive Payment workflow asks the operator to apply incoming cash
to a specific SO line set or Invoice; if the target SO has open
shipment lines, the credit posts to 2210/2220 (operator picks based on
contract). When the related Invoice posts, the deposit auto-clears
against AR.

#### 2b. Per-item backorder-allowance flag

Some items must never be backorderable (perishable goods,
configured-to-order, regulated). Add to Item:

```
inventory_items.allow_backorder boolean default true
```

When `false`, a SO confirm that requires backorder lines is rejected;
the operator must reduce qty or refuse the order.

#### 2c. Standard happy-path flow

```
SO created with insufficient stock
   ├─ available portion  → reserved on item_warehouse_balances
   └─ shortage portion   → backorder line on SO, no inventory effect

Customer prepayment received
   Dr Bank
   Cr Customer Deposit (2210)   ← liability, NOT revenue

Stock arrives via Receipt
   ├─ on_hand increases
   └─ open backorder lines auto-promote to reserved
       Algorithm: complete-fill FIFO by sales_orders.confirmed_at (ms precision)
                  No partial-fractional allocation. No priority queue (V1).

Operator records Shipment + Invoice (per-shipment, NOT per-SO)
   Dr COGS / Cr Inventory Asset           ← physical truth fires
   Dr AR  / Cr Revenue                    ← financial truth fires
   Dr Customer Deposit / Cr AR            ← deposit clears partial
   (Each shipment clears deposit pro-rata; one SO can produce multiple
    Invoices over time. Operator can also choose 'final invoice covers all'.)
```

#### 2d. Hard-block escape hatch — admin override with forced adjustment

When a user wants to ship despite on_hand = 0 (cycle-count gap,
data-migration catch-up), V1 does NOT silently allow negative stock.
Instead the override **forces a synchronous Inventory Adjustment Gain
at placeholder cost** so the books stay consistent:

```
admin override: allow shipment when on_hand < shipment_qty

   1. Generate inventory_adjustment_gain
      qty       = shipment_qty - on_hand
      unit_cost = COALESCE(latest cost layer's base_unit_cost,
                           item.default_purchase_price)
      reason    = 'admin_override_placeholder'

   GL: Dr Inventory Asset / Cr Inventory Adjustment (5920, tagged 'placeholder_cost')

   2. Normal shipment posts (now on_hand is sufficient)

   3. Future receipt posts as usual

   4. Bill price vs placeholder cost difference → PPV (5910)
   5. Real qty vs placeholder qty difference → admin-only adjustment correction
```

Every override step appends to an immutable audit log with reason
code, approver, and the original shipment reference. Override is
gated by `company_inventory_policies.allow_negative_stock_override`
(default false; admin-only).

#### 2e. Partial shipment and deposit clearing math

A SO with a Customer Deposit may produce multiple shipments. Deposit
clears proportionally per shipment, NOT all-at-once on first
shipment.

```
Worked example:
  SO: 100 units @ $50 = $5,000, customer prepaid $5,000
  Initial: Dr Bank 5000 / Cr Customer Deposit 5000

  Shipment 1: 60 units
    Dr COGS                    1800   (60 × cost-layer-derived)
    Cr Inventory Asset         1800

    Dr AR                      3000   (60 × $50)
    Cr Revenue                 3000

    Dr Customer Deposit        3000   (clear pro-rata)
    Cr AR                      3000

    Remaining: 40 units reserved, Customer Deposit balance $2,000

  Shipment 2: 40 units → same pattern, deposit clears to $0
```

Per-shipment Invoice generation is the default (cleaner audit). One-
final-invoice mode is offered as an SO-level toggle for low-volume
customers who prefer monthly billing.

#### 2f. SO state machine + cancellation

```
SO lifecycle:
   draft
     → confirmed                (locks reservation, may receive deposit)
       → partially_shipped *    (one or more shipments out, more pending)
         → fully_shipped → closed
       → cancelled

Cancellation sub-flow (only legal from confirmed / partially_shipped):
   1. Release remaining reservation (item_warehouse_balances.reserved_qty -=)
   2. Release remaining backorder lines (no inventory effect)
   3. Already-shipped portion: NOT cancellable here; must go through
      Customer Return workflow (separate document).
   4. Customer Deposit handling — operator picks one of three:
      a. Cash refund:    Dr Customer Deposit / Cr Bank
      b. Store credit:   Dr Customer Deposit / Cr Customer Credit (2240)
      c. Transfer to SO: Dr Customer Deposit (old SO) / Cr Customer Deposit (new SO)
   5. Audit log: who cancelled, when, reason code, deposit disposition.
```

`Customer Credit (2240)` is a third deposit-family account, also
seeded at activation wizard time, used only for store-credit
disposition of cancelled SO deposits.

#### 2g. Schema additions

```sql
-- Promote sales_orders to first-class (currently exists in projection space)
-- Specific schema TBD in M5; the contract is:
sales_orders                          -- header
sales_orders.confirmed_at timestamptz -- ms-precision for FIFO promote
sales_orders.status text              -- draft|confirmed|partially_shipped|fully_shipped|closed|cancelled
sales_order_lines.reserved_qty
sales_order_lines.backorder_qty
sales_order_lines.shipped_qty

-- inventory_items new field
alter table inventory_items
  add column if not exists allow_backorder boolean not null default true;

-- company_inventory_policies extensions (already in §5 schema list)
allow_negative_stock_override boolean not null default false
customer_deposit_account_id   uuid null references accounts(id)        -- 2210
customer_advance_account_id   uuid null references accounts(id)        -- 2220
customer_credit_account_id    uuid null references accounts(id)        -- 2240

-- item_warehouse_balances.reserved_qty already exists (Phase D); wire it
```

### Decision #3 — GR/IR Write-off (manual, audited, with offset choice)

The Phase D H.11/H.12 design covers GR/IR posting in the happy path
(receipt → cost layer → GR/IR bridge → bill matches → clears). It
does not cover the unhappy path: receipts that never get a matching
bill, or bills that never get a matching receipt.

V1 adds a **GR/IR Workbench**:

- Surfaces all open GR/IR bridge rows, sorted by age.
- Aging buckets: 0–30, 31–60, 61–90, >90 days.
- For each row, three actions:
  1. **Match to bill** — happy-path; the existing H.11 settlement.
  2. **Write off as gain** — for receipts that will never get a bill.
     Posts `Dr GR/IR Clearing / Cr Other Income (4910)`. Audit trail
     references the original receipt; a comment field is mandatory.
  3. **Write off as loss** — for bills that will never see goods. Posts
     `Dr Inventory Adjustment (5920) / Cr GR/IR Clearing` and reverses
     the cost-layer emission. Audit trail references the original
     bill; comment mandatory.
- Soft block at year-end close: any GR/IR bridge row aged > 90 days
  flags the year-end review screen and requires explicit "leave open"
  acknowledgement from an admin to proceed.

### Decision #4 — Inventory Adjustment as a first-class subdomain

Already exists per Phase D D4. V1 product surface adds:

- **Reason taxonomy** (configurable per company):
  - `cycle_count_gain`, `cycle_count_loss`
  - `damage`, `loss_theft`, `loss_other`
  - `revaluation` (admin only)
  - `manual_adjustment`
- **Approval threshold** on `company_inventory_policies`:
  - `adjustment_approval_threshold_amount decimal null` — when set,
    adjustments whose absolute base-amount exceeds this value require
    approval before posting.
- **GL routing**: by default all adjustments go through `5920
  Inventory Adjustment`. The `revaluation` reason routes through `5910
  Purchase Price Variance` instead, since revaluation is fundamentally
  about correcting cost, not quantity.
- **Item-master change**: no new field on Items. The adjustment
  account is company-level (`company_inventory_policies.
  inventory_adjustment_account_id`), seeded by the activation wizard.

### Decision #5 — Drop-ship as a new ItemKind

Some items are sold by us but never enter our warehouse — vendor
ships directly to customer. These items must not pretend to track
inventory.

Schema addition:

```
ItemKind enum gains a fourth value: drop_ship

inventory_items.item_kind check constraint expands:
   item_kind in ('stock', 'non_stock', 'service', 'drop_ship')
```

Drop-ship items:

- **Never** appear in inventory_documents (Receipt / Shipment / etc.).
- Are visible in `BillItemPicker` and `InvoiceItemPicker`.
- Have these account fields on the item master (others hidden):
  - `default_sales_revenue_account_id`
  - `default_drop_ship_clearing_account_id` (defaults to company `2160`)
  - `default_sales_tax_code_id`
  - `default_purchase_tax_code_id`

GL flow:

```
Vendor Bill posted (drop-ship line):
   Dr Drop-ship Clearing (2160)
   Cr AP

Customer Invoice posted (same drop-ship item):
   Dr AR
   Cr Revenue
   Dr COGS
   Cr Drop-ship Clearing            ← matches and clears the bill side
```

Matching is by `(company_id, item_id, qty_in_period)`. Mismatches
appear in a Drop-ship Clearing aging report that shares the same
mechanics as the GR/IR Workbench (`match | write-off as gain |
write-off as loss`).

UI: the Items page New Item form, when Kind = `Drop-ship`, hides the
Inventory Asset / COGS / Write-off / Purchase Variance pickers and
shows only Sales Revenue + Drop-ship Clearing + Sales Tax + Purchase
Tax.

### Decision #6 — Period Close × Backdating policy

V1 adds a formal period-close model and explicit backdating rules.
Without this, year-end audits become impossible and FIFO retroactive
re-flow becomes unbounded.

#### Period model

```
accounting_periods
  company_id           uuid
  period_start         date
  period_end           date
  status               text -- open | closing | closed | locked
  closing_started_at   timestamptz null
  closed_at            timestamptz null
  locked_at            timestamptz null
```

State machine:

```
open
  └→ closing  (admin starts month-close; 7-day grace, admin-only post)
      └→ closed (no new posts; reversals create offsetting posts in current open)
          └→ locked (audit-only, even reversals forbidden)
```

#### Backdating rules

- Posting date is always the system clock — it equals the current open
  period.
- Effective date is operator-chosen.
- Effective dates within an open period: allowed for any user.
- Effective dates within a closing period: admin-only.
- Effective dates within a closed period: blocked. The UI message:
  "Period [yyyy-mm] is closed. Post into the current open period; use
  the memo to record the original transaction date."
- Effective dates within a locked period: blocked unconditionally.

#### FIFO retroactive cost re-flow

Backdated receipts within the open period can shift FIFO consumption
order. Tralanz handles this by:

1. Inserting the new cost layer at its effective date.
2. Re-attributing all shipments dated on or after the new layer's
   effective date in chronological order.
3. Posting the difference between original COGS and re-computed COGS
   as a single "Inventory cost re-flow adjustment" JE in the current
   open period (not amending closed periods).

Heavy-operation guard: if the re-attribution would touch more than
100 shipment lines OR cross a closed-period boundary, the backdated
receipt is blocked and the operator is told to use the current period
date with a memo.

#### Year-end pre-close blockers

Before allowing a year-end close, the system checks:

- GR/IR bridge rows aged > 90 days
- Drop-ship Clearing rows aged > 90 days
- Sales Orders with negative-availability backorder lines older than
  30 days

Each is a soft block: dashboard warning + admin override + audit log.

---

## 4. Robustness Investments (cross-cutting)

Recent silent-failure incidents (FX direction wrong undetected for
weeks; 5 search-projection Seed steps failing in one transaction; CTS
lifecycle bug crashing pickers; search index empty without any error
surfacing) prove the design philosophy is fine but the **executional
robustness** is under-invested. Six cross-cutting concerns must be
woven into the build, not deferred to "later":

### 4.1 End-to-end smoke-test suite (M0 — before M1)

A CI-running test harness that spins up an empty company and walks a
full business cycle, asserting GL balance + inventory truth + AR/AP
truth at each step. At minimum five golden paths:

- Single-currency service-only company (baseline)
- Single-currency stock company (Bill → Receipt → Sales Issue → Invoice)
- Multi-currency invoice (FX direction round-trip)
- Multi-currency bill with GR/IR clearing
- Cross-period partial shipment with deposit clearing

The FX-direction bug we shipped to prod would have been caught by
test #3 if it had existed. This suite is the safety net that makes
every subsequent milestone safe to ship. Not optional.

### 4.2 Concurrency & row locking (woven into M3 / M5)

Hot rows (`item_warehouse_balances`, `inventory_cost_layers`,
`sales_orders`) need explicit `SELECT ... FOR UPDATE` during
posting transactions. Two simultaneous shipments must not
double-consume the same cost layer. Two simultaneous SO confirms
must not over-reserve. Strategy:

- Postgres row-level locks scoped to `(company_id, item_id, warehouse_id)`
- Optimistic-concurrency `version` column on hot aggregates
- Application-layer retry with exponential backoff on serialization
  failures

V1 traffic may not trigger this, but the day a customer runs a
promotion, five orders land at once, and the system oversells, this
becomes the bug-of-the-month.

### 4.3 Idempotency keys on mutating endpoints (M2.5)

Every posting endpoint (Bill / Invoice / Receipt / Shipment /
Adjustment / Payment / JE) must accept an `Idempotency-Key` HTTP
header. The server stores
`(company_id, idempotency_key) → response_body` for 24h. Replays
return the cached response without re-executing. Stripe-style.

Without this: a network blip + client retry = double-posted Bill =
audit nightmare.

### 4.4 Real database-migration framework (M1.5)

The current `EnsureSchemaAsync` pattern is convenient but is not a
migration system: no version tracking, no down migration, no rollback
path, no detection of "this column already exists with a different
type". Introduce **FluentMigrator** (or DbUp / EF Core Migrations) and
move all new schema work to it. Existing `EnsureSchema*` methods
remain for back-compat; new schema goes through the migration
framework.

Without this: every schema change is a roll-of-the-dice; data
recovery from a failed migration is manual.

### 4.5 Declarative state machines (woven into M5 onward)

SO / Bill / Invoice / Receipt / Shipment / Period each have status
state machines whose transitions live as scattered if/else in
handlers. Move each to a declarative state-machine class (Stateless
library or 30-line homegrown), enforce transitions at the application
layer, and write one row per transition to a `state_transitions`
audit table.

Without this: illegal transitions creep in (closed → open, reversed
→ posted_again) and corrupt the audit narrative.

### 4.6 Cross-cutting audit log (M1.5, alongside migration framework)

A single `audit_events` table:

```sql
audit_events (
  id              uuid primary key,
  company_id      uuid not null,
  user_id         uuid null,
  occurred_at     timestamptz not null default now(),
  action          text not null,
  entity_type     text not null,
  entity_id       uuid null,
  before_json     jsonb null,
  after_json      jsonb null,
  client_ip       text null,
  source_route    text null
)
```

Application interceptor captures before/after on every state-changing
API call. Backs the "who changed this CoA account / item / policy /
override" question that no current table answers.

---

## 5. Bridge Roadmap

The inventory-truth engine (Phase D) is independent and largely
complete. The remaining work joins it to AP, AR, and the GL. The
robustness items from §4 are interleaved as M0 / M1.5 / M2.5.

### M0 — End-to-end smoke-test scaffolding (3–5 days, NO product code)

Per §4.1. New test project (`Citus.Accounting.SmokeTests` under
`backend/tests/`) that:

- Spins up a fresh Postgres test schema per run (or uses Testcontainers).
- Seeds an empty company with canonical CoA.
- Walks 5 golden paths end-to-end via the API surface (no in-process
  shortcuts):
  1. Service-only single-currency
  2. Stock single-currency (Bill → Receipt → Sales Issue → Invoice)
  3. Multi-currency invoice (FX direction round-trip)
  4. Multi-currency bill with GR/IR
  5. Cross-period partial shipment with deposit clearing
- After each step, asserts:
  - GL debit total == credit total (per JE and per period)
  - Inventory on_hand matches expected
  - AR / AP control-account balances match expected
  - FX-derived base amounts match expected to 2dp
- CI gate: PR cannot merge if smoke suite red.

### M1 — Module flag + standard CoA + Items page gate (1–2 days)

- Schema: add the `inventory_module_enabled` columns on `companies`.
- Add `accounting_periods` table.
- Add the eight standard CoA accounts to the canonical CoA seeder
  (current seeder is in `Citus.Accounting.Application.CoaSeeding` —
  add codes 1310, 2150, 2160, 2210, 2220, 2240, 5000, 5910, 5920,
  4910 and bind each to a `system_role` constant).
- Add `default_drop_ship_clearing_account_id`,
  `inventory_adjustment_account_id`, `customer_deposit_account_id`,
  `customer_advance_account_id`, `customer_credit_account_id` to
  `company_inventory_policies`.
- Items page: gate `Stock` Kind selection on the flag; greyed Stock
  option with upsell text when off.
- This milestone touches **no GL posting code**. Pure structural prep.

### M1.5 — Migration framework + audit_events table (4–6 days)

Per §4.4 + §4.6. Foundational infrastructure:

- Introduce **FluentMigrator** as the canonical schema-change
  framework. New `backend/src/Infrastructure/PostgreSQL/Migrations/`
  folder; each migration class numbered + dated.
- Create `__migrations` tracking table; on app start, the runner
  applies any pending migration and refuses to start on a downgrade.
- Existing `EnsureSchemaAsync` calls remain untouched (back-compat);
  but a lint/CI rule warns on any new `EnsureSchema*` additions.
- Create `audit_events` table per §4.6 schema.
- Add an interceptor (`AuditingHandler`) on the API pipeline that
  captures before/after JSON for every `[POST/PUT/DELETE]` route and
  writes one `audit_events` row.
- Backfill: nothing — audit log starts from this commit.

### M2 — Activation wizard (3–5 days)

- Five-step wizard at `/company/inventory/activate`.
- Backend endpoint: `POST /accounting/inventory/activate` accepts the
  collected choices, runs the inventory-foundation seeding (existing
  D1 helper), creates Main Warehouse, and posts opening-balance
  receipts via the existing Phase D D2 path.
- Lock-on-first-transaction enforcement: any inventory_document
  insert checks and stamps `inventory_module_locked_at`.

### M2.5 — Idempotency keys on mutating endpoints (3–5 days)

Per §4.3. Cross-cutting infrastructure:

- New `idempotency_records` table:
  `(company_id, idempotency_key, request_hash, response_status,
   response_body, expires_at)`.
- Middleware: every `POST` / `PUT` / `DELETE` handler checks the
  `Idempotency-Key` header. On hit, returns cached response without
  re-execution. Misses execute and cache.
- TTL: 24h.
- Frontend: `BusinessSessionHeaderHandler` adds a fresh
  Idempotency-Key per posting attempt; client-side retry reuses the
  same key.

### M3 — Sales Issue → COGS posting bridge (1–2 weeks)

This is the **single biggest gap** in the current code: the inventory
engine emits SalesIssue documents that consume cost layers, but
nothing posts the resulting Dr COGS / Cr Inventory Asset to the GL.

Work:

- New posting fragment builder:
  `BuildSalesIssueAccountingFragments` consumes the cost-layer
  consumption rows (`inventory_layer_consumptions`) emitted by the
  Phase D D3 sales-issue posting and produces GL fragments at the
  frozen historical FX rate of each consumed layer (per the
  multi-currency dual-truth rule).
- Trigger: when an Invoice posts and references a SalesIssue, or when
  a SalesIssue posts standalone, the bridge emits.
- Recoverability: the bridge is persisted (mirrors the H.11/H.12
  GR/IR bridge model) so partial / retried emissions are safe.

### M4 — GR/IR + PPV journal writing (1–2 weeks)

Phase D H.12 stops at the persisted bridge read/control lane. M4
turns it into actual GL postings:

- Receipt-backed inventory recognition:
  `Dr Inventory Asset / Cr GR/IR Clearing`
- Bill-backed clearing:
  `Dr GR/IR Clearing / Cr AP`
- PPV: when bill price ≠ receipt price for the same matched slice,
  `Dr/Cr Purchase Price Variance` carries the difference.
- Bridge state transitions drive the journal — no journal writing
  without bridge eligibility.

### M5 — Sales Order + Reservation + Customer Deposit + Backorder (1–2 weeks)

- Promote Sales Order to a first-class entity (currently the
  projection schema knows about it, but the workflow surface is
  thin).
- Wire `item_warehouse_balances.reserved_qty` to SO confirm /
  cancel.
- Customer payment received against an SO with un-shipped lines:
  routes to Customer Deposit liability.
- Receipts auto-promote backorder lines to reserved (FIFO by SO
  date).
- Shipment + Invoice clears the deposit.

### M6 — Drop-ship ItemKind + flow (3–5 days, after M1+M3+M4)

- Add `drop_ship` to ItemKind enum + DB constraint.
- Items page: drop-ship-specific account form (5 fields, see
  Decision #5).
- Bill posting: drop-ship line debits Drop-ship Clearing, not
  Inventory Asset.
- Invoice posting: drop-ship line additionally posts `Dr COGS /
  Cr Drop-ship Clearing`.
- Drop-ship Clearing aging workbench (mirrors GR/IR workbench).

### M7 — Period close + backdating enforcement (1 week)

- `accounting_periods` lifecycle UI under Settings → Accounting.
- Posting paths gain effective-date validation against period state.
- FIFO retroactive cost re-flow (heavy-operation guard included).
- Year-end pre-close dashboard.

### M8+ — ERP tier (deferred)

Multi-warehouse, lot/batch/serial, BOM/manufacturing, per-item
costing-method override, period revaluation tooling. Each its own
slice; sequencing TBD when the Inventory tier customer base requests
it.

---

## 6. Schema Summary (additions only)

These tables / columns are net new on top of what Phase D already
has. Existing inventory tables (`inventory_documents`,
`inventory_ledger_entries`, `inventory_cost_layers`,
`item_warehouse_balances`, `warehouse_transfers`,
`inventory_adjustments`, `boms`, `bom_lines`,
`receipt_grir_bridge_lines`, etc.) are unchanged.

```sql
-- companies.inventory module flag
alter table companies
  add column if not exists inventory_module_enabled boolean not null default false,
  add column if not exists inventory_module_enabled_at timestamptz null,
  add column if not exists inventory_module_locked_at timestamptz null,
  add column if not exists inventory_profile_tag text null;

-- company_inventory_policies extensions
alter table company_inventory_policies
  add column if not exists default_drop_ship_clearing_account_id uuid null references accounts(id),
  add column if not exists inventory_adjustment_account_id uuid null references accounts(id),
  add column if not exists customer_deposit_account_id uuid null references accounts(id),
  add column if not exists adjustment_approval_threshold_amount numeric(18, 4) null,
  add column if not exists allow_negative_stock_override boolean not null default false;

-- new period table
create table if not exists accounting_periods (
  id                    uuid primary key default gen_random_uuid(),
  company_id            uuid not null references companies(id) on delete cascade,
  period_start          date not null,
  period_end            date not null,
  status                text not null,
  closing_started_at    timestamptz null,
  closed_at             timestamptz null,
  locked_at             timestamptz null,
  constraint ck_accounting_periods_status
    check (status in ('open', 'closing', 'closed', 'locked')),
  constraint ux_accounting_periods_company_period
    unique (company_id, period_start, period_end)
);

-- inventory_items.item_kind expansion
alter table inventory_items
  drop constraint if exists ck_inventory_items_item_kind;
alter table inventory_items
  add constraint ck_inventory_items_item_kind
    check (item_kind in ('stock', 'non_stock', 'service', 'drop_ship'));

-- standard CoA seed entries (handled in code by canonical CoA seeder,
-- not raw SQL — listed here for reference only)
--   1310 Inventory Asset
--   2150 GR/IR Clearing
--   2160 Drop-ship Clearing
--   2210 Customer Deposit
--   5000 Cost of Goods Sold
--   5910 Purchase Price Variance
--   5920 Inventory Adjustment
--   4910 Other Income
```

---

## 7. Open Decisions Deferred

These are intentionally **not** decided in V1; flag them when a real
customer scenario surfaces them:

1. Multiple concurrent costing methods for one company (per-item
   override) — ERP tier.
2. Lot / batch / serial dimension on cost layers — ERP tier.
3. Reservations with priority queues (express vs standard SO) —
   future.
4. Inventory revaluation tooling for cost-method change — admin
   support tool, not customer-facing in V1.
5. Inter-company inventory transfers (between two Tralanz tenants) —
   ERP tier.
6. Consignment ownership (vendor-owned stock at our location, or our
   stock at their location) — ERP tier; schema must leave space for
   `ownership_party_id` separate from warehouse.
7. Tax recoverable at receipt vs at bill — defer; default behavior is
   tax recovery at bill posting.

---

## 8. Authority

This document complements `INVENTORY_PHASE_D_DESIGN.md` and respects
its rule set. Where this document and Phase D appear to conflict,
Phase D wins on inventory-truth questions; this document wins on
commercial / activation / bridge-sequence questions.
