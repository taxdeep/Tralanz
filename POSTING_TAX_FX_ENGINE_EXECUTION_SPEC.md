# Posting, Tax, And FX Engine Execution Spec

## 1. Purpose

This document defines the executable backend rules for:

- Posting Engine
- Tax Engine
- FX resolution and conversion
- Journal Entry creation
- Ledger creation
- transactional safety around posting

Authority order:

`CITUS_PRODUCT_ENGINEERING_AUTHORITY.md > this document > POSTING_ENGINE_MULTICURRENCY_DESIGN.md > task notes > temporary implementation habits`

## 2. Non-Negotiable Rule

`All formal accounting must go through the Posting Engine.`

Forbidden:

- bypassing the Posting Engine
- writing formal ledger entries directly
- creating posted JE detached from a source document
- using UI preview or live provider response as accounting truth

## 3. Standard Execution Flow

```text
Document
-> Normalize
-> Validation
-> Tax Calculation
-> FX / Currency Resolution
-> Posting Fragments
-> Aggregation
-> Balance Check
-> Journal Entry
-> Ledger Entries
-> Open Item Updates
-> Audit / Outbox
```

## 4. Normalized Source Documents

Formal document types must normalize into backend-owned domain models.

Phase 1 required document families:

- `ManualJournalDocument`
- `InvoiceDocument`
- `BillDocument`
- `ReceivePaymentDocument`
- `PayBillsDocument`

Normalization rules:

- backend derives defaults and canonical field shape
- frontend-submitted derived values are never source of truth
- document identity, company, lifecycle, currency, tax inputs, and line semantics must be explicit

## 5. Validation Layer

Validation must confirm:

- company consistency
- document lifecycle legality
- numbering legality where applicable
- account legality and active status
- tax-code legality
- currency legality
- duplicate-post prevention
- source-specific required fields

Validation must run on the backend even if the frontend already validated.

## 6. Tax Engine Rules

Core principle:

`Tax = line-level calculation -> account-level aggregation`

Rules:

- tax starts from normalized document lines
- tax results are produced by backend-owned tax rules
- sales tax posts to tax payable
- recoverable purchase tax posts to recoverable tax
- partially recoverable tax follows governed split logic
- non-recoverable tax is absorbed into expense/inventory as appropriate

The UI may preview tax, but backend tax output is authoritative.

## 7. FX Resolution Rules

### 7.1 General

- JE supports a single transaction currency per JE
- every JE persists transaction currency and base currency
- exchange rate must come from a locally stored snapshot or an approved manual override
- live provider calls during save/post are forbidden

### 7.2 Accepted FX Inputs

- `identity`
- `manual`
- `company_override`
- `system_stored`
- `provider_fetched`

### 7.3 Save-Time Rules

- backend validates the exact locally accepted FX snapshot identity, or a defined equivalent local snapshot state
- client-submitted base amounts are never ledger truth
- backend derives base amounts from transaction amounts

### 7.4 Rounding Policy

Phase 1 policy:

- convert each line individually
- use banker's rounding
- round by target currency `minor_unit`
- if base totals do not balance exactly, block save

No automatic balancing entry is allowed until a governed rounding account exists.

## 8. Posting Fragments

Posting fragments are intermediate accounting results produced after tax and FX resolution.

Fragment fields should include:

- `company_id`
- `source_type`
- `source_id`
- `account_id`
- `currency_code`
- `tx_debit`
- `tx_credit`
- `debit`
- `credit`
- `tax_component_type`
- `control_role`
- `description`
- `party_id` where applicable

Fragments are not final JE lines. They exist so tax, FX, and control logic can remain composable.

## 9. Aggregation Rules

Formal JE should be readable and traceable.

Default aggregation rules:

- aggregate compatible fragments by account semantics
- preserve source traceability
- preserve transaction/base amount visibility
- do not merge rows that would weaken auditability

Recommended non-merge boundaries:

- different company
- different source
- different transaction currency
- different tax meaning
- different control-account meaning

## 10. Journal Entry Requirements

JE must include at least:

- `company_id`
- `status`
- `source_type`
- `source_id`
- `transaction_currency_code`
- `base_currency_code`
- `exchange_rate`
- `exchange_rate_date`
- `exchange_rate_source`
- totals/summary fields
- posting metadata
- auditability metadata

Required statuses:

- `draft`
- `posted`
- `voided`
- `reversed`

## 11. Ledger Entry Requirements

Ledger truth is base-currency truth.

Rules:

- ledger entries are created only from posted JE
- base debit/credit are authoritative balances
- transaction-currency amounts must remain visible where applicable
- posted truth is append-only; corrections use reversal/adjustment flows

## 12. Concurrency And Atomicity

Posting must run in a DB transaction.

Required protections:

- source row locking
- idempotency / duplicate-post prevention
- atomic source status update
- atomic JE creation
- atomic ledger creation
- full rollback on failure

## 13. C# Service Boundaries

Minimum interfaces:

```csharp
public interface IPostingEngine
{
    Task<PostingResult> PostAsync(IPostingDocument document, PostingContext context, CancellationToken cancellationToken);
}

public interface ITaxEngine
{
    Task<TaxComputationResult> CalculateAsync(IPostingDocument document, CancellationToken cancellationToken);
}

public interface IFxResolutionService
{
    Task<FxResolutionResult> ResolveAsync(FxResolutionRequest request, CancellationToken cancellationToken);
}

public interface IJournalEntryWriter
{
    Task<JournalEntryWriteResult> WriteAsync(JournalEntryDraft draft, CancellationToken cancellationToken);
}
```

Implementation rule:

- handlers/controllers call application services
- application services call engines
- engines own accounting transformation logic

## 14. Audit And Observability

Record at minimum:

- posting started/succeeded/failed
- FX snapshot selected/overridden
- status transitions
- reversal decisions
- duplicate-post blocks

## 15. Test Matrix

Important tests must cover:

- happy path posting
- cross-company rejection
- duplicate post rejection
- no-live-provider-at-save enforcement
- tax aggregation correctness
- line-level FX conversion correctness
- blocked unbalanced foreign-currency save
- reversal/void status consistency
