# UI, Navigation, And Design System Spec

## 1. Purpose

This document defines the executable UI/UX rules for:

- navigation structure
- shell behavior
- long-duration accounting workflows
- multi-currency surfaces
- design-system tone and constraints

Authority order:

`CITUS_PRODUCT_ENGINEERING_AUTHORITY.md > this document > task notes > temporary implementation habits`

## 2. Product Feel

Citus must feel:

- clean
- stable
- business-first
- professional
- restrained

Forbidden direction:

- flashy
- noisy
- game-like
- over-animated
- decorative at the cost of readability

## 3. Navigation Architecture

The sidebar is the main business navigation anchor.

Official structure:

### Core

- Dashboard
- Journal Entry
- Invoices
- Bills

### Sales & Get Paid

- Customers
- Receive Payment

### Expense & Bills

- Vendors
- Pay Bills

### Accounting

- Chart of Accounts
- Reconciliation
- Reports

### Settings

- Settings remains a distinct entry point with internal subsections

Explicitly forbidden:

- reintroducing top-level Contacts
- reintroducing top-level Banking
- moving Reports elsewhere
- breaking business meaning in navigation

## 4. Shell Rules

The shell must consistently provide:

- current company display
- company switcher
- stable sidebar
- clear top-level context

When company changes:

- shell frame may remain
- company-scoped content, permissions, reports, settings, numbering, currencies, and caches must refresh

## 5. Form Principles

Long-duration financial forms must optimize for:

- stable hierarchy
- dense but readable layout
- low-glare surfaces
- predictable actions
- reviewability before posting

Rules:

- backend truth is never implied from client-side computed values
- destructive or irreversible actions need clear confirmation
- posted-state fields must become read-only where required
- currency and tax meaning must be explicit, not hidden in placeholders

## 6. Table Principles

Tables must support operational finance work.

Rules:

- maintain strong alignment and readable numeric columns
- preserve clear status visibility
- make totals and summaries easy to scan
- support long-run use without visual fatigue
- avoid excessive decoration in dense accounting tables

## 7. Multi-Currency Surface Rules

Multi-currency UI must make these distinctions clear:

- transaction currency
- base currency
- exchange rate
- effective date
- source label

Rules:

- do not clutter the form with duplicate values before they are needed
- when foreign currency is selected, show enough context to explain conversion
- posted FX snapshot display must be immutable and read-only
- if historical FX truth is uncertain, show unavailable/unknown honestly

## 8. Reconciliation UX Direction

Reconciliation should be:

- QuickBooks-like in clarity
- control-oriented
- summary-bar driven
- inflow/outflow separated

Completion rule:

- reconciliation may only complete when `difference == 0`

## 9. Dashboard Direction

Dashboard is an operational overview, not heavy BI.

It should emphasize:

- current operational position
- overdue/open states
- cash and receivable/payable visibility
- actionable finance context

## 10. Reports Direction

Reports is the standard reporting home.

Rules:

- report meaning is backend-owned
- HTML, print, and CSV should stay semantically aligned
- acceleration/caching must never invent truth

## 11. Design System Tokens And Components

The design system should progressively standardize:

- spacing scale
- semantic color roles
- typography roles
- density modes where needed
- table, card, form, dialog, and badge patterns

Component rules:

- buttons, inputs, dialogs, tables, and alerts must share consistent interaction language
- success/warning/error colors must remain restrained and readable
- dark mode should be low-glare and purpose-designed, not simple inversion

## 12. Accessibility And Comfort

Required direction:

- readable contrast without harsh glare
- keyboard support for primary workflows
- accessible labels for critical controls
- clear focus states
- predictable error messages

## 13. AI Surface Rules

AI in UI is advisory only.

Allowed patterns:

- suggestion panels
- explanations
- anomaly hints
- rankings
- writing assistance for controlled text fields

Forbidden patterns:

- AI buttons that directly post accounting data
- AI flows that bypass validation
- AI output presented as accounting truth

## 14. Implementation Guidance

### 14.1 Frontend

- centralize shell, navigation, and common interaction patterns
- avoid page-by-page visual drift
- do not embed accounting policy in UI-only logic

### 14.2 Backend View Models

- return explicit status, lifecycle, and currency context
- do not force UI to infer accounting meaning from partial data

## 15. Review Checklist

Before shipping a new UI surface, verify:

- current company context is visible
- navigation meaning follows the official structure
- long-form work remains readable
- posted vs draft state is visually clear
- currency/base/FX context is understandable
- backend authority is not undermined by UI assumptions
