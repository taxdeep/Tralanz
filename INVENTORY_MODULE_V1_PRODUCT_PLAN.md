# Inventory Module V1 — Product Plan and Bridge Decisions

Status: Draft
Date: 2026-05-03

## Purpose

This document complements [INVENTORY_PHASE_D_DESIGN.md](./INVENTORY_PHASE_D_DESIGN.md). It does not replace any of the
inventory-truth engine design captured there. It adds the layers that
the Phase D doc deliberately leaves out:

1. The commercial layer — Tralanz Inventory as a paid module, not a
   bundled feature; the activation lifecycle that goes with that.
2. Six V1 product decisions that affect customer-visible flows (opening
   balance, backorder, GR/IR write-off, adjustment, drop-ship, period
   close and backdating).
3. The bridge order — how the inventory-truth engine that already
   exists (D1–D5, H.9–H.12) connects to AP, AR, and the GL Posting
   Engine, and the milestone sequence to get there.

The Phase D rule still holds: `Inventory` owns physical and cost truth;
`AP` and `AR` may feed it; `Accounting` may consume it; none may
replace it.

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

The replacement flow when a customer wants to commit before stock is
available:

```
SO created with insufficient stock
   ├─ available portion  → reserved on item_warehouse_balances
   └─ shortage portion   → backorder line, no inventory effect

Customer pays before fulfillment
   Dr Bank
   Cr Customer Deposit (2210)        ← liability, NOT revenue

Stock arrives via Receipt
   ├─ on_hand increases
   └─ outstanding backorder lines auto-promote to reserved (FIFO by SO date)

Operator records Shipment + Invoice
   Dr COGS / Cr Inventory Asset      ← physical truth fires
   Dr AR  / Cr Revenue               ← financial truth fires
   Dr Customer Deposit / Cr AR       ← deposit clears
```

Schema additions:

```
sales_orders                       -- already exists in projection space; promote to first-class
sales_order_lines.reserved_qty
sales_order_lines.backorder_qty
item_warehouse_balances.reserved_qty -- already exists, currently unused; wire to SO confirm

company_inventory_policies.allow_negative_stock_override boolean default false
```

The override flag is admin-only and hidden from the standard activation
wizard. It exists for migration-from-other-ERP scenarios where
historical negative stock must be preserved.

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

## 4. Bridge Roadmap

The inventory-truth engine (Phase D) is independent and largely
complete. The remaining work joins it to AP, AR, and the GL.

### M1 — Module flag + standard CoA + Items page gate (1–2 days)

- Schema: add the `inventory_module_enabled` columns on `companies`.
- Add `accounting_periods` table.
- Add the eight standard CoA accounts to the canonical CoA seeder
  (current seeder is in `Citus.Accounting.Application.CoaSeeding` —
  add codes 1310, 2150, 2160, 2210, 5000, 5910, 5920, 4910 and bind
  each to a `system_role` constant).
- Add `default_drop_ship_clearing_account_id` and
  `inventory_adjustment_account_id` to
  `company_inventory_policies`.
- Items page: gate `Stock` Kind selection on the flag; greyed Stock
  option with upsell text when off.
- This milestone touches **no GL posting code**. Pure structural prep.

### M2 — Activation wizard (3–5 days)

- Five-step wizard at `/company/inventory/activate`.
- Backend endpoint: `POST /accounting/inventory/activate` accepts the
  collected choices, runs the inventory-foundation seeding (existing
  D1 helper), creates Main Warehouse, and posts opening-balance
  receipts via the existing Phase D D2 path.
- Lock-on-first-transaction enforcement: any inventory_document
  insert checks and stamps `inventory_module_locked_at`.

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

## 5. Schema Summary (additions only)

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

## 6. Open Decisions Deferred

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

## 7. Authority

This document complements `INVENTORY_PHASE_D_DESIGN.md` and respects
its rule set. Where this document and Phase D appear to conflict,
Phase D wins on inventory-truth questions; this document wins on
commercial / activation / bridge-sequence questions.
