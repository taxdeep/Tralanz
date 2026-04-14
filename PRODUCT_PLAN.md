# Product Planning Document - Accounting MVP

## Product goal

Build an accounting MVP for small businesses that is simple, practical, and modular.

Primary users are small business owners who need basic bookkeeping workflows without enterprise complexity.

## Core product principles

- Reliable accounting first, polish second.
- Clear workflows for non-technical users.
- Small modules with clean boundaries.
- Auditability: posted transactions must be traceable.

## Target MVP outcomes

By MVP completion, a business owner should be able to:

1. Set up their company profile and accounting defaults.
2. Record core transactions (sales, expenses, manual journals).
3. Auto-post those transactions to the ledger.
4. Review key reports (Trial Balance, Income Statement, Balance Sheet).
5. Track basic AR and AP balances.

## In-scope modules (ordered)

1. Authentication
2. First-time company setup wizard
3. Chart of accounts generation
4. Dashboard
5. Journal entries
6. Reports
7. Sales / AR
8. Expense / AP

## Out of scope for MVP

- Payroll
- Inventory costing methods
- Bank feed integrations
- Multi-company in one user account
- Multi-currency revaluation complexity
- Advanced role-based permissions
- PDF branding customization

## High-level domain model (MVP)

- User (login identity)
- Company (single tenant for now)
- Account (chart of accounts)
- JournalEntry + JournalLine
- Customer + Invoice + InvoiceLine
- Vendor + Bill + BillLine
- TaxCode (payable/recoverable mapping)
- Fiscal settings and reporting periods

## Main workflows

1. User logs in.
2. If no company setup exists, go to setup wizard.
3. Setup saves company + tax + fiscal defaults, then generates chart of accounts.
4. User lands on dashboard with summary cards and recent activity.
5. User posts transactions (journal, invoice, bill), system generates journal entries.
6. User runs reports by date range.

## Data and integrity assumptions

- One company for MVP (simplifies permissions and data filters).
- SQLite for local development and MVP simplicity.
- All monetary fields stored as decimals in database.
- Posting action is explicit and irreversible in normal flow.
- Edits to posted documents happen through reversal + replacement (later phase, if needed).

## Risks and mitigations

- **Risk:** accounting logic mistakes.
  - **Mitigation:** strict posting tests and balancing validation.
- **Risk:** scope creep.
  - **Mitigation:** enforce phase gates and out-of-scope list.
- **Risk:** confusing UX for non-accountants.
  - **Mitigation:** plain-language labels and helper text on key forms.

## Acceptance criteria for MVP

- Login and first-time setup redirect logic works.
- Company setup is saved once and can be viewed/updated.
- Chart of accounts is generated and visible.
- Dashboard shows date-filtered summaries and recent transactions.
- Journal entry form prevents unbalanced posting.
- Trial Balance, Income Statement, and Balance Sheet run with date filtering.
- Invoice and bill posting create correct ledger impact.
- Canadian purchase tax logic supports recoverable and non-recoverable outcomes.
