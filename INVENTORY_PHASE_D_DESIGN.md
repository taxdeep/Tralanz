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

## Phase H.2 checkpoint

This slice stands up `Receipt / ReceiptLine` as first-class document foundation and nothing more. It should be treated as a document bootstrap, not as receipt-first inventory enablement.

- the accounting/source-document seam now persists `receipts` and `receipt_lines` as company-scoped tables distinct from inventory-owned `purchase_receipt`
- `ReceiptDocument` and `ReceiptDocumentLine` now own minimal document truth only:
  - vendor
  - warehouse
  - receipt date
  - status
  - memo
  - source/vendor references
  - line-level item / quantity / unit
  - reserved `tracking_capture_home`
- lifecycle is intentionally limited to `draft` and `posted`
- the `posted` state in H.2 is only document-state truth; it does not invoke inventory movement, receipt-first operational gating, or GL posting
- this slice intentionally stops before all bridge behavior:
  - no `ReceiveStockFromReceipt`
  - no inventory ledger / balance / cost-layer mutation
  - no GR/IR posting
  - no bill behavior change
  - no bill/receipt matching
  - no PPV / variance
  - no tracked operational intake
- after H.2, bill remains the current transitional inbound truth lane; receipt has simply become a standing first-class document that later phases can safely bridge into

## Phase H.3 checkpoint

`Receipt-first inbound matching / posting-gate activation` now sits on top of the H.2 receipt foundation, but it still stops short of receipt-driven inventory or GL truth.

- bill/receipt bridge truth is now persisted through `bill_receipt_matching_allocations`, rather than being hidden inside convenience summaries
- the first authoritative matching anchors are intentionally narrow:
  - company
  - vendor
  - item
  - warehouse
  - stock UOM
  - posted receipt only
- bill/receipt matching now supports partial and split coverage while enforcing hard ceilings:
  - bill line coverage may not exceed the bill line quantity basis
  - receipt line allocation may not exceed remaining posted receipt quantity
  - draft receipts never count toward receipt-first posting eligibility
- matching policy now orders bill consumption in a control-friendly way (`posted -> submitted -> draft`) before applying date/id tie-breakers, so formal bill truth does not lose posted receipt coverage to younger drafts
- bill posting gate authority now consumes persisted matching truth from the application seam, not only older summary-style receipt review logic
- browser/detail/editor continuity remains intact, but H.3 still avoids turning receipt into an operational inventory document:
  - no `ReceiveStockFromReceipt`
  - no inventory ledger or balance mutation from receipt
  - no cost-layer creation from receipt
  - no GR/IR
  - no PPV / variance
  - no PO truth
  - no tracked operational enablement
  - no vendor return lane
  - no shell-wide receipt expansion
- after H.3, receipt is now a real participant in inbound control truth, but bill is still the transitional inbound AP path and receipt still does not own inbound inventory or accounting truth

## Phase H.4 checkpoint

The next inbound hardening slice now persists discrepancy / investigation truth without turning receipt into an operational inventory document.

- current inbound mismatch state now persists in `bill_receipt_matching_discrepancy_lanes`, so unresolved bill-side receipt gaps survive reloads and no longer depend only on live match recomputation
- the first discrepancy vocabulary remains intentionally small:
  - `missing_receipt_coverage`
  - `partial_receipt_coverage`
- discrepancy rows remain anchored to authoritative bill-line inbound truth (`vendor + item + warehouse + stock UOM`) and are refreshed from posted receipt coverage, not from draft receipt guesses
- browser/detail/editor continuity now shows both:
  - receipt-first posting gate truth
  - persisted inbound discrepancy lane truth
- this slice deliberately stops before a full investigation workflow entity:
  - there is no manual `resolve / close` transition yet
  - there is still no receipt-driven inventory posting
  - no inventory ledger or cost-layer mutation from receipt
  - no GR/IR
  - no PPV / variance
  - no PO truth
  - no tracked operational enablement
  - no vendor return lane
- after H.4, the project can explicitly distinguish:
  - `bill/receipt matching is incomplete`
  - `bill posting is on hold`
  - `an inbound discrepancy lane exists and is reviewable`
  without overclaiming that receipt has already taken over inbound inventory truth

## Phase H.5 checkpoint

The next inbound bridge slice now introduces the smallest coherent `receipt-driven inventory activation` path, while still refusing to overclaim valuation or GL truth.

- only `posted` first-class `ReceiptDocument` truth may activate inbound inventory quantity
- the bridge is anchored at receipt-line granularity through `receipt_inventory_activation_lines`
- activation is intentionally one-time and idempotent:
  - each `(company, receipt, receipt_line_number)` may activate only once
  - retrying activation against an already-posted receipt reuses the existing activation summary instead of creating a second inbound mutation
  - any partial activation state is treated as an investigation-grade inconsistency
- H.5 now lets first-class receipt truth create inbound inventory quantity truth:
  - `inventory_documents`
  - `inventory_document_lines`
  - `inventory_ledger_entries`
  - `item_warehouse_balances`
- H.5 still stops before valuation truth:
  - no receipt-driven cost layers
  - no GR/IR
  - no PPV / variance
  - no PO truth
  - no tracked operational enablement
- the system therefore now has a deliberate split:
  - receipt owns inbound quantity truth
  - receipt does **not** yet own inbound valuation / AP settlement truth

## Phase H.6 checkpoint

Receipt activation is now hardened for review and retry, while the project still avoids jumping into valuation or GL truth.

- activation state is now a stable read-model truth:
  - `not_posted`
  - `posted_not_activated`
  - `activated`
  - `activation_failed_retryable`
  - `activation_inconsistent`
- failed activation attempts are persisted as retryable operational truth in `receipt_inventory_activation_failures`
- retry stays on the same authority path as receipt posting through `PostReceiptWorkflow`; there is no separate "force activate" side door
- first-class receipt sources are explicitly blocked from the legacy purchase receipt workflow:
  - `receipt_document`
  - `first_class_receipt`
- the legacy `ap_bill` source path is still a transitional fallback, not a permanent authority model
- H.6 still does not introduce:
  - receipt valuation / cost layers
  - GR/IR
  - PPV / variance
  - tracked receipt enablement
  - full retirement of the legacy bill-origin inbound path

The next hardening decision is when to make the `ap_bill` legacy path conditional on first-class receipt coverage, so that transitional usability does not become a long-term dual-path quantity risk.

## Phase H.7 checkpoint

The legacy AP bill inbound path is now governed by explicit retirement policy instead of relying on operators to avoid dual-path quantity creation.

- `LegacyInboundReceiptPathPolicy` defines the first formal retirement rules for the old `ap_bill` fallback
- first-class receipt sources are still not allowed to use the legacy purchase receipt workflow
- `ap_bill` fallback now requires source bill policy truth and is blocked when:
  - first-class receipt matching coverage already exists
  - the requested line anchor is not present on the source bill
  - the request exceeds the source bill's remaining legacy quantity ceiling
  - the source bill has no inventory-grade inbound quantity
- `ap_bill` fallback is still temporarily allowed when:
  - the bill has inventory-grade inbound quantity
  - no first-class receipt coverage exists
  - the request stays within remaining bill quantity after prior legacy receipts
- non-bill manual inventory receipts remain outside this retirement policy
- H.7 still avoids:
  - fully deleting legacy AP bill fallback
  - receipt valuation / cost-layer replacement
  - GR/IR
  - PPV / variance
  - tracked receipt enablement

This materially reduces dual-path risk while preserving a narrow transitional lane for existing AP bill-origin purchase receipt usage.

## Phase H.8 checkpoint

Inbound valuation now has a formal boundary lane instead of being implied by whether cost layers happen to exist.

- first-class Receipt still owns physical inbound quantity truth
- Bill still owns supplier charge / AP evidence and cannot recreate physical truth
- persisted Bill/Receipt matching now provides the eligibility bridge for valuation boundary truth
- the new `receipt_inventory_valuation_lines` lane records bill-backed valuation slices against activated receipt lines
- valuation refresh is idempotent:
  - repeated refreshes do not duplicate the same receipt/bill line valuation slice
  - valued quantity is capped by activated receipt quantity
  - over-covered or over-valued states surface as `valuation_inconsistent`
- receipt read models can now distinguish:
  - quantity not activated
  - quantity activated but awaiting bill coverage
  - quantity activated but not yet valued
  - partially valued
  - valuation boundary complete
  - inconsistent valuation state
- H.8 still does **not** create inventory cost layers from first-class receipt valuation
- H.8 still does **not** introduce GR/IR, PPV, PO truth, GL posting, or tracked receipt enablement

This is an intentional bridge state: valuation evidence is now explicit and reviewable, while actual cost-layer ownership remains a separate design decision.

## Immediate Recommendation

The next formal planning step should be:

- decide how and when `receipt_inventory_valuation_lines` should emit or reconcile `inventory_cost_layers`
- decide whether that emission happens before or after AP bill posting in the transitional architecture
- decide whether GR/IR should become the accounting bridge before PPV / variance
- keep tracked receipt enablement behind a later tracking-aware document integration gate

The implementation should continue to protect the existing authority rule:

- `Inventory` is independent
- `AP` and `AR` may feed it
- `Accounting` may consume it
- none of those modules may replace inventory truth

## Phase H.9 checkpoint

Receipt valuation evidence now has a formal emission path into inventory cost-layer truth.

- H.9 adds a dedicated emission seam instead of treating missing/present cost layers as hidden state:
  - `IReceiptInventoryCostLayerEmissionStore`
  - `receipt_inventory_cost_layer_emission_lines`
- emission is anchored to the existing truth chain:
  - posted first-class receipt -> quantity activation line
  - bill/receipt matching -> valuation evidence line
  - posted bill -> emission eligibility
  - emission line -> `inventory_cost_layers`
- submitted bills may still create H.8 valuation evidence, but H.9 does not let them create cost layers; cost-layer emission requires posted bill-backed evidence
- emission is partial and retry-safe:
  - only eligible valuation slices emit
  - each valuation line may emit at most once
  - retries reuse persisted emission truth rather than creating duplicate cost layers
- receipt review can now distinguish:
  - quantity activated but not valuation-backed
  - valuation-backed but awaiting posted bill
  - valuation-backed but not emitted
  - partially emitted
  - fully emitted
  - emission inconsistent
- H.9 still does not introduce:
  - GR/IR
  - PPV / variance
  - PO truth
  - GL posting
  - tracked receipt enablement
  - vendor return

The deliberate remaining gap is accounting settlement, not inventory valuation usability: first-class receipts can now feed cost layers for outbound costing, while AP/GL reconciliation remains a later bridge.

## Phase H.10 checkpoint

Receipt valuation emission is now hardened with reconciliation truth and an outbound costing guard.

- cost-layer emission review no longer stops at "was emission attempted?"
- the receipt read model can now compare emission rows against actual `inventory_cost_layers`:
  - emitted quantity
  - cost-layer quantity
  - emitted base cost
  - cost-layer original base cost
  - missing cost-layer count
  - orphan cost-layer count
- the reconciliation status vocabulary is deliberately small:
  - no emission
  - reconciled
  - cost layer missing
  - orphan cost layer
  - quantity mismatch
  - amount mismatch
- outbound costing now refuses the risky split-brain state where quantity exists but cost-layer coverage does not:
  - this prevents physical receipt quantity from silently becoming zero-cost or under-costed outbound truth
  - the operator-facing error explicitly points back to receipt valuation emission review
- H.10 adds an opt-in PostgreSQL integration test path for the emission store:
  - creates an isolated test schema
  - seeds activated receipt quantity and posted bill-backed valuation evidence
  - emits cost layers twice
  - verifies idempotency and reconciliation
- this phase still does not introduce:
  - GR/IR
  - PPV / variance
  - PO truth
  - GL posting
  - tracked receipt enablement

The next unresolved accounting boundary is not whether inventory can value outbound stock; it is how AP/GL should formally settle receipt-backed value through GR/IR and later variance handling.

## Phase H.11 checkpoint

GR/IR is now defined as the next accounting boundary, but only as design authority in this slice.

Core boundary:

- Receipt-backed inventory value may enter accounting only after the inventory side is complete and reconciled.
- Quantity activation alone is not enough.
- Valuation evidence alone is not enough.
- Cost-layer emission alone is not enough unless the emission reconciles back to actual cost layers.
- Bill can provide valuation evidence in the transitional architecture, but Bill cannot retake physical truth ownership.

Authoritative flow:

1. Receipt posts physical inbound truth.
2. Receipt activation creates inventory quantity truth.
3. Bill/Receipt matching creates valuation evidence.
4. Receipt valuation emission creates cost-layer truth.
5. Emission reconciliation proves cost-layer truth is intact.
6. GR/IR bridge may then recognize receipt-backed inventory value into accounting.

Minimum GR/IR bridge design:

- The future bridge should be persisted and reviewable, not hidden inside journal-writing side effects.
- It should anchor to receipt valuation/emission truth:
  - `receipt_id`
  - `receipt_line_number`
  - `valuation_line_id`
  - `cost_layer_emission_line_id`
  - linked `bill_id` / `bill_line_number` while bill remains transitional valuation evidence
- It should track slice-level quantity and amount, so partial receipt valuation can be bridged without waiting for unrelated lines.
- It should expose bridge state before writing journals.

Recommended state model:

- `not_eligible`
- `eligible_not_posted`
- `partially_posted`
- `posted`
- `blocked_reconciliation_required`
- `blocked_variance_required`

Accounting semantics for the later implementation:

- Receipt-backed inventory value recognition:
  - Dr Inventory Asset
  - Cr GR/IR Clearing
- AP bill settlement against the same bridge:
  - Dr GR/IR Clearing
  - Cr AP

Explicitly deferred:

- PPV / purchase price variance
- PO ordered / received / billed truth
- GL posting implementation in H.11
- automatic variance recognition
- tracked receipt enablement
- rewriting H.5 quantity activation
- rewriting H.9 cost-layer emission
- rewriting H.10 reconciliation truth

Decision:

The next implementation step should not jump straight to journal writing. It should first introduce a persisted GR/IR bridge read/control lane that proves which receipt-backed cost-layer slices are accounting-eligible and which are blocked.

## Phase H.12 checkpoint

H.12 adds the persisted GR/IR bridge read/control lane without starting journal writing.

New persisted lane:

- `receipt_grir_bridge_lines`
- one row per receipt-backed cost-layer emission slice
- anchored to receipt, receipt line, valuation line, cost-layer emission line, cost layer, bill, and bill line

Status truth:

- `not_eligible`
- `eligible_not_posted`
- `partially_posted`
- `posted`
- `blocked_reconciliation_required`
- `blocked_variance_required`

Current H.12 behavior:

- refreshes bridge rows from existing cost-layer emission truth
- treats reconciled emission/cost-layer slices as `eligible_not_posted`
- treats missing/orphaned/mismatched cost-layer truth as `blocked_reconciliation_required`
- preserves future `posted` / `partially_posted` states if later journal work has already touched a line
- exposes receipt-level summary through receipt list/detail read models

Still explicitly out of scope:

- GR/IR journal posting
- AP bill clearing against GR/IR
- PPV / variance
- PO truth
- tracked receipt enablement
- changing H.5 receipt quantity activation
- changing H.9 cost-layer emission
- changing H.10 reconciliation truth

Authority note:

H.12 makes accounting eligibility reviewable before accounting writes happen. This keeps the source/inventory/accounting truth boundary intact: Receipt owns physical quantity, valuation/emission/reconciliation prove inventory value, and GR/IR remains a later accounting bridge rather than a hidden side effect.

## Phase H.13 checkpoint

H.13 adds minimal GR/IR journal posting while keeping the bridge boundary narrow.

What changed:

- `eligible_not_posted` GR/IR bridge lines can be posted through the existing Posting Engine.
- A persisted posting batch represents the accounting source document.
- Posted bridge lines are linked to the generated journal entry and move to `posted`.
- The receipt list/detail GR/IR summary can now progress beyond eligibility into posted truth.

Accounting entry:

- Debit: item-level inventory asset account
- Credit: operator-supplied GR/IR clearing account

Hard guards:

- blocked bridge lines cannot post
- unrefreshed or non-eligible bridge lines cannot post
- bridge lines already attached to a posting batch cannot be posted again
- item master must provide an inventory asset account
- GR/IR clearing account must be an active company account

Still not included:

- AP bill settlement / GR/IR clearing against AP
- PPV / variance
- PO truth
- tracked receipt enablement
- automatic GR/IR account governance

Authority note:

This is the first accounting write after the receipt-backed valuation ladder. It deliberately consumes only reconciled, eligible bridge truth and does not let Bill retake quantity ownership.

## Phase H.13 hardening checkpoint

H.13 hardening keeps the same posting boundary but makes the control seam safer and more reviewable.

What changed:

- A persisted company-level GR/IR clearing account policy was added.
- GR/IR posting can use the company default clearing account when the request does not pass an explicit account.
- The receipt GR/IR bridge read model now shows posted accounting linkage:
  - `JournalEntryId`
  - `JournalEntryDisplayNumber`
  - `PostedAmountBase`
  - `LastPostedAt`
- PostgreSQL integration coverage now proves the full path from eligible bridge truth to journal entry and linked posted bridge lines.

Still not included:

- PPV / variance
- PO truth
- AP settlement against GR/IR
- tracked receipt enablement
- automatic UI workbench expansion

Authority note:

This is hardening, not a new valuation universe. GR/IR posting still consumes only eligible bridge truth and does not allow Bill to re-own inbound physical quantity.

## Phase H.14 checkpoint

H.14 introduces the first persisted GR/IR against AP Bill settlement control lane.

Boundary:

- This is read/control truth only.
- It does not create AP settlement journal entries.
- It does not recognize PPV / variance.
- It does not introduce PO truth.
- It does not enable tracked receipt settlement flows.

What changed:

- `receipt_grir_ap_settlement_lines` tracks which posted GR/IR bridge slices can be settled against posted AP Bills.
- Settlement eligibility requires:
  - posted GR/IR bridge line
  - posted journal entry linkage
  - posted Bill
  - AP open item anchor for the Bill
- Partial clearing can be represented through persisted settled quantity/amount and remaining amount.
- Receipt list/detail and Bill detail can expose settlement status and blocked reason counts.
- GR/IR clearing account governance was hardened to require an active liability account in the active company.

Still not included:

- posting settlement entries
- clearing GR/IR against AP open item balances
- PPV / variance
- PO ordered / received / billed truth
- tracked receipt enablement
- Shell-wide settlement workbench

Authority note:

H.14 keeps the truth ladder intact. Receipt owns physical inbound quantity, GR/IR bridge owns posted accounting recognition, and settlement control only determines whether posted GR/IR value is eligible to be cleared against AP Bill truth.

## Phase H.15 checkpoint

H.15 introduces minimal execution for the GR/IR against AP Bill settlement lane.

Boundary:

- Execution consumes only previously eligible settlement control slices.
- It does not create PPV / variance.
- It does not introduce PO truth.
- It does not enable tracked receipt flows.
- It does not expand into a Shell-wide workbench.
- It does not yet perform full AP settlement journal posting.

What changed:

- `receipt_grir_ap_settlement_batches` stores execution attempts with idempotency keys.
- `receipt_grir_ap_settlement_batch_lines` stores consumed settlement-line slices.
- Execution can partially settle a remaining slice and preserve remaining amount truth.
- Duplicate retry with the same idempotency key returns the original batch result.
- Over-settlement is blocked before any slice is consumed.
- Receipt settlement summary now treats partially settled remaining amount as still eligible for later execution.

Still not included:

- AP clearing journal universe
- PPV / variance
- PO ordered / received / billed truth
- tracked receipt enablement
- reverse / void settlement reversal
- Shell-wide settlement operations surface

Authority note:

H.15 moves from "reviewable settlement eligibility" to "reviewable settlement execution" without letting Bill retake physical truth or turning this phase into a full AP settlement accounting universe.

## Phase H.16 checkpoint

H.16 adds the minimal journal boundary for H.15 GR/IR settlement execution.

Boundary:

- Only executed H.15 settlement batches can be journal-posted.
- It does not clear AP open item balances.
- It does not create PPV / variance.
- It does not introduce PO truth.
- It does not enable tracked receipt flows.

What changed:

- `receipt_grir_ap_settlement_batches` now carries settlement journal status and journal linkage.
- `ReceiptGrIrSettlementPostingDocument` posts through the standard posting engine.
- Journal fragments debit GR/IR clearing and credit the matched Bill line expense/inventory account.
- The settlement journal source type is `receipt_grir_ap_settlement_posting`.
- A dedicated GR/IR settlement authority seam protects execute/post endpoints.
- Journal refresh detects non-posted linked settlement journals and marks the batch `journal_inconsistent`.

Still not included:

- AP open item balance clearing
- settlement reversal documents
- PPV / variance
- PO ordered / received / billed truth
- tracked receipt enablement
- Shell-wide settlement operations surface

Authority note:

H.16 gives the settlement execution lane an accounting boundary while preserving the truth ladder: Receipt owns physical quantity, Bill owns supplier charge, GR/IR owns interim recognition, and this journal only clears the matched GR/IR/Bill-side recognition slice.

## Phase H.17 checkpoint

H.17 formalizes settlement journal reversal / stale reconciliation as a reviewable control truth.

Boundary:

- This is not AP open item clearing.
- This is not AP subledger settlement application.
- This is not PPV / variance.
- This is not PO truth.
- This is not tracked receipt enablement.

What changed:

- Settlement journal lifecycle is now explicit in the settlement control lane:
  - `not_posted`
  - `posted`
  - `journal_stale`
  - `journal_inconsistent`
- `journal_stale` is used when a linked settlement journal exists but is no longer posted.
- `journal_inconsistent` is reserved for broken anchors, missing journals, or source mismatches.
- Receipt and Bill settlement read models now expose:
  - settlement batch count
  - journal not-posted / posted / stale / inconsistent counts
  - overall journal reconciliation status
  - last journal refresh timestamp
- A dedicated journal reconciliation refresh endpoint keeps review truth current without executing AP clearing:
  - `POST /receipts/{id}/grir-settlement/journal-reconciliation/refresh`
- Settlement journal reposting now respects stale / inconsistent lifecycle truth and blocks instead of silently reusing bad state.

Still not included:

- AP open item balance clearing
- settlement applications
- settlement reversal documents
- PPV / variance
- PO ordered / received / billed truth
- tracked receipt enablement
- Shell-wide settlement operations surface

Authority note:

H.17 makes the settlement journal lane safe enough to be consumed by the later AP open item clearing phase. Future clearing must require posted, reconciled settlement journal truth and must block stale or inconsistent batches.

## Phase H.18 checkpoint

H.18 adds the minimal AP open item clearing integration for the GR/IR settlement lane.

Boundary:

- Only posted, reconciled H.16 settlement journal truth can clear AP open items.
- `journal_stale` and `journal_inconsistent` settlement batches are blocked.
- This does not introduce PPV / variance.
- This does not introduce PO truth.
- This does not enable tracked receipt flows.
- This does not expand into a Shell-wide settlement workbench.

What changed:

- `receipt_grir_ap_settlement_batches` now carries AP open-item clearing state:
  - `not_cleared`
  - `cleared`
  - `reversed`
  - `clearing_stale`
  - `clearing_inconsistent`
  - blocked journal / AP-open-item / amount states
- Receipt and Bill review summaries now expose:
  - open-item not-cleared batch count
  - open-item cleared batch count
  - open-item reversed batch count
  - open-item blocked batch count
  - open-item stale / inconsistent batch counts
  - overall open-item clearing status
  - last open-item cleared timestamp
  - last open-item reversed timestamp
- A minimal clearing endpoint was added:
  - `POST /receipts/{id}/grir-settlement/{batchId}/ap-open-item/clear`
- A minimal reversal endpoint was added:
  - `POST /receipts/{id}/grir-settlement/{batchId}/ap-open-item/reverse`
- Clearing writes `settlement_applications` using source type `receipt_grir_ap_settlement`.
- Clearing reduces the target AP open item and closes it when the remaining amount reaches zero.
- If the linked settlement journal later becomes void/stale/inconsistent, refresh marks the clearing state as stale/inconsistent without automatically touching AP open item truth.
- Explicit reversal restores the AP open item from the persisted clearing application slice, removes the stale application rows, and records reversed amount/count on the settlement batch.
- Base-currency AP open items are supported; FX-aware GR/IR settlement clearing remains intentionally deferred.

Still not included:

- generic settlement reversal / unapply beyond the minimal stale-clearing restoration boundary
- PPV / variance
- PO ordered / received / billed truth
- tracked receipt enablement
- FX settlement clearing
- Shell-wide settlement operations surface

Authority note:

H.18 connects the posted GR/IR settlement journal boundary to AP subledger truth. It remains receipt-first: Receipt owns physical inbound quantity, GR/IR owns interim accounting recognition, and this lane only consumes reconciled settlement slices against AP open item balances.

### Phase H.18.1 sidecar hardening

H.18.1 hardens the AP open-item clearing lifecycle by moving clear/reverse execution behind explicit application command handlers with `IUnitOfWork`.

What changed:

- `POST /receipts/{id}/grir-settlement/{batchId}/ap-open-item/clear` now executes through `ClearReceiptGrIrSettlementOpenItemCommandHandler`.
- `POST /receipts/{id}/grir-settlement/{batchId}/ap-open-item/reverse` now executes through `ReverseReceiptGrIrSettlementOpenItemClearingCommandHandler`.
- The command handlers wrap the settlement store operations in the same UnitOfWork pattern already used by settlement execution and settlement journal posting.
- Integration coverage now uses the command-handler path for clear, stale-blocked clear, reversal, and reversal retry.

Boundary:

- No PPV / variance.
- No PO truth.
- No tracked receipt enablement.
- No Shell-wide settlement operations surface.
- No semantic expansion beyond transactional execution hardening.

## Phase H.19 checkpoint

H.19 adds the minimum purchase-variance boundary after GR/IR settlement and AP open-item clearing.

Boundary:

- This is persisted read/control truth only.
- This does not post PPV / variance journals.
- This does not introduce PO ordered / received / billed truth.
- This does not enable tracked receipt flows.
- This does not create a Shell-wide variance workbench.

What changed:

- `receipt_grir_ap_purchase_variance_lines` persists variance candidate truth at the settlement batch-line slice.
- The lane compares cleared GR/IR settlement value against the proportional posted Bill line charge.
- Receipt and Bill review summaries now expose purchase variance counts, status, amount, and refresh timestamp.
- A refresh endpoint exists:
  - `POST /receipts/{id}/grir-settlement/purchase-variance/refresh`
- Supported review states include:
  - `no_variance`
  - `candidate_not_reviewed`
  - blocked settlement / journal / AP clearing / bill / quantity-basis states

Still not included:

- PPV journal posting
- PPV approval or variance disposition workflow
- PO three-way truth
- FX-aware purchase variance
- tracked receipt enablement
- Shell-wide variance operations surface

Authority note:

H.19 keeps the truth ladder intact. Receipt owns inbound physical quantity, GR/IR owns interim accounting recognition, AP open-item clearing proves the settled subledger slice, and purchase variance is surfaced as unresolved control truth rather than silently posted.
