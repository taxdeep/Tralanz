# Sales Tax Module — Design

Status: **DRAFT for review (2026-05-29)**
Supersedes: existing single-rate "Tax Rates" feature
Companion docs: `POSTING_TAX_FX_ENGINE_EXECUTION_SPEC.md`, `AUDIT_2026-05-20.md`

> This design replaces the flat per-company "Tax Rates" list with a true
> Sales Tax Module that models jurisdiction, registration, multi-component
> taxes, effective-dated rates, recoverability, immutable transaction
> snapshots, and downstream return preview / filing. Target jurisdictions
> for MVP: Canada GST / HST / PST / QST. Designed so US state/local
> sales tax and EU VAT (incl. OSS / IOSS / reverse charge) can land in
> later phases without schema rewrites.

---

## Table of contents

- [A. Why "Tax Rates" is too narrow](#a-why-tax-rates-is-too-narrow)
- [B. Core concepts and data model](#b-core-concepts-and-data-model)
- [C. Computation logic](#c-computation-logic)
- [D. Posting Engine integration](#d-posting-engine-integration)
- [E. Reporting and Filing](#e-reporting-and-filing)
- [F. Recommended phased roadmap](#f-recommended-phased-roadmap)
- [G. Risks and prevention rules](#g-risks-and-prevention-rules)
- [Deliverables](#deliverables)
  - [1. Recommended table structure](#1-recommended-table-structure)
  - [2. Recommended service / engine layering](#2-recommended-service--engine-layering)
  - [3. Recommended UI structure](#3-recommended-ui-structure)
  - [4. Recommended test list](#4-recommended-test-list)
  - [5. First batch execution order](#5-first-batch-execution-order)

---

## A. Why "Tax Rates" is too narrow

The current `tax_codes` table (`TRALANZ_POSTGRESQL_MIGRATION_DRAFT.sql:645-667`) and `TaxRatesPage.razor` capture **one rate per code** with a single `payable_account_id` / `recoverable_account_id` and a free-text `registration_number`. That collapses six distinct concerns into one row:

1. **Jurisdiction** — which government you owe (CRA federal, Revenu Québec, BC, Florida DOR, Ireland VAT). Today: no jurisdiction concept. The `tax_returns` table has a free-text `tax_regime` column, but `tax_codes` has no FK to anything regime-shaped.
2. **Registration** — your company's status in that jurisdiction (registration number, effective dates, filing frequency, reporting calendar). Today: one `registration_number` text per tax_code; a company that registers for both GST and QST has to type the same number on two unrelated rows.
3. **Selectable code** — what the operator picks on an invoice line. Today: one selectable = one rate. There is no way to model "BC GST + PST" as a single picker option; the operator must add two lines or pick one and forget the other.
4. **Component** — the atomic piece of tax that flows to a specific GL account and a specific return box. Today: one component per code, hardcoded as `lineAmount × rate`. No compound flag, no piggyback (QC QST historically was computed on GST-inclusive base).
5. **Effective-dated rate** — historical accuracy. Today: `rate_percent` is one scalar column. The day BC changes PST from 7% to 8%, **editing the row breaks every historic invoice that was correctly priced at 7%** because pickers stop offering 7% and reports re-pulling the rate get 8%.
6. **Recoverability** — already partly in the schema (`recoverability_mode` / `is_recoverable_on_purchase`) but **not exposed in the UI** (`ITaxCodeStore.cs:8-14`), and `'partial'` has no percentage column to make it usable. Bill lines carry a single `is_tax_recoverable` boolean, so the recoverable portion isn't split from the non-recoverable portion when the engine posts.

There is also a deeper architectural problem the rename has to solve: **today the tax math happens in the Blazor create-pages, not in the Posting Engine.** Each page computes `tax_amount = lineAmount × rate_percent / 100` and persists it onto `*_lines.tax_amount`. The Posting Engine then reads the persisted amount and only does account routing (`DefaultPostingSupport.cs:534-548, 977-991`). The `ITaxEngine` interface exists but is wired to `NullTaxEngine` (`DefaultPostingSupport.cs:368-376`). Consequences:

- Tax math is in the UI layer, where it can't be re-applied or verified.
- One `tax_amount` per line is the only thing the engine sees — components are impossible.
- Account routing JOINs live to `tax_codes` (`PostgresBillDocumentRepository.cs:147-202`) — **editing a tax code's GL accounts changes where a future re-post or void lands**, even on historic documents.
- Tax returns hardcode five canonical chart codes (25000 / 13700 / 25001 / 25002 / 13701, `PostgresTaxReturnDocumentRepository.cs:13-26`) — multi-regime companies cannot separate GST payable from QST payable from VAT payable.

The "Sales Tax Module" framing forces the design to handle the full lifecycle: jurisdiction → registration → code → application (with snapshot) → ledger → period totals → return preview → filing. The rename is the small visible part; the redesign is the substantive change.

---

## B. Core concepts and data model

### Nine first-class concepts

1. **TaxJurisdiction** — cross-tenant catalog. Identified by `(country_code, region_code, city_code, regime_type)`. Examples:
   - `CA / – / – / gst` — Canada Revenue Agency federal GST
   - `CA / CA-BC / – / pst` — BC Provincial Sales Tax
   - `CA / CA-QC / – / qst` — Revenu Québec QST
   - `CA / CA-ON / – / hst` — combined HST (jurisdiction is the federal CRA, but rate is the harmonized 13%)
   - `US / US-CA / – / us_sales` — California state sales tax
   - `US / US-CA / SF / local_sales` — San Francisco local
   - `IE / – / – / vat` — Ireland VAT
   - `EU / – / – / oss` — EU One-Stop-Shop pseudo-jurisdiction
2. **TaxRegistration** — per-(company × jurisdiction). Captures: registration number, effective dates, filing frequency, reporting calendar, default GL routing override.
3. **TaxCode** — the user-facing selectable. Per-company. Identified by `(company_id, code)`. Has a `treatment` (taxable / zero-rated / exempt / out-of-scope / reverse-charge / import-tax) and `applies_to` (sales / purchase / both).
4. **TaxCodeComponent** — the atomic piece. A TaxCode has 1..N components. Each component links to a jurisdiction, carries recoverability + GL routing, and (optionally) a compound flag.
5. **TaxCodeComponentRate** — effective-dated rate, keyed on `(component_id, effective_from)`. Lookup: as-of-date.
6. **TaxTreatment** — flag on the TaxCode driving engine behaviour:
   - `taxable` (default)
   - `zero_rated` — tax = 0 but reportable as taxable supply; ITCs on purchases still recoverable
   - `exempt` — tax = 0, not reportable as taxable supply
   - `out_of_scope` — no snapshot row, no tax line at all (employee reimbursements, certain inter-company)
   - `reverse_charge` — buyer self-assesses, emits both payable AND recoverable fragments
   - `import_tax` — buyer-paid at customs, recoverable as ITC
7. **Recoverability** (per component on the purchase side):
   - `full` — recoverable = tax_amount
   - `partial` (with `recoverable_percent`) — recoverable = tax_amount × pct
   - `none` — recoverable = 0, full amount rolled into expense
8. **Compound flag** (per component) — `is_compound=true` means this component's base = lineAmount + sum of previously-applied components' tax. Used historically for QC QST on GST-inclusive base (rule changed in 2013; the data model needs to support both eras).
9. **TransactionTaxSnapshot** — one row per (document, line, component). Captures jurisdiction_id, regime, component_id, rate_percent_snapshot, taxable_amount, tax_amount, recoverable_amount, non_recoverable_amount, reporting_box_codes[], computed_at. **Immutable once the parent document is posted.** This is the single source of truth for reports, returns, voids, re-posts.

### Replaced shape of existing transaction-line tables

Today: `invoice_lines.tax_code_id` + `invoice_lines.tax_amount`. Same on credit_note_lines, bill_lines, vendor_credit_lines, sales_receipt_lines, refund_receipt_lines.

New:
- Keep `tax_amount` on the line as a denormalized aggregate (sum of snapshot rows for the line) for query speed. **Engine writes it from snapshots, never from page math.**
- Remove `tax_code_id` from posted-line tables; add it as a "last selected" hint on the **draft** state only. Posted lines reference the snapshot table.
- Add `document_line_tax_snapshots` table (schema in [Deliverables §1](#1-recommended-table-structure)) keyed on `(document_type, document_id, line_id, sequence)`.

This split also formalizes the existing free-text `journal_entry_lines.tax_component_type` tag into an FK to `tax_code_component_id`.

---

## C. Computation logic

The engine's contract: take a list of `(line_amount, tax_code_id)` plus a tax_point_date (the date used to look up effective-dated rates) and return per-line per-component snapshot rows.

### Sales side (invoice, credit note, sales receipt, refund receipt)

For each line:
- Resolve TaxCode → components ordered by `sequence`.
- For each component (in order):
  - `base = lineAmount` if `!is_compound`, else `lineAmount + Σ previous_components.tax_amount`
  - `rate = effective_rate_at(component_id, tax_point_date)` (lookup against tax_code_component_rates)
  - `tax_amount = round(base × rate / 100, currency.minor_unit, RoundingPolicy)`
  - Emit a snapshot row.
- `line.tax_amount = Σ snapshot rows for line`.

`RoundingPolicy` defaults to `ToEven` (banker's rounding) for currency-minor-unit scale (2 for CAD/USD/EUR, 0 for JPY). Configurable per jurisdiction in Phase 2 if a specific authority mandates `AwayFromZero`.

### Purchase side (bill, vendor credit, expense)

Same per-component pass. For each component:
- `recoverable_amount` = depends on recoverability mode:
  - `full` → `tax_amount`
  - `partial` → `round(tax_amount × recoverable_percent / 100, …)`
  - `none` → `0`
- `non_recoverable_amount = tax_amount - recoverable_amount`

The bill-line aggregate `tax_amount` is the sum; the recoverable / non-recoverable split lives in the snapshot rows. The Posting Fragment Builder reads the snapshot to emit:
- Dr `recoverable_account_id` for the recoverable portion (an asset / reduction-of-liability — depending on the jurisdiction's accounting convention, but the account is configured per component)
- Dr expense for the non-recoverable portion (folded into the expense line, matching the current behaviour at `DefaultPostingSupport.cs:961-963` but now explicit per component)
- Cr accounts payable for the gross

### Zero-rated vs Exempt vs Out-of-Scope — three distinct paths

| Treatment      | Tax amount | Snapshot row | Counts toward taxable supplies | ITCs allowed | Typical example |
|---|---|---|---|---|---|
| `taxable`      | computed   | yes          | yes                             | yes          | regular sale |
| `zero_rated`   | 0          | yes (amount=0) | yes                            | yes          | basic groceries (CA), export sale |
| `exempt`       | 0          | yes (amount=0, treatment=exempt) | no                | no on related inputs | financial services, residential rent |
| `out_of_scope` | 0          | **NO**       | no                              | n/a          | employee reimbursement, inter-company at cost |

Putting zero-rated and exempt in snapshots (with amount=0) is what lets the return preview correctly populate "Line 91 Total taxable supplies" for CRA GST34 — which includes zero-rated but excludes exempt and out-of-scope.

### Reverse charge (EU B2B, intra-EU services, some imports)

- `treatment = reverse_charge`
- Engine computes `tax_amount` at the destination jurisdiction's rate.
- Posting Fragment Builder emits **two** ledger fragments:
  - +`tax_amount` to the destination-jurisdiction's payable account
  - -`tax_amount` (i.e. recoverable) to the destination-jurisdiction's recoverable account
- Net P&L impact: 0. Both sides land on the VAT return: the payable side bumps "VAT due on acquisitions", the recoverable side bumps "VAT deductible on acquisitions".
- Snapshot rows record both legs with the same `component_id` but a `leg` discriminator (`self_assessed_payable` / `self_assessed_recoverable`).

### Tax-inclusive vs tax-exclusive pricing

Per-document flag `tax_pricing_mode`:
- `exclusive` (default for North America) — line.amount is the taxable amount; tax is added on top.
- `inclusive` (common in EU retail, sometimes BC commercial) — line.amount is the gross; engine derives:
  - `effective_rate = Σ components.rate (+ compound stacking)`
  - `taxable_amount = round(gross / (1 + effective_rate/100), …)`
  - `tax_amount = gross - taxable_amount`
  - Then distribute tax_amount across components proportionally to their rate × stacking factor.

Display: subtotal / tax / total triple reverses meaning. The TaxCodePicker UI surfaces the document-level mode so the operator knows what they're entering.

### Place-of-supply (Phase 1: operator picks; Phase 4: resolver)

Phase 1 makes no attempt at automatic place-of-supply determination. The operator picks the TaxCode on the line; the jurisdiction is implicit in the code. We surface the customer's shipping address country / region as a hint next to the picker so a Toronto vendor selling to BC sees "BC ship-to" and picks `GST + PST_BC` instead of `HST_ON`.

Phase 4 introduces `ITaxResolver`:
```
ITaxResolver.ResolveAsync(SalesContext) → TaxCodeId
  SalesContext = { customer, ship_to_address, ship_from_address, item, item_category, document_date }
```
Implementations:
- `DefaultTaxResolver` — looks up `(item_category, ship-to-region)` → fallback to customer.default_tax_code_id
- `AvalaraTaxResolver` (optional) — calls Avalara's `/api/v2/transactions/createoradjust` and maps response to a Tralanz TaxCode

---

## D. Posting Engine integration

### Today's flow (problematic)

1. Operator types $100 on InvoiceCreatePage with `GST_5pct` selected.
2. Page computes `tax_amount = 5.00`, persists `invoice_lines.tax_amount = 5.00` and `invoice_lines.tax_code_id = <GST>`.
3. Operator Posts.
4. Posting handler calls `PostgresInvoiceDocumentRepository.GetForPostingAsync` which JOINs to `tax_codes` to grab `payable_account_id`.
5. `DefaultPostingSupport.cs:534-548` builds: Dr AR $105, Cr Revenue $100, Cr Sales Tax Payable $5.
6. JE persisted with `tax_component_type='sales_tax_payable'` on the tax line.

Where it breaks:
- (2) hard-codes single-component math. Multi-component impossible without rewriting every create-page.
- (4) live JOIN — operator edits `tax_codes.payable_account_id`, then re-posts a voided historic doc → lands in the new account, ledger mismatch.
- (5) routes through one account; no per-component visibility, no per-jurisdiction split.
- (6) `tax_component_type` is a free-form string with no FK back to tax_code or component or jurisdiction. Reports can't filter by regime cleanly.

### New flow

```
draft save          → engine.ComputeAsync(lines, code_ids, date) → snapshots
post                → fragment_builder.Build(snapshots, registration_accounts) → fragments
                    → posting_engine.Aggregate(fragments) → JE
                    → snapshot rows linked to JE lines via tax_snapshot_id FK
void                → fragment_builder.BuildReversal(snapshots) → negative-mirror fragments
report / preview    → tax_reporting_engine.Query(jurisdiction, period) → reads snapshots
```

### New interfaces

```csharp
public interface ISalesTaxEngine
{
    Task<SalesTaxComputationResult> ComputeAsync(
        SalesTaxComputationRequest request,
        CancellationToken ct);
}

public sealed record SalesTaxComputationRequest(
    CompanyId CompanyId,
    DateOnly TaxPointDate,
    string DocumentCurrencyCode,
    TaxPricingMode PricingMode,
    IReadOnlyList<SalesTaxLineRequest> Lines);

public sealed record SalesTaxLineRequest(
    Guid LineId,
    decimal LineAmount,
    Guid TaxCodeId,
    bool IsPurchaseSide,
    string? TreatmentOverride = null);

public sealed record SalesTaxComputationResult(
    IReadOnlyList<SalesTaxLineResult> Lines);

public sealed record SalesTaxLineResult(
    Guid LineId,
    decimal TotalTaxAmount,
    IReadOnlyList<TaxSnapshotDraft> Snapshots);

public sealed record TaxSnapshotDraft(
    int Sequence,
    Guid TaxCodeId,
    Guid ComponentId,
    Guid JurisdictionId,
    string RegimeType,
    decimal RateSnapshot,
    decimal TaxableAmount,
    decimal TaxAmount,
    decimal RecoverableAmount,
    decimal NonRecoverableAmount,
    bool IsCompoundSnapshot,
    IReadOnlyList<string> ReportingBoxCodes);

public interface ISalesTaxFragmentBuilder
{
    IReadOnlyList<PostingFragment> Build(
        IReadOnlyList<TaxSnapshotPersisted> snapshots,
        DocumentSide side);

    IReadOnlyList<PostingFragment> BuildReversal(
        IReadOnlyList<TaxSnapshotPersisted> snapshots,
        DocumentSide side);
}
```

The existing `IPostingFragmentBuilder.BuildAsync` (`PostingEngineContracts.cs:23-44`) stays as the orchestrator; `ISalesTaxFragmentBuilder` is invoked from inside the per-document builders (`DefaultPostingSupport.cs:534-…`).

### Immutability of snapshots

- `document_line_tax_snapshots` rows created on draft save MAY be replaced on subsequent draft saves (the operator can change rates / codes while drafting).
- The moment the parent document.status transitions to `posted`, snapshot rows for that document become immutable. Enforced by:
  - Database trigger: `BEFORE UPDATE OR DELETE ON document_line_tax_snapshots` checks `parent.status` and raises if posted.
  - Application layer: `ISalesTaxEngine.ComputeAsync` refuses to overwrite rows whose `(document_type, document_id)` parent is posted.
- Void = a NEW journal entry posted with negative-mirror snapshot rows (same fields, negated amounts). The original snapshot rows stay unchanged. Net to ledger: balanced. Reports correctly show "voided" by joining to JE status.

### Audit trail

Every ledger entry produced by the tax module can join back: `journal_entry_lines.tax_snapshot_id → document_line_tax_snapshots.id → component → tax_code → jurisdiction → registration`. Drill-down from any GL transaction to "what tax decision drove this" is one JOIN chain.

---

## E. Reporting and Filing

Three layers, all reading **snapshots**, never live tax_codes:

### Layer 1 — Sales Tax Summary report

Aggregate snapshots by `(jurisdiction, period, side, treatment)`. Output columns:
- Period
- Jurisdiction
- Side (collected / ITC)
- Treatment (taxable / zero-rated / exempt — exempt and OOS shown for reconciliation only)
- Taxable amount
- Tax amount

Operator self-serve, parameterized by date range. No filing context.

### Layer 2 — Sales Tax Detail report

Same query but ungrouped — one row per snapshot. Used for audit drill-down ("show me every BC PST line in Q1"). Exports to CSV.

### Layer 3 — Tax Return Preview

The pre-filing handoff. Operator selects (jurisdiction, period, filing_frequency). Engine:
- Queries snapshots where `posted_at ∈ [period_start, period_end]` AND `component.jurisdiction_id = selected`
- Joins to `tax_code_component_box_mappings` to bucket each snapshot's amounts into reporting boxes
- Returns a `TaxReturnPreview` object with one entry per box

Each jurisdiction has its own box schema. Examples:

#### CRA GST/HST return (form GST34, simplified)
| Box  | Source                                              |
|------|-----------------------------------------------------|
| 101  | Total sales / supplies (taxable + zero-rated, excl exempt / OOS) |
| 103  | GST/HST collected (sum of payable side snapshot amounts) |
| 104  | Adjustments to GST/HST collected (operator-entered) |
| 105  | Total GST/HST and adjustments (103 + 104) |
| 106  | ITCs (sum of recoverable side snapshot amounts) |
| 107  | Adjustments to ITCs (operator-entered) |
| 108  | Total ITCs and adjustments (106 + 107) |
| 109  | Net tax (105 - 108) |
| 110  | Instalment / installment payments |
| 113  | Balance owing / refund |

#### Revenu Québec QST return (form FPZ-500)
Equivalent boxes; separate jurisdiction means separate preview.

#### US state sales tax (varies)
Each state has its own form. The reporting-box catalog is per-jurisdiction. Modeled as `tax_reporting_boxes` rows with `box_code` matching the state's form.

#### EU VAT return
Boxes 1-9 (UK), declarations per country. OSS / IOSS have their own simplified structure.

### Filing workflow

1. Operator runs `TaxReturnPreview` for (jurisdiction, period).
2. Engine computes box totals from snapshots.
3. Operator clicks "Create return draft" → preview converts to a `tax_returns` row with `status='draft'`.
4. Operator adds adjustments (penalties, prior-period corrections) with `tax_return_adjustments` rows (new sub-table).
5. Operator posts → engine builds JE: Dr Sales Tax Payable (clearing the period's collected), Cr Input Tax Recoverable (clearing the period's recoverable), Cr Tax Filing Liability (net owed) or Dr Tax Filing Receivable (refund).
6. JE references the `tax_returns.id` so the GL can drill back to the filing.
7. Once filed externally, operator updates `tax_returns.regulator_reference_no` and `filed_at`.

**Per-jurisdiction GL routing** replaces the current five hardcoded canonical chart codes (`PostgresTaxReturnDocumentRepository.cs:13-26`). Each `tax_registrations` row carries:
- `collected_clearing_account_id`
- `recoverable_clearing_account_id`
- `adjustment_account_id`
- `return_liability_account_id`
- `return_receivable_account_id`

A multi-regime company files four separate returns and each posts to its own quintet.

---

## F. Recommended phased roadmap

### MVP — Phase 1: Canada (GST / HST / PST / QST)

Goal: rename + structural redesign + multi-component + effective dates + snapshot immutability + per-regime return routing, all on Canadian regimes.

| Batch | Scope |
|-------|-------|
| S1 | Migrations: tax_jurisdictions (catalog), tax_registrations, tax_code_components, tax_code_component_rates, document_line_tax_snapshots, tax_reporting_boxes, tax_code_component_box_mappings. Seed CA federal + 10 provinces. Data-migration script: existing tax_codes → new shape (1 component, 1 rate, jurisdiction inferred from code name pattern + operator confirmation). Schema-drift fix: `registration_number` moves from `tax_codes` to `tax_registrations`. |
| S2 | `SalesTaxEngine` real implementation. Engine writes snapshots on draft save. Pages stop computing tax math. `tax_amount` denormalized aggregate written from snapshots. |
| S3 | UI rename: TaxRatesPage → SalesTaxesPage. New SalesTaxEditorPage with components grid (still one component per code for migrated rows). TaxCodePicker grows a jurisdiction badge. |
| S4 | Multi-component support enabled. Test scenario: BC operator creates `GST_PST_BC` code with 2 components. End-to-end sale → invoice → snapshot → JE → report all show two components. |
| S5 | `SalesTaxFragmentBuilder` reads snapshot's stored account IDs (no live JOIN to tax_code). Posted documents become rate / account immutable. |
| S6 | Tax registrations CRUD + per-jurisdiction return routing replaces hardcoded 5-chart-codes. |
| S7 | Reporting box catalog + mappings UI. Sales Tax Summary report. CRA GST34 preview (Phase 1's only return format). |

### Phase 2 — US state sales tax

- Add state-level jurisdictions for all 50 states + DC.
- Local jurisdictions (city / county / district) as additional component layers on top of state.
- Reporting-box catalogs per state (one row per state's form line).
- No automatic place-of-supply — operator still picks the code; we surface ship-to state as a hint.
- Test matrix: California (state 7.25 + district variable), Texas (state 6.25 + local up to 2), Florida (state 6 + discretionary surtax).

### Phase 3 — EU VAT

- Country-level jurisdictions (27 EU member states + UK + Norway + Switzerland).
- Reverse-charge treatment fully wired (the schema already supports it from Phase 1; this phase exercises it for cross-border B2B).
- Tax-inclusive pricing turned on for retail invoices.
- OSS / IOSS pseudo-jurisdictions for cross-border e-commerce.
- Multi-currency reporting (a Canadian company with EU sales reports VAT in EUR but posts in CAD).

### Phase 4 — Tax resolver / 3rd-party API

- `ITaxResolver` interface (described in §C).
- `AvalaraTaxResolver` / `TaxJarTaxResolver` adapters.
- Per-company toggle: "Use Tralanz codes" (default) vs "Use Avalara".
- Resolver only suggests; operator can override.

### Phase 5 — Electronic filing

- CRA WAC integration for GST/HST direct file.
- NetFile / SafeFile equivalents for QC, BC, ON.
- US state portals (varies — many states accept CSV upload).
- This phase is operationally heavy (per-jurisdiction credentials, signing, retry) and best deferred until volumes justify it.

---

## G. Risks and prevention rules

| # | Risk                                                  | Rule                                                                                          | Enforcement                                                                                          |
|---|-------------------------------------------------------|-----------------------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------------|
| 1 | Historical rate overwritten by edit                   | Rates are effective-dated; never UPDATE the active row, always INSERT a new effective_from    | DB trigger on `tax_code_component_rates`: forbid UPDATE on `rate_percent` of rows whose effective_from < CURRENT_DATE or whose effective_to is set; UPDATE only allowed on `effective_to` (closing the range) |
| 2 | Posted document's tax mutated                         | Snapshots are immutable once parent.status='posted'                                           | DB trigger on `document_line_tax_snapshots`: BEFORE UPDATE OR DELETE check parent doc status |
| 3 | Code rename changes historic display                  | Detail page reads snapshot's denormalized code/name fields, not live tax_code                 | Snapshot stores `code_snapshot text`, `name_snapshot text`; query layer always selects snapshot when document.status='posted' |
| 4 | Tax report disagrees with ledger                      | Report and ledger both derive from the same snapshot rows                                     | Daily reconciliation job: SUM(snapshot.tax_amount WHERE jurisdiction=X AND period=P) == SUM(JE lines linked to those snapshots); alert on mismatch |
| 5 | company_id leak                                       | All tax tables carry company_id NOT NULL (except shared jurisdiction catalog)                 | Composite FK constraints: `(company_id, account_id)` from components → accounts; same for snapshots; existing `(company_id, code)` UNIQUE preserved |
| 6 | Inactive code accidentally used                       | Inactive codes excluded from pickers but readable on historic docs                            | Picker query: `WHERE is_active=true`; detail page reads snapshot regardless of current code.is_active |
| 7 | Tax code deletion breaks historic reports             | Physical deletion forbidden                                                                   | `ITaxCodeStore` has no Delete method (matches current behaviour); only `SetActiveAsync` |
| 8 | Cross-company tax / account / jurisdiction reference  | Per-row composite FKs                                                                         | `(company_id, account_id)` on every routing column; rejected at write via composite FK; runtime check in store layer |
| 9 | Component sequence break (drop or reorder)            | UNIQUE(tax_code_id, sequence); reordering forbidden after first transaction                   | DB constraint + check in `tax_code_components` that no `document_line_tax_snapshots` exists for the component before allowing sequence change |
| 10| Wrong jurisdiction default chosen by operator         | Picker shows jurisdiction badge + ship-to hint; can't enforce automatically without resolver  | UI surface (Phase 1); resolver (Phase 4) |
| 11| Tax-inclusive computation precision drift             | All intermediate math uses decimal(18,6); final rounding once, at snapshot persistence        | Engine contract: snapshot writes `taxable_amount`, `tax_amount` at currency.minor_unit scale only |
| 12| Reverse-charge double-counted on P&L                  | Two snapshots / two fragments / net to 0 with explicit `leg` discriminator                    | Engine emits both legs in one call; fragment builder MUST emit both or fail; integration test asserts P&L delta == 0 |
| 13| Multi-currency tax on foreign-currency invoices       | Snapshot stores tax_amount in document currency AND base-currency equivalent                  | `document_line_tax_snapshots` columns: `tax_amount`, `tax_amount_base`, `fx_rate_snapshot`; FX engine handles the conversion at posting time |
| 14| Editing default tax_code on a customer affects history| customer.default_tax_code_id is a default for NEW lines only                                  | Engine never reads customer.default_tax_code_id for existing lines; always reads line.tax_code_id |
| 15| Rate change effective mid-period mis-allocates        | Each snapshot's `rate_snapshot` is the as-of-tax-point-date rate, not period-aggregate        | Engine looks up rate per snapshot; integration test: invoice dated 2026-07-01 with rate change on 2026-07-01 uses new rate, invoice dated 2026-06-30 uses old |
| 16| Refund of a posted invoice from prior tax period      | Refund snapshot inherits original snapshot's rate; bookkeeping period is refund date          | Engine: `SalesTaxEngine.ComputeRefundAsync(originalInvoiceId)` clones the original's component rates; tax-return preview for refund period includes the refund as a negative |

---

## Deliverables

### 1. Recommended table structure

```sql
-- ===========================================================================
-- Cross-tenant catalog (NO company_id)
-- ===========================================================================

CREATE TABLE tax_jurisdictions (
    id              uuid PRIMARY KEY,
    country_code    char(2)  NOT NULL,                  -- ISO 3166-1
    region_code     text     NULL,                      -- ISO 3166-2, e.g. "CA-BC"
    city_code       text     NULL,                      -- e.g. "SF" (optional)
    display_name    text     NOT NULL,                  -- "British Columbia"
    authority_name  text     NOT NULL,                  -- "Canada Revenue Agency"
    regime_type     text     NOT NULL CHECK (
        regime_type IN ('gst','hst','pst','qst','vat','us_sales','local_sales','oss','ioss')),
    is_active       boolean  NOT NULL DEFAULT true,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    UNIQUE (country_code, region_code, city_code, regime_type)
);

CREATE TABLE tax_reporting_boxes (
    id              uuid PRIMARY KEY,
    jurisdiction_id uuid NOT NULL REFERENCES tax_jurisdictions(id),
    box_code        text NOT NULL,                      -- "101", "105", "Box 1"
    box_description text NOT NULL,
    side            text NOT NULL CHECK (
        side IN ('taxable_supplies','collected','itc','adjustment','net','instalment','balance')),
    sort_order      int  NOT NULL,
    UNIQUE (jurisdiction_id, box_code)
);

-- ===========================================================================
-- Per-company configuration (company_id NOT NULL)
-- ===========================================================================

CREATE TABLE tax_registrations (
    id                            uuid PRIMARY KEY,
    company_id                    char(7) NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
    jurisdiction_id               uuid    NOT NULL REFERENCES tax_jurisdictions(id),
    registration_number           text    NOT NULL,
    effective_from                date    NOT NULL,
    effective_to                  date    NULL,
    filing_frequency              text    NOT NULL CHECK (filing_frequency IN ('monthly','quarterly','annual')),
    reporting_calendar            text    NOT NULL DEFAULT 'calendar',
    base_currency_code            char(3) NOT NULL,
    -- Per-jurisdiction return GL routing (replaces hardcoded 5-chart-codes)
    collected_clearing_account_id   uuid REFERENCES accounts(id),
    recoverable_clearing_account_id uuid REFERENCES accounts(id),
    adjustment_account_id           uuid REFERENCES accounts(id),
    return_liability_account_id     uuid REFERENCES accounts(id),
    return_receivable_account_id    uuid REFERENCES accounts(id),
    is_active                     boolean NOT NULL DEFAULT true,
    created_at                    timestamptz NOT NULL DEFAULT now(),
    updated_at                    timestamptz NOT NULL DEFAULT now(),
    UNIQUE (company_id, jurisdiction_id, effective_from)
);

CREATE TABLE tax_codes (
    id              uuid PRIMARY KEY,
    company_id      char(7) NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
    code            text    NOT NULL,                   -- "GST_PST_BC"
    name            text    NOT NULL,                   -- "BC GST 5% + PST 7%"
    treatment       text    NOT NULL CHECK (treatment IN (
        'taxable','zero_rated','exempt','out_of_scope','reverse_charge','import_tax')),
    applies_to      text    NOT NULL CHECK (applies_to IN ('sales','purchase','both')),
    is_active       boolean NOT NULL DEFAULT true,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    UNIQUE (company_id, code)
);

CREATE TABLE tax_code_components (
    id                          uuid PRIMARY KEY,
    company_id                  char(7) NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
    tax_code_id                 uuid    NOT NULL REFERENCES tax_codes(id) ON DELETE CASCADE,
    jurisdiction_id             uuid    NOT NULL REFERENCES tax_jurisdictions(id),
    sequence                    int     NOT NULL,
    is_compound                 boolean NOT NULL DEFAULT false,
    recoverability_mode         text    NOT NULL DEFAULT 'full' CHECK (
        recoverability_mode IN ('full','partial','none')),
    recoverable_percent         numeric(5,2) NULL,      -- required when 'partial'
    payable_account_id          uuid REFERENCES accounts(id),
    recoverable_account_id      uuid REFERENCES accounts(id),
    non_recoverable_account_id  uuid REFERENCES accounts(id),
    created_at                  timestamptz NOT NULL DEFAULT now(),
    updated_at                  timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tax_code_id, sequence),
    CHECK (recoverability_mode <> 'partial' OR recoverable_percent IS NOT NULL)
);

CREATE TABLE tax_code_component_rates (
    id                  uuid PRIMARY KEY,
    component_id        uuid NOT NULL REFERENCES tax_code_components(id) ON DELETE CASCADE,
    rate_percent        numeric(9,6) NOT NULL,
    effective_from      date NOT NULL,
    effective_to        date NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    UNIQUE (component_id, effective_from)
);

CREATE TABLE tax_code_component_box_mappings (
    id              uuid PRIMARY KEY,
    component_id    uuid NOT NULL REFERENCES tax_code_components(id) ON DELETE CASCADE,
    box_id          uuid NOT NULL REFERENCES tax_reporting_boxes(id),
    side            text NOT NULL CHECK (side IN ('collected','itc')),
    UNIQUE (component_id, box_id, side)
);

-- ===========================================================================
-- Transaction snapshot — single source of truth for posted documents
-- ===========================================================================

CREATE TABLE document_line_tax_snapshots (
    id                          uuid PRIMARY KEY,
    company_id                  char(7) NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
    document_type               text    NOT NULL CHECK (document_type IN (
        'invoice','credit_note','sales_receipt','refund_receipt',
        'bill','vendor_credit','expense','journal_entry')),
    document_id                 uuid    NOT NULL,
    line_id                     uuid    NOT NULL,
    sequence                    int     NOT NULL,
    leg                         text    NOT NULL DEFAULT 'primary' CHECK (
        leg IN ('primary','self_assessed_payable','self_assessed_recoverable')),
    tax_code_id                 uuid    NOT NULL REFERENCES tax_codes(id) ON DELETE RESTRICT,
    component_id                uuid    NOT NULL REFERENCES tax_code_components(id) ON DELETE RESTRICT,
    jurisdiction_id             uuid    NOT NULL REFERENCES tax_jurisdictions(id),
    -- Denormalized snapshot fields (immutable once posted)
    code_snapshot               text    NOT NULL,
    name_snapshot               text    NOT NULL,
    regime_type_snapshot        text    NOT NULL,
    treatment_snapshot          text    NOT NULL,
    rate_percent_snapshot       numeric(9,6) NOT NULL,
    is_compound_snapshot        boolean NOT NULL,
    reporting_box_codes         text[]  NOT NULL DEFAULT '{}',
    -- Amounts in document currency
    taxable_amount              numeric(18,4) NOT NULL,
    tax_amount                  numeric(18,4) NOT NULL,
    recoverable_amount          numeric(18,4) NOT NULL DEFAULT 0,
    non_recoverable_amount      numeric(18,4) NOT NULL DEFAULT 0,
    -- Multi-currency
    document_currency_code      char(3) NOT NULL,
    tax_amount_base             numeric(18,4) NOT NULL,
    fx_rate_snapshot            numeric(18,8) NOT NULL DEFAULT 1,
    computed_at                 timestamptz NOT NULL DEFAULT now(),
    UNIQUE (document_type, document_id, line_id, sequence, leg)
);

CREATE INDEX idx_tax_snapshot_jurisdiction_period
    ON document_line_tax_snapshots (company_id, jurisdiction_id, computed_at);

-- ===========================================================================
-- Modifications to existing transaction-line tables
-- ===========================================================================
-- invoice_lines, credit_note_lines, sales_receipt_lines, refund_receipt_lines,
-- bill_lines, vendor_credit_lines:
--   KEEP: tax_amount (now: written by engine from snapshots, never by page)
--   DEPRECATED for posted lines: tax_code_id (only meaningful while draft)
--   ADD: tax_snapshot_count int — for query-side sanity check
--
-- journal_entry_lines:
--   KEEP: tax_component_type text (for backward compat during migration)
--   ADD: tax_snapshot_id uuid REFERENCES document_line_tax_snapshots(id) NULL
--   AFTER migration: tax_component_type derived from snapshot.regime_type +
--                    side; legacy column dropped in a follow-up.

-- ===========================================================================
-- Tax returns (existing table) + new sub-table for adjustments
-- ===========================================================================
-- ALTER TABLE tax_returns ADD COLUMN jurisdiction_id uuid REFERENCES tax_jurisdictions(id);
-- ALTER TABLE tax_returns ADD COLUMN registration_id uuid REFERENCES tax_registrations(id);
-- Migrate tax_regime free-text → jurisdiction_id FK.

CREATE TABLE tax_return_adjustments (
    id                  uuid PRIMARY KEY,
    company_id          char(7) NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
    tax_return_id       uuid    NOT NULL REFERENCES tax_returns(id) ON DELETE CASCADE,
    box_id              uuid    NOT NULL REFERENCES tax_reporting_boxes(id),
    amount              numeric(18,4) NOT NULL,
    note                text NOT NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    created_by_user_id  text NOT NULL
);
```

### 2. Recommended service / engine layering

```
backend/src/Modules/SalesTax/
├── Domain.Shared/
│   ├── TaxJurisdiction.cs                  (record + enum-shaped values)
│   ├── TaxRegistration.cs
│   ├── TaxCode.cs                          (renamed; carries treatment, applies_to)
│   ├── TaxCodeComponent.cs
│   ├── TaxCodeComponentRate.cs
│   ├── TaxTreatment.cs                     (enum-like static class)
│   ├── TaxRecoverabilityMode.cs
│   ├── DocumentLineTaxSnapshot.cs          (read model)
│   └── TaxReportingBox.cs
├── Application.Contracts/
│   ├── ISalesTaxEngine.cs                  (ComputeAsync)
│   ├── ISalesTaxFragmentBuilder.cs         (Build / BuildReversal)
│   ├── ITaxJurisdictionCatalog.cs          (read-only; queries the cross-tenant catalog)
│   ├── ITaxRegistrationStore.cs            (per-company CRUD)
│   ├── ITaxCodeStore.cs                    (per-company CRUD; replaces existing)
│   ├── ITaxReportingEngine.cs              (Sales Tax Summary / Detail queries)
│   ├── ITaxReturnPreviewEngine.cs          (jurisdiction × period → box totals)
│   └── ITaxResolver.cs                     (Phase 4 — pluggable suggestion)
├── Application/
│   ├── SalesTaxEngine.cs                   (the real implementation; replaces NullTaxEngine)
│   ├── SalesTaxFragmentBuilder.cs
│   ├── TaxReportingEngine.cs
│   ├── TaxReturnPreviewEngine.cs
│   └── DefaultTaxResolver.cs               (Phase 4 — Tralanz-internal default)
└── Infrastructure/
    ├── Persistence/
    │   ├── PostgreSqlTaxJurisdictionCatalog.cs
    │   ├── PostgreSqlTaxRegistrationStore.cs
    │   ├── PostgreSqlTaxCodeStore.cs       (rewrites existing)
    │   ├── PostgreSqlTaxSnapshotRepository.cs
    │   └── PostgreSqlTaxReportingRepository.cs
    └── Adapters/
        ├── AvalaraTaxResolver.cs           (Phase 4)
        └── TaxJarTaxResolver.cs            (Phase 4)
```

`DefaultPostingSupport.cs` is rewired:
- `NullTaxEngine` → constructor-injected `ISalesTaxEngine`
- Each per-document fragment builder (invoice / bill / credit / etc.) calls `ISalesTaxFragmentBuilder.Build(snapshots, side)` instead of inline single-account routing.
- The existing `PostingFragment.TaxComponentType` string stays (backward compat) but is now populated from `snapshot.regime_type + '_' + side`.

The Posting Engine itself (the orchestrator that aggregates fragments into a JE) does not change shape — the change is entirely on the tax side of the pipeline.

### 3. Recommended UI structure

```
/settings/sales-taxes                       (SalesTaxesPage — replaces TaxRatesPage)
    list: code / name / treatment / components-summary (badges) / status / actions

/settings/sales-taxes/new                   (SalesTaxEditorPage — create)
/settings/sales-taxes/{id}                  (SalesTaxEditorPage — edit)
    sections:
      - Header (code, name, treatment, applies_to, active)
      - Components grid (sequence, jurisdiction picker, recoverability, GL routing per component, reporting-box mappings)
      - Effective-dated rate history per component (read-only table with "Add new effective date" button)
      - Preview pane: "$100 sale" / "$100 bill" worked example

/settings/tax-registrations                 (TaxRegistrationsPage)
    list: jurisdiction / registration # / effective from-to / frequency / status / GL routing
    inline editor for the per-jurisdiction return GL quintet

/sysadmin/tax-jurisdictions                 (TaxJurisdictionsPage — sysadmin only)
    catalog editor — only for Tralanz operators adding new regimes; tenants read-only

/sysadmin/tax-jurisdictions/{id}/boxes      (TaxReportingBoxesPage — sysadmin only)
    box catalog per jurisdiction (CRA GST34 boxes, Quebec FPZ-500, etc.)

/reports/sales-tax-summary                  (SalesTaxSummaryReportPage)
    filters: jurisdiction (multi-select), period, side
    columns: period / jurisdiction / side / treatment / taxable amount / tax amount

/reports/sales-tax-detail                   (SalesTaxDetailReportPage)
    one row per snapshot; CSV export

/tax-returns                                (TaxReturnsPage — existing; minor reshape)
    add jurisdiction column (replacing free-text regime)
    add "New return from preview" button

/tax-returns/preview                        (TaxReturnPreviewPage — new)
    parameters: jurisdiction, period
    output: jurisdiction-specific box layout pre-filled from snapshots
    "Create draft from preview" → /tax-returns/{newId}

/tax-returns/{id}                           (TaxReturnDetailPage — existing; expanded)
    add tax_return_adjustments grid
    post action emits per-jurisdiction JE
```

`TaxCodePicker` (used by 12+ create pages today, per audit) grows:
- Jurisdiction badge ("CRA GST", "RQ QST", "BC PST")
- Multi-component preview on hover ("GST 5% + PST 7% = 12%")
- Ship-to address hint when invoked from invoice/sales-receipt forms

### 4. Recommended test list

Unit tests on `SalesTaxEngine.ComputeAsync`:

| #  | Scenario                                                | Assertion                                                                    |
|----|---------------------------------------------------------|------------------------------------------------------------------------------|
| 1  | Single-component GST 5% on $100 sale                    | snapshot count=1, tax_amount=5.00, line.tax_amount=5.00                      |
| 2  | Multi-component GST + PST_BC on $100 sale               | snapshot count=2, tax_amounts=[5.00, 7.00], line.tax_amount=12.00            |
| 3  | Compound QC GST + QST on $100 sale (pre-2013 era rates) | snapshot 1: GST=5.00, snapshot 2: QST on (100+5)=9.4725 → rounded 9.47       |
| 4  | Partial-recoverability bill (PST_BC, 50% rec) on $100   | snapshot.tax=7.00, recoverable=3.50, non_recoverable=3.50                    |
| 5  | Reverse-charge VAT bill on $1000 EU service             | TWO snapshots (one payable, one recoverable), each $200; P&L impact 0        |
| 6  | Zero-rated sale on $500 grocery                         | snapshot count=1, tax_amount=0, treatment_snapshot='zero_rated'              |
| 7  | Exempt sale on $1000 residential rent                   | snapshot count=1, tax_amount=0, treatment_snapshot='exempt', no box mapping  |
| 8  | Out-of-scope employee reimbursement on $50              | snapshot count=0, no tax line emitted                                        |
| 9  | Tax-inclusive invoice gross $112 with 12% effective     | taxable_amount=100.00, tax_amount=12.00, snapshot reconciles                 |
| 10 | Effective-dated rate change                             | Invoice dated 2026-06-30 → rate=7%; same code dated 2026-07-01 → rate=8%     |
| 11 | Editing tax_code after posting                          | Snapshot row unchanged; picker shows new code/rate on draft only             |
| 12 | Voiding posted invoice                                  | NEW snapshot rows with negated amounts, original rows untouched              |
| 13 | Multi-jurisdiction company quarterly close              | 4 separate return previews, each filtered by jurisdiction                    |
| 14 | Re-post of voided then reissued bill                    | Uses snapshot account_ids (from snapshot table), not current tax_code routing|

Unit tests on `SalesTaxFragmentBuilder.Build`:

| 15 | Snapshot count=2 → fragment count=2                      | Each fragment routes to its snapshot's stored payable/recoverable account    |
| 16 | Reverse-charge snapshot → BOTH legs produce fragments    | Total fragment debits == total credits                                       |
| 17 | Non-recoverable portion folds into expense fragment      | Bill fragment for non_recoverable_amount has account = expense, not tax     |

Integration tests:

| 18 | CRA GST34 preview for Q1 2026                            | Boxes 101, 103, 105, 106, 108, 109 populated; sums match snapshots          |
| 19 | Posting a GST34 return                                   | JE balanced; clears period's collected + recoverable; emits net liability    |
| 20 | Reconciliation job daily                                 | Σ snapshots per (jurisdiction, period) == Σ JE lines linked via snapshot_id  |
| 21 | Schema drift: registration_number migrated from tax_codes to tax_registrations | One-shot data migration script idempotent; old column dropped safely |
| 22 | Cross-company isolation                                  | Inserting a snapshot with mismatched (company_id, account_id) rejected by FK |
| 23 | Effective rate boundary                                  | Rate change effective 2026-07-01: invoice dated end-of-day 2026-06-30 uses old; 2026-07-01 00:00 uses new (UTC-based comparison) |
| 24 | Foreign-currency invoice with tax                        | snapshot.tax_amount in doc currency; tax_amount_base = tax × fx_rate; GL posts in base |

### 5. First batch execution order

The MVP scope (Phase 1, Canada-only) breaks into 7 batches. Each ships behind a feature flag (`sales_tax_v2_enabled`) so the legacy `tax_codes` table keeps serving until the migration is verified end-to-end.

**Batch S1 — Foundation (schema + seed + data migration)**
- Create tables: tax_jurisdictions, tax_registrations, tax_code_components, tax_code_component_rates, tax_reporting_boxes, tax_code_component_box_mappings, document_line_tax_snapshots, tax_return_adjustments.
- Seed CA federal + 10 provinces + GST34 reporting boxes.
- Schema drift fix: registration_number column move from tax_codes to tax_registrations.
- Data migration script: each existing tax_code → new tax_codes row + 1 tax_code_components row + 1 tax_code_component_rates row, jurisdiction inferred from code-name heuristic (operator confirms via SysAdmin one-time wizard if ambiguous).
- Backfill snapshots for existing posted documents: read each invoice_lines / bill_lines row, write a snapshot row with rate=tax_amount/lineAmount.
- Verification: snapshot row count == posted line count with non-zero tax.

**Batch S2 — Engine (compute + write snapshots)**
- Implement `SalesTaxEngine.ComputeAsync` (single-component support only, mirrors current math).
- Wire engine into existing create pages: pages now call engine.ComputeAsync at save time instead of computing tax_amount inline.
- Engine writes snapshot rows on draft save.
- Pages stop persisting tax_amount directly; it's derived from snapshots.
- Verification: existing posted documents' totals unchanged; new draft documents' snapshots present and reconcile.

**Batch S3 — UI rename (Tax Rates → Sales Taxes)**
- Rename TaxRatesPage → SalesTaxesPage; route /settings/tax-rates → /settings/sales-taxes with a permanent redirect.
- New SalesTaxEditorPage replaces inline form; still single-component, but introduces the "Components" grid (one row pre-filled).
- TaxCodePicker: jurisdiction badge.
- Verification: all 12+ pages using TaxCodePicker render unchanged.

**Batch S4 — Multi-component**
- SalesTaxEditorPage components grid: "Add component" button; per-row recoverability + GL routing.
- Engine: multi-component pass with compound flag support.
- End-to-end smoke: BC operator creates GST_PST_BC code → uses on a $100 invoice → snapshot count=2 → JE has separate Cr lines for GST payable and PST payable.

**Batch S5 — Snapshot immutability + fragment builder rewrite**
- SalesTaxFragmentBuilder.Build reads snapshot's stored account IDs (not live JOIN to tax_codes).
- DB trigger on document_line_tax_snapshots: BEFORE UPDATE OR DELETE checks parent document status.
- Detail pages read snapshot's `code_snapshot` / `name_snapshot` / `rate_percent_snapshot` for posted documents.
- Verification: edit a posted invoice's tax_code → snapshot unchanged → detail page still shows original code.

**Batch S6 — Tax registrations + per-jurisdiction return routing**
- TaxRegistrationsPage CRUD.
- ALTER tax_returns: add jurisdiction_id + registration_id FKs; data-migrate from free-text regime.
- Per-jurisdiction GL quintet replaces hardcoded 5 chart codes in tax-return posting (`PostgresTaxReturnDocumentRepository.cs:13-26`).
- Verification: multi-regime company files GST34 + QC FPZ-500 in same quarter; each posts to its own GL accounts.

**Batch S7 — Reporting + return preview**
- ITaxReportingEngine: Sales Tax Summary + Detail queries.
- TaxReturnPreviewEngine: jurisdiction × period → box totals (CRA GST34 first).
- TaxReturnPreviewPage UI.
- Daily reconciliation job (snapshots vs JE).
- Verification: end-of-quarter close walkthrough produces a populated GST34 draft matching ledger.

Each batch is independently shippable and individually verifiable. The `sales_tax_v2_enabled` flag can roll out per-company so a small set of beta tenants validates before general rollout.

---

## Decisions confirmed (2026-05-29 review)

| # | Question | Decision | Implication for design |
|---|----------|----------|------------------------|
| 1 | Historical migration ambiguity | **Default + post-fix in UI** | S1 migration script applies a heuristic-based default (e.g. `TAX_13` → CRA HST), inserts an audit-log entry, and flags the migrated row in `tax_codes.needs_jurisdiction_review = true`. SalesTaxesPage shows a yellow "Needs jurisdiction confirmation" badge on flagged rows; operator clicks → editor → confirms → flag clears. No upfront wizard blocks the migration. |
| 2 | Manual JE tax snapshot | **Strictly required** | JournalEntryCreatePage / JournalEntryReviewPage line editors gain TaxCodePicker WHEN the picked account.account_subtype ∈ (`sales_tax_payable`, `input_tax_recoverable`, `non_recoverable_tax`, `tax_clearing`). Save is blocked without tax_code_id + amount split. Engine reads JE lines and writes snapshot rows just like invoice/bill lines. Tax-return preview's reconciliation guarantee holds. |
| 3 | Item-level default tax | **Per-region mapping** | New table `item_default_tax_codes (company_id, item_id, region_code, tax_code_id)`. Lookup order on invoice line: (1) operator override on the line, (2) item_default_tax_codes[customer.ship_to_region], (3) items.default_tax_code_id (global fallback). ItemsPage editor gets a "Region-specific defaults" sub-grid; empty by default. |
| 4 | Reporting box mapping UI | **Per-jurisdiction template + per-component override** | SysAdmin defines `tax_jurisdiction_box_templates (jurisdiction_id, treatment, side, default_box_codes[])`. Creating a component auto-fills box mappings from the template; component editor has an "Override boxes" toggle. Reduces 90% of operator clicks; specialists can still override for unusual cases (e.g. partial-recoverability splits across two boxes). |
| 5 | Tax pricing mode default | **Document-level `tax_pricing_mode='exclusive'` default** | All documents default to tax-exclusive. Each document header gets a `Pricing mode: Exclusive ▾` dropdown so EU/retail flows switch to inclusive per-document. No jurisdiction-based auto-detect (predictability wins over magic). |
| 6 | Reverse charge phasing | **Included in Phase 1** | Schema, engine path, and fragment builder all support `treatment=reverse_charge` from day one. Phase 1 test scenarios include reverse-charge sale (#5 in test list). EU VAT in Phase 3 only needs to add jurisdictions + treatment selection in UI; the path is already exercised. Estimated incremental cost: +1 engine method (`ComputeReverseChargeAsync`), +2 integration tests. |

### Schema additions driven by these decisions

```sql
-- Decision 1: flag for migrated rows that need operator review
ALTER TABLE tax_codes ADD COLUMN needs_jurisdiction_review boolean NOT NULL DEFAULT false;

-- Decision 3: per-region item default
CREATE TABLE item_default_tax_codes (
    id              uuid PRIMARY KEY,
    company_id      char(7) NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
    item_id         uuid    NOT NULL REFERENCES items(id) ON DELETE CASCADE,
    region_code     text    NOT NULL,                     -- ISO 3166-2, matches tax_jurisdictions.region_code
    tax_code_id     uuid    NOT NULL REFERENCES tax_codes(id) ON DELETE RESTRICT,
    created_at      timestamptz NOT NULL DEFAULT now(),
    UNIQUE (company_id, item_id, region_code)
);

-- Decision 4: jurisdiction box mapping template
CREATE TABLE tax_jurisdiction_box_templates (
    id                  uuid PRIMARY KEY,
    jurisdiction_id     uuid NOT NULL REFERENCES tax_jurisdictions(id),
    treatment           text NOT NULL,        -- 'taxable' / 'zero_rated' / 'reverse_charge' / ...
    side                text NOT NULL CHECK (side IN ('collected','itc')),
    default_box_codes   text[] NOT NULL,
    UNIQUE (jurisdiction_id, treatment, side)
);

-- Decision 4: per-component override flag (defaults to "use template")
ALTER TABLE tax_code_components ADD COLUMN box_mapping_overridden boolean NOT NULL DEFAULT false;

-- Decision 5: per-document pricing mode (default exclusive)
ALTER TABLE invoices       ADD COLUMN tax_pricing_mode text NOT NULL DEFAULT 'exclusive' CHECK (tax_pricing_mode IN ('exclusive','inclusive'));
ALTER TABLE bills          ADD COLUMN tax_pricing_mode text NOT NULL DEFAULT 'exclusive' CHECK (tax_pricing_mode IN ('exclusive','inclusive'));
ALTER TABLE credit_notes   ADD COLUMN tax_pricing_mode text NOT NULL DEFAULT 'exclusive' CHECK (tax_pricing_mode IN ('exclusive','inclusive'));
ALTER TABLE vendor_credits ADD COLUMN tax_pricing_mode text NOT NULL DEFAULT 'exclusive' CHECK (tax_pricing_mode IN ('exclusive','inclusive'));
-- (sales_receipts / refund_receipts / expenses likewise)
```

### Batch-order impact

- **S1** now also seeds `tax_jurisdiction_box_templates` for the 4 CA regimes (GST / HST / PST / QST) so component editor "Use template" works out of the box.
- **S2** engine adds `ComputeReverseChargeAsync` path (Decision 6).
- **S3** SalesTaxEditorPage gets the "Override boxes" toggle and the "Needs jurisdiction confirmation" flag UI.
- **S4** ItemsPage editor gets the region-defaults sub-grid (Decision 3).
- **S5** JournalEntryCreatePage / JournalEntryReviewPage line editors gain conditional TaxCodePicker; engine reads JE → snapshots (Decision 2).
- **S6** Tax registrations CRUD already in plan; no change.
- **S7** Reporting layer treats reverse-charge snapshots correctly (both legs in their respective boxes); reconciliation job alerts on JE-without-snapshot tax accounts (Decision 2 enforcement).

No phase-shift: still 7 batches to MVP. Two batches grew (S3, S5); none added.
