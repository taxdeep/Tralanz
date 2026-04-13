# Phased Implementation Plan

This plan breaks the MVP into small phases. Complete each phase before moving to the next.

---

## Phase 0 - Project bootstrap (foundation)

### Goal

Create a clean, runnable baseline project with the required stack.

### Build tasks

1. Initialize Next.js project with TypeScript and Tailwind CSS.
2. Add Prisma and SQLite configuration.
3. Create baseline folder structure:
   - `src/app` (routes/pages)
   - `src/components` (UI)
   - `src/lib` (shared utilities)
   - `src/server` (services, business logic)
   - `prisma` (schema, migrations, seed)
4. Add environment file template (`.env.example`).
5. Create basic README with run instructions.

### Done criteria

- App starts locally.
- Prisma can run migration and generate client.
- Basic lint/typecheck passes.

---

## Phase 1 - Authentication

### Goal

Allow user login and route correctly based on setup status.

### Build tasks

1. Implement login page (username/email + password).
2. Implement auth session handling.
3. Add route guard for authenticated pages.
4. Add post-login redirect logic:
   - If company setup missing -> setup wizard
   - Else -> dashboard

### Database scope

- `User` model (minimal fields for MVP auth).
- Optional `Session` model depending on auth approach.

### Validation and tests

- Invalid login handling.
- Successful login redirect tests.
- Unauthorized route access check.

### Done criteria

- Login works end-to-end.
- Redirect logic is correct for both setup states.

---

## Phase 2 - First-time company setup wizard

### Goal

Collect foundational company settings on first use.

### Build tasks

1. Build setup wizard form with required fields:
   - Legal name
   - Company type
   - Business number
   - Address
   - Base currency
   - Fiscal year end
   - Tax settings
2. Save setup data in database.
3. Prevent re-running first-time wizard once complete (except future edit screen).

### Database scope

- `Company`
- `CompanySettings` (or equivalent)
- `TaxSettings` (initial)

### Validation and tests

- Required field validation.
- Company type enum validation.
- Setup completion status logic.

### Done criteria

- Setup wizard persists all required data.
- User cannot proceed to dashboard until setup is complete.

---

## Phase 3 - Chart of accounts generation

### Goal

Auto-generate starter chart of accounts immediately after setup.

### Build tasks

1. Define starter account template list (assets, liabilities, equity, revenue, expenses).
2. Generate accounts after setup completion.
3. Ensure idempotency (no duplicate accounts on retries).
4. Create simple accounts list page.

### Database scope

- `Account` model
- Account type/category enums

### Validation and tests

- Account generation runs once.
- Essential accounts exist (Cash, AR, AP, Revenue, Expense, Tax Payable, Tax Recoverable).

### Done criteria

- New company receives usable chart of accounts automatically.

---

## Phase 4 - Dashboard

### Goal

Show financial snapshot and recent activity.

### Build tasks

1. Add period filter (month, year, custom).
2. Create summary cards:
   - Sales
   - Expenses
   - Net income
   - AR
   - AP
   - Bank
3. Show recent sales and expenses lists.
4. Build data queries using posted ledger-backed data.

### Validation and tests

- Date filters return expected totals.
- Empty-state handling for new businesses.

### Done criteria

- Dashboard loads quickly and displays correct totals for selected period.

---

## Phase 5 - Journal entries

### Goal

Create and post balanced manual journal entries.

### Build tasks

1. Journal entry form with date, account, debit/credit, details.
2. Multi-line entry support.
3. Balance validation before post.
4. Post action that locks entry.
5. Journal list/detail views with posted status.

### Database scope

- `JournalEntry`
- `JournalLine`

### Validation and tests

- Prevent unbalanced post.
- Posted entries cannot be edited.
- Debit/credit totals persisted correctly.

### Done criteria

- Users can reliably post balanced manual journals.

---

## Phase 6 - Reports

### Goal

Provide key financial statements with date filters.

### Build tasks

1. Trial Balance report.
2. Income Statement report.
3. Balance Sheet report.
4. Shared report period filter component.

### Validation and tests

- Statement totals reconcile with journal data.
- Date filtering works and excludes out-of-period entries.

### Done criteria

- All 3 reports render correctly from live ledger data.

---

## Phase 7 - Sales / AR

### Goal

Manage customers and invoices with posting to ledger.

### Build tasks

1. Customer CRUD (minimal MVP fields).
2. Invoice create/edit.
3. Sales tax calculation and tax code selection.
4. Invoice post action that creates journal entries.

### Database scope

- `Customer`
- `Invoice`
- `InvoiceLine`

### Validation and tests

- Posting creates correct AR, revenue, and tax payable entries.
- Posted invoices are locked.

### Done criteria

- End-to-end invoice posting impacts AR and financial statements correctly.

---

## Phase 8 - Expense / AP

### Goal

Manage vendors and bills with posting to ledger and Canadian tax handling.

### Build tasks

1. Vendor CRUD (minimal MVP fields).
2. Bill create/edit.
3. Sales tax handling:
   - recoverable purchase tax
   - non-recoverable purchase tax
4. Bill post action that creates journal entries.
5. Tax code mapping to payable and recoverable accounts.

### Database scope

- `Vendor`
- `Bill`
- `BillLine`
- `TaxCode`

### Validation and tests

- Recoverable tax posts to recoverable tax account.
- Non-recoverable tax is expensed/capitalized per rule.
- AP totals and report impacts are correct.

### Done criteria

- End-to-end bill posting correctly updates AP and tax-related ledger balances.

---

## Working method for every phase

For each phase, follow this exact loop:

1. Define small scope for the phase (1-3 deliverables).
2. Implement code changes.
3. Apply Prisma schema changes and migration.
4. Run tests/checks.
5. Explain:
   - what changed in code
   - what changed in database
   - how to test it
6. Confirm phase completion against done criteria.

## Recommendation for next action

Start with **Phase 0** and complete only foundational setup in the first coding session.
