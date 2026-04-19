# Inventory Phase D Design

Status: Draft  
Date: 2026-04-17

## Purpose

This document defines the formal `Inventory Phase D` direction for Citus.

The goal is to open `Inventory` as an independent module that can integrate with `AP`, `AR`, and the Posting Engine without collapsing inventory truth into bill truth, invoice truth, or ad-hoc UI state.

The design target is:

- multi-warehouse from the start
- append-only inventory movement truth
- governed cost-layer and valuation truth
- explicit warehouse transfer lifecycle
- BOM-aware manufacturing entry point
- clean hand-off into accounting for inventory asset / COGS / write-off / GRNI style posting

## Core Product Position

`Inventory` is not a side effect of AP or AR.

It is an independent module that owns:

- item stock truth
- warehouse-scoped quantity truth
- reservation truth
- in-transit truth
- inbound receipt truth
- outbound issue truth
- write-off truth
- transfer truth
- BOM consumption / receipt truth
- cost-layer truth
- valuation truth
- COGS source truth

`AP Bill` and future `Sales Out` remain upstream business entry surfaces that may trigger or reference inventory documents, but they do not own quantity truth or cost truth.

## Module Boundary

The target module boundary is:

- `AP` owns vendor commercial truth and payable truth
- `AR` owns customer commercial truth and receivable truth
- `Inventory` owns quantity, warehouse, movement, cost-layer, valuation, and COGS-source truth
- the Posting Engine owns formal accounting-entry truth

The integration rule is:

- `AP Bill` may create or link a governed `InventoryReceipt`
- `Sales Out` may create or link a governed `InventoryIssue`
- `Inventory` then emits accounting-relevant facts for posting
- no AP or AR document may directly mutate inventory balances or cost layers

## Phase D Scope

Phase D should open the first governed inventory slice with these capabilities:

1. item master for stock-managed items
2. warehouse master
3. item + warehouse balance cache
4. inventory inbound receipt truth
5. inventory outbound issue truth
6. append-only stock ledger / movement history
7. cost-layer creation and consumption
8. warehouse transfer as a first-class entity
9. write-off / adjustment
10. BOM and basic manufacturing issue / receipt

The design intentionally leaves advanced serial / lot / batch / MRP as later slices.

## Inventory Documents

Phase D should use explicit inventory business documents rather than one generic "stock entry" purpose bucket.

### Inbound documents

- `PurchaseReceipt`
- `CustomerReturnReceipt`
- `TransferReceive`
- `ManufacturingReceipt`
- `OpeningBalanceReceipt`
- `InventoryAdjustmentGain`

### Outbound documents

- `SalesIssue`
- `VendorReturnIssue`
- `TransferShip`
- `ManufacturingIssue`
- `InventoryWriteOff`
- `InventoryAdjustmentLoss`

### Shared cross-document entity

- `WarehouseTransfer`
  - draft
  - shipped
  - received
  - optional cancelled / voided lifecycle

### Manufacturing entities

- `Bom`
- `BomLine`
- `ManufacturingIssue`
- `ManufacturingReceipt`

## Strategy Enums

Inventory should follow entity-level strategy enums instead of freeform if/else behavior.

### `ManageInventoryMethod`

- `DontManageStock`
- `ManageStock`
- `ManageStockBySku`

Phase D should only fully implement:

- `DontManageStock`
- `ManageStock`

`ManageStockBySku` may remain a reserved path until later.

### `CostingMethod`

- `MovingAverage`
- `Fifo`

Recommended Phase D rollout:

- default `MovingAverage`
- governed opt-in `Fifo`

### `BackorderMode`

- `Disallow`
- `AllowNegative`
- `AllowNegativeWithWarning`

Phase D default:

- `Disallow`

Governance rule:

- strict mode is the default and recommended company behavior
- a governed company-level override may allow negative stock for exceptional operating models
- negative stock must never silently become the default because of convenience UI paths

### `LowStockActivity`

- `Nothing`
- `Warn`
- `BlockOutbound`

## Required Quantity Model

The quantity model should not collapse into a single stock field.

For each item + warehouse pair, Citus should explicitly support:

- `on_hand_qty`
- `reserved_qty`
- `in_transit_out_qty`
- `in_transit_in_qty`

Read-only computed quantities:

- `available_qty = on_hand_qty - reserved_qty`
- `net_in_warehouse_qty = on_hand_qty + in_transit_in_qty - in_transit_out_qty`

This allows:

- normal selling against available quantity
- reserving before shipment
- auditing transfer-in-transit separately from source and destination on-hand

## Core Persistence Model

The following tables or equivalent storage records should exist in Phase D.

### 1. `inventory_items`

Purpose:
- stock-managed item master

Minimum fields:
- `id`
- `company_id`
- `item_code`
- `name`
- `description`
- `item_kind` (`stock`, `non_stock`, `service`)
- `stock_uom_code`
- `manage_inventory_method`
- `costing_method`
- `backorder_mode`
- `low_stock_activity`
- `default_inventory_asset_account_id`
- `default_cogs_account_id`
- `default_write_off_account_id`
- `default_purchase_variance_account_id`
- `default_sales_tax_code_id`
- `default_purchase_tax_code_id`
- `is_active`

Reserved future fields:
- `track_lot`
- `track_serial`
- `allow_fractional_qty`
- `reorder_policy_json`

### 2. `inventory_warehouses`

Purpose:
- warehouse / location master

Minimum fields:
- `id`
- `company_id`
- `warehouse_code`
- `name`
- `address_line`
- `city`
- `province_state`
- `country`
- `postal_code`
- `is_active`

### 3. `item_warehouse_balances`

Purpose:
- current cached warehouse balance

Minimum fields:
- `item_id`
- `warehouse_id`
- `on_hand_qty`
- `reserved_qty`
- `in_transit_out_qty`
- `in_transit_in_qty`
- `last_movement_at`

This table is a balance cache, not the primary historical truth.

### 4. `inventory_documents`

Purpose:
- inventory document header truth

Minimum fields:
- `id`
- `company_id`
- `document_type`
- `status`
- `document_number`
- `posting_date`
- `posting_time`
- `memo`
- `source_module`
- `source_document_id`
- `source_document_number`
- `counterparty_id`
- `counterparty_type`
- `created_by_user_id`
- `posted_by_user_id`
- `posted_at`

### 5. `inventory_document_lines`

Purpose:
- document-input truth before or alongside movement posting

Minimum fields:
- `id`
- `document_id`
- `line_number`
- `item_id`
- `warehouse_id`
- `uom_code`
- `quantity`
- `base_quantity`
- `currency_code`
- `fx_rate_to_base`
- `unit_cost_tx`
- `unit_cost_base`
- `extended_cost_base`
- `reason_code`
- `memo`
- `source_module`
- `source_document_id`
- `source_document_line_id`
- `counterparty_id`
- `counterparty_type`

Reserved future fields:
- `lot_batch_id`
- `serial_bundle_id`
- `project_id`
- `department_id`
- `location_bin_code`
- `bom_id`
- `work_order_id`
- `transfer_id`
- `inventory_dimension_json`

### 6. `inventory_ledger_entries`

Purpose:
- append-only quantity + value movement truth

Minimum fields:
- `id`
- `company_id`
- `item_id`
- `warehouse_id`
- `document_id`
- `document_line_id`
- `movement_direction`
- `movement_type`
- `quantity_delta`
- `quantity_after`
- `cost_amount_delta_base`
- `cost_amount_after_base`
- `reserved_delta`
- `reserved_after`
- `in_transit_out_delta`
- `in_transit_out_after`
- `in_transit_in_delta`
- `in_transit_in_after`
- `message`
- `created_by_user_id`
- `created_at`

This table is the authoritative movement history.

### 7. `inventory_cost_layers`

Purpose:
- inbound cost-layer truth

Minimum fields:
- `id`
- `company_id`
- `item_id`
- `warehouse_id`
- `origin_document_id`
- `origin_document_line_id`
- `received_qty`
- `remaining_qty`
- `unit_cost_base`
- `total_cost_base`
- `currency_code`
- `fx_rate_to_base`
- `created_at`

### 8. `inventory_layer_consumptions`

Purpose:
- outbound consumption trace by issue against one or more inbound layers

Minimum fields:
- `id`
- `issue_document_id`
- `issue_document_line_id`
- `cost_layer_id`
- `consumed_qty`
- `consumed_cost_base`
- `created_at`

This table is critical for audit, FIFO, and COGS explanation.

### 9. `warehouse_transfers`

Purpose:
- first-class transfer lifecycle

Minimum fields:
- `id`
- `company_id`
- `transfer_number`
- `status`
- `source_warehouse_id`
- `target_warehouse_id`
- `posting_date`
- `shipped_at`
- `received_at`
- `created_by_user_id`
- `received_by_user_id`
- `memo`

### 10. `warehouse_transfer_lines`

Minimum fields:
- `id`
- `transfer_id`
- `line_number`
- `item_id`
- `uom_code`
- `quantity`
- `base_quantity`

### 11. `boms`

Minimum fields:
- `id`
- `company_id`
- `bom_code`
- `finished_item_id`
- `description`
- `status`
- `effective_from`
- `is_active`

### 12. `bom_lines`

Minimum fields:
- `id`
- `bom_id`
- `line_number`
- `component_item_id`
- `required_qty`
- `scrap_factor_percent`
- `uom_code`

## Ledger-First Rule

The design should follow this rule:

- movement history is the formal truth
- balance rows are a cache

That means:

- every inbound, outbound, reservation, release, transfer ship, transfer receive, write-off, return, and manufacturing movement must append ledger rows
- cached balances may be recomputed from ledger rows if needed
- cost layers and layer consumptions must remain fully reconstructible from posted history

## Reservation and Fulfillment Model

Phase D should separate reservation from physical issue.

Recommended lifecycle:

1. reserve
   - `reserved_qty +n`
   - `on_hand_qty` unchanged
2. ship / issue
   - `reserved_qty -n`
   - `on_hand_qty -n`
3. cancel reservation
   - `reserved_qty -n`
4. transfer ship
   - `on_hand_qty -n`
   - `in_transit_out_qty +n`
5. transfer receive
   - source `in_transit_out_qty -n`
   - target `on_hand_qty +n`

For Phase D, reservation may be introduced first for sales-out / shipment flows and later reused by manufacturing allocation.

## AP / Inventory Integration

`AP Bill` remains a commercial and payable document.

Recommended integration:

- bill line captures vendor / item / quantity / price / currency / tax
- bill may create or link `PurchaseReceipt`
- `PurchaseReceipt` creates inventory quantity truth
- bill drives payable truth
- depending on receiving-accounting mode:
  - disabled mode: inventory receipt is operational truth only, bill becomes the first formal accounting entry
  - enabled mode: inventory receipt may post inventory / GRNI style entry through Posting Engine, bill later clears GRNI or variance

This keeps inventory independent without forcing receipt accounting in every company.

## AR / Sales / Inventory Integration

Sales-side inventory should not start from the invoice.

Recommended integration:

- `SalesOut` or equivalent shipment / issue workflow captures:
  - date
  - customer
  - warehouse
  - item
  - qty
  - uom
  - memo
- system derives cost from inventory cost layers
- outbound issue writes ledger + layer consumption
- accounting may then post COGS / inventory reduction
- invoice / receivable may reference shipment truth, but must not own inventory truth

This avoids manually keying outbound cost and keeps COGS governed.

## Accounting Integration

Inventory must remain the quantity and cost authority.

Posting integration should support these families:

- purchase receipt accounting
  - `Inventory` / `GRNI` when enabled
- sales issue accounting
  - `COGS` / `Inventory`
- vendor return accounting
- customer return accounting
- write-off accounting
  - `WriteOffExpense` / `Inventory`
- manufacturing issue / receipt accounting
- transfer accounting only if cross-legal-entity or special transit clearing is required; ordinary same-company transfer should usually not hit P&L

## BOM and Manufacturing

Phase D should include a minimal manufacturing loop, not full MRP.

Target:

- define BOM master
- issue components to production
- receive finished goods from production
- derive finished-goods layer cost from consumed component layers plus optional governed overhead later

Out of scope for this slice:

- advanced routing
- work center scheduling
- subcontracting
- deep MRP planning

## Reporting Targets

Phase D should make these read models possible:

- Stock Ledger
- Stock Balance by warehouse
- Inventory Valuation
- Item Availability
- Transfer In Transit
- Write-off Register
- BOM Cost Roll-up

These reports should read from:

- ledger truth
- layer truth
- balance cache

not from AR/AP commercial document state.

## Permission and Governance Hooks

Inventory will need explicit company-scoped authority hooks.

These should not be left implicit.

### Required controlled actions

- create / edit inventory item
- create / edit warehouse
- post purchase receipt
- post sales issue
- create / ship / receive warehouse transfer
- create / approve / post write-off
- create / post inventory adjustment
- create / activate BOM
- post manufacturing issue / receipt
- change costing policy
- allow negative stock override

### Recommended first permission hooks

- `inventory.item.manage`
- `inventory.warehouse.manage`
- `inventory.receipt.post`
- `inventory.issue.post`
- `inventory.transfer.manage`
- `inventory.transfer.ship`
- `inventory.transfer.receive`
- `inventory.adjustment.post`
- `inventory.writeoff.approve`
- `inventory.bom.manage`

### Governance note

The exact placement of most operational hooks inside the current CompanyAccess permission model still needs confirmation.

In particular:

- whether `inventory.writeoff.approve` should be owner-only in Phase D
- transfer ship and transfer receive are intentionally split and must remain separate authorities
- inventory costing-policy change belongs to company governance and should not be treated as a normal warehouse-operator permission

## Open Questions That Need Confirmation Before Implementation

1. `SalesOut` boundary
- Should `SalesOut` be a formal `Inventory` document surfaced through Web Shell, or should it initially live under a sales-facing shell and call Inventory workflows underneath?

2. receipt-accounting default
- Should first companies default to operational receipt only, or to Inventory / GRNI posting?

3. write-off authority
- Is write-off a two-step governed flow from day one, or can owner-level direct execution exist for MVP?

4. negative stock policy override shape
- strict mode is now the approved default
- remaining question is only how the governed company-level "allow negative stock" override should be surfaced and audited

5. item master unification path
- the current `Product / Service Setup` should remain available as an entry surface
- for trading companies, it should evolve toward a stricter inventory item master
- for professional-service companies, it should evolve toward a lightweight service catalog and later task-management integration
- first-time setup and shell navigation should default-close inventory-facing governance for service-led companies
- the inventory-facing path should be opened by default only for trading companies; service-led companies should continue through the lighter product/service catalog unless company governance deliberately enables the stricter inventory path later

6. warehouse granularity
- Do we need warehouse + location/bin in Phase D, or warehouse only with bin reserved for later?

7. BOM approval
- Is BOM activation a normal owner edit, or should it require explicit governance / approval once stock has posted history?

## Recommended Delivery Order

### D1. Foundation

- inventory item master
- warehouse master
- item warehouse balance cache
- inventory ledger entry model
- costing policy scaffold

### D2. Inbound

- purchase receipt
- receipt-driven balance update
- initial cost-layer creation
- optional AP Bill linkage

Current checkpoint:

- the first implemented D2 slice is an inventory-owned `purchase_receipt` workbench and storage path
- it posts directly into `inventory_documents`, `inventory_document_lines`, `inventory_ledger_entries`, `inventory_cost_layers`, and `item_warehouse_balances`
- the implemented D1.3 item-master slice now keeps `stock_uom_code` and default `inventory asset / COGS / write-off / purchase variance` accounts inside the inventory foundation seam, so stock items stop being under-specified before outbound, write-off, and receipt/accounting bridge work begins
- this is deliberately **not** wired to `AP Bill` yet because current bill truth does not carry inventory-grade `item / warehouse / quantity / unit cost` fields
- the AP bridge should therefore be treated as a later, explicit design step rather than hidden inside the first receipt implementation
- the reserved follow-on AP stream should explicitly introduce `AP Bill` lifecycle states of `draft -> submitted -> posted -> cancelled/reversed` before any serious AP-to-Inventory receipt bridge is considered complete
- that AP lifecycle is therefore part of the post-Phase-D bridging roadmap, not part of the current inventory-owned D2 implementation boundary

### D3. Outbound

- sales issue / shipment
- cost-layer consumption
- COGS source output
- first availability and stock-ledger report

Current checkpoint:

- the first implemented D3 slice is an inventory-owned `sales_issue` workbench and storage path
- it posts directly into `inventory_documents`, `inventory_document_lines`, `inventory_ledger_entries`, `inventory_layer_consumptions`, and updates `item_warehouse_balances` plus open `inventory_cost_layers`
- outbound cost is intentionally inventory-derived from FIFO / moving-average cost layers; operators do not key cost manually on the issue workbench
- this is deliberately **not** wired to AR invoice or shipment truth yet because the current AR/source-document seam still does not own inventory-grade outbound `warehouse / quantity / issue-posting` truth
- the AR bridge should therefore be treated as a later, explicit outbound design step rather than hidden inside the first sales-issue implementation
- the next implemented D3.2 slice is a read-only `inventory-availability` review surface
- it projects `item_warehouse_balances`, remaining `inventory_cost_layers`, and recent `inventory_ledger_entries` into one compact dashboard so operators can review on-hand / reserved / available / in-transit state and recent stock truth before transfer, write-off, and AR/AP bridges expand further
- this review slice remains intentionally read-only and does not yet expose filtered stock-ledger drill-down, transfer lifecycle, write-off controls, or accounting-facing COGS output
- the next implemented D3.3 slice adds item/warehouse-scoped drill-down on top of that review surface
- the reporting seam now accepts an explicit item/warehouse filter and returns a focused drill-down block alongside the unchanged overview, so operators can inspect matching balances and a larger recent ledger window without losing the company-wide picture
- this still remains an inventory-owned, read-only inspection slice; transfer lifecycle, write-off controls, accounting-facing COGS output, and AR/AP bridge behavior remain intentionally outside the D3.3 boundary

### D4. Transfer and Adjustment

- warehouse transfer
- in-transit lifecycle
- inventory adjustment
- write-off

Current D4 checkpoint:

- the first D4 slice is now implemented as an inventory-owned `warehouse transfer` seam
- it keeps `draft -> submitted -> shipped -> received` inside `warehouse_transfers` instead of collapsing transfer into generic receipt/issue pages
- ship and receive now generate their own `inventory_documents` (`transfer_ship` / `transfer_receive`), append ledger rows, update `item_warehouse_balances`, and move cost truth through source-layer consumption plus destination-layer recreation
- `Web.Shell` now exposes `/company/inventory-transfer` as the first transfer workbench, but this remains intentionally narrow: no write-off, no adjustment journal, no AR/AP bridge, and no accounting-facing transfer posting has been added in the same slice
- the second D4 slice is now implemented as an inventory-owned `inventory adjustment` seam with three controlled kinds: `gain`, `loss`, and `write-off`
- adjustment gain creates new on-hand plus fresh cost layers; adjustment loss and write-off consume existing layers and append neutral ledger rows without collapsing into sales issue or AP flows
- company costing policy now actively matters at runtime: if `RequireWriteOffApproval` is true, direct write-off posting is blocked, while ordinary count gain/loss remains available
- `Web.Shell` now exposes `/company/inventory-adjustment` as the minimal adjustment/write-off workbench, but D4 still intentionally stops short of a real approval lane or accounting bridge

### D5. BOM and Manufacturing

- BOM master
- manufacturing issue
- manufacturing receipt
- BOM cost roll-up read model

Current D5 checkpoint:

- the first D5 slice is now implemented as an inventory-owned manufacturing seam with explicit `boms` + `bom_lines` master data, a cost-roll-up read model, and a paired `manufacturing_issue` / `manufacturing_receipt` posting path
- component consumption now follows the same inventory-owned cost truth as outbound sales issue: component quantities consume open cost layers, append `inventory_layer_consumptions`, and reduce source on-hand in the selected warehouse
- finished-good receipt now creates fresh on-hand plus a new output cost layer inside the same warehouse using the total consumed component cost as the first finished-good layer value
- `Web.Shell` now exposes `/company/inventory-manufacturing` as the first BOM + manufacturing workbench, but D5 still intentionally stops short of routing/work-order logic, WIP clearing, variance accounting, subcontracting, or service/task crossover

## Phase E0 checkpoint

The first post-Phase-D `Operational Guardrail` slice should not pretend tracked document flows are ready just because inventory core already understands stricter item identity modes.

Current E0 checkpoint:

- inventory foundation now blocks *new* enablement of the guarded `ManageStockBySku` path; existing tracked rows may still be reviewed, but the system no longer invites operators to open tracking mode before tracked document inputs exist
- the inventory foundation page now states the authority boundary directly: tracked receipt, shipment, opening-balance, transfer, and build flows are still closed in this stage
- current inventory workbenches (`purchase_receipt`, `sales_issue`, `warehouse_transfer`, `inventory_adjustment`, `manufacturing`) now expose only warehouse-managed stock items instead of pretending tracked operational paths are available
- this checkpoint is deliberately small and UI-facing: it does not add tracked receipt/shipment/build logic, and it does not claim tracked transfer/build support; it just closes the misleading enablement path before bridge work begins

## Phase E checkpoint

The first post-Phase-D `Inventory Control Hardening` slice should tighten high-risk actions before AP/AR bridge work begins.

Current E checkpoint:

- write-off now has an inventory-owned `request -> approve -> post` lane instead of remaining a single-step mutation when company policy requires approval
- approved write-off posting still stays inside inventory truth: pending request lines are re-read, cost layers are consumed at post time, and posted write-off truth remains append-only rather than rewriteable
- warehouse transfer now also exposes a clearer control lane at the shell boundary: `submit`, `ship`, and `receive` are validated as distinct legal actions against current transfer status instead of behaving like generic toolbar buttons
- the shell now states the intended responsibility split directly: submit hands a move into source-warehouse review, ship declares stock left the source warehouse, and receive declares stock physically arrived at the destination warehouse
- this is still not the final permission-system integration; it is the first control hardening slice that makes the transfer/write-off lifecycle explicit before formal permission tokens land

## Phase F.1 checkpoint

`Receipt-first AP / inventory bridge` now has a small hardening layer that should be treated as structural, not cosmetic.

- bill posting gate authority moved from API-local helper code into `Citus.Accounting.Application`
- bill posting now re-checks receipt-first truth inside the command handler, not only at HTTP edge
- bill hold projection for source browsing now runs as a batch inventory projection rather than per-bill summary lookups
- this checkpoint intentionally preserves the current truth model: no persisted discrepancy lane yet, but the existing bill/receipt gate is now harder to bypass and cheaper to project

## Phase G checkpoint

The first `Shipment-first AR bridge` slice is now open, but still intentionally narrow.

- invoice lines can now carry outbound inventory-grade hand-off data (`item`, `warehouse`, `stock UOM`) without claiming shipment truth is complete
- invoice draft read/write seams persist that outbound hand-off set so the AR draft lane can feed later shipment truth without overloading invoice itself
- the invoice workbench now exposes shipment-first hand-off guidance and can hand control to the independent Sales Issue workspace
- Sales Issue can now preload invoice-origin outbound intent, but still remains the authoritative outbound inventory and cost lane
- this checkpoint does **not** yet claim shipped-eligible invoice posting, shipment document truth, or invoice/sales-issue matching; those remain later Phase G work

## Phase G.2 checkpoint

The next `Shipment-first AR bridge` slice now converts invoice-origin outbound hand-off into a real matching and posting-gate lane, while still staying honest that shipment truth is not complete yet.

- inventory now owns an `invoice -> sales_issue` hand-off summary plus a batch posting-gate projection for browser/operator review
- AR invoice posting now re-checks outbound issue coverage inside `Citus.Accounting.Application`, rather than relying on shell/API edge behavior
- the source browser and source detail page both surface invoice-side outbound hold truth, so operators can review issue coverage without reopening the invoice editor first
- the invoice workbench now reflects the same outbound post gate locally when a saved/submitted invoice participates in inventory hand-off
- this is intentionally still an interim issue-first bridge: `Shipment` as formal fulfillment truth and shipped-eligible invoicing remain later Phase G work, but invoice posting no longer acts as if inventory-grade outbound lines can skip authoritative issue coverage

## Phase G.3 checkpoint

The outbound bridge now introduces a real persisted shipment lane while preserving the authority split:

- `Shipment` is now a persisted inventory document type and owns fulfillment metadata (`carrier / tracking / shipping slip`)
- `Invoice -> Shipment` matching now survives reloads and review through `IInventoryShipmentStore`, rather than existing only as transient issue coverage
- AR invoice posting now consumes `persisted shipment truth + computed policy`, not only summary-driven issue coverage
- shell continuity is now shipment-first across browser, detail, and invoice editor, with `Open Shipment Workspace` becoming the primary fulfillment CTA
- `Sales Issue` remains downstream inventory/cost truth and can consume `shipment` as its source anchor, rather than being treated as the semantic owner of fulfillment
- this checkpoint still defers full sales-order state truth, persisted discrepancy workflows, return/disposition, and tracking-aware shipment input

## Phase G completion checkpoint

`Shipment-first AR bridge` should now be treated as phase-complete and review-ready.

- outbound matching truth is now persisted in `inventory_outbound_matching_lanes`, with separate `invoice_shipment` and `shipment_issue` lane types rather than one-off UI summaries
- the persisted lane is keyed by `(company, source document, item, warehouse, UOM)` so partial shipment, split warehouse fulfillment, and downstream issue follow-through can be expressed without forcing the full final sales-order model yet
- invoice posting authority now consumes `persisted shipment truth + computed policy`, which means stock invoicing is constrained by shipped-eligible quantity at the application seam instead of relying on transient review-only coverage
- line-level lane summaries now carry authoritative shipment and issue statuses (`pending`, `partially_*`, `fully_*`, `over_*`), plus remaining quantities to ship and to issue
- browser continuity stays batch-friendly through posting-gate snapshots, while source detail, invoice editor, and shipment workspace all expose the richer lane with downstream issue follow-through and discrepancy summaries
- the phase remains honest about later work: full `SO -> Shipment -> Sales Issue -> Invoice` rollup, explicit persisted discrepancy investigation entities, return/disposition truth, and tracking-aware document integration are still intentionally out of scope

## Phase H0 checkpoint

`Return / Disposition Truth` should begin with a very small outbound base-hardening slice. This is not a new outbound bridge; it is the minimum persisted state needed before `return_receive` can be introduced safely.

- outbound discrepancies now persist in `inventory_outbound_discrepancy_lanes`, so mismatch truth survives reloads and no longer depends on read-time recomputation alone
- discrepancy persistence currently covers:
  - invoice vs shipment over-coverage
  - shipment vs sales-issue over-consumption
  - posted invoice coverage exceeding shipped truth
- `invoice -> shipment -> sales_issue` lane summaries now also carry formal invoice-coverage state:
  - `invoiced quantity`
  - `remaining to invoice`
  - `invoice coverage status`
- browser projections remain batch-oriented while source detail, invoice editor, and shipment workspace now expose a consistent outbound base for later return eligibility review
- this still intentionally stops short of return intake itself: no `return_receive`, no inspect/disposition lane, no refund/credit bridge, and no tracked return integration yet

## Phase H1 checkpoint

The first operational return slice now introduces an inventory-owned `customer return receive` lane without collapsing return truth into invoice, refund, or immediate restock behavior.

- `customer return receive` is now a persisted inventory document type (`customer_return_receipt`) anchored to posted shipment truth
- shipment remains the physical source anchor; invoice stays only as commercial context and is not allowed to invent returnable quantity
- return line truth is grouped and guarded at the same authoritative `(item, warehouse, stock UOM)` level used by the outbound lane, so the system can enforce a hard return quantity ceiling against shipped-and-not-yet-returned quantity
- return intake now captures the future-facing physical seam required by later inspection/disposition work:
  - `condition_code`
  - `return_reason_code`
  - `disposition_reason_code`
  - per-line memo
- H1 intentionally records physical return receive truth only; it does **not** yet restore `on_hand`, create inventory ledger mutations, or touch cost layers
- that means returned stock is now formally received, but it is not yet available inventory until a later inspect/disposition slice decides whether it should be restocked or routed into write-off / scrap / damaged / unsellable outcomes
- vendor return remains explicitly deferred and will later be modeled as a separate lane anchored to receipt truth, not mixed into this first customer-return seam

## Immediate Recommendation

The next formal planning step should be:

- confirm the open questions above
- then start D1 with explicit schema and workflow definitions

The implementation should continue to protect the existing authority rule:

- `Inventory` is independent
- `AP` and `AR` may feed it
- `Accounting` may consume it
- none of those modules may replace inventory truth
