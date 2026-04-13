# Citus Backend

This directory contains the first PostgreSQL-backed .NET backend slice for the Citus accounting core.

Current layout:

- `src/Citus.Accounting.Domain`
- `src/Citus.Accounting.Application`
- `src/Citus.Accounting.Infrastructure`
- `src/Citus.Accounting.Api`
- `src/Citus.SysAdmin.Api`
- `tests/Citus.Accounting.Domain.Tests`
- `tests/Citus.Accounting.Application.Tests`
- `tests/Citus.Accounting.IntegrationTests`

Notes:

- The current machine resolves correctly with `C:\Program Files\dotnet\dotnet.exe`, and the backend solution now builds/tests from that 64-bit host.
- The backend now targets `.NET 10` through `backend/Directory.Build.props`, so local build/test/run no longer depends on `DOTNET_ROLL_FORWARD=Major`.
- You can start and stop the API from any directory with `D:\Coding\Citus\backend\start-accounting-api.ps1` and `D:\Coding\Citus\backend\stop-accounting-api.ps1`.
- `Citus.Accounting.Api` now wires the manual-journal, invoice, credit-note, bill, vendor-credit, receive-payment, credit-application, pay-bill, vendor-credit-application, and FX revaluation paths against PostgreSQL.
- The current slice reads `manual_journal_documents`, `invoices`, `credit_notes`, `bills`, `vendor_credits`, `receive_payments`, `credit_applications`, `pay_bills`, `vendor_credit_applications`, and `fx_revaluation_batches`, resolves FX snapshots from `company_fx_rate_snapshots` when needed, and writes `journal_entries`, `journal_entry_lines`, `ledger_entries`, `ar_open_items`, `ap_open_items`, `settlement_applications`, and `fx_revaluation_batch_lines`.
- Posted invoices and credit notes create `ar_open_items`, posted bills and vendor credits create `ap_open_items`, and posted settlements update open-item balances through `settlement_applications`.
- Foreign-currency receive-payment/pay-bill posting now uses the source document FX snapshot as the authoritative settlement rate, writes realized FX to dedicated gain/loss accounts, and reduces open-item carrying base separately from settlement base.
- Receive-payment and pay-bill draft preparation now has dedicated backend endpoints for listing open receivables/payables by party and inserting draft source documents into `receive_payments` / `pay_bills` before the normal posting path runs.
- FX revaluation now supports period-end draft preparation by currency, unrealized FX posting through the Posting Engine, carrying-base updates back onto foreign-currency open items, and explicit next-period unwind drafts that reverse a posted batch through the same engine path.
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

- `GET /accounting/manual-journals/{documentId}?companyId={companyId}`
- `POST /accounting/manual-journals/{documentId}/post`
- `GET /accounting/invoices/{documentId}?companyId={companyId}`
- `POST /accounting/invoices/{documentId}/post`
- `GET /accounting/credit-notes/{documentId}?companyId={companyId}`
- `POST /accounting/credit-notes/{documentId}/post`
- `GET /accounting/bills/{documentId}?companyId={companyId}`
- `POST /accounting/bills/{documentId}/post`
- `GET /accounting/vendor-credits/{documentId}?companyId={companyId}`
- `POST /accounting/vendor-credits/{documentId}/post`
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
- `POST /accounting/fx-revaluation-batches/prepare`
- `POST /accounting/fx-revaluation-batches/{documentId}/prepare-next-period-unwind`
- `GET /accounting/fx-revaluation-batches/{documentId}/cascade-unwind-plan?companyId={companyId}`
- `POST /accounting/fx-revaluation-batches/{documentId}/prepare-cascade-unwind`
- `POST /accounting/fx-revaluation-batches/{documentId}/auto-post-cascade-unwind`
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

You can also override it with:

- `CITUS_ACCOUNTING_DB`

## Next recommended slices

- replace placeholder test projects with real test frameworks and posting tests
- add create/update endpoints for manual journal, invoice, and bill drafts
- add party_type propagation into JE lines and open-item/application flows
- add create/update endpoints for credit note and vendor credit drafts
- add update endpoints for receive payment and pay bill source documents
- add create/update endpoints for credit application and vendor credit application source documents
- add end-to-end tests for foreign-currency settlement, realized FX posting, and partial-application carry-forward behavior
- add end-to-end tests for FX revaluation draft preparation and stale-batch rejection
- add payment/open-item adjustment and bank/account remeasurement flows
