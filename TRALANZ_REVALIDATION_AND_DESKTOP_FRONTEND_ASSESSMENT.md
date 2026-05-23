# Tralanz Revalidation And Desktop Frontend Assessment

Date: 2026-05-20

This document records the current accounting/business/UI revalidation findings and a first-pass estimate for turning the product into a desktop frontend.

## 1. Revalidation Record

### 1.1 Overall Conclusion

The backend has a real accounting foundation:

- Posting Engine driven journal creation
- double-entry balance checks before ledger write
- AR/AP open item records
- payment/bill settlement logic
- source-document FX snapshot handling
- PostgreSQL unit-of-work transaction boundary
- partial company access and owner governance controls
- a UnitySearch projection/search module
- a substantial inventory module with ledger/cost-layer concepts

However, the product should not be treated as ready for real small-business accounting users until the items below are fixed or explicitly accepted as staged limitations.

### 1.2 Critical Risks

1. Bill posting state mismatch

   Location:

   - `backend/src/Citus.Accounting.Application/Commands/PostBillCommandHandler.cs`
   - `backend/src/Citus.Accounting.Infrastructure/Persistence/PostgresJournalEntryWriter.cs`
   - `backend/src/Citus.Accounting.Infrastructure/Persistence/PostgresBillDocumentRepository.cs`

   Finding:

   - bill posting requires `submitted`
   - bill submit changes status to `submitted`
   - journal writer marks bill posted only when source status is `draft`

   Risk:

   - normal Bill -> AP posting flow can fail
   - AP lifecycle is not closed

   Recommended fix:

   - align bill lifecycle rules and add an integration test for draft -> submitted -> posted -> AP open item

2. Backend module/action permission enforcement is incomplete

   Location:

   - `backend/src/Citus.Accounting.Api/Program.cs`
   - `backend/src/Citus.Accounting.Api/BusinessApprovalAuthority.cs`
   - `backend/src/Modules/CompanyAccess/Memberships/CompanyMembershipPermissionCatalog.cs`

   Finding:

   - many endpoints rely on company membership/session only
   - only selected governance operations have explicit authority checks
   - permission catalog is too coarse for real accounting use

   Risk:

   - users may perform create/edit/post/void/export actions outside their intended module authority

   Recommended fix:

   - introduce a unified `module + operation` backend permission gate
   - add permissions for Sales, Purchases, Payments, Banking, Reports, Settings, Users/Roles, Audit Log, Tasks, Inventory
   - add operations such as View, Create, Edit, Delete, Post, Void, Reverse, Approve, Export, SendEmail

3. Direct route company parameters may bypass active-company validation

   Location:

   - `backend/src/Citus.Accounting.Api/BusinessRequestContractGuard.cs`
   - direct `CompanyId companyId` endpoint handlers in `Program.cs`

   Finding:

   - the guard validates `CompanyId` and `UserId` properties on request objects
   - direct handler arguments of type `CompanyId` / `UserId` need explicit validation too

   Risk:

   - possible cross-company access if route/query company id is accepted directly

   Recommended fix:

   - extend the guard to validate direct typed arguments
   - add cross-company denial tests

### 1.3 High Risks

1. UnitySearch lacks module permission filtering

   Risk:

   - users may see search results for modules they cannot access

   Recommended fix:

   - filter search candidates by company and permission
   - verify navigation targets again on backend access

2. Invoice posting has post-commit soft-failure side effects

   Risk:

   - invoice revenue/AR can be posted while COGS, drop-ship COGS, or deposit application fails afterward

   Recommended fix:

   - decide whether these operations must be atomic
   - if not atomic, add an explicit recovery queue/workbench and audit status

3. Task billing loop is not implemented as a business accounting loop

   Finding:

   - current task-related code appears closer to Action Center / AI task scaffolding
   - no reliable Task -> time/expense/material -> billable amount -> invoice -> AR -> payment loop was found

   Recommended fix:

   - define Task Billing Authority before implementation
   - prevent duplicate billing by design

4. Tax engine boundary is weak

   Finding:

   - tax posting mostly trusts source-line tax amounts/accounts
   - centralized tax calculation/version/jurisdiction enforcement is not complete

   Recommended fix:

   - add a real tax validation/calculation layer before production use

### 1.4 Medium Risks

- journal lines do not consistently preserve `party_type`
- search coverage misses payments, task records, inventory documents, settlement records, audit records
- search uses patterns that may not scale well without better full-text/trigram/cursor pagination support
- `Program.cs` is too large and repeats route/security patterns
- UI has some visible "Coming soon" / incomplete action surfaces
- root Next.js/Prisma app may be old prototype code and needs deployment-boundary confirmation before cleanup

### 1.5 Low Risks

- Citus / Tralanz naming is mixed
- some request names are misleading, such as invoice submit using a bill submit request name
- old docs/comments still refer to Citus paths or product name

## 2. Business Loop Status

### 2.1 Mostly Present But Needs Regression Tests

- Journal Entry -> Ledger -> Reports
- Invoice -> JE/AR open item
- Receive Payment -> settlement -> AR balance update
- Pay Bill -> settlement -> AP balance update
- multi-currency source snapshot -> base amount -> ledger

### 2.2 Blocked Or Incomplete

- Bill -> AP posting is blocked by the submitted/draft mismatch
- Invoice -> COGS has post-commit soft-failure risk
- Task -> billable -> invoice -> AR is not closed
- Inventory -> GL reconciliation needs more explicit operational verification
- Search -> permissions -> navigation is not secure enough yet
- UI permissions are not yet a complete mirror of backend permissions

## 3. Search Assessment

Current search appears to cover:

- Customers
- Vendors
- Accounts
- Products / Services
- Inventory Items
- Warehouses
- Quotes
- Sales Orders
- Purchase Orders
- Invoices
- Bills
- Credit Notes
- Vendor Credits
- Journal Entries
- Reports / JumpTo entries

Missing or weak search coverage:

- Receive Payments
- Pay Bills
- bank deposits / transfers as first-class payment records
- Tasks
- inventory receipts / shipments / issues / adjustments / transfers
- settlement applications
- audit records

Recommended search phases:

1. Add permission filtering to existing UnitySearch results.
2. Add providers for payments, tasks, and inventory documents.
3. Add consistent navigation contracts and backend route authorization.
4. Add scalable pagination and indexed full-text/trigram queries.
5. Consolidate old pickers/dropdowns into UnitySearch/SmartPicker patterns.

## 4. Permission Assessment

Current permission state:

- company membership exists
- owner governance exists
- last-owner protection exists
- some approval and reconciliation checks exist
- permission audit exists for membership permission changes

Missing for a production accounting desktop app:

- explicit module permissions for all modules
- operation-level permissions
- consistent backend enforcement on every write/post/void/export path
- search/report/export permission enforcement
- UI hide/disable logic driven from the same permission model

Minimum target model:

- `Accounting.View`
- `Accounting.Post`
- `Sales.View/Create/Edit/Post/Void/SendEmail`
- `Purchases.View/Create/Edit/Post/Void`
- `Payments.View/Create/Post/Void`
- `Banking.View/Reconcile/Transfer/Deposit`
- `Reports.View/Export`
- `Settings.View/Edit`
- `UsersRoles.View/Edit`
- `AuditLog.View/Export`
- `Tasks.View/Create/Edit/Bill/Close/Void`
- `Inventory.View/Receive/Ship/Adjust/Transfer/Post`

## 5. Desktop Frontend Size Assessment

The existing business UI is already substantial:

- about 104 `.razor` files under `backend/src/Citus.Business.Blazor`
- roughly 70+ feature/page-level Razor surfaces
- modules include invoices, bills, expenses, payments, banking, sales, purchase orders, inventory, settings, reports, journal entries, counterparties, auth, company shell

This means the desktop frontend effort depends heavily on whether the current Blazor UI is reused.

### 5.1 Recommended Path: Desktop Shell Reusing Existing Blazor UI

Possible technologies:

- .NET MAUI Blazor Hybrid
- WPF/WinUI + WebView2 hosting the existing Blazor UI
- packaged local API + Blazor frontend

Estimated effort:

- proof of concept: 3-5 working days
- internal MVP desktop shell: 2-4 weeks
- production-grade desktop app: 6-10 weeks

Scope included:

- desktop app host
- local backend/API process orchestration or embedded hosting
- local PostgreSQL connection/profile management
- login/session persistence
- company selection
- window layout and desktop menu integration
- installer
- local config files
- logs and diagnostics
- backup/restore entry points
- update strategy
- packaging/signing

Risk:

- low UI rewrite cost
- medium packaging/runtime complexity
- backend correctness and permission fixes must still happen first

Recommendation:

- this is the best route if the goal is to reach a QuickBooks Desktop / Sage-like local app quickly

### 5.2 Medium Path: Electron/Tauri Wrapper Around Web UI

Estimated effort:

- MVP: 3-6 weeks
- production: 8-12 weeks

Pros:

- can reuse a web UI
- mature desktop packaging patterns

Cons:

- mixed .NET backend + JS desktop host increases operational complexity
- larger distribution footprint if Electron
- not as natural for a C#-centric product

Recommendation:

- acceptable if a web-first distribution is also planned
- not the cleanest fit for this codebase

### 5.3 High-Cost Path: Native WPF / WinUI Rewrite

Estimated effort:

- usable accounting MVP: 3-5 months
- feature parity with current Blazor surface: 6-12 months
- production polish including reports, permissions, inventory, search, and settings: 9-15 months

Why so large:

- every page/form/grid/dialog must be rebuilt
- current Razor components and layout cannot be directly reused
- validation, pickers, error states, empty states, permission visibility, search, navigation, and theming all need reimplementation
- accounting software has many dense transactional workflows, not just simple CRUD pages

Recommendation:

- do not choose this unless there is a hard requirement for fully native Windows UI controls

## 6. Suggested Desktop Roadmap

### Phase 0: Fix Accounting/Security Blockers

Duration:

- 1-3 weeks depending test depth

Must finish first:

- bill posting state mismatch
- company argument guard
- backend module/action permission gate
- search permission filtering

### Phase 1: Desktop Proof Of Concept

Duration:

- 3-5 days

Deliverable:

- desktop host opens current Blazor shell
- can login
- can choose company
- can open dashboard/invoices/bills/search
- local API connectivity verified

### Phase 2: Desktop MVP

Duration:

- 2-4 weeks

Deliverable:

- packaged app
- local config
- stable startup/shutdown
- API process lifecycle
- basic logs
- connection health
- backup/restore placeholder or first implementation

### Phase 3: Production Desktop Hardening

Duration:

- 4-8 additional weeks

Deliverable:

- installer/signing
- auto-update or manual update flow
- database migration checks
- crash diagnostics
- local backup/restore
- permission-aware UI
- desktop navigation polish
- smoke/regression suite

## 7. Practical Recommendation

Do not start with a full native desktop rewrite.

The practical path is:

1. fix the accounting and permission blockers
2. keep the current Blazor UI as the main frontend
3. wrap it in a C# desktop host
4. only build native desktop controls for narrow cases where they clearly improve accounting workflows, such as file backup/restore, import/export, printing, and local database diagnostics

Estimated realistic size:

- if reusing Blazor: desktop frontend is a medium project
- if rewriting native: desktop frontend is a major product rebuild

## 8. Repair Log

### 2026-05-20 First Critical Repair Batch

Completed:

- fixed direct `CompanyId` / `UserId` argument validation in the business request guard
- allowed submitted posting documents to enter the Posting Engine where appropriate
- fixed invoice journal-source marking to accept submitted invoices
- fixed bill journal-source marking to accept submitted bills
- added unit coverage for direct company/user argument rejection
- added no-database validator coverage for submitted bill documents
- added a database-backed AP regression test for submitted bill -> posted bill -> posted JE -> AP open item

Verification:

- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter BusinessRequestContractGuardTests` passed
- `dotnet test backend/src/Tests/AP/Tests.AP.csproj --filter DefaultPostingValidatorTests` passed
- `dotnet build backend/src/Tests/AP/Tests.AP.csproj` passed
- `dotnet test backend/src/Tests/AP/Tests.AP.csproj --filter PostBillCommandHandler_PostsSubmittedBillAndCreatesApOpenItem` compiled and reached execution, but could not complete because local PostgreSQL on `127.0.0.1:5432` was not running

Still open:

- module/action permission enforcement
- UnitySearch permission filtering
- Task billing authority and closed loop
- invoice COGS/deposit soft-failure policy
- full database-backed AP/AR lifecycle regression run

### 2026-05-21 Second High-Risk Repair Batch

Completed:

- added API-level UnitySearch permission filtering for grouped search results
- filtered UnitySearch recent selections by the same permission boundary
- blocked UnitySearch click-stat recording for entity types the current session cannot access
- added first module tokens for the permission catalog: `sales`, `purchases`, `payments`, `banking`, `audit_log`, `tasks`, `inventory`
- added tests for UnitySearch permission filtering and module-boundary access decisions

Current search permission behavior:

- `owner` can see all search result types
- `ar` / `sales` can see customer-side records such as customers, quotes, sales orders, invoices, and credit notes
- `ap` / `purchases` can see vendor-side records such as vendors, purchase orders, bills, and vendor credits
- `reports` can see report entries
- `company_book_governance` / `company_accounting_settings` can see accounting records such as accounts and journal entries
- inventory/catalog results require an inventory, sales, purchases, AR, or AP-oriented role

Verification:

- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter "BusinessRequestContractGuardTests|UnitySearchPermissionFilterTests"` passed
- `dotnet build backend/src/Citus.Accounting.Api/Citus.Accounting.Api.csproj` passed
- `dotnet test backend/src/Tests/CompanyAccess/Tests.CompanyAccess.csproj --filter CompanyMembershipPermissionWorkflowTests` passed
- `dotnet test backend/src/Tests/CompanyAccess/Tests.CompanyAccess.csproj --filter CompanyMembershipPermission` reached the database persistence smoke test but could not complete because local PostgreSQL on `127.0.0.1:5432` was not running

Still open:

- backend operation-level enforcement for create/edit/post/void/reverse/export actions
- UI hide/disable behavior driven by the same module permission model
- search provider coverage for payments, tasks, inventory movements, settlements, and audit logs
- database-backed search permission integration tests

### 2026-05-21 Third High-Risk Repair Batch

Completed:

- added a backend operation-level authority decision path for high-risk posting actions
- protected manual journal posting and save-and-post with accounting/book-governance authority
- protected invoice and credit-note posting with sales/AR-oriented authority
- protected bill and vendor-credit posting with purchases/AP-oriented authority
- protected receive-payment and AR credit-application posting with AR/payment/banking authority
- protected pay-bill and vendor-credit-application posting with AP/payment/banking authority
- added unit coverage for allowed and blocked business-operation authority decisions

Current operation permission behavior:

- `owner` can perform all covered posting operations
- `book_governance`, `company_book_governance`, and `company_accounting_settings` can post manual journals and covered accounting-impacting documents
- `ar` / `sales` can post covered customer-side documents
- `ap` / `purchases` can post covered vendor-side documents
- `payments` / `banking` can post covered AR/AP settlement documents
- incompatible module users are blocked before the handler posts to GL, AR, AP, or settlement tables

Verification:

- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter "BusinessApprovalAuthorityTests|BusinessRequestContractGuardTests|UnitySearchPermissionFilterTests"` passed
- `dotnet build backend/src/Citus.Accounting.Api/Citus.Accounting.Api.csproj` passed

Still open:

- operation-level enforcement for draft create/edit/delete, void, reverse, approve, export, email, inventory adjustment, and Task billing actions
- UI hide/disable behavior driven by the same operation authority model
- database-backed endpoint tests proving unauthorized post calls return `403` before any journal/open-item/payment mutation
- finer-grained permission storage so module roles can become explicit `module + action` grants rather than role-name conventions

### 2026-05-21 Fourth High-Risk Repair Batch

Completed:

- added report-export authority to the shared business-operation permission path
- protected Trial Balance, Income Statement, Balance Sheet, AR Aging, and AP Aging CSV export endpoints with `reports` authority
- protected source-document lifecycle `void`, `reverse`, reverse-request `submit`, `cancel`, and `execute` mutations with accounting/book-governance authority
- protected direct journal-entry void with accounting/book-governance authority
- protected legacy AP bill void and expense void endpoints with accounting/book-governance authority
- protected receipt GR/IR bridge posting with the existing GR/IR settlement execution authority
- added unit coverage for report export access and stricter journal-entry void access decisions

Current void/reverse/export permission behavior:

- `owner` can perform covered lifecycle, void, reverse, and report-export actions
- `book_governance`, `company_book_governance`, and `company_accounting_settings` can perform covered lifecycle/void/reverse actions
- `reports` can export covered accounting reports but cannot perform accounting lifecycle mutations
- ordinary AR/AP/Sales/Purchases users cannot void/reverse journal-impacting documents through the covered endpoints
- GR/IR bridge posting remains limited to company owner or accounting-governance users through the existing GR/IR settlement authority

Verification:

- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter "BusinessApprovalAuthorityTests|BusinessRequestContractGuardTests|UnitySearchPermissionFilterTests"` passed
- `dotnet build backend/src/Citus.Accounting.Api/Citus.Accounting.Api.csproj` passed

Still open:

- operation-level enforcement for draft create/edit/delete, approve variants not already covered, send email, customer/vendor address delete, inventory adjustments, and Task billing actions
- UI hide/disable behavior driven by the same operation authority model
- database-backed endpoint tests proving unauthorized lifecycle/export calls return `403` before mutation or data export
- eventual migration from role-name convention checks to stored explicit `module + action` permission grants

### 2026-05-21 Fifth High-Risk Repair Batch

Completed:

- added `inventory` and `tasks` module authority paths to the shared business-operation permission gate
- protected Action Center task regeneration and task state mutations with `tasks` authority
- protected inventory activation, warehouse updates, item create/update/activate/deactivate with `inventory` authority
- protected invoice, credit-note, bill, and vendor-credit draft create/update/submit/cancel mutations with sales or purchases authority
- protected receive-payment and pay-bill draft preparation with AR/AP payment authority
- protected customer and vendor shipping-address create/update/delete/default mutations with sales or purchases authority
- protected receipt draft create/update/post, receipt inventory retry/valuation/cost-layer/GRIR refresh actions with inventory or GR/IR governance authority
- protected drop-ship clearing write-off and sales-issue COGS posting with inventory authority
- added unit coverage for inventory and task operation authority decisions

Current draft/task/inventory permission behavior:

- `sales` / `ar` users can manage covered customer-side drafts and customer shipping-address records
- `purchases` / `ap` users can manage covered vendor-side drafts and vendor shipping-address records
- `payments` / `banking` users can prepare covered AR/AP settlement drafts
- `inventory` users can manage covered inventory setup, item, receipt, COGS, and clearing write-off operations
- `tasks` users can mutate Action Center task lifecycle state
- unrelated module users are blocked before the covered handlers persist drafts, mutate inventory state, or change task state

Verification:

- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter "BusinessApprovalAuthorityTests|BusinessRequestContractGuardTests|UnitySearchPermissionFilterTests"` passed
- `dotnet build backend/src/Citus.Accounting.Api/Citus.Accounting.Api.csproj` passed

Still open:

- legacy quote/sales-order/AP bill/AP expense CRUD surfaces still need operation gates
- save-and-post convenience endpoints for sales receipts, refund receipts, credit memos, vendor credits, bank transfers, bank deposits, and tax returns need explicit operation gates
- company-book governance write endpoints need session-bound backend authority checks
- UI hide/disable behavior driven by the same operation authority model
- database-backed endpoint tests proving unauthorized draft/task/inventory calls return `403` before mutation
- eventual migration from role-name convention checks to stored explicit `module + action` permission grants

### 2026-05-21 Sixth High-Risk Repair Batch

Completed:

- added `banking` operation authority to the shared business-operation permission gate
- protected legacy quote and sales-order create/update/status/convert/confirm/cancel/deposit mutations with sales or AR-payment authority
- protected legacy AP bill create/update and AP purchase-order create/update/status/convert mutations with purchases authority
- protected AP expense create and PO-to-expense conversion with AP-payment authority
- protected sales receipt, refund receipt, credit memo, vendor credit, bank transfer, bank deposit, and tax return `save-and-post` convenience endpoints
- protected company-book governance signal, closed-period, issued-statement, filed-tax, and governed-change request mutations with accounting/book-governance authority
- added unit coverage for banking operation authority decisions

Current legacy/save-and-post/governance permission behavior:

- `sales` / `ar` users can manage covered quote and sales-order workflows, while sales-order deposits require AR-payment authority
- `purchases` / `ap` users can manage covered legacy AP bill and AP purchase-order workflows
- `payments` / `banking` users can perform covered bank transfer and bank deposit save-and-post workflows
- tax return save-and-post and company-book governance writes require accounting/book-governance authority
- unrelated module users are blocked before covered handlers persist, post, or mutate governance state

Verification:

- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter "BusinessApprovalAuthorityTests|BusinessRequestContractGuardTests|UnitySearchPermissionFilterTests"` passed
- `dotnet build backend/src/Citus.Accounting.Api/Citus.Accounting.Api.csproj` passed

Still open:

- tax code, payment term, invoice template, chart-of-accounts, and other settings/catalog write endpoints still need explicit settings/governance operation gates
- list/detail read endpoints still need broader backend module permission enforcement, not only write/search filtering
- UI hide/disable behavior driven by the same operation authority model
- database-backed endpoint tests proving unauthorized legacy/save-and-post/governance calls return `403` before mutation
- eventual migration from role-name convention checks to stored explicit `module + action` permission grants

### 2026-05-21 Seventh High-Risk Repair Batch

Completed:

- protected company currency enablement with accounting/book-governance authority
- protected customer create/update with sales authority and vendor create/update with purchases authority
- protected invoice send with AR-payment authority
- protected invoice-template create/update/set-default with accounting/book-governance authority
- protected tax-code create/update/activate/deactivate with accounting/book-governance authority
- protected payment-term create/update/activate/deactivate with accounting/book-governance authority
- protected chart-of-accounts create/update/activate/deactivate and CoA template apply with accounting/book-governance authority

Current settings/catalog permission behavior:

- accounting/book-governance users control configuration that can affect posting, tax, invoice rendering, chart of accounts, and currency control accounts
- sales/AR users can maintain covered customer master data
- purchases/AP users can maintain covered vendor master data
- AR-payment users can send invoices through the backend email endpoint
- unrelated module users are blocked before covered settings/catalog handlers mutate persistent state

Verification:

- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter "BusinessApprovalAuthorityTests|BusinessRequestContractGuardTests|UnitySearchPermissionFilterTests"` passed
- `dotnet build backend/src/Citus.Accounting.Api/Citus.Accounting.Api.csproj` passed

Still open:

- list/detail read endpoints still need broader backend module permission enforcement, not only write/search filtering
- UI hide/disable behavior driven by the same operation authority model
- database-backed endpoint tests proving unauthorized settings/catalog calls return `403` before mutation
- internal control-plane endpoints need explicit SysAdmin/session hardening before any public exposure
- eventual migration from role-name convention checks to stored explicit `module + action` permission grants

### 2026-05-21 Eighth High-Risk Repair Batch

Completed:

- protected financial report read endpoints for Trial Balance, Income Statement, Balance Sheet, AR Aging, and AP Aging with `reports` authority
- protected journal-entry list/detail/by-source read endpoints with accounting/book-governance authority
- protected journal-entry next-number preview with accounting/book-governance authority

Current read permission behavior:

- `reports` users can view covered financial reports without receiving broader journal-entry authority
- accounting/book-governance users can view covered journal-entry read surfaces
- ordinary AR/AP/Sales/Purchases users are blocked from the covered GL/report read endpoints unless they also hold the relevant report/accounting authority

Verification:

- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter "BusinessApprovalAuthorityTests|BusinessRequestContractGuardTests|UnitySearchPermissionFilterTests"` passed
- `dotnet build backend/src/Citus.Accounting.Api/Citus.Accounting.Api.csproj` passed

Still open:

- AR/AP document, payment, customer/vendor, inventory, and settings list/detail read endpoints still need backend module permission enforcement
- UI hide/disable behavior driven by the same operation authority model
- database-backed endpoint tests proving unauthorized read calls return `403` before data is loaded
- internal control-plane endpoints need explicit SysAdmin/session hardening before any public exposure
- eventual migration from role-name convention checks to stored explicit `module + action` permission grants

### 2026-05-21 Ninth High-Risk Repair Batch

Completed:

- protected customer and vendor master-data list/detail reads with sales or purchases authority
- protected customer/vendor shipping-address read surfaces, customer/vendor financial summary reads, and transaction timelines with the relevant sales, purchases, AR-payment, or AP-payment authority
- protected quote, sales-order, sales-order deposit, legacy AP bill, AP purchase-order, and AP expense list/detail reads
- protected canonical invoice, credit-note, bill, vendor-credit, receive-payment, credit-application, pay-bill, and vendor-credit-application detail/list reads
- protected warehouse, inventory activation state, inventory item, drop-ship clearing, sales-issue COGS status, receipt, GR/IR policy, receipt purchase-variance, and receipt settlement-batch reads
- protected canonical purchase-order read surfaces, approval-request reads, lifecycle-audit reads, and bill receipt-matching reads
- added active-company equality checks to the covered query-parameter based read endpoints before loading data from repositories

Current AR/AP/inventory read permission behavior:

- sales users can view covered customer, quote, sales-order, invoice, and credit-note read surfaces
- purchases users can view covered vendor, AP bill, purchase-order, bill, vendor-credit, and vendor-side transaction read surfaces
- AR-payment users can view covered customer receivable, receive-payment, credit-application, and deposit read surfaces
- AP-payment users can view covered vendor payable, pay-bill, AP expense, and vendor-credit-application read surfaces
- inventory users can view covered warehouse, item, receipt, COGS, GR/IR, and inventory clearing read surfaces
- covered endpoints that still accept an explicit `companyId` now reject mismatches with the active business session before repository access

Verification:

- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter "BusinessApprovalAuthorityTests|BusinessRequestContractGuardTests|UnitySearchPermissionFilterTests"` passed
- `dotnet build backend/src/Citus.Accounting.Api/Citus.Accounting.Api.csproj` passed

Still open:

- settings/catalog read endpoints still need backend module permission enforcement
- several canonical purchase-order mutation endpoints still need explicit session-bound operation gates and company-id equality checks
- UI hide/disable behavior driven by the same operation authority model
- database-backed endpoint tests proving unauthorized AR/AP/inventory reads return `403` before data is loaded
- internal control-plane endpoints need explicit SysAdmin/session hardening before any public exposure
- eventual migration from role-name convention checks to stored explicit `module + action` permission grants

### 2026-05-21 Tenth High-Risk Repair Batch

Completed:

- protected invoice-template list/detail and PDF-preview surfaces with accounting/book-governance authority
- protected invoice send-history reads with AR-payment authority
- protected tax-code, payment-term, chart-of-accounts, and CoA-template read surfaces with accounting/book-governance authority
- added active-company equality checks to invoice-template read/write/preview endpoints that accept `companyId` through query parameters
- added session-bound company-id checks to canonical purchase-order approval-request, draft create/update, approve, approval reverse, issue, reopen-for-amendment, close, cancel, and quantity-discrepancy mutation endpoints
- added missing purchase-module operation gates to canonical purchase-order draft, approval-request, and quantity-discrepancy mutation endpoints before repository mutation
- moved sensitive purchase-order approval/reject paths to validate session/company before loading approval or document records

Current settings/catalog and purchase-order permission behavior:

- accounting/book-governance users can view covered settings catalogs and invoice templates
- AR-payment users can view covered invoice send history without receiving broader accounting settings authority
- purchases/AP users can perform covered canonical purchase-order draft, approval-request, and discrepancy-review operations
- purchase-order approval/release/close/cancel specialty gates now run only after session and active-company consistency has been verified
- covered endpoints that still accept explicit `companyId` now reject active-company mismatches before reads or writes

Verification:

- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter "BusinessApprovalAuthorityTests|BusinessRequestContractGuardTests|UnitySearchPermissionFilterTests"` passed
- `dotnet build backend/src/Citus.Accounting.Api/Citus.Accounting.Api.csproj` passed

Still open:

- remaining query-parameter based read endpoints, including some journal/report/search/recent/history surfaces, should receive uniform active-company equality checks
- database-backed endpoint tests proving unauthorized settings and purchase-order calls return `403` before data access or mutation
- UI hide/disable behavior driven by the same operation authority model
- internal control-plane endpoints need explicit SysAdmin/session hardening before any public exposure
- eventual migration from role-name convention checks to stored explicit `module + action` permission grants

### 2026-05-21 Eleventh High-Risk Repair Batch

Completed:

- added a shared `RequireActiveCompanyQuery` guard for query-parameter based endpoints that still accept explicit `companyId`
- applied active-company equality checks to covered financial report reads/exports before report repository calls
- applied active-company equality checks to journal-entry list/detail/by-source reads before journal repository calls
- hardened UnitySearch search, recent-query, recent-selection, and click recording endpoints so request `companyId` must match the active session company
- hardened UnitySearch user scoping so callers cannot request or record recent-search/click data for another `userId`
- protected Sales/Expense overview report reads (`sales/cash-flow`, `sales/income-over-time`, `expense/cash-outflow`, `expense/over-time`) with reports authority and active-company checks
- protected AR/AP open-item drilldown, adjustment preview, adjustment-request read/readiness/execution-plan, and adjustment execution request paths with module authority and active-company checks
- protected open-item adjustment account mapping lookup/save/deactivate with active-company checks and accounting/governance authority
- changed AR/AP open-item adjustment actor resolution to use the backend business session user instead of trusting request-body `userId`

Current query-company permission behavior:

- covered report and journal reads now reject explicit `companyId` values outside the active business session before data access
- covered UnitySearch endpoints now enforce both company isolation and per-user recent/click isolation
- covered AR/AP open-item adjustment flows now bind actor identity to the resolved session and no longer accept cross-company request bodies

Verification:

- `dotnet build backend/src/Citus.Accounting.Api/Citus.Accounting.Api.csproj` passed
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter "BusinessApprovalAuthorityTests|BusinessRequestContractGuardTests|UnitySearchPermissionFilterTests"` passed

Still open:

- V1 convenience list/detail endpoints for sales receipts, refund receipts, bank transfers, bank deposits, and tax returns still need active-company and module read gates
- company-book governance and document-review query-parameter reads still need the same shared active-company guard
- database-backed endpoint tests proving unauthorized report/journal/search/open-item calls return `403` before data access
- UI hide/disable behavior driven by the same operation authority model
- internal control-plane endpoints need explicit SysAdmin/session hardening before any public exposure
- eventual migration from role-name convention checks to stored explicit `module + action` permission grants

### 2026-05-21 Twelfth High-Risk Repair Batch

Completed:

- protected company-book governance reads, governance-signal reads, governed-change request reads, apply-readiness reads, and remeasurement-policy reads with active-company checks and accounting authority
- added active-company checks to company-book governance write/preview/prepare/submit/cancel endpoints that accept `companyId` in request bodies
- protected generic source-document browser, source-document lifecycle preview/action reads, document-review detail reads, invoice PDF downloads, and invoice send flows with active-company checks
- protected source-document lifecycle void/reverse/reverse-request submit/cancel/execute/readiness/execution-plan/blocker/reversal reads with active-company checks
- protected V1 convenience list/detail reads for sales receipts, refund receipts, credit memos, vendor credits, bank transfers, bank deposits, and tax returns with module-specific authority
- kept generic document-review/lifecycle surfaces under accounting authority because they expose journal linkage, lifecycle controls, and reversal/subledger state beyond a single sales or purchases view

Current company/document/V1 read permission behavior:

- accounting/book-governance users can inspect covered company-book governance and generic source-document lifecycle surfaces
- AR-payment users can view covered sales receipt/refund receipt detail and invoice PDF/send-history surfaces
- sales users can list covered credit memos, purchases users can list covered vendor credits, banking users can view covered bank transfer/deposit lists and details
- all covered endpoints reject explicit `companyId` values outside the active business session before repository access

Verification:

- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter "BusinessApprovalAuthorityTests|BusinessRequestContractGuardTests|UnitySearchPermissionFilterTests"` passed
- `dotnet build backend/src/Citus.Accounting.Api/Citus.Accounting.Api.csproj` passed

Still open:

- database-backed endpoint tests proving unauthorized company-book/document-review/V1 calls return `403` before data access
- several write endpoints still trust body `UserId`; they should be migrated to backend session user in a later, carefully scoped batch
- UI hide/disable behavior driven by the same operation authority model
- internal control-plane endpoints need explicit SysAdmin/session hardening before any public exposure
- eventual migration from role-name convention checks to stored explicit `module + action` permission grants

### 2026-05-21 Thirteenth High-Risk Repair Batch

Completed:

- added a shared `RequireBusinessSessionActor` guard so sensitive write/post endpoints can bind audit actor identity to the resolved backend business session
- migrated company-book governance signal, closed-period, issued-statement, filed-tax, governed-change prepare, submit, and cancel actions from request-body `UserId` to the session actor
- migrated source-document reverse, reverse-request submit/cancel, and governed reverse execution actor resolution to the session actor
- added active-company, accounting authority, and session-actor gates to FX revaluation cascade-unwind prepare, cascade auto-post, and FX revaluation batch post endpoints
- migrated manual journal post, invoice post, credit-note post, bill post, vendor-credit post, receive-payment prepare/post, credit-application post, pay-bill prepare/post, and vendor-credit-application post to use the session actor
- migrated V1 save-and-post endpoints for sales receipts, refund receipts, credit memos, vendor credits, bank transfers, bank deposits, and tax returns to use the session actor for both draft save and posting commands
- added active-company checks to the covered post/prepare/save-and-post endpoints before executing handlers that mutate accounting state

Current actor and posting permission behavior:

- covered GL/AR/AP/banking/tax posting flows no longer accept a forged body `userId` as the audit/posting actor
- covered save-and-post flows can preserve their existing request shapes for frontend compatibility while backend authority derives the actor from `BusinessSessionContext`
- covered FX revaluation posting endpoints now require an active company session and accounting authority before command execution

Verification:

- `dotnet build backend/src/Citus.Accounting.Api/Citus.Accounting.Api.csproj` passed with 0 warnings and 0 errors
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter "BusinessApprovalAuthorityTests|BusinessRequestContractGuardTests|UnitySearchPermissionFilterTests"` passed, 51/51
- `git diff --check` passed; Git only reported existing LF-to-CRLF working-copy warnings

Still open:

- invoice, credit-note, bill, vendor-credit, purchase-order, receipt, GR/IR, and inventory draft/submit workflow endpoints still contain body `UserId` usage and need a separate lower-risk migration batch
- AR/AP open-item approval/rejection actor paths still use nullable session actor shape and should be normalized to `RequireBusinessSessionActor`
- manual-journal and FX revaluation read/preview surfaces still need a focused active-company/read-authority pass where not already covered
- database-backed endpoint tests should prove forged body `UserId` is ignored and active-company mismatch returns before mutation
- UI hide/disable behavior driven by the same operation authority model
- internal control-plane endpoints need explicit SysAdmin/session hardening before any public exposure
- eventual migration from role-name convention checks to stored explicit `module + action` permission grants

### 2026-05-21 Fourteenth High-Risk Repair Batch

Completed:

- migrated invoice draft create/update/submit endpoints from request-body `UserId` to the resolved backend business-session actor
- migrated credit-note draft create/update endpoints from request-body `UserId` to the session actor
- migrated bill draft create/update/submit/cancel endpoints from request-body `UserId` to the session actor
- migrated vendor-credit draft create/update endpoints from request-body `UserId` to the session actor
- added active-company checks to the covered AR/AP draft mutation endpoints before repository save/submit/cancel calls

Current AR/AP draft mutation behavior:

- covered invoice, credit-note, bill, and vendor-credit draft mutations now reject active-company mismatches before repository mutation
- covered AR/AP draft audit actor values are now session-bound rather than client-supplied
- request shapes remain compatible with the existing UI while backend identity authority comes from `BusinessSessionContext`

Verification:

- `dotnet build backend/src/Citus.Accounting.Api/Citus.Accounting.Api.csproj` passed with 0 warnings and 0 errors
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter "BusinessApprovalAuthorityTests|BusinessRequestContractGuardTests|UnitySearchPermissionFilterTests"` passed, 51/51
- `git diff --check` passed; Git only reported existing LF-to-CRLF working-copy warnings

Still open:

- purchase-order draft/approval/issue/reopen/close/cancel endpoints still contain body `UserId` usage and should be migrated in the next batch
- receipt, GR/IR, receipt valuation/cost-layer/settlement, and inventory workflow endpoints still contain body `UserId` usage
- AR/AP open-item approval/rejection actor paths still use nullable session actor shape and should be normalized to `RequireBusinessSessionActor`
- manual-journal and FX revaluation read/preview surfaces still need a focused active-company/read-authority pass where not already covered
- database-backed endpoint tests should prove forged body `UserId` is ignored and active-company mismatch returns before mutation
- UI hide/disable behavior driven by the same operation authority model
- internal control-plane endpoints need explicit SysAdmin/session hardening before any public exposure
- eventual migration from role-name convention checks to stored explicit `module + action` permission grants

### 2026-05-21 Fifteenth High-Risk Repair Batch

Completed:

- migrated purchase-order approval-request create/submit/reject endpoints from request-body `UserId` to the resolved backend business-session actor
- migrated purchase-order draft create/update endpoints from request-body `UserId` to the session actor
- migrated purchase-order approve, approval reverse, issue/release, reopen-for-amendment, close, and cancel endpoints from request-body `UserId` to the session actor
- migrated purchase-order quantity-discrepancy refresh/review endpoints from request-body `UserId` to the session actor
- preserved existing active-company checks, approval-threshold authority checks, release/amendment/close/cancel governance checks, and purchase-order repository workflows

Current purchase-order mutation behavior:

- covered purchase-order mutations now reject active-company mismatches before repository mutation and use the authenticated business-session user for audit/lifecycle actor fields
- approval-threshold and governance gates still run before approval/rejection/release/amendment/close/cancel mutations
- request DTO shapes remain compatible with current UI clients while backend actor authority is session-bound

Verification:

- `dotnet build backend/src/Citus.Accounting.Api/Citus.Accounting.Api.csproj` passed with 0 warnings and 0 errors
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter "BusinessApprovalAuthorityTests|BusinessRequestContractGuardTests|UnitySearchPermissionFilterTests"` passed, 51/51
- `git diff --check` passed; Git only reported existing LF-to-CRLF working-copy warnings

Still open:

- receipt, GR/IR, receipt valuation/cost-layer/settlement, and inventory workflow endpoints still contain body `UserId` usage and should be migrated in the next batch
- AR/AP open-item approval/rejection actor paths still use nullable session actor shape and should be normalized to `RequireBusinessSessionActor`
- action-center endpoints intentionally pass nullable session users and need a separate audit policy decision before hardening
- manual-journal and FX revaluation read/preview surfaces still need a focused active-company/read-authority pass where not already covered
- database-backed endpoint tests should prove forged body `UserId` is ignored and active-company mismatch returns before mutation
- UI hide/disable behavior driven by the same operation authority model
- internal control-plane endpoints need explicit SysAdmin/session hardening before any public exposure
- eventual migration from role-name convention checks to stored explicit `module + action` permission grants

### 2026-05-21 Sixteenth High-Risk Repair Batch

Completed:

- migrated GR/IR clearing-account policy save from request-body `UserId` to the resolved backend business-session actor
- migrated receipt draft create/update and receipt post/retry flows from request-body `UserId` to the session actor
- migrated receipt inventory valuation refresh and cost-layer emission endpoints from request-body `UserId` to the session actor
- migrated receipt GR/IR bridge refresh/post endpoints from request-body `UserId` to the session actor
- migrated receipt GR/IR settlement refresh, journal-reconciliation refresh, purchase-variance refresh, settlement execution, settlement journal post, AP open-item clear, and AP open-item reverse endpoints from request-body `UserId` to the session actor
- added active-company checks to the covered receipt/GRIR/inventory mutation endpoints before repository/store/handler mutation
- preserved existing inventory, GR/IR, settlement, variance, and posting business rules without changing cost or quantity logic

Current receipt / inventory / GRIR mutation behavior:

- covered receipt and GR/IR mutation flows now reject active-company mismatches before mutating receipt, inventory activation/valuation, cost-layer, GR/IR bridge, settlement journal, or AP open-item state
- covered inventory and GR/IR audit actor values are now session-bound instead of client-supplied
- request DTO shapes remain compatible with current UI clients while backend actor authority is session-bound

Verification:

- `dotnet build backend/src/Citus.Accounting.Api/Citus.Accounting.Api.csproj` passed with 0 warnings and 0 errors
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter "BusinessApprovalAuthorityTests|BusinessRequestContractGuardTests|UnitySearchPermissionFilterTests"` passed, 51/51
- `git diff --check` passed; Git only reported existing LF-to-CRLF working-copy warnings

Still open:

- AR/AP open-item approval/rejection actor paths still use nullable session actor shape and should be normalized to `RequireBusinessSessionActor`
- action-center endpoints intentionally pass nullable session users and need a separate audit policy decision before hardening
- UnitySearch request `UserId` is now only used as an explicit consistency check and should eventually be removed from public request contracts
- manual-journal and FX revaluation read/preview surfaces still need a focused active-company/read-authority pass where not already covered
- database-backed endpoint tests should prove forged body `UserId` is ignored and active-company mismatch returns before mutation
- UI hide/disable behavior driven by the same operation authority model
- internal control-plane endpoints need explicit SysAdmin/session hardening before any public exposure
- eventual migration from role-name convention checks to stored explicit `module + action` permission grants

### 2026-05-21 Seventeenth High-Risk Repair Batch

Completed:

- normalized open-item adjustment account mapping save/deactivate endpoints to require a backend business-session actor instead of accepting request-body `UserId`
- normalized AR open-item adjustment request, submit, cancel, approve, reject, and execute paths to use `RequireBusinessSessionActor`
- normalized AP open-item adjustment request, submit, cancel, approve, reject, and execute paths to use `RequireBusinessSessionActor`
- removed nullable actor fallbacks from governed AR/AP open-item adjustment execution command construction
- added active-company, accounting authority, and session-actor gates to FX revaluation batch prepare and next-period unwind prepare endpoints
- migrated FX revaluation prepare/unwind prepare actor values from request-body `UserId` to the session actor

Current open-item / FX actor behavior:

- covered AR/AP open-item adjustment and mapping mutation flows now require a concrete authenticated business-session actor before repository mutation or posting command execution
- covered FX revaluation preparation flows now reject active-company mismatches and require accounting authority before draft generation
- `Program.cs` no longer uses request-body `UserId` as a mutation actor; the remaining `request.UserId` usage is a UnitySearch backward-compatible consistency check that rejects mismatched client user ids

Verification:

- `dotnet build backend/src/Citus.Accounting.Api/Citus.Accounting.Api.csproj` passed with 0 warnings and 0 errors
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter "BusinessApprovalAuthorityTests|BusinessRequestContractGuardTests|UnitySearchPermissionFilterTests"` passed, 51/51
- `git diff --check` passed; Git only reported existing LF-to-CRLF working-copy warnings

Still open:

- UnitySearch request `UserId` is still present as an explicit consistency check and should eventually be removed from public request contracts
- action-center endpoints intentionally pass nullable session users and need a separate audit policy decision before hardening
- manual-journal and FX revaluation read/list/detail/plan surfaces still need a focused active-company/read-authority pass where not already covered
- database-backed endpoint tests should prove forged body `UserId` is ignored and active-company mismatch returns before mutation
- UI hide/disable behavior driven by the same operation authority model
- internal control-plane endpoints need explicit SysAdmin/session hardening before any public exposure
- eventual migration from role-name convention checks to stored explicit `module + action` permission grants

### 2026-05-21 Eighteenth High-Risk Repair Batch

Completed:

- protected FX revaluation cascade-unwind plan reads with active-company checks and accounting authority
- protected FX revaluation batch list reads with active-company checks and accounting authority
- protected FX revaluation batch detail reads with active-company checks and accounting authority
- protected manual-journal detail reads with active-company checks and accounting authority
- kept existing FX revaluation prepare/post and manual-journal post/session-actor hardening intact

Current GL / FX read behavior:

- covered manual-journal and FX revaluation read surfaces now reject explicit `companyId` values outside the active business session before repository access
- covered FX revaluation list/detail/plan reads now require accounting/book-governance authority because they expose ledger-linked FX adjustment and unwind state
- covered manual journal detail reads now require accounting/book-governance authority before exposing journal lines

Verification:

- `dotnet build backend/src/Citus.Accounting.Api/Citus.Accounting.Api.csproj` passed with 0 warnings and 0 errors
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter "BusinessApprovalAuthorityTests|BusinessRequestContractGuardTests|UnitySearchPermissionFilterTests"` passed, 51/51
- `git diff --check` passed; Git only reported existing LF-to-CRLF working-copy warnings

Still open:

- UnitySearch request `UserId` is still present as an explicit consistency check and should eventually be removed from public request contracts
- action-center endpoints intentionally pass nullable session users and need a separate audit policy decision before hardening
- remaining database-backed endpoint tests should prove unauthorized GL/FX read calls return `403` before repository access
- database-backed endpoint tests should prove forged body `UserId` is ignored and active-company mismatch returns before mutation
- UI hide/disable behavior driven by the same operation authority model
- internal control-plane endpoints need explicit SysAdmin/session hardening before any public exposure
- eventual migration from role-name convention checks to stored explicit `module + action` permission grants

### 2026-05-21 Nineteenth High-Risk Repair Batch

Completed:

- removed `UserId` from the server-side UnitySearch click recording request contract and stopped reading client-supplied click user ids
- normalized UnitySearch usage tracking and report usage tracking to require a concrete backend business-session actor
- normalized dashboard suggestion list/generate flows to use the backend business-session actor instead of nullable session-user fallback
- normalized action-center task list/regenerate/start/complete/dismiss/snooze flows to require a backend business-session actor
- added dashboard suggestion ownership checks before accept/dismiss/snooze so one user in the same company cannot transition another user's suggestion by id
- changed accepted dashboard suggestions to create widgets for the authenticated session actor

Current AI/search/task actor behavior:

- `Program.cs` no longer uses request-body `UserId` as a mutation or usage actor
- UnitySearch click recording, UnitySearch usage learning, report usage learning, dashboard suggestions, and action-center task actions now bind actor identity to `BusinessSessionContext`
- dashboard suggestion transitions now enforce company scope plus user ownership before state mutation

Verification:

- `dotnet build backend/src/Citus.Accounting.Api/Citus.Accounting.Api.csproj` passed with 0 warnings and 0 errors
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter "BusinessApprovalAuthorityTests|BusinessRequestContractGuardTests|UnitySearchPermissionFilterTests"` passed, 51/51
- `git diff --check` passed; Git only reported existing LF-to-CRLF working-copy warnings

Still open:

- remaining database-backed endpoint tests should prove unauthorized GL/FX read calls return `403` before repository access
- database-backed endpoint tests should prove forged body `UserId` is ignored and active-company mismatch returns before mutation
- UI hide/disable behavior driven by the same operation authority model
- internal control-plane endpoints need explicit SysAdmin/session hardening before any public exposure
- eventual migration from role-name convention checks to stored explicit `module + action` permission grants

### 2026-05-21 Twentieth High-Risk Repair Batch

Completed:

- removed optional `UserId` from UnitySearch query and recent-query HTTP contracts
- removed `UserId` from V1 save-and-post request contracts for sales receipts, refund receipts, credit memos, vendor credits, bank transfers, bank deposits, and tax returns
- kept all affected operations bound to `BusinessSessionContext` through `RequireBusinessSessionActor`
- kept active-company and module-operation gates ahead of repository mutations for the affected V1 save-and-post endpoints

Current public-contract behavior:

- UnitySearch query/recent/click flows no longer expose user identity as caller-supplied API input
- V1 save-and-post endpoints no longer advertise `UserId` in request bodies; extra legacy JSON fields from older frontend builds should be ignored by the ASP.NET JSON binder
- audit and posting actor identity for these flows is now session-derived only

Verification:

- `dotnet build backend/src/Citus.Accounting.Api/Citus.Accounting.Api.csproj` passed with 0 warnings and 0 errors
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter "BusinessApprovalAuthorityTests|BusinessRequestContractGuardTests|UnitySearchPermissionFilterTests"` passed, 51/51
- `rg -n "query\\.UserId|request\\.UserId|public UserId UserId|public UserId\\? UserId" backend/src/Citus.Accounting.Api/Program.cs` returned no matches

Still open:

- remaining database-backed endpoint tests should prove unauthorized GL/FX read calls return `403` before repository access
- database-backed endpoint tests should prove forged body `UserId` is ignored and active-company mismatch returns before mutation
- UI hide/disable behavior driven by the same operation authority model
- internal control-plane endpoints need explicit SysAdmin/session hardening before any public exposure
- eventual migration from role-name convention checks to stored explicit `module + action` permission grants

### 2026-05-21 Twenty-First High-Risk Repair Batch

Completed:

- added `PublicRequestActorContractTests` to lock down the session-actor-only public API contract
- added regression coverage proving UnitySearch and V1 save-and-post contracts do not expose public `UserId` properties
- added regression coverage proving those contracts remain company-scoped by `BusinessRequestContractGuard`
- added regression coverage proving matching company-scoped requests still pass without caller-supplied `UserId`

Current test behavior:

- future attempts to reintroduce caller-supplied `UserId` into UnitySearch query/recent/click contracts will fail tests
- future attempts to reintroduce caller-supplied `UserId` into sales receipt, refund receipt, credit memo, vendor credit, bank transfer, bank deposit, or tax return save-and-post request bodies will fail tests
- guard-level company mismatch coverage now exists for both search-style contracts and V1 save-and-post-style contracts

Verification:

- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter PublicRequestActorContractTests` passed, 18/18
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter "BusinessApprovalAuthorityTests|BusinessRequestContractGuardTests|BusinessRouteGuardTests|DocumentAndOpenItemMutationGateTests|PublicRequestActorContractTests|UnitySearchPermissionFilterTests"` passed, 91/91

Still open:

- remaining database-backed endpoint tests should prove unauthorized GL/FX read calls return `403` before repository access
- full HTTP-level tests should prove legacy JSON `UserId` fields are ignored by model binding and cannot become audit/posting actors
- UI hide/disable behavior driven by the same operation authority model
- internal control-plane endpoints need explicit SysAdmin/session hardening before any public exposure
- eventual migration from role-name convention checks to stored explicit `module + action` permission grants

### 2026-05-21 Twenty-Second High-Risk Repair Batch

Completed:

- extended `PublicRequestActorContractTests` with JSON deserialization regression coverage
- verified legacy JSON payloads that still include `userId` deserialize into the new session-actor-only request contracts without exposing a caller-supplied actor
- verified those legacy JSON payloads still pass the company-scope guard only when the payload company matches the authenticated session company

Current test behavior:

- old frontend payloads with extra `userId` are covered as ignored JSON fields for UnitySearch query/recent/click contracts
- old frontend payloads with extra `userId` are covered as ignored JSON fields for sales receipt, refund receipt, credit memo, vendor credit, bank transfer, bank deposit, and tax return save-and-post contracts
- if any of those contracts later reintroduce public `UserId`, the regression suite will fail before the field can become an audit/posting actor again

Verification:

- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter PublicRequestActorContractTests` passed, 28/28
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter "BusinessApprovalAuthorityTests|BusinessRequestContractGuardTests|BusinessRouteGuardTests|DocumentAndOpenItemMutationGateTests|PublicRequestActorContractTests|UnitySearchPermissionFilterTests"` passed, 101/101

Still open:

- remaining database-backed endpoint tests should prove unauthorized GL/FX read calls return `403` before repository access
- full HTTP-level tests should prove endpoint execution uses session actor when legacy JSON includes `UserId`
- UI hide/disable behavior driven by the same operation authority model
- internal control-plane endpoints need explicit SysAdmin/session hardening before any public exposure
- eventual migration from role-name convention checks to stored explicit `module + action` permission grants

### 2026-05-21 Twenty-Third High-Risk Repair Batch

Completed:

- removed caller-supplied `UserId` from core AR/AP source-document request contracts whose endpoints already use `BusinessSessionContext`
- covered invoice draft save/post, credit-note draft save/post, bill draft save/post/submit, receive-payment prepare/post, and pay-bill prepare/post contracts
- updated route-guard tests to construct the new session-actor-only contracts
- expanded `PublicRequestActorContractTests` so future reintroduction of `UserId` into these contracts fails the regression suite

Current public-contract behavior:

- the affected AR/AP document and payment request bodies now carry `CompanyId` but no actor identity
- legacy JSON with an extra `userId` remains ignored by deserialization for these contracts
- endpoint actor identity remains session-derived in `Program.cs`; no `request.UserId` reads were introduced

Verification:

- `dotnet build backend/src/Citus.Accounting.Api/Citus.Accounting.Api.csproj` passed with 0 warnings and 0 errors
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter PublicRequestActorContractTests` passed, 50/50
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter "BusinessApprovalAuthorityTests|BusinessRequestContractGuardTests|BusinessRouteGuardTests|DocumentAndOpenItemMutationGateTests|PublicRequestActorContractTests|UnitySearchPermissionFilterTests"` passed, 123/123

Still open:

- more public contracts in `ManualJournalContracts.cs` still expose caller-supplied `UserId`; each should be removed in batches only after verifying the endpoint uses session actor
- remaining database-backed endpoint tests should prove unauthorized GL/FX read calls return `403` before repository access
- full HTTP-level tests should prove endpoint execution uses session actor when legacy JSON includes `UserId`
- UI hide/disable behavior driven by the same operation authority model
- internal control-plane endpoints need explicit SysAdmin/session hardening before any public exposure
- eventual migration from role-name convention checks to stored explicit `module + action` permission grants

### 2026-05-21 Twenty-Fourth High-Risk Repair Batch

Completed:

- removed caller-supplied `UserId` from GL/Company Book/FX request contracts whose endpoints already require a business-session actor
- covered FX revaluation prepare/unwind/post contracts, manual-journal post, company-book governance signal/period/statement/tax registration, and company-book governed-change prepare/transition contracts
- expanded `PublicRequestActorContractTests` so these contracts are locked to session-actor-only request bodies

Current public-contract behavior:

- the affected GL, FX, and company-book request bodies now carry `CompanyId` but no caller-supplied actor identity
- legacy JSON with an extra `userId` remains ignored by deserialization for these contracts
- endpoint actor identity remains session-derived through `RequireBusinessSessionActor`

Verification:

- `dotnet build backend/src/Citus.Accounting.Api/Citus.Accounting.Api.csproj` passed with 0 warnings and 0 errors
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter PublicRequestActorContractTests` passed, 70/70
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter "BusinessApprovalAuthorityTests|BusinessRequestContractGuardTests|BusinessRouteGuardTests|DocumentAndOpenItemMutationGateTests|PublicRequestActorContractTests|UnitySearchPermissionFilterTests"` passed, 143/143

Still open:

- remaining `ManualJournalContracts.cs` `UserId` public fields are concentrated in open-item adjustment, receipt/purchase-order/inventory, vendor-credit application, and vendor-credit draft/post flows
- each remaining group should be removed only after verifying the endpoint uses session actor and updating any route-guard tests
- remaining database-backed endpoint tests should prove unauthorized GL/FX read calls return `403` before repository access
- full HTTP-level tests should prove endpoint execution uses session actor when legacy JSON includes `UserId`
- UI hide/disable behavior driven by the same operation authority model
- internal control-plane endpoints need explicit SysAdmin/session hardening before any public exposure
- eventual migration from role-name convention checks to stored explicit `module + action` permission grants

### 2026-05-21 Twenty-Fifth High-Risk Repair Batch

Completed:

- removed caller-supplied `UserId?` from open-item adjustment and adjustment-account-mapping request contracts
- covered AR/AP adjustment request/create/submit/cancel/govern/execute contracts plus mapping save/deactivate contracts
- updated mutation-gate tests to construct the new session-actor-only contracts
- expanded `PublicRequestActorContractTests` so these contracts cannot reintroduce public `UserId`

Current public-contract behavior:

- open-item adjustment and mapping request bodies now carry `CompanyId` but no actor identity
- legacy JSON with an extra `userId` remains ignored by deserialization for these contracts
- endpoint actor identity remains session-derived through `RequireBusinessSessionActor`

Verification:

- `dotnet build backend/src/Citus.Accounting.Api/Citus.Accounting.Api.csproj` passed with 0 warnings and 0 errors
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter PublicRequestActorContractTests` passed, 82/82
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter "BusinessApprovalAuthorityTests|BusinessRequestContractGuardTests|BusinessRouteGuardTests|DocumentAndOpenItemMutationGateTests|PublicRequestActorContractTests|UnitySearchPermissionFilterTests"` passed, 155/155

Still open:

- remaining `ManualJournalContracts.cs` `UserId` public fields are now concentrated in receipt, purchase-order, inventory/GRIR, vendor-credit draft/post/application, and credit-application flows
- each remaining group should be removed only after verifying the endpoint uses session actor and updating any route-guard tests
- remaining database-backed endpoint tests should prove unauthorized GL/FX read calls return `403` before repository access
- full HTTP-level tests should prove endpoint execution uses session actor when legacy JSON includes `UserId`
- UI hide/disable behavior driven by the same operation authority model
- internal control-plane endpoints need explicit SysAdmin/session hardening before any public exposure
- eventual migration from role-name convention checks to stored explicit `module + action` permission grants

### 2026-05-21 Twenty-Sixth High-Risk Repair Batch

Completed:

- removed caller-supplied `UserId` from receipt draft/post request contracts
- removed caller-supplied `UserId` from receipt GR/IR bridge, settlement execute, settlement journal post, and GR/IR clearing-account policy contracts
- expanded `PublicRequestActorContractTests` so these receipt/GRIR contracts cannot reintroduce public `UserId`

Current public-contract behavior:

- receipt and GR/IR request bodies now carry `CompanyId` but no caller-supplied actor identity
- legacy JSON with an extra `userId` remains ignored by deserialization for these contracts
- endpoint actor identity remains session-derived through `RequireBusinessSessionActor`

Verification:

- `dotnet build backend/src/Citus.Accounting.Api/Citus.Accounting.Api.csproj` passed with 0 warnings and 0 errors
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter PublicRequestActorContractTests` passed, 94/94
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter "BusinessApprovalAuthorityTests|BusinessRequestContractGuardTests|BusinessRouteGuardTests|DocumentAndOpenItemMutationGateTests|PublicRequestActorContractTests|UnitySearchPermissionFilterTests"` passed, 167/167

Still open:

- remaining `ManualJournalContracts.cs` `UserId` public fields are now concentrated in purchase-order lifecycle/discrepancy, vendor-credit draft/post/application, credit-application, and pay-bill prepare flows
- each remaining group should be removed only after verifying the endpoint uses session actor and updating any route-guard tests
- remaining database-backed endpoint tests should prove unauthorized GL/FX read calls return `403` before repository access
- full HTTP-level tests should prove endpoint execution uses session actor when legacy JSON includes `UserId`
- UI hide/disable behavior driven by the same operation authority model
- internal control-plane endpoints need explicit SysAdmin/session hardening before any public exposure
- eventual migration from role-name convention checks to stored explicit `module + action` permission grants

### 2026-05-21 Twenty-Seventh High-Risk Repair Batch

Completed:

- removed caller-supplied `UserId` from purchase-order draft save contracts
- removed caller-supplied `UserId` from purchase-order approval request, submit, reject, and reverse contracts
- removed caller-supplied `UserId` from purchase-order approve, issue, reopen, close, and cancel contracts
- removed caller-supplied `UserId` from purchase-order quantity-discrepancy refresh and review contracts
- expanded `PublicRequestActorContractTests` so these purchase-order contracts cannot reintroduce public `UserId`

Current public-contract behavior:

- purchase-order request bodies now carry `CompanyId` but no caller-supplied actor identity
- legacy JSON with an extra `userId` remains ignored by deserialization for these contracts
- endpoint actor identity remains session-derived through `RequireBusinessSessionActor`

Verification:

- `dotnet build backend/src/Citus.Accounting.Api/Citus.Accounting.Api.csproj` passed with 0 warnings and 0 errors
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter PublicRequestActorContractTests` passed, 118/118
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter "BusinessApprovalAuthorityTests|BusinessRequestContractGuardTests|BusinessRouteGuardTests|DocumentAndOpenItemMutationGateTests|PublicRequestActorContractTests|UnitySearchPermissionFilterTests"` passed, 191/191

Still open:

- remaining `ManualJournalContracts.cs` `UserId` public fields are now concentrated in vendor-credit draft/post/application and credit-application flows
- each remaining group should be removed only after verifying the endpoint uses session actor and updating any route-guard tests
- remaining database-backed endpoint tests should prove unauthorized GL/FX read calls return `403` before repository access
- full HTTP-level tests should prove endpoint execution uses session actor when legacy JSON includes `UserId`
- UI hide/disable behavior driven by the same operation authority model
- internal control-plane endpoints need explicit SysAdmin/session hardening before any public exposure
- eventual migration from role-name convention checks to stored explicit `module + action` permission grants

### 2026-05-21 Twenty-Eighth High-Risk Repair Batch

Completed:

- removed caller-supplied `UserId` from vendor-credit draft save and vendor-credit post request contracts
- removed caller-supplied `UserId` from customer credit-application post contracts
- removed caller-supplied `UserId` from vendor-credit application post contracts
- expanded `PublicRequestActorContractTests` so these final AP/AR credit application contracts cannot reintroduce public `UserId`

Current public-contract behavior:

- `ManualJournalContracts.cs` no longer exposes public `UserId` or `UserId?` fields on these accounting HTTP request contracts
- vendor-credit and credit-application request bodies now carry `CompanyId` but no caller-supplied actor identity
- legacy JSON with an extra `userId` remains ignored by deserialization for these contracts
- endpoint actor identity remains session-derived through `RequireBusinessSessionActor`

Verification:

- `rg -n "UserId UserId|UserId\\? UserId" backend/src/Citus.Accounting.Api/ManualJournalContracts.cs` returned no matches
- `rg -n "request\\.UserId|query\\.UserId" backend/src/Citus.Accounting.Api/Program.cs` returned no matches
- `dotnet build backend/src/Citus.Accounting.Api/Citus.Accounting.Api.csproj` passed with 0 warnings and 0 errors
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter PublicRequestActorContractTests` passed, 126/126
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter "BusinessApprovalAuthorityTests|BusinessRequestContractGuardTests|BusinessRouteGuardTests|DocumentAndOpenItemMutationGateTests|PublicRequestActorContractTests|UnitySearchPermissionFilterTests"` passed, 199/199

Still open:

- scan remaining public API request/query contracts outside `ManualJournalContracts.cs` for caller-supplied actor identity
- remaining database-backed endpoint tests should prove unauthorized GL/FX read calls return `403` before repository access
- full HTTP-level tests should prove endpoint execution uses session actor when legacy JSON includes `UserId`
- UI hide/disable behavior driven by the same operation authority model
- internal control-plane endpoints need explicit SysAdmin/session hardening before any public exposure
- eventual migration from role-name convention checks to stored explicit `module + action` permission grants

### 2026-05-22 Twenty-Ninth High-Risk Repair Batch

Completed:

- scanned API-layer request/query contracts outside `ManualJournalContracts.cs` for remaining caller-supplied actor identity
- verified Inventory application-layer contracts with `UserId` are currently constructed from the business session by API endpoints rather than directly bound as public HTTP bodies
- added an assembly-wide API wire-contract regression test that fails if any `Citus.Accounting.Api` `*HttpRequest`, `*HttpQuery`, `*LookupQuery`, `*Query`, or `*Request` type exposes a public `UserId`

Current public-contract behavior:

- API wire request/query contracts are now guarded both by explicit named-contract tests and an assembly-wide reflection scan
- future public API request/query contracts that accidentally expose caller-supplied `UserId` should fail `PublicRequestActorContractTests`
- internal application contracts may still carry an actor id, but the API edge must derive it from `BusinessSessionContext`

Verification:

- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter PublicRequestActorContractTests` passed, 127/127
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter "BusinessApprovalAuthorityTests|BusinessRequestContractGuardTests|BusinessRouteGuardTests|DocumentAndOpenItemMutationGateTests|PublicRequestActorContractTests|UnitySearchPermissionFilterTests"` passed, 200/200

Still open:

- add explicit endpoint-level tests for Inventory API routes proving internal `Inventory*Request.UserId` values are always session-derived
- remaining database-backed endpoint tests should prove unauthorized GL/FX read calls return `403` before repository access
- full HTTP-level tests should prove endpoint execution uses session actor when legacy JSON includes `UserId`
- UI hide/disable behavior driven by the same operation authority model
- internal control-plane endpoints need explicit SysAdmin/session hardening before any public exposure
- eventual migration from role-name convention checks to stored explicit `module + action` permission grants

### 2026-05-22 Thirtieth High-Risk Repair Batch

Completed:

- added a regression test for Inventory item API actor mapping
- deserializes an `InventoryItemUpsertHttpRequest` from legacy JSON containing a spoofed `userId`
- verifies the internal `InventoryItemUpsertRequest.UserId` is the authenticated business-session user, not the caller-supplied JSON value

Current public-contract behavior:

- Inventory item HTTP bodies remain free of public `UserId`
- item create/update mapping derives the internal application-layer actor from the session parameter passed by the API edge
- the existing API wire-contract scan continues to prevent reintroducing public `UserId` on API request/query contracts

Verification:

- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter PublicRequestActorContractTests` passed, 128/128
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter "BusinessApprovalAuthorityTests|BusinessRequestContractGuardTests|BusinessRouteGuardTests|DocumentAndOpenItemMutationGateTests|PublicRequestActorContractTests|UnitySearchPermissionFilterTests"` passed, 201/201

Still open:

- add similar session-actor mapping tests for Inventory activation and warehouse update once the route construction is factored behind small mapper helpers
- add full HTTP-level tests when the API test project has a lightweight TestServer/WebApplicationFactory setup
- remaining database-backed endpoint tests should prove unauthorized GL/FX read calls return `403` before repository access
- UI hide/disable behavior driven by the same operation authority model
- internal control-plane endpoints need explicit SysAdmin/session hardening before any public exposure
- eventual migration from role-name convention checks to stored explicit `module + action` permission grants

### 2026-05-22 Thirty-First High-Risk Repair Batch

Completed:

- factored Inventory activation policy construction into `InventoryActivationRequestParser.BuildPolicyUpdateRequest`
- factored Inventory activation default-warehouse construction into `InventoryActivationRequestParser.BuildDefaultWarehouseRequest`
- factored warehouse rename/update construction into `WarehouseRequestMapper.BuildWarehouseUpsertRequest`
- added regression tests proving activation and warehouse rename JSON bodies cannot spoof the internal `InventoryWarehouseUpsertRequest.UserId` or `InventoryCostingPolicyUpdateRequest.UserId`

Current public-contract behavior:

- Inventory activation and warehouse update HTTP bodies remain free of public `UserId`
- internal Inventory policy and warehouse upsert requests now derive actor identity from the session user passed at the API edge
- legacy JSON with an extra `userId` is ignored by deserialization and cannot alter the internal application-layer actor

Verification:

- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter PublicRequestActorContractTests` passed, 130/130
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter "BusinessApprovalAuthorityTests|BusinessRequestContractGuardTests|BusinessRouteGuardTests|DocumentAndOpenItemMutationGateTests|PublicRequestActorContractTests|UnitySearchPermissionFilterTests"` passed, 203/203

Still open:

- add full HTTP-level tests when the API test project has a lightweight TestServer/WebApplicationFactory setup
- extend the same session-actor mapping pattern to future Inventory HTTP routes before exposing adjustment, shipment, manufacturing, or transfer endpoints
- remaining database-backed endpoint tests should prove unauthorized GL/FX read calls return `403` before repository access
- UI hide/disable behavior driven by the same operation authority model
- internal control-plane endpoints need explicit SysAdmin/session hardening before any public exposure
- eventual migration from role-name convention checks to stored explicit `module + action` permission grants

### 2026-05-22 Thirty-Second High-Risk Repair Batch

Completed:

- factored UnitySearch usage-event input construction into `UnityAiHttpRequestMapper.BuildUnitysearchEventInput`
- factored report usage-event input construction into `UnityAiHttpRequestMapper.BuildReportUsageEventInput`
- added regression tests proving usage/report JSON bodies cannot spoof the internal `UnitysearchEventInput.UserId` or `ReportUsageEventInput.UserId`

Current public-contract behavior:

- UnityAI usage/report HTTP bodies remain free of public `UserId`
- internal usage/report application inputs now derive actor identity from the session user passed at the API edge
- legacy JSON with an extra `userId` is ignored by deserialization and cannot alter per-user learning/report usage records

Verification:

- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter PublicRequestActorContractTests` passed, 132/132
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter "BusinessApprovalAuthorityTests|BusinessRequestContractGuardTests|BusinessRouteGuardTests|DocumentAndOpenItemMutationGateTests|PublicRequestActorContractTests|UnitySearchPermissionFilterTests"` passed, 205/205

Still open:

- add full HTTP-level tests when the API test project has a lightweight TestServer/WebApplicationFactory setup
- keep new learning/dashboard/action-center endpoints behind the same session-derived actor mapper pattern
- remaining database-backed endpoint tests should prove unauthorized GL/FX read calls return `403` before repository access
- UI hide/disable behavior driven by the same operation authority model
- internal control-plane endpoints need explicit SysAdmin/session hardening before any public exposure
- eventual migration from role-name convention checks to stored explicit `module + action` permission grants

### 2026-05-22 Thirty-Third High-Risk Repair Batch

Completed:

- added `SysAdminControlPlaneGate` to validate `X-Citus-SysAdmin-Session` through `ISysAdminAuthRepository`
- registered `ISysAdminAuthRepository` in the Accounting API and included its schema in runtime schema management
- hardened `/internal/ai/distill-unitysearch` so it requires a valid SysAdmin session before running hint distillation
- changed the distillation trigger audit from `triggeredByUserId: null` to the authenticated SysAdmin account id
- added tests for missing, invalid, and valid SysAdmin control-plane sessions

Current control-plane behavior:

- the internal UnitySearch hint-distillation trigger no longer runs anonymously
- missing or invalid SysAdmin session headers return `401`
- successful manual distillation jobs carry the SysAdmin account id into AI job-run audit metadata

Verification:

- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter SysAdminControlPlaneGateTests` passed, 3/3
- `dotnet build backend/src/Citus.Accounting.Api/Citus.Accounting.Api.csproj` passed with 0 warnings and 0 errors
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter "BusinessApprovalAuthorityTests|BusinessRequestContractGuardTests|BusinessRouteGuardTests|DocumentAndOpenItemMutationGateTests|PublicRequestActorContractTests|UnitySearchPermissionFilterTests|SysAdminControlPlaneGateTests"` passed, 208/208

Still open:

- add full HTTP-level tests for `/internal/ai/distill-unitysearch` once the API test project has a lightweight TestServer/WebApplicationFactory setup
- consider moving manual AI distillation behind the SysAdmin API surface so browser clients do not need to call Accounting API internal routes directly
- remaining database-backed endpoint tests should prove unauthorized GL/FX read calls return `403` before repository access
- UI hide/disable behavior driven by the same operation authority model
- eventual migration from role-name convention checks to stored explicit `module + action` permission grants

### 2026-05-22 Thirty-Fourth High-Risk Repair Batch

Completed:

- added `BusinessEndpointReadGate` as a reusable API-edge guard for company-scoped read endpoints
- centralized the combined checks for authenticated business session, active-company match, and module-operation authority
- migrated the company-book list endpoint to the shared read gate
- migrated the company-book governance-signals read endpoint to the shared read gate
- added regression tests for missing session, cross-company query, missing module authority, and successful authorized read

Current company-scoped read behavior:

- selected company-book read endpoints now fail before workflow/repository access when the request has no business session
- selected company-book read endpoints now fail before workflow/repository access when `companyId` does not match the active company
- selected company-book read endpoints now reuse `BusinessApprovalAuthority.EvaluateBusinessOperation` for operation-level accounting permission checks
- the guard preserves existing public response behavior for session and company mismatch while making the decision testable

Verification:

- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter BusinessEndpointReadGateTests` passed, 4/4
- `dotnet build backend/src/Citus.Accounting.Api/Citus.Accounting.Api.csproj` passed with 0 warnings and 0 errors
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter "BusinessApprovalAuthorityTests|BusinessRequestContractGuardTests|BusinessRouteGuardTests|BusinessEndpointReadGateTests|DocumentAndOpenItemMutationGateTests|PublicRequestActorContractTests|UnitySearchPermissionFilterTests|SysAdminControlPlaneGateTests"` passed, 212/212
- `git diff --check` passed with line-ending warnings only

Still open:

- migrate the remaining company-book, report, open-item, FX, and inventory query endpoints to the shared company-scoped read/write gate pattern in small batches
- add repository-spy endpoint tests proving blocked reads do not call database-backed workflows
- add full HTTP-level tests once the API test project has a lightweight TestServer/WebApplicationFactory setup
- UI hide/disable behavior driven by the same operation authority model
- eventual migration from role-name convention checks to stored explicit `module + action` permission grants

### 2026-05-22 Thirty-Fifth High-Risk Repair Batch

Completed:

- migrated Trial Balance read and CSV export endpoints to `BusinessEndpointReadGate`
- migrated Income Statement read and CSV export endpoints to `BusinessEndpointReadGate`
- migrated Balance Sheet read and CSV export endpoints to `BusinessEndpointReadGate`
- added report/export-specific regression coverage for the shared company-scoped read gate

Current report read/export behavior:

- selected report endpoints now use one shared decision path for business-session presence, active-company match, and report-module operation authority
- selected CSV export endpoints are explicitly covered by the same report-module permission model as interactive report reads
- AR-only users remain blocked from report export even if they submit a valid active-company `companyId`

Verification:

- `dotnet build backend/src/Citus.Accounting.Api/Citus.Accounting.Api.csproj` passed with 0 warnings and 0 errors
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter BusinessEndpointReadGateTests` passed, 6/6
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter "BusinessApprovalAuthorityTests|BusinessRequestContractGuardTests|BusinessRouteGuardTests|BusinessEndpointReadGateTests|DocumentAndOpenItemMutationGateTests|PublicRequestActorContractTests|UnitySearchPermissionFilterTests|SysAdminControlPlaneGateTests"` passed, 214/214
- `git diff --check` passed with line-ending warnings only

Still open:

- migrate AR/AP aging and remaining cash-flow/report widgets to the same shared read gate
- migrate open-item, FX, inventory, and company-book mutation/readiness query endpoints in small batches
- add repository-spy endpoint tests proving blocked report reads/exports do not call `IAccountingReportRepository`
- add full HTTP-level tests once the API test project has a lightweight TestServer/WebApplicationFactory setup
- UI hide/disable behavior driven by the same operation authority model
- eventual migration from role-name convention checks to stored explicit `module + action` permission grants

### 2026-05-22 Thirty-Sixth High-Risk Repair Batch

Completed:

- migrated AR Aging read and CSV export endpoints to `BusinessEndpointReadGate`
- migrated AP Aging read and CSV export endpoints to `BusinessEndpointReadGate`
- migrated Sales Cash Flow and Income Over Time report-widget endpoints to `BusinessEndpointReadGate`
- migrated Expense Cash Outflow and Expense Over Time report-widget endpoints to `BusinessEndpointReadGate`
- added report-widget-specific regression coverage for the shared company-scoped read gate

Current report/widget behavior:

- all current Accounting API report endpoints in the Trial Balance / Income Statement / Balance Sheet / AR Aging / AP Aging family now share the same company/session/permission gate
- Sales and Expense overview widgets now use report-module permission instead of depending on their route prefix alone
- sales-only users remain blocked from report-widget data when they do not have Reports authority

Verification:

- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter BusinessEndpointReadGateTests` passed, 8/8
- `dotnet build backend/src/Citus.Accounting.Api/Citus.Accounting.Api.csproj` passed with 0 warnings and 0 errors
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter "BusinessApprovalAuthorityTests|BusinessRequestContractGuardTests|BusinessRouteGuardTests|BusinessEndpointReadGateTests|DocumentAndOpenItemMutationGateTests|PublicRequestActorContractTests|UnitySearchPermissionFilterTests|SysAdminControlPlaneGateTests"` passed, 216/216
- `git diff --check` passed with line-ending warnings only

Still open:

- migrate open-item, FX, inventory, and remaining company-book query endpoints in small batches
- add repository-spy endpoint tests proving blocked report reads/exports do not call `IAccountingReportRepository`
- add full HTTP-level tests once the API test project has a lightweight TestServer/WebApplicationFactory setup
- UI hide/disable behavior driven by the same operation authority model
- eventual migration from role-name convention checks to stored explicit `module + action` permission grants

### 2026-05-22 Thirty-Seventh High-Risk Repair Batch

Completed:

- migrated AR open-item drilldown, adjustment preview, latest adjustment request, readiness, and execution-plan query endpoints to `BusinessEndpointReadGate`
- migrated AP open-item drilldown, adjustment preview, latest adjustment request, readiness, and execution-plan query endpoints to `BusinessEndpointReadGate`
- migrated open-item adjustment account mapping lookup to `BusinessEndpointReadGate`
- added AR/AP module-boundary regression coverage for the shared company-scoped read gate

Current open-item read behavior:

- AR open-item read/preflight endpoints now share the same company/session/AR-payments authority gate
- AP open-item read/preflight endpoints now share the same company/session/AP-payments authority gate
- adjustment account mapping lookup remains under Accounting authority and active-company enforcement
- AR users are blocked from AP open-item reads, and AP users are blocked from AR adjustment previews

Verification:

- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter BusinessEndpointReadGateTests` passed, 12/12
- `dotnet build backend/src/Citus.Accounting.Api/Citus.Accounting.Api.csproj` passed with 0 warnings and 0 errors
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter "BusinessApprovalAuthorityTests|BusinessRequestContractGuardTests|BusinessRouteGuardTests|BusinessEndpointReadGateTests|DocumentAndOpenItemMutationGateTests|PublicRequestActorContractTests|UnitySearchPermissionFilterTests|SysAdminControlPlaneGateTests"` passed, 220/220
- `git diff --check` passed with line-ending warnings only

Still open:

- migrate source-document lookup/reversal, FX, inventory, and remaining company-book query endpoints in small batches
- add repository-spy endpoint tests proving blocked open-item reads do not call AR/AP repositories
- add full HTTP-level tests once the API test project has a lightweight TestServer/WebApplicationFactory setup
- UI hide/disable behavior driven by the same operation authority model
- eventual migration from role-name convention checks to stored explicit `module + action` permission grants

### 2026-05-22 Thirty-Eighth High-Risk Repair Batch

Completed:

- migrated source-document browser, lifecycle preview, lifecycle action preview, reverse-request lookup, reverse blockers, settlement application reversals, reverse apply-readiness, reverse execution-plan, and source-document review GET endpoints to `BusinessEndpointReadGate`
- added source-document reversal-plan regression coverage for the shared company-scoped read gate

High-risk issue addressed:

- business scenario: source-document review and reversal planning exposes posted document status, journal-entry linkage, settlement blockers, FX settlement data, reversal audit actors, and execution-plan readiness
- accounting impact: unauthorized or cross-company visibility into reversal/blocker data can mislead operators before void/reverse execution and expose sensitive AR/AP settlement, tax, FX, and audit details
- technical impact: these endpoints repeated company and authority checks locally, increasing drift risk across browser, lifecycle, blocker, readiness, and execution-plan views
- repair approach: centralized authenticated session, active-company match, and Accounting authority checks through `BusinessEndpointReadGate` before repository access

Current source-document read behavior:

- source-document browser/review endpoints now use one shared company/session/accounting-authority gate
- reverse-request and reverse-plan read endpoints now use the same gate before exposing blockers, settlement reversals, or execution readiness
- sales-only users are blocked from source-document reversal planning even when the requested company id matches their active company

Verification:

- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter BusinessEndpointReadGateTests` passed, 14/14
- `dotnet build backend/src/Citus.Accounting.Api/Citus.Accounting.Api.csproj` passed with 0 warnings and 0 errors
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter "BusinessApprovalAuthorityTests|BusinessRequestContractGuardTests|BusinessRouteGuardTests|BusinessEndpointReadGateTests|DocumentAndOpenItemMutationGateTests|PublicRequestActorContractTests|UnitySearchPermissionFilterTests|SysAdminControlPlaneGateTests"` passed, 222/222
- `git diff --check` passed with line-ending warnings only

Still open:

- write paths for source-document void/reverse still need a focused audit review, especially whether every persisted transition captures actor, idempotency, and transaction boundaries consistently
- migrate FX, inventory, invoice PDF download, and remaining company-book query endpoints in small batches
- add repository-spy endpoint tests proving blocked source-document reads do not call review repositories
- add full HTTP-level tests once the API test project has a lightweight TestServer/WebApplicationFactory setup
- UI hide/disable behavior driven by the same operation authority model
- eventual migration from role-name convention checks to stored explicit `module + action` permission grants

### 2026-05-22 Thirty-Ninth High-Risk Repair Batch

Completed:

- added `BusinessEndpointMutationGate` for company-scoped mutation endpoints that require business session, active-company match, operation authority, and a non-empty session actor
- migrated source-document lifecycle `void`, `reverse`, reverse-request `submit`, reverse-request `cancel`, and reverse-request `execute` endpoints to the shared mutation gate
- fixed a write-path audit precondition gap where `void source documents` did not require a concrete business actor before reaching lifecycle command handling
- added mutation-gate regression coverage for missing session, cross-company mutation, missing actor, missing module authority, and successful accounting-governance mutation

High-risk issue addressed:

- business scenario: source-document lifecycle mutations can start or transition governed void/reverse flows that ultimately affect posted documents, linked journal entries, AR/AP settlement history, and reversal audit trails
- accounting impact: lifecycle writes without a concrete actor would make later void/reverse audit trails incomplete; inconsistent mutation guards can also let users reach write handlers without the same company and module-operation boundary
- technical impact: repeated endpoint-local checks made company isolation, authority, and actor validation easy to drift between `void`, `reverse`, `submit`, `cancel`, and `execute`
- repair approach: centralized mutation gate and reused the returned session actor for reverse-request audit writes

Current source-document mutation behavior:

- selected source-document lifecycle writes now fail before repository/workflow access if there is no business session, active-company match, authorized Accounting operation, or concrete actor
- reverse request creation, submission, cancellation, and execution continue to pass the authenticated business actor into audit/reversal persistence
- void remains skeleton-only in persistence today, but now still requires an accountable actor before command handling

Verification:

- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter BusinessEndpointMutationGateTests` passed, 5/5
- `dotnet build backend/src/Citus.Accounting.Api/Citus.Accounting.Api.csproj` passed with 0 warnings and 0 errors
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter "BusinessApprovalAuthorityTests|BusinessRequestContractGuardTests|BusinessRouteGuardTests|BusinessEndpointReadGateTests|BusinessEndpointMutationGateTests|DocumentAndOpenItemMutationGateTests|PublicRequestActorContractTests|UnitySearchPermissionFilterTests|SysAdminControlPlaneGateTests"` passed, 227/227
- `git diff --check` passed with line-ending warnings only

Still open:

- source-document reverse execution still has a cross-service transaction boundary: GL journal reversal uses its own transaction while source-document completion is recorded afterward
- add idempotency / retry semantics review for reverse execution so a failure between GL reversal and completion recording cannot leave an ambiguous half-completed state
- migrate other mutation endpoints to `BusinessEndpointMutationGate` in small batches, starting with company-book governed changes and open-item adjustment transitions
- add full HTTP-level tests once the API test project has a lightweight TestServer/WebApplicationFactory setup
- eventual migration from role-name convention checks to stored explicit `module + action` permission grants

### 2026-05-22 Fortieth High-Risk Repair Batch

Completed:

- added an inventory-adjustment journal plan for Gain, Loss, and approved Write-off accounting directions
- updated `PostgreSqlInventoryAdjustmentStore` so posted inventory adjustments and approved write-offs create GL journal entries and ledger rows in the same PostgreSQL transaction as inventory documents, inventory ledger entries, cost layers, and layer consumptions
- added company-scoped active-account resolution for inventory asset and inventory adjustment/write-off accounts before inventory quantity/cost mutations proceed
- added posting-period governance checks before inventory adjustments can affect inventory or GL
- added audit-log writes for posted inventory adjustments and approved write-offs when the shared audit table is available
- added regression tests locking the debit/credit direction for Inventory Adjustment Gain, Inventory Adjustment Loss, and Inventory Write-off

High-risk issue addressed:

- business scenario: cycle-count gains/losses and approved write-offs changed inventory quantity and cost layers but did not create matching GL entries
- accounting impact: inventory subledger value could diverge from GL Inventory Asset, and write-off losses/gains would not appear in the ledger or reports
- technical impact: stock movement, cost-layer consumption, and document status could commit without a journal source record, weakening reconciliation and audit traceability
- repair approach: generate balanced base-currency journal lines from the same computed inventory cost, resolve accounts within the same company, and commit stock plus GL atomically

Current inventory adjustment GL behavior:

- Gain: Dr Inventory Asset, Cr Inventory Adjustment offset
- Loss: Dr Inventory Adjustment, Cr Inventory Asset
- Approved Write-off: Dr item write-off account when configured, otherwise Inventory Adjustment; Cr Inventory Asset
- zero-value quantity adjustments do not create zero-amount GL lines, but still keep inventory quantity/cost movement in the inventory subledger
- journal entries use source type `inventory_adjustment_gain`, `inventory_adjustment_loss`, or `inventory_write_off` with the inventory document id as source id

Verification:

- `dotnet build backend/src/Infrastructure/PostgreSQL/Infrastructure.PostgreSQL.csproj` passed with 0 warnings and 0 errors
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj --filter InventoryAdjustmentJournalPlanTests` passed, 4/4
- `dotnet build backend/src/Citus.Accounting.Api/Citus.Accounting.Api.csproj` passed with 0 warnings and 0 errors
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj` passed, 433/433
- `git diff --check` passed with line-ending warnings only

Still open:

- inventory adjustment GL posting is direct SQL inside the inventory store; a later architecture pass should move it behind the common posting engine once source status claiming can share the same transaction boundary
- zero-value inventory quantity adjustments still need product-owner confirmation for reporting treatment, since they have no monetary GL impact
- manufacturing and warehouse transfer GL remain intentionally out of this batch because WIP, Finished Goods, Raw Materials, In-Transit accounts, migrations, backfill, and tests are not yet validated
- add database-backed integration tests that verify `journal_entries`, `journal_entry_lines`, `ledger_entries`, `inventory_ledger_entries`, and `inventory_layer_consumptions` commit/rollback together
- add UI surfacing for the created journal entry link from inventory adjustment/write-off detail pages

### 2026-05-23 Forty-First High-Risk Repair Batch

Completed:

- hardened GL journal-entry reversal retry behavior for source-document reverse execution
- updated `PostgreSqlJournalEntryLifecycleStore` so a retry returns the existing compensation JE when the original JE is already `reversed` or `voided` and the matching compensation JE already exists
- added a GL smoke regression test that reverses a journal entry twice and verifies only one reversal JE exists

High-risk issue addressed:

- business scenario: governed source-document reverse execution records `execution_requested`, runs linked GL journal-entry reversal, then records source-document completion
- accounting impact: if the process failed after the GL reversal but before source-document completion, a retry could previously fail because the original JE was no longer `posted`; the source document could remain unreversed while the GL already had a reversal
- technical impact: the split transaction boundary between GL reversal and source-document completion had no idempotent recovery path at the GL lifecycle layer
- repair approach: keep normal posted-JE lifecycle rules, but allow idempotent return of the existing compensation JE when the requested lifecycle state and compensation source already match

Current reverse retry behavior:

- first reverse still creates the compensation JE and marks the original JE reversed
- retry after that returns the existing compensation JE instead of creating a duplicate or failing on original status
- the API reverse execution path can now call `CompleteReverseRequestExecutionAsync` after a retry and finish source-document status/audit completion using the existing compensation JE details

Verification:

- `dotnet build backend/src/Infrastructure/PostgreSQL/Infrastructure.PostgreSQL.csproj` passed with 0 warnings and 0 errors
- `dotnet build backend/src/Citus.Accounting.Api/Citus.Accounting.Api.csproj` passed with 0 warnings and 0 errors
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj` passed, 433/433
- `dotnet test backend/src/Tests/GL/Tests.GL.csproj --filter JournalEntryLifecycleSmokeTests` could not be environment-verified because local PostgreSQL at `127.0.0.1:5432` refused the connection

Still open:

- source-document reverse execution still has two physical transaction scopes; this batch adds retry recovery but does not make GL reversal and source completion a single database transaction
- run the new GL smoke regression with PostgreSQL available to confirm the database path end to end
- add an API-level reverse-execution retry test that simulates GL success followed by completion failure, then verifies a second request completes source status without duplicate reversal JE
- consider moving source-document completion into the same orchestration boundary as GL lifecycle once the shared transaction/unit-of-work pattern is ready for cross-module source documents

### 2026-05-23 Forty-Second High-Risk Repair Batch

Completed:

- made source-document reversed status marking idempotent during governed reverse execution completion
- updated `MarkSourceDocumentReversedAsync` so an already-`reversed` source document in the same company no longer blocks completion audit recording
- added an AR smoke scenario where the source invoice is already marked `reversed` before `CompleteReverseRequestExecutionAsync` runs

High-risk issue addressed:

- business scenario: reverse execution can fail after the source document status is changed to `reversed` but before the reverse request records `reverse_execution_completed`
- accounting impact: GL may already be reversed and the source document may already be historical-only, but audit/completion state can remain `execution_requested`; previously a retry would fail instead of completing the audit trail
- technical impact: the completion step was not idempotent at the source-status mutation point, leaving a narrow but serious half-completed state in a split transaction workflow
- repair approach: keep the same company/document guard, preserve existing status behavior for non-reversed documents, and treat an already-reversed source document as successful for retry completion

Current completion retry behavior:

- open-item voiding remains idempotent because already-voided rows are skipped
- source status update now succeeds when the source is already `reversed`
- completion audit can be appended after a retry, allowing the reverse request to move to `journal_entry_reversed`

Verification:

- `dotnet build backend/src/Citus.Accounting.Infrastructure/Citus.Accounting.Infrastructure.csproj` passed with 0 warnings and 0 errors
- `dotnet build backend/src/Tests/AR/Tests.AR.csproj` passed with 0 warnings and 0 errors
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj` passed, 433/433
- `dotnet test backend/src/Tests/AR/Tests.AR.csproj --filter CompleteReverseRequestExecutionAsync_CompletesWhenSourceAlreadyMarkedReversed` could not be environment-verified because local PostgreSQL at `127.0.0.1:5432` refused the connection

Still open:

- the AR smoke test should be rerun with PostgreSQL available to validate the full persistence path
- settlement unapply replay remains sensitive: if failure occurs after deleting settlement applications but before completion audit, a retry sees no applications to unapply and records zero unapplied rows in the completion payload; preserve separate settlement reversal audit rows or add an aggregate recovery reader
- add an end-to-end API retry test that chains GL idempotent reversal and source completion idempotency together
- long-term fix remains a shared orchestration transaction/outbox for GL reversal plus source-document completion

### 2026-05-23 Forty-Third High-Risk Repair Batch

Completed:

- made governed source-document reverse completion internally transactional
- updated `PostgresCommandScope` with an explicit transactional scope that commits only after all completion mutations succeed and rolls back otherwise
- changed `CompleteReverseRequestExecutionAsync` so settlement unapply, source open-item voiding, source status reversal, and `reverse_execution_completed` audit transition share one PostgreSQL transaction
- added settlement-unapply completion recovery from existing `settlement_application_reversal` audit rows when live settlement applications were already deleted by a previous partial run
- added an AR PostgreSQL smoke regression for the already-deleted settlement application recovery path

High-risk issue addressed:

- business scenario: receive payment / credit application / pay bill / vendor credit application reversal completion updates target AR/AP open items, deletes settlement applications, marks the source reversed, and records completion audit
- accounting impact: without one transaction, a failure between those statements could over-open AR/AP balances on retry, understate unapplied settlement totals, or leave a reversed source without a matching completion event
- technical impact: previously each command could autocommit when no ambient execution context was present, creating partial-success states inside one accounting workflow
- repair approach: keep the existing GL reversal boundary separate, but make the source-document completion phase atomic and add audit-based recovery for older or already-partial settlement unapply states

Current completion behavior:

- if any completion mutation fails before commit, settlement application rows, open-item balances, source status, and completion audit roll back together
- if settlement applications are already gone but their per-application reversal audit exists for the same company, request, source type, and source id, completion payload recovers the unapplied count and amounts from audit
- company isolation remains enforced through every affected query by `company_id`

Verification:

- `dotnet build backend/src/Citus.Accounting.Infrastructure/Citus.Accounting.Infrastructure.csproj` passed with 0 warnings and 0 errors
- `dotnet build backend/src/Tests/AR/Tests.AR.csproj` passed with 0 warnings and 0 errors
- `dotnet build backend/src/Tests/AP/Tests.AP.csproj` passed with 0 warnings and 0 errors
- `dotnet test backend/tests/Citus.Accounting.Api.Tests/Citus.Accounting.Api.Tests.csproj` passed, 433/433
- `dotnet test backend/src/Tests/AR/Tests.AR.csproj --filter FullyQualifiedName~CompleteReverseRequestExecutionAsync_UnappliesPostedReceivePaymentBeforeMarkingReversed` could not be environment-verified because local PostgreSQL at `127.0.0.1:5432` refused the connection
- `dotnet test backend/src/Tests/AR/Tests.AR.csproj --filter FullyQualifiedName~CompleteReverseRequestExecutionAsync_RecoversSettlementUnapplySummaryWhenApplicationsAlreadyDeleted` could not be environment-verified because local PostgreSQL at `127.0.0.1:5432` refused the connection

Still open:

- GL journal-entry reversal and source-document completion still remain two physical orchestration phases; this batch makes the second phase atomic but does not merge the GL phase
- run AR/AP PostgreSQL smoke tests with PostgreSQL available to verify transaction rollback and audit recovery end to end
- add a true failure-injection integration test that aborts inside the transaction and verifies rollback, in addition to the newly added recovery smoke test for legacy partial state
- consider an outbox/orchestration table for cross-service GL reversal plus source completion so recovery can be explicit instead of inferred from lifecycle state
