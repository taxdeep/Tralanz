# QuickBooks-like Accounting MVP Planning Document

This document follows `PROJECT_RULES.md`:
- keep MVP simple
- do not over-engineer
- build in small phases
- keep code modular and maintainable

## 1) Module list

1. Authentication
2. Company setup wizard (first-time setup)
3. Chart of accounts
4. Dashboard
5. Journal entries
6. Reports
7. Sales / AR
8. Expense / AP
9. Tax codes (Canadian tax support)

## 2) Page list

### Authentication
- `/login`

### Setup
- `/setup/company` (wizard)

### Core app
- `/dashboard`
- `/accounts` (chart of accounts list)
- `/journal-entries`
- `/journal-entries/new`
- `/journal-entries/[id]`

### Reports
- `/reports/trial-balance`
- `/reports/income-statement`
- `/reports/balance-sheet`

### Sales / AR
- `/customers`
- `/customers/new`
- `/invoices`
- `/invoices/new`
- `/invoices/[id]`

### Expense / AP
- `/vendors`
- `/vendors/new`
- `/bills`
- `/bills/new`
- `/bills/[id]`

### Settings
- `/settings/tax-codes`
- `/settings/company`

## 3) User flow

1. User opens app and signs in on `/login`.
2. System checks if company setup exists.
3. If setup is missing, redirect to `/setup/company`.
4. User completes company setup (legal, fiscal, tax settings).
5. System auto-generates chart of accounts.
6. User is redirected to `/dashboard`.
7. User creates transactions:
   - manual journals, or
   - invoices (sales), or
   - bills (expenses).
8. On posting, system creates journal entries and locks posted records.
9. Reports read posted ledger transactions only.

## 4) Field list for each module

## 4.1 Authentication

### Login
- `emailOrUsername` (string, required)
- `password` (string, required)

### User (MVP minimal)
- `id`
- `email` (unique)
- `username` (optional unique)
- `passwordHash`
- `isActive`
- `createdAt`
- `updatedAt`

## 4.2 Company setup wizard

### Company profile
- `legalName` (required)
- `companyType` (enum: corporation, llp_partnership, sole_proprietorship_individual)
- `businessNumber`
- `addressLine1`
- `addressLine2`
- `city`
- `provinceState`
- `postalCode`
- `country` (default Canada)
- `baseCurrency` (default CAD)
- `fiscalYearEndMonth` (1-12)
- `fiscalYearEndDay` (1-31)

### Tax settings
- `isTaxRegistered` (boolean)
- `defaultSalesTaxCodeId` (optional)
- `defaultPurchaseTaxCodeId` (optional)

## 4.3 Chart of accounts

### Account
- `id`
- `code` (string, unique)
- `name` (string, required)
- `type` (enum: asset, liability, equity, revenue, expense)
- `reportingCategory` (required; see accounting rules)
- `isActive` (boolean)
- `isSystem` (boolean for generated defaults)
- `createdAt`
- `updatedAt`

### Recommended reporting categories
- `current_asset`
- `non_current_asset`
- `current_liability`
- `non_current_liability`
- `equity`
- `operating_revenue`
- `cost_of_sales`
- `operating_expense`
- `other_income`
- `other_expense`

## 4.4 Journal entries

### Journal entry header
- `id`
- `entryNumber`
- `entryDate`
- `memo`
- `status` (draft, posted)
- `postedAt` (nullable)
- `sourceType` (manual, invoice, bill, system)
- `sourceId` (nullable)
- `createdByUserId`

### Journal entry lines
- `id`
- `journalEntryId`
- `accountId`
- `description`
- `debitAmount` (decimal, default 0)
- `creditAmount` (decimal, default 0)

## 4.5 Reports filters (shared)

- `fromDate`
- `toDate`
- `periodPreset` (month, year, custom)

## 4.6 Sales / AR

### Customer
- `id`
- `displayName` (required)
- `email`
- `phone`
- `billingAddress`
- `isActive`

### Invoice
- `id`
- `invoiceNumber`
- `invoiceDate`
- `dueDate`
- `customerId`
- `status` (draft, posted, void)
- `subtotal`
- `taxAmount`
- `total`
- `memo`
- `postedAt` (nullable)

### Invoice line
- `id`
- `invoiceId`
- `description`
- `quantity`
- `unitPrice`
- `lineAmount`
- `revenueAccountId`
- `taxCodeId` (optional)

## 4.7 Expense / AP

### Vendor
- `id`
- `displayName` (required)
- `email`
- `phone`
- `address`
- `isActive`

### Bill
- `id`
- `billNumber`
- `billDate`
- `dueDate`
- `vendorId`
- `status` (draft, posted, void)
- `subtotal`
- `taxAmount`
- `total`
- `memo`
- `postedAt` (nullable)

### Bill line
- `id`
- `billId`
- `description`
- `lineAmount`
- `expenseAccountId`
- `taxCodeId` (optional)

## 4.8 Tax codes (Canadian)

### Tax code
- `id`
- `code` (example: GST5, HST13, PST7, EXEMPT)
- `name`
- `ratePercent`
- `appliesTo` (sales, purchase, both)
- `isRecoverableOnPurchase` (boolean)
- `payableAccountId` (required when taxable)
- `recoverableAccountId` (optional; required if recoverable)
- `isActive`

## 5) Accounting logic rules

These are mandatory for correctness:

1. Journal entries must balance before posting:
   - total debits = total credits
2. Posted journal entries must be locked:
   - no direct edit after posting
3. Reports must use only posted transactions:
   - draft transactions are excluded
4. Sales and bills must create journal entries when posted:
   - invoice post creates AR/revenue/tax entries
   - bill post creates expense-or-asset/AP/tax entries
5. Chart of accounts must support reporting categories:
   - each account must map to a reporting category used by reports
6. Every posted source document should be traceable:
   - journal entry stores `sourceType` and `sourceId`

## 6) Canadian tax logic overview

MVP approach:

1. Use tax codes to calculate tax amounts per line (or per document based on settings).
2. On sales:
   - tax collected increases tax payable liability.
3. On purchases:
   - if recoverable: tax amount goes to recoverable tax asset.
   - if non-recoverable: tax amount is added to expense/asset cost.
4. Tax code must map to payable and recoverable accounts to keep posting deterministic.
5. Reports and balances use posted journal entries only.

## 7) MVP scope vs later scope

## MVP scope (now)

- Single-company setup flow
- Login and redirect logic
- Basic chart of accounts generation
- Dashboard with summary cards and recent activity
- Manual journal entry posting with lock
- Trial Balance, Income Statement, Balance Sheet
- Customers + invoices with posting
- Vendors + bills with posting
- Canadian tax code mapping with recoverable/non-recoverable purchase logic

## Later scope (after MVP)

- Bank feeds and transaction matching
- Reversals and adjusting entry workflows UI
- Recurring invoices/bills
- File attachments
- Multi-currency revaluation
- Multi-user roles and approvals
- Audit export packages and advanced report layouts

## 8) Phased development plan

Follow strict phase order and complete one phase at a time.

### Phase 1: Authentication
- build login
- session handling
- redirect to setup or dashboard

### Phase 2: Company setup wizard
- collect required legal/fiscal/tax fields
- persist setup
- mark setup complete

### Phase 3: Chart of accounts
- generate starter accounts once
- include reporting categories
- add account list page

### Phase 4: Dashboard
- period filter (month/year/custom)
- summary cards (sales, expenses, net income, AR, AP, bank)
- recent sales/expenses

### Phase 5: Journal entries
- create multi-line manual journals
- validate balanced entries
- post and lock posted entries

### Phase 6: Reports
- Trial Balance
- Income Statement
- Balance Sheet
- use posted transactions only

### Phase 7: Sales / AR
- customer management
- invoice entry
- tax code application
- posting creates journal entries

### Phase 8: Expense / AP
- vendor management
- bill entry
- recoverable/non-recoverable tax behavior
- posting creates journal entries

## 9) Working rule for each phase

For every major step, clearly explain:
1. code changes
2. database changes
3. testing steps

Do not start next phase until current phase meets its acceptance criteria.
