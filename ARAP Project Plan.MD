# AR/AP Project Plan

This file records the current working plan so the AR/AP stream can resume cleanly after interruptions.

Current sequencing note:

- Platform identity / MFA work is still active, but it is now treated as a bounded enabling track.
- The intended trigger to return to AR/AP is: after the remaining Phase 6 slice in `CITUS_USER_PERMISSION_SYSTEM_PLAN.md` reaches minimal closure:
  - `MFA recovery policy closure`
- `TOTP enrollment + challenge skeleton` is now materially in place, so AR/AP is no longer waiting on second-factor protocol shape.
- Once that remaining slice lands, AR/AP becomes the next primary stream again before opening a full `Inventory` implementation track.

## Current baseline

- `.NET 11 + C# 15` is now the framework baseline.
- The repository currently runs on `.NET 11 preview SDK`, so language/runtime alignment should be treated as intentional rather than incidental.
- Cache baseline must follow the authority exactly:
  - `Phase 1 = HybridCache / Distributed Cache`
  - `Phase 2 = Redis`, with `Tag-based Eviction` treated as a primary invalidation approach
  - cache keys must include at least `company_id`, and when Citus multi-tenancy is enabled they must include both `tenant_id` and `company_id`
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
- completed

### Step 3
Connect those pages to richer lookup/navigation.

- replace raw GUID entry with governed lookup surfaces
- link prepared drafts into detail/review surfaces
- add open item drill-down and source-document browser continuity

Status:
- completed

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

Status:
- in progress
- backend `draft create/update/read` is in place for `Invoice / Bill / CreditNote / VendorCredit`
- `Web.Shell` now has draft editor pages for all four source document types, using real party/account/tax-code lookup and draft save endpoints
- saved drafts can now be reopened from browser/detail surfaces and continue editing through richer draft-read models
- those editors can now also post directly into document detail and JE review continuity from the same shell surface
- backend smoke tests now explicitly cover the `posted -> update rejected` guard for all four source document types, so post-save continuity is still bounded by backend lifecycle truth
- source document detail now carries its own JE review continuity when a linked journal entry exists, so posted review no longer depends on the editor page alone
- source document detail now also carries linked JE lifecycle truth (`posted / voided / reversed` plus timestamps), and the shell explicitly warns when the accounting effect has already been voided or reversed
- source document review now also carries a source-owned lifecycle summary (`LifecycleMode`, `CanEditDraft`, `CanPostDraft`, `LifecycleReason`) so UI surfaces can show governed next-step boundaries without inventing lifecycle writes early
- source document review now also carries per-action lifecycle preview rows (`Edit Draft / Post Draft / Reopen / Void / Reverse`) so the next-step contract is explicit before source-owned lifecycle APIs exist
- source document lifecycle preview now also has its own backend endpoint, separate from full review payloads, so future source-owned lifecycle commands can hang off a dedicated contract
- source document lifecycle preview now also has action-specific backend preview endpoints, so `void / reverse / reopen` can be evaluated one action at a time before any write command is introduced
- source document lifecycle now also has a first write-shaped `void` command skeleton, but it is still deliberately non-mutating: it formalizes backend legality and returns `blocked` vs `not_implemented` without pretending that source-owned void persistence already exists
- source document lifecycle now also persists governed `reverse` requests into audit trail when legality allows, so we now have a real write-side request surface without faking reversal accounting entries
- persisted reverse requests are now also queryable by source document, so the governed request path has both write and read surfaces before true reversal execution exists
- governed reverse requests now also have an append-only state flow: `draft -> submitted -> cancelled`, plus an `apply-readiness` read model that explicitly says governance may pass before true reverse execution exists
- governed reverse requests now also have a governed execution path: a submitted request records `execution_requested`, and step 4 can now complete by reversing the linked JE plus appending `reverse_execution_completed`
- that governed execution path still blocks on AR/AP settlement/application trail, so subledger-backed source documents with applied history do not advance into faux execution before settlement/application reversal orchestration exists
- the backend now also defines the minimal subledger-backed reversal execution order as an explicit execution plan contract: request status gate -> governance readiness gate -> subledger reversal gate -> reverse linked JE -> mark source reversed
- shell source-document detail now also surfaces the latest reverse request with its request status, execution status, and transition timestamps, so governed reverse state is visible at the same drill-down level as lifecycle preview and linked JE continuity
- step 5 now has a minimal safe implementation for `invoice / credit_note / bill / vendor_credit`: once linked JE reversal completes, source open items with no settlement/application trail are voided and the source document is marked `reversed`; richer settlement-application reversal remains a later orchestration layer
- settlement-document reverse execution now has a first governed unapply skeleton for `receive_payment / pay_bill / credit_application / vendor_credit_application`: completion restores target open-item balances, writes a `settlement_application_reversal` audit/event trail for each unapplied row, removes the current operational settlement application rows, reverses the linked JE through GL lifecycle, marks the settlement source `reversed`, and records the unapply count/totals in the reverse-request audit payload. AR/AP smoke coverage now exercises all four paths
- settlement-document reversal now also has a product-facing read model: backend exposes settlement-application reversal rows by source document, and shell source-document detail displays the unapplied application trail, compensation JE references, completion timestamps, and target open-item drill-down links
- shell source-document detail now also exposes guarded reverse-flow commands (`request reverse`, `submit reverse request`, `execute governed reverse`) that call backend lifecycle endpoints through the existing business-session header pipeline and then reload backend truth
- shell source-document detail now also surfaces backend reverse `apply-readiness` and `execution-plan` contracts, so operators can see governance readiness, execution mode, ordered execution steps, and backend block reasons before executing
- source-document reverse blockers now also have a backend read model and shell panel: if `invoice / credit_note / bill / vendor_credit` reversal is blocked by AR/AP settlement/application trail, the detail page shows the blocking settlement source document, target open item, applied amounts, FX fields, and drill-down links instead of only a generic blocked reason
- the blocker panel now also has the first minimal governed operator action: `Request Governed Unapply` creates a reverse request on the blocking settlement source document through the existing backend lifecycle contract, keeping actual unapply execution backend-owned and auditable
- blocker rows now also surface the blocking settlement source's latest reverse-request status and execution status, so operators can distinguish `not_requested`, `draft`, `submitted`, `execution_requested`, and completed blockers without leaving the original source document detail
- blocker rows with `draft` blocking-source requests can now submit the governed unapply request directly from the original source-document detail, still through the backend lifecycle contract
- blocker rows with `submitted` blocking-source requests can now request governed execution directly from the original source-document detail, still through the backend execute contract and without UI-owned subledger mutation
- blocker rows with existing reverse requests now also show backend readiness and execution-plan summaries inline, including governance readiness, ready/blocked step counts, and backend reason
- original `invoice / credit_note / bill / vendor_credit` detail pages now show a `Subledger Gate` card: if active blockers remain, it summarizes the remaining governed unapply work; if the backend returns no active blockers and the original reverse request is executable, it exposes `Execute Original Governed Reverse` as the next legal backend-owned action
- AR/AP regression coverage now closes the full guided reversal loop for `invoice -> receive_payment` and `bill -> pay_bill`: the original source is blocked, the blocking settlement source is governed-unapplied, blockers clear, the original execution plan becomes executable, and the original source completes governed reversal
- AR/AP regression coverage now also closes the credit-side guided reversal loops for `credit_note -> credit_application` and `vendor_credit -> vendor_credit_application`, including source credit open-item blockers, application unapply, blocker clearing, and final source reversal
- AR/AP write-off / small-balance adjustment now has the first safe governance skeleton: backend preview endpoints validate source/open-item status and open balances, request endpoints persist `open_item_adjustment_request` audit rows, and no open-item or JE truth is mutated before a Posting Engine-backed execution path exists
- AR/AP regression coverage now verifies that governed open-item adjustment requests can be recorded without changing receivable/payable open-item balance truth
- open-item adjustment governance now also has a minimal append-only request state flow: latest lookup, submit, cancel, active duplicate prevention, and cancellation-safe renewed requests are all backed by audit-log transitions without changing open-item or JE truth
- open-item adjustment governance now also has backend readiness and execution-plan read models for AR and AP: submitted requests re-check current open-item truth, expose the ordered execution skeleton, and honestly block final execution until a Posting Engine-backed adjustment source document exists
- open-item adjustment governance now closes the final minimal execution loop: submitted AR/AP write-off or small-balance requests can post governed `ar_open_item_adjustment` / `ap_open_item_adjustment` source documents through the Posting Engine, create linked JEs, reduce or close the target open item, and append audit completion without letting the UI mutate accounting truth
- open-item drill-down in `Web.Shell` now surfaces the adjustment control layer: preview, latest request, submit/cancel, readiness, execution plan, adjustment account selection, and Posting Engine-backed execution are visible from the open-item detail surface
- open-item adjustment account selection now has a first governed account guard: UI candidates are limited to active manual-posting revenue / cost-of-sales / expense accounts, and backend execution rejects asset, liability, equity, bank, AR/AP control, tax, or other non-adjustment offset accounts
- open-item adjustment policy now supports partial requested amounts. If no amount is supplied the backend defaults to the current open balance; if a smaller legal amount is supplied, execution posts only that amount and leaves the open item `partially_applied` with the remaining balance.
- open-item adjustment governance now has a first backend-owned threshold gate and approval skeleton. Amounts above the temporary base-currency threshold are recorded as requiring approval; submitted requests remain blocked by `blocked_by_approval_required` until an append-only approve transition is recorded, after which readiness and execution-plan can pass if current open-item truth still matches.
- open-item adjustment approval now has backend and shell transitions for `approve` and `reject`. Approval/rejection is stored in `audit_logs`, surfaced on the open-item detail page, and never mutates open-item or accounting truth by itself.
- open-item adjustment approval now also has a first authority guard: approve/reject requires a concrete user actor, the requester cannot approve or reject their own governed adjustment request, and the API approval endpoints require an `owner` or book-governance session role before entering the repository transition. This is the first CompanyAccess-aligned gate and remains replaceable by full Citus Permission Management.
- open-item adjustment account policy now has a formal company/book-aware mapping schema, management API, and Shell UI. `open_item_adjustment_account_mappings` is initialized by the accounting API, active mappings are managed through backend authority-guarded endpoints, the Shell page lets governance users save/deactivate company/default or book-specific mappings, and execution must use a mapped adjustment account once a company/book policy scope exists. If no mapping exists yet, the backend keeps the structural account guard as a safe compatibility fallback.
- open-item adjustment account mapping management now has search/policy polish in `Web.Shell`: governance users can search loaded mappings, search eligible offset accounts, see active/company-default/book-specific/inactive policy counts, and review a policy-preview note before saving a governed scope. This remains UI polish only; backend mapping authority and Posting Engine-backed execution are still the source of truth.
- open-item adjustment account mapping management now also has a Primary Book execution comparison matrix. The Shell shows company default, Primary Book, and non-primary book coverage across AR/AP write-off and small-balance policy cells, while explicitly warning that current execution evaluates only company-default plus Primary Book scopes; non-primary book mappings remain governance visibility until book-specific execution is introduced.
- open-item adjustment account mapping lookup now also has backend-owned search/scope polish. Accounting API supports `searchText`, `bookId`, `policyScope`, and `limit`, returns summary counts (`total / visible / returned / active / company-default / book-specific / inactive`), and the Shell mapping page now consumes those backend filters instead of relying only on local text filtering for governance review.
- open-item adjustment policy continuity now also reaches product surfaces: the mapping page accepts query-based prefilters, `Open Item Detail` shows the current primary-execution mapping truth for the selected adjustment type and deep-links into governance review, and `Source Document Detail` can jump directly into the relevant AR/AP adjustment policy scope.
- source-document browser continuity is now also query-driven. Detail and drill-down pages can open `/documents/source-browser` with preselected `sourceType / counterpartyRole / counterpartyId`, so operators land in a filtered AR/AP browser context instead of restarting navigation from an unfiltered list.
- source-document drill-down continuity now also has a source-type fallback path. When full counterparty context is unavailable, detail pages can still deep-link into a filtered source browser by supported `sourceType`, and reverse-blocker rows now use that path for the blocked AR/AP source type instead of forcing operators to bounce only between individual detail pages.
- settlement-application reversal history now follows the same continuity rule. Reversal rows expose both `Open Source` and `Browse Source Type`, so governed unapply history can move from audit-style rows back into filtered AR/AP source browsing without inventing new navigation roots.
- settlement document line drill-down now follows the same pattern for supported AR/AP source types. Application lines keep their direct detail/open-item links, but now also expose source-type browser fallback from both `Source` and `Target`, so operators can pivot from one settlement line into a filtered document list instead of dead-ending on a single document.
- source browser itself now echoes navigation context through an `origin` query hint. Links coming from source detail, open-item drill-down, blocker rows, reversal history, and settlement lines can tell the browser why the filter was pre-applied, reducing “why am I looking at this subset?” confusion during AR/AP control work.
- source browser now also supports return continuity through `returnHref / returnLabel`. When it is opened from source detail or open-item drill-down, the page can offer a contextual back path instead of collapsing every filtered browsing hop back to the shell root.
- source browser summary is now origin-aware. Instead of only echoing raw filter tokens, the page describes the current business focus in AR/AP terms such as open-item related history, reverse-blocker follow-up, governed unapply history, or settlement-line source/target follow-up.
- open-item detail and source-document detail now also compress existing backend truth into business-facing summary cards. Adjustment control, reverse request state, subledger gate, and governed unapply history can be understood at a glance before operators drill into the lower tables and execution-plan rows.
- open-item settlement/application trail now also has a glanceable summary layer. Operators can see unapplied vs partially/fully applied state, recorded application-row mix, and approximate coverage of the original amount before scanning the detailed settlement/application table.
- source-detail reverse control now also exposes a backend-truth-based `Next Legal Action` summary. The shell compresses original reverse request state, subledger blocker progress, and governed execution readiness into one operator-facing decision cue instead of forcing users to combine those panels mentally.
- open-item detail now also exposes operator-facing next-step summaries for both adjustment control and settlement/application trail. The shell tells users whether the next move is refresh preview, request/submit/review/execute adjustment, await posting, or simply review consuming application history before acting.
- source/open-item summary captions are now being normalized toward AR/AP operator wording. Labels such as reverse workflow, settlement gate, governed unapply trail, adjustment policy, request workflow, posting readiness, and linked applications are replacing more generic technical headings.
- open-item adjustment control now also compresses preview/readiness/execution-plan truth into a short operator bullet summary. The shell keeps the detailed backend reason tables below, but the first read now explains whether the current blockage is policy, open-item state, execution planning, or simply missing account selection.

Current AR/AP shell checkpoint:

- the governance-side mapping/search/policy surfaces are now backend-filtered instead of shell-only filtered
- source detail, open-item detail, reverse blockers, governed unapply history, and settlement lines now form one continuous drill-down path
- source/open-item surfaces now expose business-facing summary cards plus next-step cues, so operators can see current control state before reading the lower tables
- the current work is still shell/control-layer polish; accounting truth, lifecycle legality, and execution remain backend-owned

Near-term plan from this checkpoint:

1. compress preview/readiness/execution-plan blocker reasons into shorter operator-facing bullet summaries on open-item/source pages
2. finish the remaining AR/AP detail surfaces that still feel demo-driven, especially any page that exposes backend truth without a clear operator decision cue
3. once the AR/AP control surfaces feel product-grade, shift from shell polish into the next business-module slice rather than extending identity again
- approval and mapping authority now has its first persisted CompanyAccess bridge. `company_memberships.permissions` tokens are read from PostgreSQL into session roles, Accounting API route guarding now prefers `CompanyAccess` membership truth before static configuration, and the default demo directory no longer silently authorizes requests when persisted membership is available but missing.
- CompanyAccess now also has a first owner-governed membership permission management surface. A fixed permission catalog controls allowed tokens, `Web.Shell` exposes `/company/membership-permissions`, owners can save company-scoped membership permission tokens into `company_memberships.permissions`, and those tokens feed the existing session-role bridge used by downstream Accounting API authority checks.
- CompanyAccess membership permission grants now append a business audit event. Saving permissions runs in a PostgreSQL transaction, updates `company_memberships.permissions`, and writes `membership_permissions_saved` to `audit_logs` with actor, target membership/user, previous tokens, saved tokens, added tokens, and removed tokens.
- CompanyAccess membership permission management now also has a grant audit drill-down surface. `Web.Shell` reads recent `membership_permissions_saved` events through the CompanyAccess workflow and shows actor, target user, timestamp, added tokens, removed tokens, and resulting permission set on `/company/membership-permissions`.

### Step 6
Tighten AR/AP control layer.

- application trails
- document detail alignment
- aging drill-down consistency
- write-off governance skeleton preview/request recording, submit/cancel flow, readiness/plan, Posting Engine-backed adjustment source document, final execution command, JE linkage, open-item reduce/close behavior, and audit completion are now in place
- open-item detail UI now exposes that backend-owned adjustment flow without taking ownership of accounting mutation
- next remaining work is deeper Citus Permission Management synchronization plus optional mapping search and side-by-side primary-book policy comparison

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

Move from the now-closed AR/AP control-layer skeleton into product-grade adjustment and UI hardening:

- surface open-item adjustment latest request/readiness/execution-plan/execution result in the shell
- keep source documents read-only once posted
- keep the blocker panel as the entry point for settlement/application unapply decisions
- keep execution backend-owned and company-scoped
- do not let UI remove `settlement_applications` directly
- preserve audit trail rows and open-item restoration semantics already proven by settlement-document reversal tests
- add formal management surfaces around company/book mapping and permission authority; allowed accounts, optional active mapping enforcement, threshold approval, approve/reject transitions, self-approval guard, session-role approval authority, persisted CompanyAccess permission tokens, owner-governed membership permission grants, and partial adjustment amounts now have first backend-owned skeletons
- keep posting-engine execution and company-scoped audit trail as hard boundaries

## Identity handoff note

Before returning fully to the AR/AP stream, the last platform-identity hardening slice was closed to a practical stop point:

- TOTP enrollment now protects new secrets at rest instead of storing fresh `secret_base32` values in plaintext
- business sessions and MFA challenges now snapshot `security_stamp`, so password/MFA security changes revoke old authenticated state instead of leaving it silently reusable
- MFA challenge completion now has a real retry ceiling plus temporary lockout, rather than infinite second-factor guessing
- MFA recovery policy closure is now visible in both Business Profile and SysAdmin read surfaces

Planned sequencing from here:

1. resume `AR/AP mapping/search/policy polish`
2. close remaining AR/AP surfaces that still feel demo-driven
3. then open `Inventory` with multi-warehouse as a first-class assumption

## Inventory Phase D checkpoint

An explicit `Inventory Phase D` design draft now exists in [INVENTORY_PHASE_D_DESIGN.md](d:/Coding/Citus/INVENTORY_PHASE_D_DESIGN.md).

Current planning direction:

- `Inventory` stays an independent module, not a side effect of AP bills or AR invoices
- `AP Bill` may create or link inbound inventory receipt truth, but may not overwrite quantity or cost truth
- `Sales Out` / shipment may create or link outbound inventory issue truth, but outbound cost remains inventory-derived instead of operator-entered
- Phase D should start with multi-warehouse, append-only stock ledger truth, item+warehouse balance cache, cost layers, layer consumption, transfer lifecycle, write-off / adjustment, and BOM-ready manufacturing entry

Phase D / D1 implementation has now started:

- new `Citus.Modules.Inventory.Domain.Shared` foundation types now define item kind, inventory methods, backorder/low-stock policy, document types/status, movement direction/type, and the first inventory foundation record models
- new `Citus.Modules.Inventory.Application.Contracts` and `Citus.Modules.Inventory.Application` projects now define the first `InventoryFoundationWorkflow` seam plus the `IInventoryFoundationStore` contract
- `Infrastructure.PostgreSQL` now has a first `PostgreSqlInventoryFoundationStore` that creates the company-scoped inventory foundation tables (`company_inventory_policies`, `inventory_items`, `inventory_warehouses`, `item_warehouse_balances`, `inventory_documents`, `inventory_document_lines`, `inventory_ledger_entries`, `inventory_cost_layers`, `inventory_layer_consumptions`, `warehouse_transfers`, `warehouse_transfer_lines`, `boms`, `bom_lines`) and can ensure/read company foundation summary
- this is still D1 foundation only: no runtime UI/workflow has been attached yet, and no AP/AR integration has been switched over to inventory truth in the same slice
- `Web.Shell` now also has a first `/company/inventory-foundation` management entry backed by the inventory module seam; company costing policy, inventory item master rows, and warehouse master rows can be reviewed and minimally governed without collapsing inventory truth back into shell-only tables
- `Web.Shell` now also respects company type when exposing that inventory-facing entry: trading companies keep `Inventory Foundation` visible by default, while service-led companies stay on the lighter `Product / Service Setup` path unless company governance later opens the stricter inventory track
- `Inventory D1.3` now extends item-master truth beyond naming and tracking flags: stock-managed items now carry `stock_uom_code` plus default `inventory asset / COGS / write-off / purchase variance` account references inside the inventory seam, and `/company/inventory-foundation` now governs those defaults without making shell invent its own direct GL lookup path
- `Inventory D2` now has a first independent `purchase_receipt` truth path: `Citus.Modules.Inventory.Application` exposes `InventoryReceiptWorkflow`, `Infrastructure.PostgreSQL` persists posted purchase receipts into `inventory_documents` / `inventory_document_lines` / `inventory_ledger_entries` / `inventory_cost_layers`, and `Web.Shell` exposes `/company/inventory-purchase-receipt` as the first inbound inventory workbench
- this D2 slice is intentionally inventory-owned and does not yet collapse into `AP Bill`; the current bill truth still lacks `item / warehouse / quantity / unit cost` semantics, so the AP bridge remains a later, explicit follow-up rather than a hidden partial integration
- `Inventory D4` has now started with transfer-only scope: `Citus.Modules.Inventory.Application` exposes `InventoryTransferWorkflow`, `Infrastructure.PostgreSQL` persists `warehouse_transfers` as first-class transfer masters, and ship/receive now append `transfer_ship` / `transfer_receive` inventory documents plus ledger/balance/cost-layer effects without touching AR/AP bridge logic
- `Web.Shell` now exposes `/company/inventory-transfer` as the minimal transfer workbench, and that workbench stays deliberately narrow: save draft, submit, ship, receive, and review recent transfers; write-off / adjustment remains a later D4 slice
- `Inventory D4` now also includes the first adjustment/write-off seam: `InventoryAdjustmentWorkflow` plus `PostgreSqlInventoryAdjustmentStore` post `inventory_adjustment_gain`, `inventory_adjustment_loss`, and `inventory_write_off` as inventory-owned truth, updating ledger, balances, and cost layers/layer consumptions without reaching into AR/AP
- `Inventory D5` is now implemented as the first manufacturing seam: `InventoryManufacturingWorkflow` plus `PostgreSqlInventoryManufacturingStore` now govern `boms`, component lines, `manufacturing_issue`, and `manufacturing_receipt` entirely inside Inventory without borrowing AP/AR document truth
- `Web.Shell` now exposes `/company/inventory-manufacturing` as the minimal BOM + manufacturing workbench, including BOM maintenance, estimated roll-up review from open cost layers, and the first inventory-owned manufacturing post path that consumes component layers and creates finished-good layers
- this D5 slice intentionally stays inside inventory truth: there is still no accounting bridge, no WIP/variance accounting, no routing/work-order layer, and no service/task crossover in the same step
- `Web.Shell` now exposes `/company/inventory-adjustment` as the minimal count-gain / count-loss / write-off workbench, and company policy is already respected there: when `RequireWriteOffApproval` is true, direct write-off remains blocked until a later approval surface arrives
- `Inventory D3` now has a first independent `sales_issue` truth path: `Citus.Modules.Inventory.Application` exposes `InventoryIssueWorkflow`, `Infrastructure.PostgreSQL` persists posted sales issues into `inventory_documents` / `inventory_document_lines` / `inventory_ledger_entries` / `inventory_layer_consumptions`, and `Web.Shell` exposes `/company/inventory-sales-issue` as the first outbound inventory workbench
- this first D3 slice is deliberately inventory-owned and still independent from AR invoice / shipment truth; outbound cost is derived from inventory cost layers instead of being operator-entered, and AR-to-inventory shipment bridging remains a later explicit seam
- `Inventory D3.2` now adds the first inventory-owned review surface: `Citus.Modules.Inventory.Application` exposes `InventoryReportingWorkflow`, `Infrastructure.PostgreSQL` projects `item_warehouse_balances + inventory_cost_layers + inventory_ledger_entries` into a compact availability/ledger dashboard, and `Web.Shell` exposes `/company/inventory-availability`
- this D3.2 slice is still read-only by design: it confirms on-hand / reserved / available / in-transit state plus recent stock-ledger truth without introducing transfer, write-off, or accounting bridge behavior ahead of schedule
- `Inventory D3.3` now adds server-side item/warehouse drill-down on top of that review surface: the same inventory reporting seam can focus both balances and ledger rows by selected item and/or warehouse, while keeping the broader overview intact
- this D3.3 slice is still intentionally read-only and inventory-owned; it improves inspection depth without pulling in transfer lifecycle, write-off controls, or AR/AP bridging yet
- the first `Phase E0 / Operational Guardrail` slice is now in place at the shell/foundation boundary: new stock items can no longer be newly switched into the guarded `ManageStockBySku` path, the inventory foundation UI now states that tracked lot/serial document flows are not open yet, and the current receipt / sales-issue / transfer / adjustment / manufacturing workbenches all explicitly expose only warehouse-managed stock items instead of pretending tracked operational flows are ready
- the first `Phase E / Inventory Control Hardening` slice is now in place for write-off and transfer control lanes: write-off can move through `request -> approve -> post` inside the inventory seam, and warehouse transfer now treats `submit / ship / receive` as distinct control steps with separate shell-side legality checks and responsibility cues even before formal permission tokens are wired
- the first `Phase F / AP Inbound Truth Bridge` seam is now in progress on the bill side: `Bill` no longer jumps straight from draft-save to post, and the shell/API/repository seam now recognizes `draft -> submitted -> posted` as the controlled bill lifecycle lane even before receipt-first matching is attached
- submitted bills now also keep source-document continuity in `Web.Shell`: source browser and detail review can reopen the bill workbench for `submitted` status without reopening line edits, so the new lifecycle seam stays operational instead of becoming a one-shot page state
- bill lines now also carry the first inventory-grade inbound seam (`item / warehouse / uom / quantity / unit cost`) without pretending that bill itself owns inventory truth; `Web.Shell` can hand those lines off into `/company/inventory-purchase-receipt`, where receipt stays authoritative and posts back into inventory with an `ap_bill` source anchor
- bill workbench now also reads a first receipt-first matching summary back from inventory truth: once a bill has inventory-grade inbound lines, shell can show `no receipt / partial / full / over-receipted` against anchored purchase receipts without letting AP mutate receipt truth
- bill draft, bill detail, and purchase receipt workspaces now share that same anchored receipt-review continuity: operators can see recent anchored receipts plus mismatch state from either AP-side review or inventory-side posting, while receipt remains the authoritative inbound truth
- bill lifecycle has now also grown its first controlled off-ramp on the AP side: `submitted` bills can be explicitly cancelled without pretending that posted reverse belongs in the bill workbench; posted reversal continues to stay on the governed source-document review lane
- receipt-first matching is no longer aggregate-only: bill workbench, bill detail, and purchase receipt now also review bill-vs-receipt groups by `item / warehouse / UOM`, so operators can see which inbound slices remain short, full, or over-receipted without letting AP overwrite inventory truth
- bill-assisted purchase receipt now defaults to the remaining quantity per `item / warehouse / UOM` group instead of replaying the full bill inbound quantity every time; when bill truth is already fully covered by anchored receipts, the purchase-receipt form now stays empty by default so operators do not drift into accidental over-receipt
- purchase receipt now also exposes a `Load Remaining From Bill` reset path, so operators can realign the receipt form back to authoritative outstanding bill-linked groups after manual edits without reconstructing grouped remaining quantities by hand
- source browser now lets bill rows jump straight into `/company/inventory-purchase-receipt?billId=...`, so AP-side review can move into receipt-first matching without always detouring through source detail first
- bill posting now has a backend-owned receipt-first gate: when inventory-grade inbound lines exist, `submitted -> posted` is blocked until anchored receipt truth is either fully covered or not participating in receipt-first matching at all, and the bill workbench now surfaces that hold state explicitly instead of treating post as an unconditional next step
- the receipt-first posting gate is now formalized as a small backend authority object (`BillReceiptPostingGate`) with dedicated accounting-api tests, so bill posting legality no longer depends on duplicated inline switch statements alone
- source browser bill rows now also project that same receipt-first posting gate: AP operators can see `post enabled / hold` state and the backend-owned reason before opening the bill workbench, instead of discovering the hold only when they drill into the bill editor

Post-Phase-D follow-up now explicitly reserved:

- `AP Bill` should later grow a formal lifecycle of `draft -> submitted -> posted -> cancelled/reversed`
- that AP lifecycle work belongs to the follow-on AP/inventory bridging stream, not the current inventory-owned D2 slice
- the future AP bridge should only start once bill truth is upgraded to inventory-grade inbound semantics instead of trying to reuse the current expense-only bill lines

Resolved governance constraints guiding D1:

- write-off authority remains a later user-permission surface, but `require_writeoff_approval` is part of the company-scoped foundation now
- `transfer ship` and `transfer receive` remain separate permission seams and must not be collapsed into one operational action right
- inventory costing policy belongs to company governance
- negative stock defaults to strict `Disallow`; any override must remain a deliberate company-scoped governance choice
- current `Product / Service Setup` remains the shared entry seam, but company type decides whether it evolves toward a strict inventory item master or a lighter service catalog

## Tax note

`Tax Setup` should no longer be treated as a terminal configuration screen.

The intended progression is:

1. governed tax code setup
2. sales tax report visibility
3. governed `File Sales Tax` workflow

This means tax-code semantics must remain reporting-safe:

- company-scoped
- line-level truth carrying through document lifecycle
- posting-aware recoverability semantics
- compatible with later filed-period reporting and filing actions

## UI theme note

The imported `Volo.Abp.LeptonXLiteTheme.SourceCode` package can be integrated as a reference and source-code aid, but it should not become the primary Citus product UI direction.

Recommended stance:

- keep the current Citus Blazor Web App + MudBlazor accounting workflow design as the primary product surface
- absorb LeptonX patterns selectively for Citus-governed account/admin pages, layout density, theme-token organization, and shell polish
- do not let LeptonX generic admin layout override Citus accounting-specific navigation, company context, source-document review, AR/AP control panels, or backend-owned workflow cues
- avoid deep coupling to the imported `5.1.1` source package while the repository is moving on `.NET 11 + C# 15 preview`; use it as reference material unless a specific component proves worth adapting

## Phase F.1 checkpoint

The first hardening slice after `Receipt-first AP / inventory bridge` should solidify the current bill/receipt lane without reopening the whole phase.

Current F.1 checkpoint:

- `BillReceiptPostingGate` now lives in `Citus.Accounting.Application` as a formal posting policy seam instead of remaining API-local glue
- `PostBillCommandHandler` now enforces the receipt-first gate directly, so non-HTTP posting paths cannot bypass the bill/receipt hold lane
- source-browser bill hold enrichment now uses a batch inventory projection instead of per-row summary calls, keeping the same authority output while removing the obvious N+1 shape
- this hardening slice does not introduce persisted discrepancy workflow yet; it only freezes the existing posting gate and browser truth into more stable seams

## Phase G checkpoint

The first `Shipment-first AR bridge` slice should start by upgrading invoice truth into an outbound inventory-grade hand-off seam without pretending shipment truth already exists.

Current G checkpoint:

- invoice draft lines now carry optional outbound inventory-grade fields: `item`, `warehouse`, and `stock UOM`
- invoice persistence, draft read models, and API save/read contracts now preserve those outbound hand-off fields inside the AR draft seam
- the invoice workbench now exposes a shipment-first hand-off section that stays explicit about the boundary: invoice only carries outbound intent, while Sales Issue still owns authoritative outbound inventory and cost truth
- the Sales Issue workspace now accepts invoice-origin hand-off context and can prefill customer plus outbound issue lines from saved invoice truth
- this slice deliberately stops short of shipment posting gate, shipped-eligible invoicing, or invoice-vs-issue reconciliation; it only establishes the first outbound bridge seam

## Phase G.2 checkpoint

The next `Shipment-first AR bridge` slice now hardens the first outbound seam into an operator-visible review and posting gate without claiming formal shipment truth is finished.

- inventory now projects `invoice -> sales_issue` matching truth through a real hand-off summary plus a batch posting-gate snapshot
- invoice posting authority moved into `Citus.Accounting.Application`, so AR posting can no longer bypass outbound issue coverage by staying on the HTTP edge
- source browser now shows invoice-side outbound hold state in the same read surface that already carried bill-side receipt holds
- source detail now exposes issue-first review for invoices, including anchored sales issues and grouped item/warehouse/UOM remaining quantities
- the invoice workbench itself now shows the outbound post gate once a saved/submitted invoice participates in inventory hand-off, so operators see the hold before backend rejection
- this is still an interim issue-first control seam: shipment truth and shipped-eligible invoicing are not yet complete, but outbound AR posting no longer pretends invoice truth alone is enough once inventory-grade hand-off lines exist

## Phase G.3 checkpoint

This slice moves the outbound lane from `issue-gated usable` into a more formal `shipment-first matching / posting / fulfillment-truth` lane without pretending the whole SO/fulfillment model is done.

- inventory now owns a persisted `shipment` document type inside `inventory_documents` / `inventory_document_lines`, so invoice outbound coverage is no longer only an issue summary plus gate
- shipment truth now carries fulfillment-owned metadata (`carrier / tracking / shipping slip`) and stays distinct from both invoice charge truth and sales-issue cost truth
- AR invoice posting now gates against anchored shipment coverage in `Citus.Accounting.Application`, so stock invoicing no longer outruns persisted fulfillment truth once outbound inventory-grade hand-off lines exist
- the source browser, source detail page, and invoice workbench now all project shipment-first hold state and continuity, with `Open Shipment Workspace` replacing the old issue-first entry point
- Sales Issue remains in the lane, but now as the downstream inventory/cost consumer: shipment can hand control into sales issue through `sourceType=shipment&shipmentId=...`
- this slice still intentionally defers full `SO -> Shipment -> Sales Issue -> Invoice` state truth, persisted discrepancy investigation, return/disposition, and tracking-aware document integration

## Phase G completion checkpoint

`Shipment-first AR bridge` is now complete as a coherent phase boundary. The project has moved from `issue-gated usability` to a persisted outbound matching lane that can survive reloads, browser review, and later investigation work without collapsing invoice truth into fulfillment truth.

- inventory now persists outbound matching projections in `inventory_outbound_matching_lanes` for both `invoice_shipment` and `shipment_issue`, keyed by `company + source document + item + warehouse + UOM`
- invoice posting now consumes `persisted shipment truth + computed posting policy`, so shipped-eligible invoicing for stock lines is enforced from the application seam rather than only from ephemeral summaries
- partial and split fulfillment are now expressed through persisted line-level lane summaries: `pending / partially / fully / over` shipped and issued states are calculated per `(item, warehouse, stock UOM)` instead of assuming one full outbound event
- source browser remains batch-friendly by reading shipment posting-gate snapshots, while source detail, invoice workbench, and shipment workspace now expose the richer `invoice -> shipment -> sales_issue` lane including downstream issue follow-through and outbound discrepancies
- this completes Phase G without overclaiming later truths: full `SO -> Shipment -> Sales Issue -> Invoice` state rollup, explicit discrepancy/investigation entities, return/disposition, and tracking-aware document integration remain deliberately deferred to later phases

## Phase H0 checkpoint

The first `Return / Disposition Truth` prerequisite slice should only close the outbound base needed by return. It should not reopen Phase G or pretend return itself already exists.

- outbound discrepancy truth is now persisted in `inventory_outbound_discrepancy_lanes` instead of remaining only a computed review summary
- the persisted discrepancy lane currently tracks three concrete mismatch families:
  - `invoice_shipment`
  - `shipment_issue`
  - `invoice_coverage`
- invoice shipment summaries now carry formal outbound state truth for both fulfillment and AR coverage:
  - `shipped`
  - `remaining_to_ship`
  - `invoiced`
  - `remaining_to_invoice`
  - `invoice coverage status`
- source browser stays batch-friendly while still surfacing invoice coverage review, and source detail / invoice editor / shipment workspace now all project the same persisted outbound base
- this H0 slice still does **not** introduce `return_receive`, inspection, disposition, or financial return bridging; it only finishes the outbound foundation required before those lanes open

## Phase H1 checkpoint

The first real `Return / Disposition Truth` slice now opens an inventory-owned `customer return receive` lane, but still stops short of restock, write-off, or any financial bridge.

- return receive is now anchored to posted shipment truth, not to invoice truth; `invoice` remains commercial context while `shipment` remains the physical source anchor
- the system now persists `customer_return_receipt` documents inside the inventory seam and ties them back to the originating shipment document
- H1 enforces a real return quantity ceiling: each grouped `(item, warehouse, stock UOM)` return line can only receive up to the shipped-and-not-yet-returned remaining quantity
- return intake now captures physical-return seam data up front:
  - source anchor
  - item
  - quantity
  - condition
  - return reason
  - disposition-reason seam
- this slice deliberately does **not** move stock back to `on_hand`; no inventory ledger, balance, or cost-layer mutation occurs until later inspect/disposition logic decides whether the returned goods are restockable
- vendor return remains intentionally deferred to a later lane anchored to receipt truth rather than being mixed into this first customer-return intake path

## Phase H.2 checkpoint

This slice introduces `Receipt / ReceiptLine` as first-class source-document foundation only. It does **not** reopen the receipt-first bridge or transfer inbound inventory truth away from the current transitional bill path.

- `receipts` and `receipt_lines` now exist as company-scoped persisted document tables inside the accounting/source-document seam
- `ReceiptDocument` and `ReceiptDocumentLine` now provide the minimal document model for:
  - vendor
  - warehouse
  - receipt date
  - status
  - memo
  - source/vendor references
  - line-level item / quantity / unit
  - reserved `tracking_capture_home`
- the lifecycle skeleton is intentionally minimal:
  - `draft`
  - `posted`
- `posted` now exists only as document status truth; in this phase it does **not** drive inventory movement, cost truth, or GL truth
- H.2 exposes only document-foundation operations:
  - create draft
  - update draft
  - read one receipt
  - list receipts
  - mark draft as posted
- this slice explicitly does **not** introduce:
  - `ReceiveStockFromReceipt`
  - inventory movement from receipt
  - GR/IR posting
  - `receipt_required` consumers
  - bill behavior changes
  - bill/receipt matching
  - PPV / variance
  - tracked operational enablement
- after H.2, bill remains the active transitional inbound path; receipt exists as a standing first-class document foundation, but it does not yet take over inbound inventory truth

## Phase H.3 checkpoint

This slice activates `Receipt-first inbound matching / posting-gate` on top of the standing `ReceiptDocument` foundation without turning receipt into an inventory or GL driver.

- bill/receipt bridge truth is now persisted in `bill_receipt_matching_allocations`, so bill coverage no longer depends only on transient receipt summaries
- the authoritative matching anchors in this first version are:
  - `company`
  - `vendor`
  - `item`
  - `warehouse`
  - `stock UOM`
  - `posted receipt only`
- the matching lane now supports realistic partial coverage:
  - one bill line can be covered by multiple posted receipt lines
  - one receipt set can allocate across multiple bill lines
  - bill coverage never exceeds bill quantity basis
  - receipt allocation never exceeds remaining posted receipt quantity
- bill posting authority now consumes persisted bill/receipt matching truth plus computed policy from `Citus.Accounting.Application`; draft receipts do **not** satisfy the receipt-first gate
- source browser, source detail, and bill editor now all project the same receipt-first coverage truth, while staying batch-friendly through repository-level grouped refresh instead of per-line UI recomputation
- this slice still deliberately does **not** introduce:
  - receipt-driven inventory posting
  - inventory ledger / cost-layer creation from receipt
  - GR/IR
  - PPV / variance
  - PO truth
  - tracked operational enablement
  - vendor return lane
  - shell-wide receipt workspace expansion
- after H.3, bill remains the transitional inbound AP path, but posted `ReceiptDocument` truth now participates as a formal control input in whether bill posting is allowed

## Phase H.4 checkpoint

This slice adds a persisted inbound discrepancy / investigation lane on top of H.3 matching truth, but it still stops before receipt-driven inventory activation.

- inbound discrepancy truth now persists in `bill_receipt_matching_discrepancy_lanes` instead of being implied only by current match status at review time
- the first discrepancy families are intentionally narrow:
  - `missing_receipt_coverage`
  - `partial_receipt_coverage`
- discrepancy rows are keyed to the same authoritative bill-line anchors already used by receipt-first matching:
  - `company`
  - `vendor`
  - `item`
  - `warehouse`
  - `stock UOM`
  - posted receipt only
- browser, bill detail, and bill editor now expose the persisted discrepancy lane as review truth, so operators can see which inbound lines still require receipt-first investigation instead of only seeing a generic post hold
- bill posting gate itself still remains `persisted matching truth + computed policy`; H.4 does not replace the gate, it makes the unresolved inbound risk explicit and reviewable
- this is still **not** a full investigation workflow entity:
  - no manual resolve/close transition
  - no receipt-driven inventory posting
  - no GR/IR
  - no PPV / variance
  - no PO truth
  - no tracked operational enablement
  - no vendor return lane

## Phase H.5 checkpoint

This slice introduces the minimal `receipt-driven inventory activation` path, but it still stops before valuation or GL truth.

- only `posted` first-class `ReceiptDocument` truth may activate inbound inventory quantity
- activation is now guarded by a dedicated persisted line-level anchor in `receipt_inventory_activation_lines`
- activation is explicitly idempotent at the receipt-line level:
  - retrying `POST /receipts/{id}/post` on an already-posted receipt reuses the same activation lane
  - the same receipt line cannot activate inbound quantity twice
  - partial activation state is treated as an inconsistency, not silently replayed
- a posted receipt now creates quantity-only inbound inventory truth:
  - one inventory document
  - one inventory ledger movement per receipt line
  - item/warehouse on-hand balance mutation
- this slice still deliberately refuses to invent valuation or GL truth:
  - no cost-layer creation from receipt activation
  - no GR/IR
  - no PPV / variance
  - no PO truth
  - no tracked operational enablement
- after H.5, receipt becomes the authoritative inbound quantity owner for this minimal path, while bill remains a transitional AP document and does not gain a second way to activate the same posted receipt line

## Phase H.6 checkpoint

This slice hardens receipt activation review and retry without entering valuation, GR/IR, PPV, or tracked receipt enablement.

- receipt activation now exposes stable read truth instead of forcing reviewers to infer state from raw line counts:
  - `not_posted`
  - `posted_not_activated`
  - `activated`
  - `activation_failed_retryable`
  - `activation_inconsistent`
- activation failures are persisted in `receipt_inventory_activation_failures`, giving posted receipts a reviewable retry lane instead of becoming silent operational dead ends
- `POST /accounting/receipts/{id}/inventory-activation/retry` reuses the same `PostReceiptWorkflow` authority path and does not create a second activation mechanism
- successful activation clears the retryable failure record, while existing receipt-line activation guards continue to prevent duplicate inbound quantity
- the legacy purchase receipt path now has an explicit first-class receipt guard:
  - `receipt_document` and `first_class_receipt` sources must activate through the receipt activation workflow
  - `ap_bill` remains a transitional fallback for legacy inbound quantity until bill-side retirement rules are tightened in a later slice
- this slice still deliberately does **not** introduce:
  - GR/IR
  - PPV / variance
  - receipt valuation universe
  - tracked receipt operational enablement
  - full AP bill legacy-path retirement

Project bookkeeping:

- the checkpoint before H.4 was pushed to GitHub at commit `04a42d4`
- H.4, H.5, and H.6 are currently local working-tree changes until the next explicit push

## Phase H.7 checkpoint

This slice turns the remaining legacy AP bill inbound fallback from a human convention into an explicit retirement policy.

- legacy inbound policy now lives in the inventory application-contract seam through `LegacyInboundReceiptPathPolicy`
- `receipt_document` and `first_class_receipt` sources remain blocked from the legacy purchase receipt workflow
- `ap_bill` fallback is no longer an unconditional escape hatch:
  - it must include a source bill document id
  - it must load bill-side policy truth before posting
  - it is blocked when first-class receipt matching coverage already exists for the bill
  - it is blocked when the requested item / warehouse / UOM anchor does not exist on the source bill
  - it is blocked when the requested quantity exceeds the remaining legacy bill quantity after prior legacy receipts
- transitional behavior is intentionally narrow:
  - `ap_bill` fallback may still be used only when there is no first-class receipt coverage and the request remains within the bill's remaining legacy quantity ceiling
  - non-bill manual inventory receipts remain outside the AP bill retirement policy
- this slice still deliberately does **not** introduce:
  - full removal of legacy AP bill fallback
  - receipt valuation / cost-layer replacement
  - GR/IR
  - PPV / variance
  - PO truth
  - tracked receipt enablement

The next decision is whether to fully retire the `ap_bill` fallback once first-class receipt authoring/review is operationally comfortable, or to first introduce receipt valuation boundaries so the old cost-layer-creating path can be replaced safely.

## Phase H.8 checkpoint

This slice introduces an explicit inbound valuation boundary lane without copying legacy AP bill cost-layer behavior into first-class receipts.

- receipt remains the owner of inbound physical quantity truth
- bill remains supplier charge / cost evidence, not physical truth
- bill/receipt matching now acts as the eligibility bridge for valuation boundary truth
- `receipt_inventory_valuation_lines` persists bill-backed valuation slices for activated receipt lines:
  - receipt line anchor
  - bill line anchor
  - valued quantity
  - document/base currency
  - FX rate
  - unit and extended base cost evidence
- valuation refresh is explicit and idempotent through `IReceiptInventoryValuationStore`
- `PostReceiptWorkflow` now refreshes valuation truth after quantity activation, while still keeping quantity activation separate
- receipt list/detail read models now expose valuation status alongside activation status
- the first status model is deliberately bounded:
  - `no_quantity_activation`
  - `awaiting_bill_coverage`
  - `quantity_activated_unvalued`
  - `partially_valued`
  - `valuation_boundary_complete`
  - `valuation_inconsistent`
- this slice still deliberately does **not** introduce:
  - inventory cost layer creation from first-class receipt valuation
  - GR/IR
  - PPV / variance
  - PO truth
  - tracked receipt enablement
  - GL posting from receipt valuation

H.8 therefore makes the current split explicit:

- first-class receipt can own quantity now
- bill-backed valuation evidence can be persisted now
- cost layers / GRIR / PPV remain a later accounting and inventory valuation decision

## Phase H.9 checkpoint

This slice introduces the explicit receipt valuation -> cost-layer emission seam without turning it into the full GR/IR or PPV universe.

- `receipt_inventory_valuation_lines` remains valuation evidence, not cost-layer truth by itself
- `receipt_inventory_cost_layer_emission_lines` now persists emission truth for each valuation slice that has actually produced an `inventory_cost_layers` row
- cost-layer emission is deliberately strict:
  - receipt quantity must already be activated
  - valuation evidence must exist
  - the linked bill must be `posted`
  - one valuation slice can emit only once
- emission is idempotent through `IReceiptInventoryCostLayerEmissionStore`:
  - repeated workflow retries do not duplicate cost layers
  - receipt-level advisory locking plus a unique valuation-line emission guard protects concurrent retry
- `PostReceiptWorkflow` now runs:
  - quantity activation
  - valuation evidence refresh
  - cost-layer emission attempt
- receipt list/detail read models now expose emission state alongside activation and valuation state:
  - `no_quantity_activation`
  - `awaiting_bill_coverage`
  - `awaiting_posted_bill`
  - `valuation_backed_not_emitted`
  - `partially_emitted`
  - `fully_emitted`
  - `emission_inconsistent`
- H.9 still deliberately avoids:
  - GR/IR
  - PPV / variance
  - PO truth
  - GL posting
  - tracked receipt enablement
  - vendor return
  - shell-wide receipt workspace expansion

This phase makes first-class receipt valuation usable by outbound costing through cost layers, while keeping the transitional truth boundary honest: receipt owns quantity, bill-backed matching provides valuation evidence, and accounting settlement remains a later bridge.

## Phase H.10 checkpoint

This slice hardens receipt valuation emission before moving toward any accounting valuation universe.

- receipt emission now has a reconciliation read model instead of only an emission status:
  - emitted quantity / cost
  - cost-layer quantity / original cost
  - missing cost-layer count
  - orphan cost-layer count
  - reconciliation status
- the first reconciliation status model is intentionally narrow:
  - `no_emission`
  - `reconciled`
  - `cost_layer_missing`
  - `orphan_cost_layer`
  - `quantity_mismatch`
  - `amount_mismatch`
- receipt list/detail read models now expose both:
  - emission state
  - emission/cost-layer reconciliation state
- outbound sales issue now has an explicit guard for the dangerous state:
  - physical quantity is available
  - emitted cost-layer coverage is missing or insufficient
  - outbound costing is blocked until receipt valuation emission is reviewed
- an opt-in PostgreSQL integration test now exercises:
  - first-class receipt valuation emission
  - idempotent retry
  - cost-layer count stability
  - reconciliation status
- H.10 still deliberately avoids:
  - GR/IR
  - PPV / variance
  - PO truth
  - GL posting
  - tracked receipt enablement

H.10 closes the immediate operational gap between quantity truth and cost-layer truth enough for outbound costing to fail loudly and reviewably, rather than silently discovering missing valuation too late.

## Phase H.11 checkpoint

This planning slice defines the GR/IR boundary for receipt-backed inventory value without implementing PPV, PO truth, or full GL posting yet.

Authority decision:

- Receipt remains the physical quantity owner.
- `receipt_inventory_valuation_lines` remains the valuation evidence lane.
- `receipt_inventory_cost_layer_emission_lines` remains the inventory cost-layer emission lane.
- GR/IR is the accounting bridge that should consume reconciled receipt-backed inventory value.
- Bill remains supplier charge / AP truth and may clear or match GR/IR later, but it must not recreate physical quantity or cost-layer ownership.

Minimum future GR/IR bridge shape:

- a persisted GR/IR bridge lane should be keyed to the same receipt valuation/emission anchors:
  - company
  - receipt id
  - receipt line number
  - valuation line id
  - cost layer emission line id
  - bill id / bill line number as transitional valuation evidence
- GR/IR eligibility should require:
  - receipt inventory quantity activated
  - valuation evidence exists
  - cost-layer emission is complete for the relevant slice
  - emission reconciliation is `reconciled`
  - linked bill-backed valuation evidence is posted-backed in the current transitional architecture
- the initial bridge should support partial slices rather than forcing whole-receipt all-or-nothing behavior.

Minimum accounting semantics:

- On eligible receipt-backed value:
  - Dr Inventory Asset
  - Cr GR/IR Clearing
- On AP bill settlement against the same receipt-backed value:
  - Dr GR/IR Clearing
  - Cr AP
- PPV / variance is explicitly not part of H.11:
  - price differences should remain held or unresolved until a later variance lane exists
  - H.11 should not invent automatic variance postings

Required future state truth:

- `not_eligible`
- `eligible_not_posted`
- `partially_posted`
- `posted`
- `blocked_reconciliation_required`
- `blocked_variance_required`

This phase explicitly does **not** authorize:

- direct GR/IR posting from receipt quantity alone
- GR/IR posting from valuation evidence that has not emitted reconciled cost layers
- PPV / purchase price variance
- PO ordered / received / billed truth
- tracked receipt enablement
- GL implementation in this planning slice

The next implementation slice should be a minimal persisted GR/IR bridge lane and read model before any journal-writing behavior is added.

## Phase H.12 checkpoint

H.12 implements the minimal persisted GR/IR bridge read/control lane authorized by H.11.

Implemented boundary:

- `receipt_grir_bridge_lines` is the persisted control lane for receipt-backed value that is eligible for future GR/IR posting.
- The bridge is anchored to inventory-side truth:
  - receipt id
  - receipt line number
  - valuation line id
  - cost-layer emission line id
  - cost layer id
  - bill id / bill line number as transitional valuation evidence
- The bridge can be refreshed explicitly through the receipt API.
- Receipt list/detail now exposes GR/IR bridge status alongside activation, valuation, emission, and reconciliation truth.

Eligibility rule:

- The bridge only considers cost-layer emission lines.
- Emitted cost layers must reconcile back to actual `inventory_cost_layers`.
- Reconciled lines become `eligible_not_posted`.
- Missing, orphaned, quantity-mismatched, or amount-mismatched cost-layer truth blocks the bridge as `blocked_reconciliation_required`.

H.12 deliberately does not write journals.

Deferred:

- GR/IR journal posting
- AP bill settlement against GR/IR
- PPV / purchase price variance
- PO ordered / received / billed truth
- tracked receipt enablement
- Shell-wide receipt workspace expansion

Decision:

Phase H now has the full inbound truth ladder from receipt foundation through activation, valuation, cost-layer emission, reconciliation, and GR/IR bridge eligibility. The next safe step is journal-writing GR/IR posting only after this bridge lane has been reviewed and hardened.

## Phase H.13 checkpoint

H.13 introduces the first minimal GR/IR journal posting path.

Boundary:

- The posting path only consumes `eligible_not_posted` GR/IR bridge lines.
- It does not create valuation, cost layers, or receipt quantity.
- It does not perform AP bill clearing.
- It does not create PPV / purchase price variance.
- It does not introduce PO truth.
- It does not enable tracked receipt operations.

Implemented shape:

- A receipt GR/IR posting source document enters the existing Posting Engine.
- The posting engine creates the journal entry.
- The bridge posting source is persisted as a posting batch.
- Posted bridge lines are marked `posted` and linked to the generated journal entry.

Accounting semantics:

- Dr Inventory Asset
- Cr GR/IR Clearing

Control rules:

- A GR/IR clearing account must be supplied explicitly.
- Each bridge line must resolve an inventory asset account from the item master.
- Bridge lines can only be posted once.
- Existing posted batches are idempotently reusable for retry.

Still deferred:

- clearing GR/IR against AP bill settlement
- PPV / variance recognition
- PO ordered / received / billed truth
- tracked receipt enablement
- Shell-wide GR/IR workbench

## Phase H.13 hardening checkpoint

This slice hardens the minimal GR/IR posting path without expanding into PPV, PO truth, tracked receipt, or AP settlement.

What changed:

- Company-level GR/IR clearing account policy now exists as a persisted control seam.
- GR/IR posting may use either an explicit clearing account or the company default policy.
- Receipt list/detail GR/IR bridge summaries now expose posted journal linkage:
  - journal entry id
  - journal entry display number
  - posted amount
  - last posted timestamp
- PostgreSQL integration coverage now exercises the full bridge-to-journal path:
  - receipt-backed emission
  - GR/IR bridge eligibility
  - company default clearing policy
  - posting engine journal creation
  - bridge line posted linkage
  - idempotent retry

Still deferred:

- AP bill settlement against GR/IR
- PPV / variance recognition
- PO truth
- tracked receipt enablement
- Shell-wide GR/IR operations workspace

## Phase H.14 checkpoint

H.14 starts the GR/IR against AP bill settlement control lane as read/control truth only.

What changed:

- A persisted `receipt_grir_ap_settlement_lines` lane now represents receipt-backed GR/IR settlement eligibility.
- Receipt and bill read models expose settlement status, eligible amount, settled amount, remaining amount, and blocked counts.
- The lane only considers:
  - posted GR/IR bridge lines
  - posted GR/IR journal linkage
  - posted Bills
  - AP open item presence for the Bill
- Partial settlement is represented as persisted control truth through settled quantity/amount columns, but no accounting settlement posting is emitted yet.
- The GR/IR clearing account policy was hardened so the default account must be an active liability account in the active company.
- Saving the default GR/IR clearing account policy now requires owner/accounting-governance authority.

Still deferred:

- AP bill settlement journal posting
- PPV / variance recognition
- PO truth
- tracked receipt enablement
- Shell-wide GR/IR settlement workbench

CI note:

PostgreSQL integration coverage now includes valuation emission, GR/IR bridge refresh, journal linkage, idempotent retry, governance guard, and settlement eligibility. The repository currently has no root `.github` workflow folder, and project rules forbid creating new root folders without an approved path; CI workflow wiring therefore remains a separate approved-path task.

## Phase H.15 checkpoint

H.15 adds the minimal GR/IR settlement execution lane on top of the H.14 read/control truth.

What changed:

- `receipt_grir_ap_settlement_batches` records each execution attempt with an idempotency key, requested amount, settled amount, settled quantity, and line count.
- `receipt_grir_ap_settlement_batch_lines` records the exact settlement line slices consumed by each execution batch.
- Settlement execution only consumes settlement lines that remain `eligible_not_settled` or `partially_settled`.
- Partial execution is allowed, but requested amount cannot exceed the remaining eligible amount.
- Re-running the same idempotency key returns the existing execution result instead of clearing the slice again.
- Receipt-level API now has `POST /receipts/{id}/grir-settlement/execute`.

Still deferred:

- settlement journal posting / full AP clearing accounting
- PPV / variance recognition
- PO truth
- tracked receipt enablement
- reverse / void reconciliation workflow beyond the current non-posted journal block
- Shell-wide GR/IR settlement workbench

Authority note:

H.15 is settlement execution control truth, not a full AP settlement universe. It clears the persisted GR/IR/AP bridge slice at the control-lane level while preserving Receipt as physical truth owner, Bill as AP charge truth, and GR/IR bridge as accounting recognition truth.

## Inbound receipt/AP closure roadmap after H.15

Remaining work to close the receipt-first AP/inventory loop should stay incremental:

1. H.16 — minimal GR/IR settlement journal boundary: post only H.15 executed settlement batches.
2. H.17 — settlement journal reversal/stale reconciliation: formalize void/reverse handling and review status.
3. H.18 — AP open item clearing integration: decide how settlement applications reduce AP open item truth without duplicating pay-bill semantics.
4. H.19 — PPV / variance boundary: introduce price/quantity variance only after receipt, bill, GR/IR, and settlement slices are stable.
5. H.20 — PO ordered / received / billed truth: add three-way source truth without weakening receipt-first physical truth.
6. H.21 — vendor return / debit memo bridge: separate outbound-to-vendor physical truth from AP financial truth.
7. H.22 — tracked receipt enablement: wire lot/serial capture into receipt activation only after document paths can supply identity truth.
8. H.23 — Shell review continuity: expose the now-stable receipt/AP/GRIR truth without creating a premature workbench.

## Phase H.16 checkpoint

H.16 introduces the minimal GR/IR settlement journal boundary.

What changed:

- A posted H.15 settlement batch can now be posted through the accounting posting engine.
- The journal source type is `receipt_grir_ap_settlement_posting`.
- The posting consumes only an executed settlement batch and its batch-line slices.
- Journal shape is intentionally minimal:
  - Dr GR/IR clearing
  - Cr matched Bill line expense/inventory account
- Settlement journal posting has its own idempotency key and reuses source-level journal idempotency.
- `grir.settlement.execute` authority is now represented through the GR/IR settlement execution permission seam.
- Settlement batch journal state records `not_posted`, `posted`, and `journal_inconsistent`.
- If a linked settlement journal is later voided/reversed/non-posted, refresh blocks reposting as inconsistent.

Still deferred:

- AP open item balance clearing
- settlement reversal documents
- PPV / variance recognition
- PO truth
- tracked receipt enablement
- Shell-wide GR/IR settlement workbench

Authority note:

H.16 is the accounting boundary for settlement execution, not the full AP settlement universe. It does not let Bill retake physical truth, and it does not invent PPV or PO truth.

## Phase H.17 checkpoint

H.17 formalizes settlement journal reversal / stale reconciliation before AP open item clearing.

Boundary:

- This is journal lifecycle reconciliation truth only.
- It does not clear AP open item balances.
- It does not create settlement applications.
- It does not introduce PPV / variance.
- It does not introduce PO truth.
- It does not enable tracked receipt flows.

What changed:

- Settlement batch journal state now distinguishes:
  - `not_posted`
  - `posted`
  - `journal_stale`
  - `journal_inconsistent`
- `journal_stale` means the linked settlement journal exists but is no longer posted, such as voided / reversed / non-posted lifecycle state.
- `journal_inconsistent` means the linked journal anchor is broken, missing, or source-mismatched.
- Receipt and Bill GR/IR settlement summaries now expose journal reconciliation counts and status.
- Receipt API now has a dedicated journal reconciliation refresh endpoint:
  - `POST /receipts/{id}/grir-settlement/journal-reconciliation/refresh`
- Settlement journal reposting is blocked when a linked journal is stale or inconsistent.

Still not included:

- AP open item balance clearing
- AP subledger settlement applications
- settlement reversal documents
- PPV / variance
- PO ordered / received / billed truth
- tracked receipt enablement
- Shell-wide settlement operations surface

Authority note:

H.17 closes the lifecycle gap between H.16 settlement journal posting and future H.18 AP open item clearing. H.18 must consume only reconciled, posted settlement journal truth, not stale or inconsistent settlement batches.

## Phase H.18 checkpoint

H.18 introduces minimal AP open item clearing integration for posted GR/IR settlement batches.

Boundary:

- Clearing only consumes H.15 settlement batches whose H.16 journal is posted and H.17-reconciled.
- It does not create PPV / variance.
- It does not introduce PO truth.
- It does not enable tracked receipt flows.
- It does not expand into a Shell-wide AP settlement workbench.
- It does not change receipt quantity, valuation, cost-layer, or GR/IR recognition ownership.

What changed:

- Settlement batches now carry AP open item clearing state:
  - `not_cleared`
  - `cleared`
  - `reversed`
  - `clearing_stale`
  - `clearing_inconsistent`
  - blocked stale / inconsistent / missing-open-item / amount-exceeded states
- Receipt and Bill GR/IR settlement summaries expose AP open item clearing counts and status.
- A minimal clearing endpoint exists:
  - `POST /receipts/{id}/grir-settlement/{batchId}/ap-open-item/clear`
- A minimal reversal endpoint exists:
  - `POST /receipts/{id}/grir-settlement/{batchId}/ap-open-item/reverse`
- Clearing inserts a `settlement_applications` row with source type `receipt_grir_ap_settlement`.
- Clearing reduces the matched AP open item open amount and closes it when fully consumed.
- Retry is idempotent because the settlement batch is the application source anchor.
- `journal_stale` and `journal_inconsistent` batches are hard-blocked before AP open item truth changes.
- If an already-cleared settlement journal later becomes stale or inconsistent, refresh marks clearing as `clearing_stale` / `clearing_inconsistent` instead of silently mutating AP truth.
- Reversal is explicit, restores the AP open item from the persisted `settlement_applications` slice, deletes the stale application rows, and records reversed amount/count on the settlement batch.

Still deferred:

- settlement reversal documents beyond the minimal open-item restoration boundary
- generic unapply logic for all settlement application sources
- PPV / variance recognition
- PO ordered / received / billed truth
- tracked receipt enablement
- FX-aware GR/IR settlement clearing
- Shell-wide settlement operations surface

Authority note:

H.18 is the subledger clearing boundary, not the full AP settlement universe. It consumes only posted, reconciled GR/IR settlement journal truth and records AP open item consumption without letting Bill retake physical receipt ownership.

### Phase H.18.1 sidecar hardening

H.18.1 wraps GR/IR AP open-item clearing and clearing reversal execution in explicit application command handlers backed by `IUnitOfWork`.

What changed:

- API endpoints no longer execute clear/reverse by calling the settlement store directly.
- Clearing now goes through `ClearReceiptGrIrSettlementOpenItemCommandHandler`.
- Clearing reversal now goes through `ReverseReceiptGrIrSettlementOpenItemClearingCommandHandler`.
- Integration coverage exercises the command-handler path so the tested execution surface matches the production API seam.

Boundary:

- No PPV / variance.
- No PO truth.
- No tracked receipt enablement.
- No new AP settlement workbench.
- No change to H.18 clearing/reversal semantics beyond making execution transactional.

## Phase H.19 checkpoint

H.19 introduces the minimal PPV / purchase-variance boundary as persisted read/control truth.

Boundary:

- This is not PPV journal posting.
- This is not PO ordered / received / billed truth.
- This does not enable tracked receipt flows.
- This does not expand into a Shell-wide variance workbench.
- This does not let Bill retake physical quantity ownership.

What changed:

- `receipt_grir_ap_purchase_variance_lines` now persists variance review truth by receipt / bill / settlement batch line.
- Variance control consumes only already-cleared GR/IR/AP settlement truth.
- Receipt and Bill GR/IR settlement summaries expose:
  - purchase variance line count
  - candidate line count
  - no-variance line count
  - blocked line count
  - overall purchase variance status
  - candidate variance amount
  - last purchase variance refresh timestamp
- A refresh endpoint exists:
  - `POST /receipts/{id}/grir-settlement/purchase-variance/refresh`
- The current boundary can distinguish:
  - `no_variance`
  - `candidate_not_reviewed`
  - blocked settlement / journal / open-item / bill / quantity-basis states

Still deferred:

- PPV / variance journal recognition
- PPV approval or write-off style control
- PO three-way truth
- FX-aware variance calculation
- tracked receipt enablement
- Shell-wide variance review workspace

Authority note:

H.19 makes the price-difference risk visible after GR/IR settlement and AP open-item clearing, but it deliberately stops before recognizing PPV in accounting. Receipt remains physical quantity owner, GR/IR remains interim accounting recognition, Bill remains supplier charge truth, and purchase variance is only a reviewable boundary for the next phase.

## Phase H.20.0 checkpoint

H.20.0 introduces the minimal Purchase Order foundation and three-quantity truth design seam.

Boundary:

- PO owns ordered / commitment truth only.
- Receipt remains physical received truth.
- Bill remains AP / billed truth.
- Bill-Receipt matching, GR/IR, settlement, clearing, and variance lanes are not redesigned.
- This does not introduce full PO approval / close / cancel workflow.
- This does not introduce PPV journal posting.
- This does not enable tracked receipt flows.
- This does not expand into a Shell-wide PO workspace.

What changed:

- First-class `PurchaseOrderDocument` / `PurchaseOrderDocumentLine` domain foundation exists.
- A PostgreSQL PO repository persists:
  - `purchase_orders`
  - `purchase_order_lines`
- PO status skeleton exists:
  - `draft`
  - `issued`
  - `closed`
  - `cancelled`
- Minimal API endpoints exist for:
  - list PO
  - get PO detail
  - save PO draft
  - issue PO
- Receipt lines and Bill lines now support explicit PO line anchors:
  - `purchase_order_id`
  - `purchase_order_line_number`
- Three-quantity read truth is available:
  - ordered quantity
  - received quantity
  - billed quantity
  - remaining to receive
  - remaining to bill
- Received quantity only counts posted Receipt anchors.
- Billed quantity only counts posted Bill anchors.
- Draft Receipt / Bill anchors do not affect three-quantity truth.
- Over-receipt and over-bill are surfaced explicitly as control status, not silently absorbed.

Still deferred:

- full PO workflow universe
- PO approval and governance permissions
- formal PO close / cancel lifecycle execution
- PO-driven receipt creation
- PO-driven bill matching automation
- PPV journal posting
- tracked receipt enablement
- Shell-wide PO workbench

Authority note:

H.20.0 adds ordered truth without weakening receipt-first. PO line anchors are explicit and persisted; no vendor/item soft matching is treated as authoritative.

## Phase H.20.1 checkpoint

H.20.1 hardens the PO anchor governance and over-receipt / over-bill policy without expanding into a full PO workflow.

Boundary:

- PO remains ordered / commitment truth only.
- Receipt remains physical received truth.
- Bill remains AP / billed truth.
- Bill-Receipt matching, GR/IR, settlement, clearing, and variance lanes are not redesigned.
- This does not introduce PO approval, close/cancel execution, or reopen workflow.
- This does not introduce PPV posting.
- This does not enable tracked receipt flows.
- This does not expand into a Shell-wide PO workspace.

What changed:

- PO anchor governance is now explicit:
  - only `issued` PO lines can receive new Receipt / Bill anchors
  - `draft`, `closed`, and `cancelled` PO lines are blocked from new anchors
- Receipt draft save and receipt posting now validate PO anchors against:
  - same company
  - same vendor
  - same item
  - same stock UOM
  - remaining ordered quantity
- Bill draft save validates PO anchors against issued PO line truth and ordered quantity.
- Bill posting validates that PO-anchored billed quantity cannot outrun posted receipt truth.
- The three-quantity policy now distinguishes:
  - `over_received`
  - `over_billed`
  - `billed_ahead_of_received`
- A persisted PO quantity discrepancy lane now exists:
  - `purchase_order_quantity_discrepancy_lanes`
- A refresh endpoint exists:
  - `POST /purchase-orders/{id}/quantity-discrepancies/refresh`
- PO list/detail responses expose anchor governance and persisted discrepancy summary truth.

Still deferred:

- privileged over-receipt / over-bill override workflow
- discrepancy review / resolve / approve permissions
- formal PO close / cancel lifecycle execution
- PO-driven receiving or billing automation
- PPV journal posting
- tracked receipt enablement
- Shell-wide PO workbench

Authority note:

H.20.1 turns PO anchors from passive references into backend-governed source truth. Normal operation hard-blocks over-receipt and bill-ahead-of-receipt paths; any historical or externally imported overage is made reviewable through a persisted discrepancy lane rather than hidden in raw three-quantity math.

## Phase H.20.2 checkpoint

H.20.2 adds the minimum PO quantity discrepancy review / override-authority seam.

Boundary:

- This is review/control truth only.
- It does not loosen H.20.1 hard blocks.
- It does not make `override_authorized` an execution bypass.
- It does not introduce privileged over-receipt or over-bill posting.
- It does not introduce PO approval, close/cancel execution, or Shell-wide PO workspace.

What changed:

- PO quantity discrepancy lanes now support investigation statuses:
  - `open`
  - `resolved`
  - `override_authorized`
- Discrepancy rows now retain review metadata:
  - review note
  - reviewed by user
  - reviewed at timestamp
- A review endpoint exists:
  - `POST /purchase-orders/{id}/quantity-discrepancies/review`
- `resolved` lanes disappear from active PO discrepancy read truth.
- If the underlying quantity conflict still exists, the next refresh reopens a resolved lane.
- `override_authorized` lanes remain visible across refreshes, but still do not bypass Receipt/Bill hard-block policies.

Still deferred:

- permission-backed discrepancy review roles
- override execution policy
- formal PO close / cancel workflow
- PO-driven receiving or billing automation
- PPV posting
- tracked receipt
- Shell-wide PO review workbench

Authority note:

H.20.2 separates investigation truth from execution truth. Review can record that an overage was resolved or authorized for further attention, but the system still refuses to let Bill or Receipt silently mutate physical/AP truth beyond the governed PO anchors.

## Phase H.20.3 checkpoint

H.20.3 adds the minimum Purchase Order close / cancel lifecycle guard.

Boundary:

- This is lifecycle control only.
- It does not introduce PO approval.
- It does not introduce reopen.
- It does not let override-authorized discrepancy lanes bypass close / cancel rules.
- It does not introduce PO-driven receiving, PO-driven billing, PPV posting, or tracked receipt.
- It does not expand Shell into a PO workbench.

What changed:

- PO documents now persist lifecycle close / cancel metadata:
  - closed by user
  - closed at timestamp
  - cancelled by user
  - cancelled at timestamp
- Backend repository methods now own the lifecycle transitions:
  - `CloseAsync`
  - `CancelAsync`
- API endpoints expose the guarded actions:
  - `POST /purchase-orders/{id}/close`
  - `POST /purchase-orders/{id}/cancel`
- Cancel is allowed only for draft / issued POs with no posted received quantity, no posted billed quantity, and no active PO quantity discrepancy.
- Close is allowed only for issued POs whose three-quantity truth is fully aligned:
  - ordered quantity equals posted received quantity
  - ordered quantity equals posted billed quantity
  - no remaining quantity to receive
  - no remaining quantity to bill
  - no active discrepancy
- Closed / cancelled POs remain blocked by the existing anchor governance policy for new Receipt / Bill anchors.

Still deferred:

- formal PO approval workflow
- reopen / amendment workflow
- privileged overage execution policy
- PO close accounting effects
- PPV posting
- tracked receipt
- Shell-wide PO lifecycle workbench

Authority note:

H.20.3 closes the first PO lifecycle loophole without turning PO into receipt or AP truth. A PO can be cancelled only before it has authoritative downstream quantity truth; it can be closed only when ordered / received / billed truth is fully aligned and discrepancy-free.

## Mainline pause marker after H.20.3

Current pushed checkpoint:

- `a7c092c add purchase order close cancel guards`

Resume trigger:

- Do not continue the PO mainline until the user explicitly says `继续 h.20.4`.

Planned next mainline:

- H.20.4 should return to PO lifecycle hardening without reopening H.20.0-H.20.3.
- Candidate focus:
  - PO approval / release boundary, or
  - PO reopen / amendment policy hardening.

Still out of scope until explicitly restarted:

- full PO workflow universe
- PPV posting
- tracked receipt
- Shell-wide PO workbench
- any redesign of Receipt-first / Bill truth ownership

## Phase H.20.4 checkpoint

H.20.4 adds the minimum PO approval / release boundary.

Boundary:

- This is lifecycle authority only.
- It does not introduce role-backed approval permissions yet.
- It does not introduce reopen or amendment workflows.
- It does not let approved POs act as Receipt / Bill anchor truth.
- It does not introduce PO-driven receiving, PO-driven billing, PPV posting, tracked receipt, or a Shell-wide PO workbench.

What changed:

- PO documents now support an `approved` lifecycle status between `draft` and `issued`.
- Approval metadata is persisted:
  - approved by user
  - approved at timestamp
- Backend repository methods now separate approval from release:
  - `ApproveAsync`
  - `IssueAsync`
- A guarded approval endpoint exists:
  - `POST /purchase-orders/{id}/approve`
- Draft POs can no longer be issued directly.
- Only approved POs can be issued.
- Approved POs can be cancelled while untouched, but cannot be closed.
- Receipt / Bill anchor governance still only allows issued POs.

Still deferred:

- role / permission-backed PO approval authority
- approval thresholds
- approval reversal
- reopen / amendment policy
- privileged overage execution policy
- PO close accounting effects
- PPV posting
- tracked receipt
- Shell-wide PO lifecycle workbench

Authority note:

H.20.4 separates approval intent from release truth. Approval records that a draft PO is authorized for release, but it still cannot create Receipt or Bill anchor truth until the PO is explicitly issued.

## Phase H.20.5 checkpoint

H.20.5 adds the minimum PO reopen-for-amendment policy.

Boundary:

- This is amendment control only.
- It does not introduce a full amendment request / approval workflow.
- It does not reopen closed or cancelled POs.
- It does not allow amendment after any Receipt or Bill anchor row references the PO, even if the downstream document is still draft.
- It does not change Receipt-first physical truth or Bill AP truth.
- It does not introduce PPV posting, tracked receipt, or a Shell-wide PO workbench.

What changed:

- Backend repository methods now expose:
  - `ReopenForAmendmentAsync`
- A guarded amendment endpoint exists:
  - `POST /purchase-orders/{id}/reopen-for-amendment`
- Approved or issued POs can be returned to draft only while untouched by downstream Receipt / Bill anchors.
- Reopen for amendment clears current approval / issue markers because release authority must be re-earned after editing.
- Reopen for amendment records:
  - amendment started by user
  - amendment started at timestamp
- Draft, closed, and cancelled POs cannot be reopened through this route.

Still deferred:

- formal amendment request workflow
- amendment approval history / audit trail
- role / permission-backed amendment authority
- approval thresholds
- approval reversal outside amendment
- privileged overage execution policy
- PO close accounting effects
- PPV posting
- tracked receipt
- Shell-wide PO lifecycle workbench

Authority note:

H.20.5 keeps amendment before downstream truth. A PO can return to editable draft only while it has not become part of any Receipt or Bill working document, so amendment cannot rewrite physical or AP truth after operations have started.

## Phase H.20.6 checkpoint

H.20.6 adds the first CompanyAccess-aligned PO lifecycle authority guard.

Boundary:

- This is API authority gating only.
- It does not introduce approval thresholds.
- It does not introduce approval queues, rejection, reversal, or history.
- It does not move lifecycle authority into the repository contract yet.
- It does not change PO ordered truth, Receipt physical truth, or Bill AP truth.
- It does not introduce PPV posting, tracked receipt, or a Shell-wide PO workbench.

What changed:

- PO approval now requires a business session with one of:
  - owner
  - approve
  - book governance
  - accounting settings governance
- PO release now uses the same authority guard as approval.
- PO reopen-for-amendment now requires stronger governance authority:
  - owner
  - book governance
  - accounting settings governance
- Ordinary AP access is no longer enough for PO approve / release / amendment lifecycle transitions.
- Unauthorized lifecycle commands return a 403 authority outcome before touching PO state.

Still deferred:

- formal PO approval request queue
- approval rejection / reversal
- approval limit thresholds
- repository-level authority policy
- amendment approval history / audit trail
- privileged overage execution policy
- PO close accounting effects
- PPV posting
- tracked receipt
- Shell-wide PO lifecycle workbench

Authority note:

H.20.6 keeps lifecycle authority separate from lifecycle state. The existing PO state machine remains the source of lifecycle legality, while CompanyAccess session roles now decide who may invoke approval, release, and amendment transitions through the API.

## Phase H.20.7 checkpoint

H.20.7 adds append-only PO lifecycle audit continuity.

Boundary:

- This is lifecycle traceability only.
- It does not introduce approval thresholds, approval queues, rejection, or reversal.
- It does not change PO lifecycle legality.
- It does not change PO ordered truth, Receipt physical truth, or Bill AP truth.
- It does not introduce PPV posting, tracked receipt, or a Shell-wide PO workbench.

What changed:

- When `audit_logs` is available, PO lifecycle transitions now append audit rows for:
  - `purchase_order_approved`
  - `purchase_order_released`
  - `purchase_order_reopened_for_amendment`
  - `purchase_order_closed`
  - `purchase_order_cancelled`
- Audit payloads preserve:
  - purchase order document id
  - entity number
  - display number
  - from status
  - to status
  - action code
- Audit writing is transactional with the lifecycle mutation.
- Lightweight schemas without `audit_logs` continue to run without blocking PO lifecycle operations.

Still deferred:

- formal PO approval request queue
- approval rejection / reversal
- approval limit thresholds
- PO lifecycle audit read model / Shell panel
- amendment approval history beyond the transition audit row
- repository-level authority policy
- privileged overage execution policy
- PO close accounting effects
- PPV posting
- tracked receipt
- Shell-wide PO lifecycle workbench

Authority note:

H.20.7 makes lifecycle changes reviewable without making audit history operational truth. The PO tables still own lifecycle state; audit rows provide append-only continuity for who changed the lifecycle and how state moved.

## Phase H.20.8 checkpoint

H.20.8 adds the minimum PO lifecycle audit read model.

Boundary:

- This is backend read continuity only.
- It does not introduce a Shell timeline panel yet.
- It does not introduce approval thresholds, approval queues, rejection, or reversal.
- It does not change PO lifecycle legality or authority gates.
- It does not change PO ordered truth, Receipt physical truth, or Bill AP truth.
- It does not introduce PPV posting or tracked receipt.

What changed:

- Backend repository now exposes:
  - `ListLifecycleAuditAsync`
- API now exposes:
  - `GET /purchase-orders/{id}/lifecycle-audit`
- The read model returns lifecycle audit entries newest-first, including:
  - action
  - actor
  - from status
  - to status
  - document identity
  - created timestamp
- PO list and detail responses now surface the lifecycle timestamps introduced by H.20.4-H.20.5:
  - approved at
  - amendment started at
- If `audit_logs` is not available, the lifecycle audit read model returns an empty timeline rather than failing the PO page path.

Still deferred:

- Shell PO lifecycle timeline / review panel
- formal PO approval request queue
- approval rejection / reversal
- approval limit thresholds
- repository-level authority policy
- privileged overage execution policy
- PO close accounting effects
- PPV posting
- tracked receipt
- Shell-wide PO lifecycle workbench

Authority note:

H.20.8 closes the write/read loop for lifecycle audit without making audit rows authoritative. PO state remains the lifecycle truth, while the audit timeline gives operators and later Shell surfaces a stable review trail.

## Phase H.20.9 checkpoint

H.20.9 adds the first Web Shell purchase order review surface for the lifecycle audit read model.

Boundary:

- Shell read surface only.
- It does not add PO lifecycle commands to the Shell.
- It does not introduce approval thresholds, approval queues, rejection, or reversal.
- It does not change PO lifecycle legality, authority gates, or repository write policy.
- It does not change PO ordered truth, Receipt physical truth, or Bill AP truth.
- It does not introduce PPV posting or tracked receipt.

What changed:

- Source document browser now includes a `Purchase Orders` filter backed by the PO list API.
- Shell navigation and dashboard expose a Purchase Orders entry point.
- `/documents/purchase_order/{id}` now renders a read-only PO detail path.
- The PO detail path shows:
  - anchor governance summary
  - order/vendor/date summary
  - three-quantity control summary when available
  - lifecycle audit timeline from `GET /purchase-orders/{id}/lifecycle-audit`
  - purchase order lines
- PO lifecycle audit remains explanatory only; the PO document state remains authoritative.

Still deferred:

- formal PO approval request queue
- approval rejection / reversal
- approval limit thresholds
- lifecycle action buttons in Shell
- repository-level authority policy
- privileged overage execution policy
- PO close accounting effects
- PPV posting
- tracked receipt
- Shell-wide PO lifecycle workbench

Authority note:

H.20.9 makes the lifecycle trail visible to operators without moving any PO authority into the UI. The Shell can review and explain lifecycle history, while backend PO state and command endpoints continue to own lifecycle legality.

## Phase H.20.10 checkpoint

H.20.10 adds the first PO approval threshold skeleton.

Boundary:

- Threshold policy is API-owned and temporary.
- It does not introduce a persisted company threshold table yet.
- It does not introduce approval request queues, rejection, reversal, or multi-step approval routing.
- It does not move lifecycle authority into the repository contract.
- It does not change PO lifecycle state rules.
- It does not change PO ordered truth, Receipt physical truth, or Bill AP truth.
- It does not introduce PPV posting or tracked receipt.

What changed:

- `BusinessApprovalAuthority` now has a temporary PO approval governance threshold.
- Ordinary `approve` permission can approve POs only within the temporary threshold.
- Above-threshold PO approval requires owner or governance-level authority.
- PO approval endpoint evaluates the PO estimated amount before calling repository approval.
- PO detail API returns approval-threshold summary data.
- Web Shell PO detail shows the approval-threshold result as read-only context.
- Authority tests cover:
  - ordinary approver within threshold
  - ordinary approver blocked above threshold
  - owner allowed above threshold

Still deferred:

- persisted company/book approval threshold configuration
- formal PO approval request queue
- approval rejection / reversal
- multi-approver routing
- repository-level authority policy
- lifecycle action buttons in Shell
- PO close accounting effects
- PPV posting
- tracked receipt

Authority note:

H.20.10 keeps threshold authority at the API command boundary. The repository still owns PO lifecycle mutation and state legality, while the API decides whether the caller can enter the approval command for the current estimated PO amount.

## Phase H.20.11 checkpoint

H.20.11 adds the first PO approval request queue, rejection, and approval reversal boundary.

Boundary:

- Approval requests are append-only audit-backed read models for now.
- It does not introduce a formal approval workflow table yet.
- It does not introduce multi-approver routing or persisted company approval limits.
- It does not move CompanyAccess authority checks inside repository methods.
- It does not add Shell lifecycle command buttons yet.
- It does not change PO ordered truth, Receipt physical truth, or Bill AP truth.
- It does not introduce PPV posting or tracked receipt.

What changed:

- PO approval threshold policy is shared from the application repository contract layer so API and persistence use the same temporary threshold.
- Purchase orders can record an approval request while still in draft.
- Approval requests can be submitted into a queue read model backed by `audit_logs`.
- Submitted approval requests can be rejected by users with the same amount-sensitive approval authority used by PO approval.
- Approval request records expose request status, approval status, submit/reject actors, estimated amount, threshold amount, and governance requirement.
- Approved POs can have approval reversed back to draft through a governance-only API command.
- Lifecycle audit read model now includes `purchase_order_approval_reversed`.
- Authority tests now cover approval reversal authority.

Still deferred:

- persisted company/book approval threshold configuration
- formal approval workflow tables
- multi-approver routing
- approval request Shell workbench
- lifecycle action buttons in Shell
- repository-level authority policy
- PO close accounting effects
- PPV posting
- tracked receipt

Authority note:

H.20.11 keeps approval-request state explanatory and queue-oriented while PO document state remains authoritative. Rejection does not mutate ordered truth, and approval reversal is limited to approved-but-unissued POs through governance authority.

## Phase H.20.12 checkpoint

H.20.12 surfaces the PO approval request queue in the Web Shell.

Boundary:

- Web Shell read surface only.
- It does not add approval request create, submit, reject, approve, or reversal buttons to the Shell.
- It does not introduce formal workflow tables or persisted company approval limits.
- It does not change PO lifecycle legality, approval authority, or repository write policy.
- It does not change PO ordered truth, Receipt physical truth, or Bill AP truth.
- It does not introduce PPV posting or tracked receipt.

What changed:

- Web Shell review client can read PO approval requests from the accounting API.
- New PO approval request queue page lists pending records by default and can include closed records.
- Queue rows show:
  - PO display/entity numbers
  - PO lifecycle status
  - request status
  - approval status
  - requested/submitted/rejected actors
  - estimated amount and temporary governance threshold context
- Dashboard and Payables navigation expose a PO Approvals entry point.
- Purchase order detail links to the approval request queue.
- Purchase order detail shows the latest approval request summary beside approval-threshold context.
- Lifecycle audit labels now include approval reversal events.

Still deferred:

- Shell approval command buttons
- formal approval workflow tables
- multi-approver routing
- persisted company/book approval threshold configuration
- repository-level authority policy
- PO close accounting effects
- PPV posting
- tracked receipt

Authority note:

H.20.12 makes approval intent visible without moving authority into the UI. Operators can review request state and threshold pressure, while backend API and repository boundaries continue to own lifecycle transitions.

## Phase H.20.13 checkpoint

H.20.13 adds the first Web Shell command bridge for PO approval requests.

Boundary:

- Shell commands are limited to approval request lifecycle: request, submit, and reject.
- It does not add PO approve, issue, reopen, close, cancel, or approval reversal buttons to the Shell.
- It does not introduce formal workflow tables or persisted company approval limits.
- It does not change API authority decisions or repository lifecycle legality.
- It does not change PO ordered truth, Receipt physical truth, or Bill AP truth.
- It does not introduce PPV posting or tracked receipt.

What changed:

- Web Shell review client can POST PO approval request commands.
- PO detail can create an approval request from draft PO context with optional reason.
- PO detail can submit a draft approval request into the approval queue.
- PO approval request queue can reject submitted pending requests.
- Command results surface API outcome/message feedback in the Shell.
- Shell refreshes PO detail or queue state after command execution.

Still deferred:

- PO approve / reverse approval buttons in Shell
- PO issue / close / cancel buttons in Shell
- formal approval workflow tables
- multi-approver routing
- persisted company/book approval threshold configuration
- repository-level authority policy
- PO close accounting effects
- PPV posting
- tracked receipt

Authority note:

H.20.13 lets operators move approval-request intent through the UI, but approval authority remains in the API and lifecycle state remains in the PO repository. Rejection still affects only the approval-request trail and does not mutate ordered truth.
