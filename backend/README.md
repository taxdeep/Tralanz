# Citus Backend

This directory contains the PostgreSQL-backed .NET backend slice for the Citus platform core and accounting module.

Current layout:

- `src/Citus.Accounting.Domain`
- `src/Citus.Accounting.Application`
- `src/Citus.Accounting.Infrastructure`
- `src/Citus.Accounting.Api`
- `src/Citus.ConsoleApp`
- `src/Citus.Platform.Core`
- `src/Citus.Platform.Infrastructure`
- `src/Citus.SysAdmin.Api`
- `tests/Citus.Accounting.Domain.Tests`
- `tests/Citus.Accounting.Application.Tests`
- `tests/Citus.Accounting.IntegrationTests`
- `tests/Citus.Platform.Core.Tests`

Notes:

- The current machine resolves correctly with `C:\Program Files\dotnet\dotnet.exe`, and the backend solution now builds/tests from that 64-bit host.
- The backend now targets `.NET 11 + C# 15` through `backend/Directory.Build.props`, so local build/test/run no longer depends on `DOTNET_ROLL_FORWARD=Major`.
- `LangVersion` is intentionally pinned to `preview` so the repository baseline stays aligned with the current `.NET 11 / C# 15` direction.
- Cache direction follows the authority baseline: `Phase 1 = HybridCache / ABP Distributed Cache` for config, dictionaries, and low-frequency master data; `Phase 2 = Redis` for high-frequency read paths such as SmartPicker and report acceleration, with `Tag-based Eviction` as a first-class invalidation strategy. Cache keys must include at least `company_id`, and when ABP multi-tenancy is enabled they must include both `tenant_id` and `company_id`.
- Ubuntu 24.04 deployment now installs `.NET 11 preview` through the official `dotnet-install.sh` flow into `/opt/dotnet`, publishes `Web.Shell` as the primary UI, and supports one-command domain/SSL setup through `sudo ./install.sh --domain app.example.com --ssl --email ops@example.com`.
- `Web.Shell` now expects `PlatformIdentity:TotpProtectionKey` to be configured for protected TOTP-secret storage. The checked-in `appsettings.json` value is a development placeholder only and must be replaced by environment-specific secret configuration outside local development.
- You can start and stop the API from any directory with `D:\Coding\Citus\backend\start-accounting-api.ps1` and `D:\Coding\Citus\backend\stop-accounting-api.ps1`.
- `Citus.Accounting.Api` now wires the manual-journal, invoice, credit-note, bill, vendor-credit, receive-payment, credit-application, pay-bill, vendor-credit-application, and FX revaluation paths against PostgreSQL.
- `Citus.Platform.Core` now provides a WebVella-inspired platform kernel for module registration and entity metadata governance.
- `Citus.SysAdmin.Api` now boots and governs the platform kernel through PostgreSQL-backed metadata tables.
- `Citus.ConsoleApp` now provides a local operator control surface for the same kernel, inspired by `WebVella.Erp.ConsoleApp`.
- The current slice reads `manual_journal_documents`, `invoices`, `credit_notes`, `bills`, `vendor_credits`, `receive_payments`, `credit_applications`, `pay_bills`, `vendor_credit_applications`, and `fx_revaluation_batches`, resolves FX snapshots from `company_fx_rate_snapshots` when needed, and writes `journal_entries`, `journal_entry_lines`, `ledger_entries`, `ar_open_items`, `ap_open_items`, `settlement_applications`, and `fx_revaluation_batch_lines`.
- Posted invoices and credit notes create `ar_open_items`, posted bills and vendor credits create `ap_open_items`, and posted settlements update open-item balances through `settlement_applications`.
- Foreign-currency receive-payment/pay-bill posting now uses the source document FX snapshot as the authoritative settlement rate, writes realized FX to dedicated gain/loss accounts, and reduces open-item carrying base separately from settlement base.
- Receive-payment and pay-bill draft preparation now has dedicated backend endpoints for listing open receivables/payables by party and inserting draft source documents into `receive_payments` / `pay_bills` before the normal posting path runs.
- FX revaluation now supports period-end draft preparation by currency, unrealized FX posting through the Posting Engine, carrying-base updates back onto foreign-currency open items, explicit rate metadata (`rate_type`, `quote_basis`, `rate_use_case`, `posting_reason`) on the prepared batch, and next-period unwind drafts that reverse a posted batch through the same engine path.
- Company governance now seeds a default `PRIMARY` book plus a governed remeasurement policy when none exists yet, and FX revaluation batches now carry `company_book_id`, `book_code`, `accounting_standard`, `revaluation_profile`, and `fx_rounding_policy` so the batch keeps its book-policy context.
- Company book governance now also recognizes formal governance signals (`closed_period`, `reported_statement`, `filed_tax`) and uses them to escalate change previews and apply-readiness checks toward `new_secondary_or_adjustment_book` when historical locks already exist.
- Company book governance signals can now be registered through backend-owned endpoints, so closed periods, issued statements, and filed-tax locks no longer need to be side-loaded before governance preview and readiness checks can enforce them.
- `Web.Shell` now exposes a minimal `/company/book-governance` surface for reviewing active books, current policy truth, and book-governing lock registration. The shell now tries to hydrate its active company context from PostgreSQL-backed `companies` + `company_memberships` truth through `CompanyAccess`; if that membership context is unavailable, it falls back to the configured `AppHost` companies and still reloads governance data when the operator switches company in-page.
- `Web.Shell` now also has a local business-session header pipeline for future API-backed pages: it sends `X-Citus-User-Id` plus `X-Citus-Active-Company-Id` from the current shell context and probes `/accounting/session/context` so the shell can show when API session configuration still disagrees with CompanyAccess membership truth.
- `Web.Shell` now also exposes draft editors for `Invoice`, `Credit Note`, `Bill`, and `Vendor Credit`, and those pages use real party/account/tax-code lookups plus the new draft save endpoints instead of demo-only placeholders. Saved drafts can now be reopened from the source browser or document detail page, continue editing through richer draft-read endpoints, and post directly into the existing document-detail plus journal-entry review flow.
- Source-document draft lifecycle now also has explicit smoke coverage for the `posted -> update rejected` guard on `Invoice / Credit Note / Bill / Vendor Credit`, so editor continuity does not silently weaken backend authority after posting.
- Source-document detail now also exposes journal-entry review continuity when a linked JE exists, so posted `Invoice / Bill / Credit Note / Vendor Credit` no longer rely on the editor page alone to reach accounting review.
- Source-document detail now also surfaces linked journal-entry lifecycle truth (`posted / voided / reversed` plus timestamps), and the shell warns explicitly when the linked JE has already been voided or reversed.
- Source-document review now also carries a source-owned lifecycle summary (`LifecycleMode`, `CanEditDraft`, `CanPostDraft`, `LifecycleReason`) so shell surfaces can present governed next-step boundaries without inventing source-document void/reverse commands ahead of the domain rules.
- Source-document review now also carries per-action lifecycle preview rows (`Edit Draft / Post Draft / Reopen / Void / Reverse`) so the shell can show which next steps are available, blocked by status, blocked by linked JE lifecycle, or not implemented yet.
- Source-document lifecycle preview is now also available as its own backend contract, separate from full document review, so future source-owned lifecycle commands can bind to a dedicated preview endpoint instead of reusing line-detail payloads.
- Source-document lifecycle preview now also supports action-specific backend contracts for `edit_draft / post_draft / reopen_document / void_document / reverse_document`, so later write commands can bind to one action preview at a time.
- Source-document lifecycle now also has a first write-shaped `void` command skeleton: it reuses backend-owned lifecycle truth, returns `blocked` vs `not_implemented` explicitly, and still refuses to fake a historical rewrite before true void persistence exists.
- Source-document lifecycle now also has a persisted `reverse` request entrypoint: legal reverse attempts are written into `audit_logs` as governed requests, while blocked attempts are still rejected without mutating accounting truth.
- Persisted source-document reverse requests now have an append-only status flow in the backend audit trail: `draft -> submitted -> cancelled`, plus an `apply-readiness` read model that stays honest about the remaining orchestration boundary.
- Governed reverse requests now also have an `execute` path: once a request is submitted and governance still allows it, the backend records `execution_requested`, then can complete step 4 by reversing the linked journal entry and appending `reverse_execution_completed`.
- That execution skeleton now blocks explicitly on AR/AP settlement/application trail, so source documents with applied subledger history do not get a false ready-to-reverse story.
- The backend now also exposes an `execution-plan` read model for governed reverse requests, so the minimal orchestration order is explicit: submit request, pass governance readiness, clear subledger truth blockers, reverse the linked JE, then mark the source document reversed.
- Step 5 now has a minimal safe implementation for `invoice / credit_note / bill / vendor_credit`: once linked JE reversal completes, source open items with no settlement/application trail are voided and the source document is marked `reversed`; richer settlement-application reversal remains a later orchestration layer.
- Settlement-document reverse execution now has a first governed unapply skeleton for `receive_payment / pay_bill / credit_application / vendor_credit_application`: completion restores target open-item balances, writes a `settlement_application_reversal` audit/event trail for each unapplied row, removes the current operational settlement application rows, reverses the linked JE through GL lifecycle, marks the settlement source `reversed`, and records the unapply count/totals in the reverse-request audit payload. AR/AP smoke coverage now exercises all four paths.
- The backend now exposes `GET /accounting/source-document-lifecycle/{sourceType}/{documentId}/settlement-application-reversals`, a read model over the governed unapply audit trail. `Web.Shell` source-document detail reads it together with the latest reverse request, showing request status, execution status, compensation JE references, transition timestamps, and the settlement applications that were unapplied.
- `Web.Shell` source-document detail now also has guarded command buttons for the governed reverse flow: request reverse, submit reverse request, and execute governed reverse. The buttons call the backend lifecycle endpoints with the shell business-session headers; the page then reloads backend truth instead of mutating local UI state.
- `Web.Shell` source-document detail now also reads backend reverse `apply-readiness` and `execution-plan` contracts, showing governance readiness, execution mode, step order, and backend block reasons before the operator triggers governed execution.
- Source-document reverse blockers now have a product-facing drill-down contract: `GET /accounting/source-document-lifecycle/{sourceType}/{documentId}/reverse-blockers` lists the settlement/application rows that block source reversal, including the blocking settlement source document, target open item, applied amounts, FX fields, and timestamps. `Web.Shell` renders those blockers with document/open-item links instead of leaving operators with only a generic blocked message.
- The blocker panel now also starts the governed subledger-unapply path by requesting reversal on the blocking settlement source document. This still goes through the existing backend reverse-request contract and does not let the UI delete or mutate `settlement_applications` directly.
- Reverse blocker rows now also include the blocking settlement source's latest reverse-request status and execution status (`not_requested / draft / submitted / execution_requested / journal_entry_reversed`), so operators can see whether the blocker still needs request, submit, or execute work before revisiting the original source reversal.
- Reverse blocker rows with a `draft` blocking-source request can now submit that governed unapply request directly from the original source-document detail. This still calls the backend lifecycle endpoint for the blocking settlement source and keeps subledger mutation out of the UI.
- Reverse blocker rows with a `submitted` blocking-source request can now request governed execution directly from the original source-document detail. The button calls the backend execute endpoint for the blocking settlement source and still leaves actual unapply, open-item restoration, and audit trail writes under backend authority.
- Reverse blocker rows now also inline the blocking settlement source's backend readiness and execution-plan summaries when a reverse request exists, so operators can see governance readiness, step counts, blocked steps, and backend reason without leaving the original source-document detail.
- Source-document detail now also shows an explicit `Subledger Gate` card for original `invoice / credit_note / bill / vendor_credit` reversals. When the backend returns no active settlement/application blockers and the original reverse request is executable, the shell surfaces an `Execute Original Governed Reverse` action that still calls the backend governed execution endpoint.
- AR/AP smoke coverage now also verifies the full operator-guided reversal chain for `invoice -> receive_payment` and `bill -> pay_bill`: original source reversal blocks on subledger truth, blocking settlement documents are governed-unapplied, blockers disappear, the original execution plan becomes executable, and the original source can then complete governed reversal.
- That guided reversal coverage now also includes the credit-side chains `credit_note -> credit_application` and `vendor_credit -> vendor_credit_application`, including source credit open-item blockers, governed application unapply, blocker clearing, and final source reversal.
- AR/AP open items now expose a backend-owned adjustment governance skeleton: `GET /accounting/open-items/{ar|ap}/{id}/adjustment-preview` and `POST /accounting/open-items/{ar|ap}/{id}/adjustment-request` validate active company scope, source/open-item status, open balance, and application-trail visibility before recording an auditable request.
- Open-item adjustment requests now also have a minimal append-only status flow: latest request lookup plus `submit` and `cancel` transitions are persisted in `audit_logs`, while draft/submitted state still leaves open-item and accounting truth unchanged.
- Open-item adjustment requests now expose backend-owned `readiness` and `execution-plan` read models for both AR and AP. They re-check current open-item truth after submission and only become executable when the backend can build a governed Posting Engine source document.
- AR/AP write-off and small-balance adjustment now has a minimal Posting Engine-backed execution loop: submitted requests post `ar_open_item_adjustment` / `ap_open_item_adjustment` journal entries through the normal engine path, reduce or close the target open item, append audit completion, and keep the request/source linkage visible without adding UI-owned accounting mutation.
- `Web.Shell` open-item detail now surfaces that adjustment control layer from the drill-down page: preview, latest request, submit/cancel, readiness, execution plan, adjustment account selection, and execute all call backend endpoints and reload backend truth afterward.
- Open-item adjustment offset accounts now have a first governed account guard: the shell picker only offers active manual-posting revenue / cost-of-sales / expense accounts, and backend execution rejects asset, liability, equity, bank, AR/AP control, tax, or other non-adjustment accounts even if the API is called directly.
- Open-item adjustment offset accounts now also have a formal company/book-aware mapping schema, management API, and Shell management UI. The accounting API initializes `open_item_adjustment_account_mappings`, owner/book-governance sessions can save or deactivate active mappings through backend endpoints, the Shell page manages company/default or book-specific mappings, and execution must use a mapped account once a company/book policy scope exists; otherwise the backend keeps the structural account guard as a compatibility fallback.
- The Shell mapping management page now also has product-grade search and policy polish: loaded mappings can be searched by scope/account/status, eligible offset accounts can be searched before save, and the page shows active/company-default/book-specific/inactive policy counts plus a policy-preview note without taking ownership of accounting truth.
- The Shell mapping management page now also compares policy coverage by company default, Primary Book, and non-primary book scopes across AR/AP write-off and small-balance cells. The page is explicit that current execution evaluates company-default plus Primary Book mappings only, so non-primary book rows remain governance visibility rather than execution truth.
- Open-item adjustment requests now support partial requested amounts through the backend contract. When `adjustmentAmountTx` is omitted, the request defaults to the current open balance; when a smaller legal amount is supplied, execution posts only that amount and leaves the remaining open-item balance intact.
- Open-item adjustment governance now has a temporary backend-owned approval threshold plus append-only `approve` / `reject` transitions. Requests above the threshold persist as requiring approval; readiness and execution-plan block with `blocked_by_approval_required` until approval is recorded, and rejection keeps execution blocked without touching open-item or accounting truth.
- Open-item adjustment approval now also has a first authority guard: approval/rejection requires a concrete user actor, blocks requester self-approval, and the API approval endpoints require an `owner` or book-governance session role before entering the repository transition. This is CompanyAccess-aligned and remains replaceable by full ABP Permission Management.
- Accounting API session guarding now has a first persisted CompanyAccess bridge: `company_memberships.permissions` tokens are lifted into session roles, route guarding prefers PostgreSQL-backed membership truth before static configuration, and the default demo directory no longer silently authorizes requests when persisted membership is enabled but no active membership exists.
- CompanyAccess now has a first owner-governed membership permission management surface in `Web.Shell` at `/company/membership-permissions`. The page lists current company memberships, saves catalog-approved permission tokens to `company_memberships.permissions`, and feeds the existing session-role bridge used by Accounting API authority checks.
- CompanyAccess membership permission saves now append `membership_permissions_saved` business audit events in `audit_logs` inside the same PostgreSQL transaction as the permission update. The payload records actor, target membership/user, previous tokens, saved tokens, added tokens, and removed tokens.
- `/company/membership-permissions` now also surfaces recent grant audit drill-down from CompanyAccess: actor, target user, timestamp, added tokens, removed tokens, and resulting permission set are read from `membership_permissions_saved` audit events instead of inferred by the UI.
- FX revaluation cascade unwind now exposes a plan endpoint and a prepare-next-step endpoint, so an older batch can ask the system which active descendant must unwind first.
- FX revaluation auto-post cascade unwind now allows the backend to prepare and post every required unwind step from the active chain tail back to the requested batch in one request.

## Implemented backend flow

The first migrated flows are:

1. Load a manual journal draft from PostgreSQL.
2. Validate it in the Posting Engine.
3. Resolve FX locally from stored company snapshots only.
4. Aggregate posted journal lines.
5. Persist:
   - `journal_entries`
   - `journal_entry_lines`
   - `ledger_entries`
6. Mark the source manual journal document as posted.

Invoice posting follows the same engine path and then creates a debit-balance `ar_open_items` row for the posted document balance.

Credit Note posting follows the same engine path, reverses revenue/payable-tax fragments correctly, and then creates a credit-balance `ar_open_items` row for the posted document balance.

Bill posting follows the same engine path, routes recoverable vs non-recoverable purchase tax correctly, and then creates a credit-balance `ap_open_items` row for the posted payable balance.

Vendor Credit posting follows the same engine path, reverses expense/recoverable-tax fragments correctly, and then creates a debit-balance `ap_open_items` row for the posted document balance.

Receive Payment and Pay Bill now follow the same source-document -> Posting Engine -> JE -> settlement application pattern.
Receive Payment and Pay Bill draft preparation can now reserve payment numbers, stamp the correct local FX snapshot for the payment date, and persist draft headers/lines directly into PostgreSQL from API requests.
Credit Application and Vendor Credit Application now follow the same source-document -> Posting Engine -> JE -> mirrored settlement application pattern. Each application line reduces both the source credit open item and the target invoice/bill open item, and foreign-currency carrying deltas are posted to realized FX gain/loss accounts.
FX Revaluation now follows prepare draft -> review batch -> post JE -> update open-item carrying base -> prepare next-period unwind -> post unwind JE -> restore prior carrying base, and it now keys unrealized gain/loss behavior off the open item's stored `balance_side` so customer credit notes and vendor credits revalue correctly.

Current limitation:
- foreign-currency settlement requires the source payment document to carry its own FX snapshot/rate and the company chart to expose active `realized_fx_gain` and `realized_fx_loss` accounts through `accounts.system_role` or `accounts.system_key`.
- FX revaluation requires active `unrealized_fx_gain` and `unrealized_fx_loss` accounts through `accounts.system_role` or `accounts.system_key`.
- current revaluation scope is limited to open foreign-currency AR/AP items; next-period unwind now supports post-revaluation partial settlement by replaying settlement applications against the source batch, but it still requires tail-first unwind when a later posted revaluation remains active on the same open item. A direct unwind of an older batch is rejected until newer revaluation descendants have been unwound.
- auto-post cascade unwind currently runs the unwind stack inside one backend request and one unit-of-work; if any step fails, the full stack rolls back instead of leaving a partial unwind.
- test coverage is still narrow; the current `dotnet test` pass now exercises posting-fragment behavior, unwind-chain selection, and cascade orchestration, but it is not yet end-to-end PostgreSQL coverage.

## API endpoints

Platform core / sysadmin:

- `GET /core`
- `POST /core/bootstrap`
- `GET /core/modules`
- `GET /core/entities`
- `GET /core/entities/{name}`
- `POST /core/entities`

Console app:

- `dotnet run --project src/Citus.ConsoleApp/Citus.ConsoleApp.csproj -- help`
- `dotnet run --project src/Citus.ConsoleApp/Citus.ConsoleApp.csproj -- describe-webvella`
- `dotnet run --project src/Citus.ConsoleApp/Citus.ConsoleApp.csproj -- bootstrap-core`
- `dotnet run --project src/Citus.ConsoleApp/Citus.ConsoleApp.csproj -- list-modules`
- `dotnet run --project src/Citus.ConsoleApp/Citus.ConsoleApp.csproj -- list-entities accounting`
- `dotnet run --project src/Citus.ConsoleApp/Citus.ConsoleApp.csproj -- show-entity journal_entries`

Accounting:

- `GET /accounting/manual-journals/{documentId}?companyId={companyId}`
- `POST /accounting/manual-journals/{documentId}/post`
- `GET /accounting/invoices/{documentId}?companyId={companyId}`
- `GET /accounting/invoices/drafts/{documentId}?companyId={companyId}`
- `POST /accounting/invoices/drafts`
- `PUT /accounting/invoices/drafts/{documentId}`
- `POST /accounting/invoices/{documentId}/post`
- `GET /accounting/credit-notes/{documentId}?companyId={companyId}`
- `GET /accounting/credit-notes/drafts/{documentId}?companyId={companyId}`
- `POST /accounting/credit-notes/drafts`
- `PUT /accounting/credit-notes/drafts/{documentId}`
- `POST /accounting/credit-notes/{documentId}/post`
- `GET /accounting/bills/{documentId}?companyId={companyId}`
- `GET /accounting/bills/drafts/{documentId}?companyId={companyId}`
- `POST /accounting/bills/drafts`
- `PUT /accounting/bills/drafts/{documentId}`
- `POST /accounting/bills/{documentId}/post`
- `GET /accounting/vendor-credits/{documentId}?companyId={companyId}`
- `GET /accounting/vendor-credits/drafts/{documentId}?companyId={companyId}`
- `POST /accounting/vendor-credits/drafts`
- `PUT /accounting/vendor-credits/drafts/{documentId}`
- `POST /accounting/vendor-credits/{documentId}/post`
- `GET /accounting/source-document-lifecycle/{sourceType}/{documentId}?companyId={companyId}`
- `GET /accounting/source-document-lifecycle/{sourceType}/{documentId}/actions/{actionCode}?companyId={companyId}`
- `POST /accounting/source-document-lifecycle/{sourceType}/{documentId}/void?companyId={companyId}`
- `POST /accounting/source-document-lifecycle/{sourceType}/{documentId}/reverse?companyId={companyId}`
- `GET /accounting/source-document-lifecycle/{sourceType}/{documentId}/reverse-request?companyId={companyId}`
- `POST /accounting/source-document-lifecycle/{sourceType}/{documentId}/reverse-request/{requestId}/submit?companyId={companyId}`
- `POST /accounting/source-document-lifecycle/{sourceType}/{documentId}/reverse-request/{requestId}/cancel?companyId={companyId}`
- `GET /accounting/source-document-lifecycle/{sourceType}/{documentId}/reverse-request/{requestId}/apply-readiness?companyId={companyId}&asOfDate={yyyy-MM-dd}`
- `POST /accounting/source-document-lifecycle/{sourceType}/{documentId}/reverse-request/{requestId}/execute?companyId={companyId}&asOfDate={yyyy-MM-dd}`
- `GET /accounting/source-document-lifecycle/{sourceType}/{documentId}/reverse-request/{requestId}/execution-plan?companyId={companyId}&asOfDate={yyyy-MM-dd}`
- `GET /accounting/source-document-lifecycle/{sourceType}/{documentId}/reverse-blockers?companyId={companyId}`
- `GET /accounting/source-document-lifecycle/{sourceType}/{documentId}/settlement-application-reversals?companyId={companyId}`
- `GET /accounting/customers/{customerId}/open-receivables?companyId={companyId}`
- `POST /accounting/receive-payments/prepare`
- `GET /accounting/receive-payments/{documentId}?companyId={companyId}`
- `POST /accounting/receive-payments/{documentId}/post`
- `GET /accounting/credit-applications/{documentId}?companyId={companyId}`
- `POST /accounting/credit-applications/{documentId}/post`
- `GET /accounting/vendors/{vendorId}/open-payables?companyId={companyId}`
- `POST /accounting/pay-bills/prepare`
- `GET /accounting/pay-bills/{documentId}?companyId={companyId}`
- `POST /accounting/pay-bills/{documentId}/post`
- `GET /accounting/vendor-credit-applications/{documentId}?companyId={companyId}`
- `POST /accounting/vendor-credit-applications/{documentId}/post`
- `GET /accounting/open-items/ar/{openItemId}/adjustment-preview?companyId={companyId}&adjustmentAmountTx={amount?}`
- `POST /accounting/open-items/ar/{openItemId}/adjustment-request`
- `GET /accounting/open-items/ar/{openItemId}/adjustment-request?companyId={companyId}`
- `POST /accounting/open-items/ar/{openItemId}/adjustment-request/{requestId}/submit`
- `POST /accounting/open-items/ar/{openItemId}/adjustment-request/{requestId}/cancel`
- `POST /accounting/open-items/ar/{openItemId}/adjustment-request/{requestId}/approve`
- `POST /accounting/open-items/ar/{openItemId}/adjustment-request/{requestId}/reject`
- `GET /accounting/open-items/ar/{openItemId}/adjustment-request/{requestId}/readiness?companyId={companyId}&asOfDate={yyyy-MM-dd}`
- `GET /accounting/open-items/ar/{openItemId}/adjustment-request/{requestId}/execution-plan?companyId={companyId}&asOfDate={yyyy-MM-dd}`
- `POST /accounting/open-items/ar/{openItemId}/adjustment-request/{requestId}/execute`
- `GET /accounting/open-items/ap/{openItemId}/adjustment-preview?companyId={companyId}&adjustmentAmountTx={amount?}`
- `POST /accounting/open-items/ap/{openItemId}/adjustment-request`
- `GET /accounting/open-items/ap/{openItemId}/adjustment-request?companyId={companyId}`
- `POST /accounting/open-items/ap/{openItemId}/adjustment-request/{requestId}/submit`
- `POST /accounting/open-items/ap/{openItemId}/adjustment-request/{requestId}/cancel`
- `POST /accounting/open-items/ap/{openItemId}/adjustment-request/{requestId}/approve`
- `POST /accounting/open-items/ap/{openItemId}/adjustment-request/{requestId}/reject`
- `GET /accounting/open-items/ap/{openItemId}/adjustment-request/{requestId}/readiness?companyId={companyId}&asOfDate={yyyy-MM-dd}`
- `GET /accounting/open-items/ap/{openItemId}/adjustment-request/{requestId}/execution-plan?companyId={companyId}&asOfDate={yyyy-MM-dd}`
- `POST /accounting/open-items/ap/{openItemId}/adjustment-request/{requestId}/execute`
- `POST /accounting/fx-revaluation-batches/prepare`
- `POST /accounting/fx-revaluation-batches/{documentId}/prepare-next-period-unwind`
- `GET /accounting/fx-revaluation-batches/{documentId}/cascade-unwind-plan?companyId={companyId}`
- `POST /accounting/fx-revaluation-batches/{documentId}/prepare-cascade-unwind`
- `POST /accounting/fx-revaluation-batches/{documentId}/auto-post-cascade-unwind`
- `GET /accounting/company-books?companyId={companyId}&asOfDate={yyyy-MM-dd?}`
- `GET /accounting/company-books/{bookId}/governance-signals?companyId={companyId}&asOfDate={yyyy-MM-dd?}`
- `POST /accounting/company-books/{bookId}/governance-signals`
- `POST /accounting/company-books/{bookId}/close-periods`
- `POST /accounting/company-books/{bookId}/issued-statements`
- `POST /accounting/company-books/{bookId}/filed-tax`
- `POST /accounting/company-books/governed-change-preview`
- `POST /accounting/company-books/governed-change-requests/prepare`
- `GET /accounting/company-books/governed-change-requests?companyId={companyId}`
- `POST /accounting/company-books/governed-change-requests/{requestId}/submit`
- `POST /accounting/company-books/governed-change-requests/{requestId}/cancel`
- `GET /accounting/company-books/governed-change-requests/{requestId}/apply-readiness?companyId={companyId}&asOfDate={yyyy-MM-dd?}`
- `GET /accounting/company-books/remeasurement-policy?companyId={companyId}&bookId={bookId?}&asOfDate={yyyy-MM-dd?}`
- `GET /accounting/fx-revaluation-batches/{documentId}?companyId={companyId}`
- `POST /accounting/fx-revaluation-batches/{documentId}/post`

Example POST body:

```json
{
  "companyId": "11111111-1111-1111-1111-111111111111",
  "userId": "22222222-2222-2222-2222-222222222222",
  "acceptedFxSnapshotId": null,
  "idempotencyKey": "manual-journal:11111111-1111-1111-1111-111111111111:33333333-3333-3333-3333-333333333333"
}
```

## Configuration

`src/Citus.Accounting.Api/appsettings.json` contains the development connection string key:

- `ConnectionStrings:AccountingCore`

`src/Citus.SysAdmin.Api/appsettings.json` uses the same connection string key for the shared PostgreSQL platform database:

- `ConnectionStrings:AccountingCore`

You can also override it with:

- `CITUS_ACCOUNTING_DB`

## Next recommended slices

- replace placeholder test projects with real test frameworks and posting tests
- add create/update endpoints for manual journal, invoice, and bill drafts
- add party_type propagation into JE lines and open-item/application flows
- add create/update endpoints for credit note and vendor credit drafts
- add update endpoints for receive payment and pay bill source documents
- add create/update endpoints for credit application and vendor credit application source documents
- add explicit source-document void/reverse commands only after company-owned lifecycle rules are modeled, rather than inferring them from JE status alone
- add end-to-end tests for foreign-currency settlement, realized FX posting, and partial-application carry-forward behavior
- add end-to-end tests for FX revaluation draft preparation and stale-batch rejection
- add payment/open-item adjustment and bank/account remeasurement flows
