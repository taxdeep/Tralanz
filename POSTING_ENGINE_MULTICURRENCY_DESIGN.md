# Posting Engine + Multi-Currency Design

## 1. Purpose

This document defines the executable system design for:

1. FX table structure in PostgreSQL
2. Shared document fields for Bill / Invoice / Journal Entry
3. FX lookup and fallback algorithm
4. C# Posting Engine interfaces and execution flow

Primary principle:

`All formal accounting must go through the Posting Engine.`

Standard flow:

```text
Document
-> Normalize
-> Validation
-> Tax Calculation
-> FX / Currency Resolution
-> Posting Fragments
-> Aggregation + Rounding
-> Journal Entry
-> Ledger Entries
-> AP/AR Open Item Updates
-> Audit / Outbox
```

## 2. Scope And Rules

### 2.1 Currency Mode

- Default mode is single-currency.
- Company base currency is mandatory.
- Multi-currency is disabled by default.
- When multi-currency is enabled, admin selects which extra currencies are allowed.
- Only enabled currencies may be used on Bill / Invoice / Journal Entry drafts.

### 2.2 FX Source

- FX source: `Frankfurter v2`
- URL: `https://api.frankfurter.dev`
- Reference docs:
  - `https://frankfurter.dev/`
  - `https://frankfurter.dev/docs/`
- For formal accounting, always specify `providers=<provider_key>`.
- Do not use default blended rates for posted accounting.
- Recommended default provider: `ECB`

### 2.3 Date Source For FX

- Bill uses `bill_date`
- Invoice uses `invoice_date`
- Manual journal uses `entry_date`

That date becomes the `fx_requested_date`.

### 2.4 Precision Policy

- FX rates: `NUMERIC(20,10)`
- Amounts: use PostgreSQL `NUMERIC`, never float
- C# uses `decimal`
- Current business default is `minor_unit = 2`
- Future currencies may use other minor units, so design must not hardcode `2` as a system constant

Recommended storage strategy:

- Store posted monetary values in `NUMERIC(20,6)` to avoid future schema migration for 0/3-decimal currencies
- Enforce display and posting rounding by `currency.minor_unit`
- For currently enabled normal currencies, `minor_unit = 2`

### 2.5 Immutability Rules

- Draft document currency can change
- Draft document FX rate can refresh
- Posted document currency cannot change
- Posted document FX snapshot cannot change
- Posted journal entries cannot be edited directly
- Corrections must go through reversal / replacement / adjustment flow

## 3. Core Domain Concepts

### 3.1 Currency

Defines what currencies exist and how amounts are rounded.

### 3.2 FX Snapshot

An FX rate cached locally from Frankfurter or another approved source.

### 3.3 Requested Date vs Effective Date

- `fx_requested_date`: the document date we asked for
- `fx_effective_date`: the actual working date whose rate was used

Example:

- Bill date = `2026-04-12`
- 2026-04-12 has no rate
- 2026-04-11 rate is used

Then:

- `fx_requested_date = 2026-04-12`
- `fx_effective_date = 2026-04-11`

### 3.4 Posting Fragment

A pre-journal accounting piece generated after tax and FX resolution, before final aggregation.

Example for a foreign-currency bill:

- Expense debit
- Recoverable tax debit
- AP credit

### 3.5 Journal Entry

Formal accounting result created only by Posting Engine.

### 3.6 Ledger Entry

Line-level ledger posting, derived from the journal entry.

### 3.7 Open Item

AP/AR balance-tracking entity for invoices, bills, payments, receipts, credits, and settlement.

## 4. PostgreSQL FX Table Structure

## 4.1 currencies

```sql
create table currencies (
    code                varchar(3) primary key,
    name                text not null,
    numeric_code        varchar(3),
    symbol              text,
    minor_unit          smallint not null check (minor_unit between 0 and 6),
    is_active           boolean not null default true,
    created_at          timestamptz not null default now(),
    updated_at          timestamptz not null default now()
);
```

Notes:

- `minor_unit` controls rounding and UI formatting
- Current default currencies may all be `2`
- Future additions may use `0` or `3`

## 4.2 company_currency_settings

```sql
create table company_currency_settings (
    id                          uuid primary key,
    company_id                  uuid not null,
    base_currency_code          varchar(3) not null references currencies(code),
    multi_currency_enabled      boolean not null default false,
    default_fx_provider         varchar(20) not null default 'ECB',
    fallback_lookback_days      integer not null default 7,
    created_at                  timestamptz not null default now(),
    updated_at                  timestamptz not null default now(),
    unique (company_id)
);
```

Notes:

- `base_currency_code` is highly restricted after first formal posting
- `fallback_lookback_days` controls how far back the engine may search

## 4.3 company_enabled_currencies

```sql
create table company_enabled_currencies (
    company_id              uuid not null,
    currency_code           varchar(3) not null references currencies(code),
    enabled_at              timestamptz not null default now(),
    enabled_by              uuid,
    primary key (company_id, currency_code)
);
```

Notes:

- Base currency must always be enabled
- Disabling a currency only affects future drafts
- Historical documents remain valid

## 4.4 fx_rate_snapshots

```sql
create table fx_rate_snapshots (
    id                      uuid primary key,
    provider_key            varchar(20) not null,
    base_currency_code      varchar(3) not null references currencies(code),
    quote_currency_code     varchar(3) not null references currencies(code),
    effective_rate_date     date not null,
    rate                    numeric(20,10) not null check (rate > 0),
    source                  varchar(20) not null,
    raw_payload             jsonb,
    retrieved_at            timestamptz not null default now(),
    created_at              timestamptz not null default now(),
    unique (provider_key, base_currency_code, quote_currency_code, effective_rate_date)
);
```

Recommended `source` values:

- `local_seed`
- `frankfurter_api`
- `manual_override`

Indexes:

```sql
create index idx_fx_rate_snapshots_lookup
    on fx_rate_snapshots (
        provider_key,
        base_currency_code,
        quote_currency_code,
        effective_rate_date desc
    );
```

## 4.5 Optional Manual Override Table

If finance wants controlled manual rate input:

```sql
create table fx_rate_overrides (
    id                      uuid primary key,
    company_id              uuid not null,
    provider_key            varchar(20) not null,
    base_currency_code      varchar(3) not null references currencies(code),
    quote_currency_code     varchar(3) not null references currencies(code),
    effective_rate_date     date not null,
    rate                    numeric(20,10) not null check (rate > 0),
    reason                  text not null,
    approved_by             uuid,
    created_at              timestamptz not null default now(),
    unique (company_id, provider_key, base_currency_code, quote_currency_code, effective_rate_date)
);
```

## 5. Shared Document Fields

These fields are required on Bill / Invoice / Journal Entry level, even if the document has no FX effect.

## 5.1 Shared Header Fields

```text
id
company_id
document_number
status
document_date
posting_status
posted_at
posting_batch_id
posting_request_id
source_system
memo
```

Recommended semantics:

- `status`: business workflow state, e.g. `draft`, `posted`, `voided`
- `posting_status`: accounting state, e.g. `not_posted`, `posted`, `reversed`
- `posting_request_id`: idempotency key

## 5.2 Shared Currency Fields

```text
document_currency_code
base_currency_code
fx_provider_key
fx_requested_date
fx_effective_date
fx_rate
fx_source
```

Rules:

- If `document_currency_code == base_currency_code`
  - `fx_rate = 1.0000000000`
  - `fx_requested_date = document_date`
  - `fx_effective_date = document_date`
  - `fx_source = system_base_currency`
- If foreign currency
  - these fields must be resolved before posting

## 5.3 Shared Totals

```text
subtotal_doc
tax_total_doc
total_doc

subtotal_base
tax_total_base
total_base
```

Rules:

- `*_doc` means original document currency
- `*_base` means company base currency
- Base totals are what the formal ledger balances on

## 5.4 Shared Line Fields

Each Bill line / Invoice line / Journal line should support:

```text
line_number
description
currency_code
fx_rate
amount_doc
amount_base
tax_amount_doc
tax_amount_base
rounding_delta_base
```

Notes:

- Manual journal lines may have no tax
- AP/AR lines should preserve original-currency values

## 5.5 Bill Header Fields

```text
vendor_id
bill_date
due_date
payable_currency_code
```

Bill lines should also include:

```text
expense_account_id
tax_code_id
is_tax_recoverable
```

## 5.6 Invoice Header Fields

```text
customer_id
invoice_date
due_date
receivable_currency_code
```

Invoice lines should also include:

```text
revenue_account_id
tax_code_id
quantity
unit_price_doc
unit_price_base
```

## 5.7 Manual Journal Header Fields

```text
entry_date
entry_reason
```

Manual journal lines should include:

```text
account_id
debit_doc
credit_doc
debit_base
credit_base
line_currency_code
```

Rule:

- Manual journal still goes through Posting Engine
- It just skips tax calculation if not applicable

## 6. Journal Entry And Ledger Fields

## 6.1 journal_entries

```sql
create table journal_entries (
    id                          uuid primary key,
    company_id                  uuid not null,
    entry_number                text not null,
    entry_date                  date not null,
    status                      varchar(20) not null,
    source_document_type        varchar(30) not null,
    source_document_id          uuid not null,
    source_document_number      text,
    base_currency_code          varchar(3) not null references currencies(code),
    posting_batch_id            uuid not null,
    posting_request_id          uuid not null,
    memo                        text,
    created_at                  timestamptz not null default now(),
    posted_at                   timestamptz,
    unique (company_id, entry_number),
    unique (company_id, source_document_type, source_document_id, posting_request_id)
);
```

## 6.2 journal_entry_lines

```sql
create table journal_entry_lines (
    id                          uuid primary key,
    journal_entry_id            uuid not null references journal_entries(id) on delete cascade,
    line_number                 integer not null,
    account_id                  uuid not null,
    line_currency_code          varchar(3) not null references currencies(code),
    base_currency_code          varchar(3) not null references currencies(code),
    fx_rate                     numeric(20,10) not null,
    fx_effective_date           date not null,
    debit_doc                   numeric(20,6) not null default 0,
    credit_doc                  numeric(20,6) not null default 0,
    debit_base                  numeric(20,6) not null default 0,
    credit_base                 numeric(20,6) not null default 0,
    description                 text,
    customer_id                 uuid,
    vendor_id                   uuid,
    created_at                  timestamptz not null default now(),
    unique (journal_entry_id, line_number)
);
```

Rules:

- Final balancing is checked on base amounts
- `debit_base` total must equal `credit_base` total
- `debit_doc` and `credit_doc` are optional from a pure ledger perspective, but strongly recommended for FX auditability

## 7. AP / AR Open Item Fields

Because AP/AR is the next major module, the posting design must support subledger tracking from the start.

## 7.1 ar_open_items

```sql
create table ar_open_items (
    id                          uuid primary key,
    company_id                  uuid not null,
    customer_id                 uuid not null,
    source_document_type        varchar(30) not null,
    source_document_id          uuid not null,
    source_document_number      text not null,
    document_currency_code      varchar(3) not null references currencies(code),
    base_currency_code          varchar(3) not null references currencies(code),
    fx_rate                     numeric(20,10) not null,
    fx_effective_date           date not null,
    original_amount_doc         numeric(20,6) not null,
    original_amount_base        numeric(20,6) not null,
    open_amount_doc             numeric(20,6) not null,
    open_amount_base            numeric(20,6) not null,
    due_date                    date,
    status                      varchar(20) not null,
    created_at                  timestamptz not null default now()
);
```

## 7.2 ap_open_items

```sql
create table ap_open_items (
    id                          uuid primary key,
    company_id                  uuid not null,
    vendor_id                   uuid not null,
    source_document_type        varchar(30) not null,
    source_document_id          uuid not null,
    source_document_number      text not null,
    document_currency_code      varchar(3) not null references currencies(code),
    base_currency_code          varchar(3) not null references currencies(code),
    fx_rate                     numeric(20,10) not null,
    fx_effective_date           date not null,
    original_amount_doc         numeric(20,6) not null,
    original_amount_base        numeric(20,6) not null,
    open_amount_doc             numeric(20,6) not null,
    open_amount_base            numeric(20,6) not null,
    due_date                    date,
    status                      varchar(20) not null,
    created_at                  timestamptz not null default now()
);
```

## 8. FX Query And Fallback Algorithm

## 8.1 High-Level Rules

- Use local cache first
- If local exact date exists, use it
- If exact date does not exist, use latest available rate on or before requested date
- If local cache has nothing, call Frankfurter
- API call must specify provider
- For remote fetch, use time-series endpoint, not latest
- After fetching, save returned snapshots locally
- The document stores both requested and effective date

## 8.2 Frankfurter Endpoint Pattern

Recommended pattern:

```text
GET /v2/rates?base={BASE}&quotes={QUOTE}&from={DATE_MINUS_LOOKBACK}&to={REQUESTED_DATE}&providers={PROVIDER}
```

Example:

```text
https://api.frankfurter.dev/v2/rates?base=USD&quotes=CAD&from=2026-04-05&to=2026-04-12&providers=ECB
```

Reason:

- Handles weekends and holidays
- Allows explicit fallback selection
- Avoids depending on undocumented nearest-date behavior

## 8.3 Resolution Algorithm

```text
Input:
  company_id
  base_currency
  quote_currency
  requested_date
  provider_key
  lookback_days

If base_currency == quote_currency:
  return rate = 1.0000000000
  effective_date = requested_date
  source = system_base_currency

Step 1:
  query local fx_rate_snapshots
  where provider_key = provider_key
    and base_currency_code = base_currency
    and quote_currency_code = quote_currency
    and effective_rate_date = requested_date
  if found:
    return exact match

Step 2:
  query local fx_rate_snapshots
  where provider_key = provider_key
    and base_currency_code = base_currency
    and quote_currency_code = quote_currency
    and effective_rate_date <= requested_date
  order by effective_rate_date desc
  limit 1
  if found and requested_date - effective_rate_date <= lookback_days:
    return fallback local rate

Step 3:
  call Frankfurter time series endpoint
  from = requested_date - lookback_days
  to = requested_date
  providers = provider_key
  base = base_currency
  quotes = quote_currency

Step 4:
  if response contains rows:
    persist every returned row into fx_rate_snapshots
    select row with max(date) where date <= requested_date
    return selected row

Step 5:
  if still no row:
    raise FxRateNotFound
```

## 8.4 C# Pseudocode

```csharp
public async Task<FxResolution> ResolveAsync(
    FxRequest request,
    CancellationToken ct)
{
    if (request.BaseCurrency == request.QuoteCurrency)
    {
        return FxResolution.BaseCurrency(request.RequestedDate);
    }

    var exact = await _fxRepo.FindExactAsync(
        request.ProviderKey,
        request.BaseCurrency,
        request.QuoteCurrency,
        request.RequestedDate,
        ct);

    if (exact is not null)
        return FxResolution.FromSnapshot(request.RequestedDate, exact);

    var localFallback = await _fxRepo.FindLatestOnOrBeforeAsync(
        request.ProviderKey,
        request.BaseCurrency,
        request.QuoteCurrency,
        request.RequestedDate,
        ct);

    if (localFallback is not null &&
        (request.RequestedDate - localFallback.EffectiveDate).TotalDays <= request.LookbackDays)
    {
        return FxResolution.FromSnapshot(request.RequestedDate, localFallback);
    }

    var fromDate = request.RequestedDate.AddDays(-request.LookbackDays);
    var remoteRows = await _frankfurterClient.GetRatesAsync(
        providerKey: request.ProviderKey,
        baseCurrency: request.BaseCurrency,
        quoteCurrency: request.QuoteCurrency,
        fromDate: fromDate,
        toDate: request.RequestedDate,
        ct: ct);

    if (remoteRows.Count > 0)
    {
        await _fxRepo.UpsertSnapshotsAsync(remoteRows, ct);

        var chosen = remoteRows
            .Where(x => x.EffectiveDate <= request.RequestedDate)
            .OrderByDescending(x => x.EffectiveDate)
            .FirstOrDefault();

        if (chosen is not null)
            return FxResolution.FromSnapshot(request.RequestedDate, chosen);
    }

    throw new FxRateNotFoundException(
        request.BaseCurrency,
        request.QuoteCurrency,
        request.RequestedDate,
        request.ProviderKey);
}
```

## 8.5 Failure Rules

- Do not post if foreign-currency document has no resolved FX rate
- Do not silently switch provider during posting
- Do not use `/latest` for historical posting
- Do not re-resolve FX for posted documents

## 9. Posting Engine Interface Definition

## 9.1 Core Contract

```csharp
public interface IPostingEngine
{
    Task<PostingResult> PostAsync(PostCommand command, CancellationToken ct);
}
```

## 9.2 Command And Result

```csharp
public sealed record PostCommand(
    Guid CompanyId,
    string DocumentType,
    Guid DocumentId,
    Guid PostingRequestId,
    DateOnly PostingDate,
    string RequestedBy,
    bool PreviewOnly = false);

public sealed record PostingResult(
    bool Success,
    Guid? JournalEntryId,
    string EntryNumber,
    IReadOnlyList<PostingWarning> Warnings,
    IReadOnlyList<PostingError> Errors);
```

## 9.3 Pipeline Interfaces

### Document Loader

```csharp
public interface IDocumentLoader
{
    Task<AccountingDocumentEnvelope> LoadAsync(
        Guid companyId,
        string documentType,
        Guid documentId,
        CancellationToken ct);
}
```

### Normalizer

```csharp
public interface IDocumentNormalizer
{
    Task<NormalizedAccountingDocument> NormalizeAsync(
        AccountingDocumentEnvelope envelope,
        CancellationToken ct);
}
```

### Validator

```csharp
public interface IDocumentValidator
{
    Task ValidateAsync(
        NormalizedAccountingDocument document,
        CancellationToken ct);
}
```

### Tax Calculator

```csharp
public interface ITaxCalculator
{
    Task<TaxComputationResult> CalculateAsync(
        NormalizedAccountingDocument document,
        CancellationToken ct);
}
```

### FX Resolver

```csharp
public interface IFxRateResolver
{
    Task<FxResolutionBundle> ResolveAsync(
        NormalizedAccountingDocument document,
        TaxComputationResult tax,
        CancellationToken ct);
}
```

### Posting Fragment Builder

```csharp
public interface IPostingFragmentBuilder
{
    Task<IReadOnlyList<PostingFragment>> BuildAsync(
        NormalizedAccountingDocument document,
        TaxComputationResult tax,
        FxResolutionBundle fx,
        CancellationToken ct);
}
```

### Aggregator

```csharp
public interface IPostingFragmentAggregator
{
    Task<AggregatedPosting> AggregateAsync(
        IReadOnlyList<PostingFragment> fragments,
        CancellationToken ct);
}
```

### Journal Builder

```csharp
public interface IJournalEntryBuilder
{
    Task<JournalEntryDraft> BuildAsync(
        NormalizedAccountingDocument document,
        AggregatedPosting posting,
        CancellationToken ct);
}
```

### Ledger Writer

```csharp
public interface ILedgerWriter
{
    Task<LedgerWriteResult> WriteAsync(
        JournalEntryDraft draft,
        CancellationToken ct);
}
```

### AP/AR Open Item Writer

```csharp
public interface IOpenItemWriter
{
    Task WriteAsync(
        NormalizedAccountingDocument document,
        JournalEntryDraft draft,
        CancellationToken ct);
}
```

### Audit Writer

```csharp
public interface IPostingAuditWriter
{
    Task WriteAsync(
        PostingAuditRecord record,
        CancellationToken ct);
}
```

## 9.4 Posting Engine Orchestration

```csharp
public sealed class PostingEngine : IPostingEngine
{
    public async Task<PostingResult> PostAsync(PostCommand command, CancellationToken ct)
    {
        var envelope = await _loader.LoadAsync(
            command.CompanyId,
            command.DocumentType,
            command.DocumentId,
            ct);

        var normalized = await _normalizer.NormalizeAsync(envelope, ct);
        await _validator.ValidateAsync(normalized, ct);

        var tax = await _taxCalculator.CalculateAsync(normalized, ct);
        var fx = await _fxResolver.ResolveAsync(normalized, tax, ct);
        var fragments = await _fragmentBuilder.BuildAsync(normalized, tax, fx, ct);
        var aggregated = await _aggregator.AggregateAsync(fragments, ct);
        var draft = await _journalBuilder.BuildAsync(normalized, aggregated, ct);

        if (command.PreviewOnly)
        {
            return PostingResultPreviewFactory.FromDraft(draft);
        }

        var writeResult = await _ledgerWriter.WriteAsync(draft, ct);
        await _openItemWriter.WriteAsync(normalized, draft, ct);
        await _auditWriter.WriteAsync(
            PostingAuditRecord.From(command, normalized, fx, writeResult),
            ct);

        return new PostingResult(
            Success: true,
            JournalEntryId: writeResult.JournalEntryId,
            EntryNumber: writeResult.EntryNumber,
            Warnings: Array.Empty<PostingWarning>(),
            Errors: Array.Empty<PostingError>());
    }
}
```

## 10. Posting Fragment Structure

```csharp
public sealed record PostingFragment(
    string FragmentType,
    string AccountCode,
    string Side,
    string LineCurrencyCode,
    string BaseCurrencyCode,
    decimal AmountDoc,
    decimal AmountBaseRaw,
    decimal AmountBaseRounded,
    decimal FxRate,
    DateOnly FxEffectiveDate,
    string Description,
    string? CustomerId = null,
    string? VendorId = null,
    string? TaxCode = null,
    string? Dimension1 = null,
    string? Dimension2 = null);
```

Rules:

- `AmountBaseRaw` keeps pre-rounding precision
- `AmountBaseRounded` is what the ledger uses
- Rounding adjustment belongs in aggregation phase, not source line phase

## 11. Aggregation And Rounding Rules

### 11.1 General Rule

- Compute in high precision
- Aggregate by account + side + currency + dimensions
- Round only at the aggregation boundary
- Validate final base debit equals final base credit

### 11.2 Rounding Strategy

- Use `currency.minor_unit` for document-side rounding
- Use base currency `minor_unit` for base-side rounding
- One company-wide midpoint rule must be selected

Recommended:

- `MidpointRounding.AwayFromZero`

### 11.3 Rounding Delta Treatment

If fragment rounding causes imbalance:

- add a rounding adjustment fragment
- post to dedicated rounding account
- only if imbalance exceeds zero and is within defined tolerance

Recommended tolerance:

- `<= 0.01` in base currency

If above tolerance:

- fail posting

## 12. AP/AR Multi-Currency Strengthening Rules

### 12.1 Open Item Creation

- Posted invoice creates AR open item
- Posted bill creates AP open item

### 12.2 Settlement

Receipt or payment must:

1. apply against open items in document currency
2. compute settled base amount
3. compare historic base carrying amount vs settlement base amount
4. generate realized FX gain/loss if needed
5. route final accounting through Posting Engine

### 12.3 Revaluation

Period-end revaluation must:

1. select open foreign-currency AP/AR items
2. resolve period-end FX
3. compare current carrying base amount vs revalued base amount
4. create unrealized FX gain/loss journal through Posting Engine

## 13. Example Scenario

Example:

- Base currency: `CAD`
- Multi-currency enabled
- Bill currency: `USD`
- Bill date: `2026-04-12`
- Provider: `ECB`

Flow:

```text
Bill draft saved
-> user chooses USD
-> system uses bill_date = 2026-04-12 as fx_requested_date
-> local lookup for USD/CAD on 2026-04-12
-> not found
-> local latest on/before lookup
-> not found
-> remote Frankfurter time series fetch from 2026-04-05 to 2026-04-12
-> latest available date returned is 2026-04-11
-> snapshot persisted locally
-> document stores:
   fx_requested_date = 2026-04-12
   fx_effective_date = 2026-04-11
   fx_rate = x.xxxxxxxxxx
-> tax computed in USD
-> converted to CAD
-> posting fragments built
-> aggregated and rounded
-> JE created in CAD-balanced lines
-> AP open item created with both USD and CAD balances
```

## 14. Implementation Order

Recommended sequence:

1. PostgreSQL migration for currency + FX + journal extensions
2. C# domain primitives:
   - `Money`
   - `Currency`
   - `FxSnapshot`
   - `PostingFragment`
3. `IFxRateResolver`
4. `IPostingEngine` skeleton
5. Manual journal integration
6. Bill integration
7. Invoice integration
8. AP/AR open item writers
9. Settlement flows
10. FX revaluation batch

## 15. Non-Negotiable Guardrails

- No formal posting outside Posting Engine
- No float for money or rates
- No blended provider for posted accounting
- No `/latest` for historical posting
- No mutation of posted FX snapshot
- No direct edit of posted JE
- No cross-provider silent fallback

## 16. External Reference

Frankfurter official references used for this design:

- Main docs: https://frankfurter.dev/
- v2 docs: https://frankfurter.dev/docs/

Relevant verified capabilities:

- historical date query
- time series query with `from` and `to`
- provider filtering with `providers`
- currencies endpoint
- official-source rate coverage

