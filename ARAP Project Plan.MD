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
