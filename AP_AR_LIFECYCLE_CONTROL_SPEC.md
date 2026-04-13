# AP And AR Lifecycle Control Spec

## 1. Purpose

This document defines the executable lifecycle and accounting-control rules for:

- customers and AR
- vendors and AP
- invoices
- bills
- receive payment
- pay bills
- open-item behavior
- foreign-currency control routing

Authority order:

`CITUS_PRODUCT_ENGINEERING_AUTHORITY.md > this document > POSTING_TAX_FX_ENGINE_EXECUTION_SPEC.md > POSTING_ENGINE_MULTICURRENCY_DESIGN.md > task notes > temporary implementation habits`

## 2. Control Position

AP and AR are not status-only UI modules.

They are control layers built on:

- business document lifecycle
- Posting Engine outputs
- open-item truth
- settlement/application rules
- auditability

## 3. Master Data Rules

### 3.1 Customer

Customer must include:

- `company_id`
- identity/display fields
- default transaction currency
- active status

Rules:

- each customer has exactly one default transaction currency
- if historical transactions exist, default transaction currency becomes locked
- customer must not reference another company's terms, tax objects, or AR controls

### 3.2 Vendor

Vendor must include:

- `company_id`
- identity/display fields
- default transaction currency
- active status

Rules mirror customer rules.

## 4. Document Lifecycles

### 4.1 Invoice

Recommended lifecycle:

- `draft`
- `issued`
- `posted`
- `partially_paid`
- `paid`
- `voided`
- `reversed`

Rules:

- accounting truth starts at posting, not mere draft save
- posted invoice creates JE and AR open item through Posting Engine
- paid/partial state derives from applications, not just UI flags

### 4.2 Bill

Recommended lifecycle:

- `draft`
- `posted`
- `partially_paid`
- `paid`
- `voided`
- `reversed`

Rules:

- posted bill creates JE and AP open item through Posting Engine
- payment state derives from applications and remaining open amount

## 5. Open Item Model

AP/AR must use an open-item model.

Each open item should persist:

- `company_id`
- `party_id`
- `party_type`
- `source_type`
- `source_id`
- `document_currency_code`
- `base_currency_code`
- original amount in document currency
- original amount in base currency
- open amount in document currency
- open amount in base currency
- status

Open item truth must be backend-owned and derived from posted accounting events.

## 6. Settlement And Application

### 6.1 Receive Payment

Receive Payment applies money to customer open items.

It must support:

- full application
- partial application
- one-to-many application
- credits where later supported

### 6.2 Pay Bills

Pay Bills applies payment to vendor open items.

It must support:

- full payment
- partial payment
- one payment across multiple bills
- credits where later supported

### 6.3 Source Of Truth

- settlement rows/applications define paid vs open state
- UI status badges are derived, never authoritative

## 7. Multi-Currency Routing Rules

### 7.1 Default Single-Currency Behavior

- invoices route to company default `AR`
- bills route to company default `AP`

### 7.2 Foreign-Currency Control Accounts

When a foreign currency is enabled, the system creates system-owned control accounts such as:

- `AR-USD`
- `AP-USD`

Rules:

- backend control mapping decides routing
- UI text must not guess account identity
- users cannot delete or repurpose these accounts

### 7.3 Party Routing

- customer default transaction currency determines new invoice AR routing
- vendor default transaction currency determines new bill AP routing
- base-currency parties continue to use default `AR` / `AP`

## 8. Multi-Currency Settlement Rules

For foreign-currency AP/AR:

- open items retain document-currency and base-currency balances
- settlements must compare original effective rate vs settlement effective rate
- realized FX gain/loss must be posted through Posting Engine
- unrealized revaluation is a separate period-end flow

## 9. Edit And Immutability Rules

- draft documents may change within lifecycle rules
- posted documents do not silently mutate accounting truth
- corrections use reversal, replacement, or controlled adjustment
- party default transaction currency locks once history exists

## 10. Reporting Alignment

AP/AR reports must align with:

- posted document truth
- open-item balances
- settlement/application records
- party/company isolation

Required product-grade outputs include:

- AR aging
- AP aging
- party statement/drill-down support
- export consistency across HTML/CSV/print

## 11. Audit Requirements

Record at minimum:

- posting events
- partial/full applications
- unapply/reopen actions
- party currency lock decisions
- void/reverse actions
- FX gain/loss postings

## 12. Test Matrix

Important tests must cover:

- invoice post creates AR open item
- bill post creates AP open item
- partial payment updates open balances correctly
- one payment applied to multiple items
- foreign-currency routing to correct control account
- party currency change blocked after history exists
- realized FX posting on settlement
- reversed/voided documents excluded correctly from aging
