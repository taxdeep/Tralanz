# AI Product Architecture (unityAI) V1

This document is the executable architecture spec for Citus's AI capability layer, marketed and named **unityAI**.

It is subordinate to [`CITUS_PRODUCT_ENGINEERING_AUTHORITY.md`](./CITUS_PRODUCT_ENGINEERING_AUTHORITY.md). If any statement here conflicts with the authority doc, the authority doc wins.

Authority order:

`CITUS_PRODUCT_ENGINEERING_AUTHORITY.md > this document > module-level READMEs > task notes > temporary implementation habits`

Sibling specs that this document coordinates with:

- [`MULTI_COMPANY_AUTH_AND_CONTROL_SPEC.md`](./MULTI_COMPANY_AUTH_AND_CONTROL_SPEC.md) — company isolation rules apply to every AI/learning surface
- [`POSTING_TAX_FX_ENGINE_EXECUTION_SPEC.md`](./POSTING_TAX_FX_ENGINE_EXECUTION_SPEC.md) — posting truth that AI may suggest into but never own
- [`AP_AR_LIFECYCLE_CONTROL_SPEC.md`](./AP_AR_LIFECYCLE_CONTROL_SPEC.md) — document lifecycles AI drafts must respect
- [`UI_NAVIGATION_AND_DESIGN_SYSTEM_SPEC.md`](./UI_NAVIGATION_AND_DESIGN_SYSTEM_SPEC.md) — surfaces where unityAI output appears

## 1. Product Philosophy

### 1.1 Citus is an accounting system first

Citus exists to keep books correctly. unityAI exists to make that work *faster* — never *less correct*.

The accounting source of truth is, and remains:

- The Posting Engine (`backend/src/Engines/Posting/`)
- The Tax Engine (`ITaxEngine` in `Citus.Accounting.Application/Abstractions/PostingEngineContracts.cs`)
- The FX Engine (`backend/src/Engines/FX/`)
- The Journal Entry workflow (`backend/src/Modules/GL/JournalEntry/`)
- The AR/AP/Inventory/Costing/Payment/Reconciliation modules under `backend/src/Modules/`
- The permission, company-isolation, and audit-trail primitives in `Citus.Platform.Core`

unityAI never replaces these. It assists, summarizes, recommends, prepares drafts, and explains. It must never bypass backend validation, posting rules, lifecycle gates, or company isolation.

### 1.2 The unityAI control loop

Every unityAI-assisted action follows the same loop:

1. User intent — typed, clicked, or implicit (e.g. opening a picker).
2. unityAI understands the intent.
3. The Learning Module supplies company-specific habits as ranking hints / suggestions.
4. The Business Truth Layer validates company, permission, tax, accounting, lifecycle, and posting rules.
5. The system creates a *suggestion* or *draft*.
6. The user confirms when confirmation is required.
7. The Posting Engine (or the relevant business engine) records the outcome.
8. The audit trail (`PlatformAuditEvent`) records who, what, when, why.

unityAI may compress steps 1–5. It cannot replace step 7 or step 8.

### 1.3 Defaults are conservative

- AI external calls are **off** by default.
- AI-generated suggestions are **pending** by default.
- Learning is **company-scoped** by default; cross-company is forbidden.
- Decision tracing is **sample-based** by default to bound cost and storage.
- High-risk actions always require explicit user confirmation.

## 2. Four-Layer Architecture

Citus AI is organized into four layers. Each has different ownership, different storage discipline, and different change cadence.

```
+------------------------------------------------------------+
|  Layer D - AI Output Module                                 |
|  unitysearch recs, dashboard suggestions, Action Center,    |
|  insights, copilot drafts, OCR output, NL command UI.       |
+------------------------------------------------------------+
|  Layer C - AI Learning Module                               |
|  behavior events, usage stats, pair stats, learning         |
|  profiles, ranking hints, summaries.                        |
+------------------------------------------------------------+
|  Layer B - AI Infrastructure Layer (unityAI Gateway)        |
|  gateway, router, prompts, tools, jobs, request logs,       |
|  decision traces, structured-output validation, flags.      |
+------------------------------------------------------------+
|  Layer A - Business Truth Layer                             |
|  Posting / Tax / FX / Lifecycle / Permission / Audit.       |
|  Authoritative. AI-immutable.                               |
+------------------------------------------------------------+
```

Data flow rules between layers:

- Layer A is the only writer of accounting truth.
- Layer B is the only egress point for AI provider calls.
- Layer C reads from Layer A's read models (and the user-event stream) and writes only to its own learning tables. It never writes to journal/document tables.
- Layer D reads from Layer C, may call Layer B, and submits drafts/suggestions back into Layer A through the same command paths a human user would use — including all Layer A validation.

### 2.1 Business Truth Layer

This layer is authoritative and AI-immutable. Existing Citus engines and modules form it; this document does not redefine them.

Responsibilities:

- Decide whether an accounting action is legal.
- Validate `CompanyId` consistency on every write path.
- Validate user permission via the Citus permission engine.
- Validate Chart-of-Accounts entries, tax codes, customers, vendors, products/services, inventory items.
- Enforce document lifecycle (draft → posted → void/credit) per `AP_AR_LIFECYCLE_CONTROL_SPEC.md`.
- Validate posting balance, period open/closed status, inventory availability, costing, payment application, reconciliation status.
- Record `PlatformAuditEvent` for every state change.

Concrete locations:

| Concern | Code |
|---|---|
| Posting | `backend/src/Engines/Posting/`, `Citus.Accounting.Infrastructure/Posting/DefaultPostingEngine.cs` |
| Tax | `ITaxEngine` in `PostingEngineContracts.cs` |
| FX | `backend/src/Engines/FX/`, `IFxResolutionService` |
| Journal Entry workflow | `backend/src/Modules/GL/JournalEntry/` |
| AR / AP modules | `backend/src/Modules/AR/`, `backend/src/Modules/AP/` |
| Audit | `Citus.Platform.Core/Runtime/PlatformAuditEvent.cs` |

unityAI must never replicate these rules client-side or in prompts. unityAI must always defer to backend validation results.

### 2.2 AI Learning Module

Project namespace: `Citus.Modules.Learning.*`.

Responsibilities:

- Record user behavior events (action type, entity type, context, timestamp, `CompanyId`, `UserId`).
- Aggregate events into usage stats, pair stats, recent-query stats, recent-selection stats.
- Run periodic AI-assisted summarization to produce **learning profiles**: structured, company-scoped, human-readable summaries of habits.
- Emit ranking hints, dashboard widget suggestions, alias suggestions, no-match query classifications.

Submodules:

1. **Behavior Learning Engine** — generic event sink for user actions across Citus shells.
2. **UnitySearch Learning** — learns vendor / customer / account / product / tax-code / payment-account selections (extends the existing `Modules/UnitySearch/`).
3. **Report Usage Learning** — learns which reports a user opens, filters, exports, prints, drills into.
4. **Dashboard Preference Learning** — learns which widgets a user adds, removes, clicks, accepts, dismisses.
5. **Task / Action Pattern Learning** — learns how a user typically processes the Action Center queue. Generates *soft* signals only; never compliance obligations.
6. **AI Summary Worker** — scheduled job that turns recent stats into structured `LearningProfile` rows.

The Learning Module **observes and summarizes**. It does not post transactions, file tax, pay bills, or modify accounting records.

Storage tables (proposed; company-scoped):

- `behavior_events`
- `unitysearch_usage_stats`
- `unitysearch_pair_stats`
- `report_usage_events`
- `dashboard_widget_events`
- `learning_profiles`
- `ranking_hints`
- `widget_suggestions`

### 2.3 AI Output Module

Project namespace: `Citus.Modules.UnityAi.*` (output surfaces) and existing `Modules/UnitySearch/` (recommendation extensions).

Responsibilities:

- Convert learning + rules + AI analysis into user-visible output.
- Tag every output with `source` (`rule` | `learning` | `ai`) and a `confidence` if AI-derived.
- Provide every output with a *reason* string and *evidence* references.
- Make every AI-derived output dismissible and reversible where appropriate.

Surfaces:

1. **UnitySearch Recommendation** — vendor, customer, account, product/service, tax code, payment account.
2. **Dashboard Widget Suggestions** — homepage widget proposals (pending until accepted).
3. **Action Center / Task List** — rule-based and AI-assisted tasks.
4. **Report Insights** — narrative explanations of trends, unusual changes, risks.
5. **Accounting Copilot** — natural-language assistant (future; see §6).
6. **OCR / Document Extraction Output** — extracted fields from receipts, invoices, bills, statements.
7. **Natural-Language Draft Builder** — converts user commands into reviewable drafts.
8. **AI Explanation Output** — answers "why am I seeing this?" for every other surface.

unityAI Output may suggest, prepare, explain, and guide. It must not silently mutate accounting truth. High-risk actions require user confirmation and Layer A validation. Low-risk surfaces (e.g. homepage suggestion ribbon) may be ambient but must remain dismissible.

### 2.4 AI Infrastructure Layer

Project namespace: `Citus.Modules.UnityAi.Gateway.*`.

This layer makes unityAI safe, provider-agnostic, traceable, and non-black-box. Business modules **never** call an AI provider directly — they call the unityAI Gateway.

Components:

- **AI Gateway** — single egress point for all AI calls.
- **Model Router** — routes a task type to an appropriate model/provider.
- **Prompt Registry** — versioned, hash-pinned prompt templates.
- **Tool Registry** — schemas for tool/function calls available to AI.
- **AI Job Runs** — durable record of every background AI job.
- **AI Request Logs** — redacted request/response log per call.
- **Decision Traces** — structured trace of *why* a recommendation/task was generated.
- **Cost Tracking** — token and cost per company, per task type, per model.
- **Feature Flags** — see §10.
- **Structured Output Validator** — validates JSON/strongly-typed responses against a schema; rejects free-form output for tool calls.
- **Safety Policy Engine** — applies redaction, PII masking, refusal policies before payload egress.
- **Audit Integration** — every gateway call that influences a user-visible action emits a `PlatformAuditEvent` with the run id.

Wrong:

```
Invoice module ──▶ OpenAI directly
UnitySearch  ──▶ Anthropic directly
OCR module   ──▶ Gemini directly
```

Correct:

```
Business module ──▶ unityAI Gateway ──▶ Model Router ──▶ Provider Adapter
```

A `NoopProvider` ships first. Real providers are added one at a time, behind feature flags.

## 3. UnitySearch as the Closed-Loop Reference

UnitySearch already exists at `backend/src/Modules/UnitySearch/` with the standard Citus DDD layout (`Domain.Shared`, `Domain`, `Application.Contracts`, `Application`, `Blazor`). It has search engine, policy registry, recent-query and recent-selection records, and is integrated into `Citus.Accounting.Api` and `Infrastructure.PostgreSQL`.

It does **not** yet have AI/learning behavior. This document defines that extension as the canonical Learning + Output closed loop.

### 3.1 UnitySearch Learning

Records (all company-scoped):

- query string, canonicalized query, entity type, context label
- selected result, rank position, result count, no-match flag
- `create-new` action when the user creates a missing entity
- anchor entity (for pair-stat learning, e.g. anchor=vendor when picking a category)
- user, company, session, timestamp

Learns:

- frequent selections per (user, company, entity type, context)
- recent selections (decayed)
- pair stats: vendor → category, vendor → tax code, vendor → payment account, customer → product/service, product/service → revenue account
- alias terms (user typed "amzn", selected "Amazon")
- no-match classifications (typed "amazon", got nothing → suggest creating)

### 3.2 UnitySearch Output

Outputs (all attached to a `decision_trace_id` when AI-derived):

- recommended vendors / customers / accounts / products / tax codes / payment accounts
- reason text, ranking score explanation, learned-pattern attribution
- alias suggestions, no-match suggestions

Worked example. User selects Vendor = `Amazon`. UnitySearch later recommends:

- Category: `Office Supplies` — *Frequently used with Amazon in 14 of the last 30 expense entries.*
- Tax Code: `GST` — *Standard tax code for Amazon under this company since 2025-09-12.*
- Payment Account: `RBC Visa` — *7 of the last 10 Amazon expenses settled via this account.*

### 3.3 UnitySearch Hard Boundaries

These are non-negotiable:

- Context scope (entity type, company, permission filter) is decided **before** ranking.
- AI hints **rerank**; they do not **expand** scope.
- AI hints cannot recommend cross-company entities, inactive entities, or entities the user lacks permission to use.
- Final validation remains backend-owned — picking a tax code in the UI does not commit to using it; the posting validator still has final say.
- Frontend code must not decide accounting legality.

## 4. Dashboard Intelligence

Today the dashboard lives inside `Citus.Business.Blazor/Components/`. As Dashboard Intelligence formalizes, dashboard composition + suggestions will move into a dedicated `Citus.Modules.Dashboard.*` module. The shell renders it.

### 4.1 Report Usage Learning

Tracked report events (each with `CompanyId`, `UserId`, `ReportType`, `Filters`, timestamp):

- `report_opened`
- `report_filtered`
- `report_exported`
- `report_printed`
- `report_drilldown_clicked`
- `report_added_to_dashboard`
- `report_removed_from_dashboard`
- `report_suggestion_accepted`
- `report_suggestion_dismissed`

The AI Summary Worker turns a window of these into a `report_usage_profile` per (company, user, role).

### 4.2 Widget Suggestions

The dashboard suggestion service (Layer D) reads `report_usage_profile` and proposes widgets such as:

- AR Aging, AP Aging, Cash Balance
- Profit & Loss, Revenue/Expenses This Month
- GST/HST/PST/QST Payable
- Bills Due, Open Invoices
- Bank Reconciliation Status, Unmatched Bank Transactions
- Sales Tax Filing Status

Rules:

- Suggestions are **pending** until accepted.
- The system never silently mutates dashboard layout.
- Each suggestion stores `reason`, `evidence`, `confidence`, `source`.
- Each suggestion supports `accept`, `dismiss`, `snooze`.
- Accept/dismiss/snooze events feed back into `dashboard_widget_events` for learning.

Example (UX text):

> "You viewed AR Aging 8 times in the last 30 days. Add it to your dashboard?"
>
> [Add] [Dismiss] [Snooze 7 days] [Why am I seeing this?]

## 5. Action Center / Task List

Project namespace: `Citus.Modules.ActionCenter.*`.

The Action Center answers a single question for the user: **"What should I do next?"**

### 5.1 Rule-based tasks (deterministic)

Rule-based tasks are generated by the relevant business module — not by AI. Authority for each rule lives in the originating module.

Examples by category:

- **Compliance / Tax** — GST/HST/PST/QST filing period approaching, sales-tax balance to review, payroll remittance due, T4/T4A year-end pending.
- **AR** — invoices overdue, draft invoices not sent, customer payments unapplied, customer credit balances.
- **AP** — bills due soon, overdue bills, vendor credits available, bills pending approval.
- **Banking** — unmatched bank transactions, reconciliation overdue, old unreconciled items, bank feed disconnected.
- **System Setup** — SMTP not configured, sales-tax setup incomplete, invoice template missing logo, payment gateway not configured, company profile incomplete.
- **Inventory** — low stock, pending receipts, pending shipments, inventory costing discrepancy.

These are **always** rule-driven. They never require AI to be enabled to appear.

### 5.2 AI-assisted tasks (soft)

AI may surface additional tasks, but these must be marked `source = ai`, carry a `confidence` score, and remain dismissible. Examples:

- "Advertising expenses increased sharply. Review Expense Report?"
- "Customer ABC is paying later than usual. Review AR Aging?"
- "You often check GST Payable near month-end. Add the GST Payable widget?"
- "Several bills are due soon and cash balance is low. Review cash position?"

AI-suggested tasks may not mutate accounting data. They link to a review page or report.

### 5.3 Task explainability

Every task — rule or AI — must answer "Why am I seeing this?" with a structured payload, not a free-form sentence.

Example task:

```
Title:        Pay 3 bills due this week
Source:       rule (Citus.Modules.AP.AgingRules)
Reason:       3 unpaid posted bills with due dates within the next 7 days.
Evidence:
  - BILL-0012  due 2026-04-28  $450.00
  - BILL-0015  due 2026-04-30  $1,200.00
  - BILL-0016  due 2026-05-01  $80.00
Action:       /bills/pay-bills
```

### 5.4 Task record shape

Proposed table `action_center_tasks`:

| Column | Notes |
|---|---|
| `company_id` | Required. Every task is company-scoped. |
| `assigned_user_id` | Nullable; null = visible to all members of a role. |
| `task_type` | Stable string id, e.g. `ap.bill.due_soon`. |
| `source` | `rule` \| `learning` \| `ai`. |
| `source_engine` | Originating module name. |
| `source_object_type`, `source_object_id` | Optional pointer back to the underlying entity. |
| `title`, `description`, `reason` | Human-readable text. |
| `evidence_json` | Structured evidence used to render the "Why?" panel. |
| `priority` | `low` \| `medium` \| `high`. |
| `due_date` | Nullable; rule-derived where applicable. |
| `action_url` | Where the user goes to act. |
| `status` | `open` \| `in_progress` \| `done` \| `dismissed` \| `snoozed` \| `expired` \| `blocked`. |
| `fingerprint` | Idempotency key. Prevents duplicate tasks for the same underlying state. |
| `confidence` | Nullable; required when `source = ai`. |
| `decision_trace_id` | Nullable; required when `source = ai`. |
| `created_at`, `updated_at` |  |

Fingerprint construction must include `company_id`, `task_type`, and the natural-key fragment of the underlying state (e.g. for "bills due", a sorted hash of the unpaid bill ids in the window) so that re-running the rule does not produce duplicates.

## 6. Accounting Copilot Direction

The Accounting Copilot is a **future** surface. This document fixes its boundaries; it does not authorize implementation yet.

Target user moment:

> "Yesterday I used RBC Visa to buy office supplies from Amazon for 35.20 including GST."

Pipeline:

1. Parse user intent (Layer B, model router → command-parse model).
2. Determine action type (e.g. `create_expense`).
3. Resolve vendor through UnitySearch (Layer D, with Layer C hints).
4. Resolve payment account through UnitySearch.
5. Resolve category through UnitySearch.
6. Resolve tax code via tax rules + learned habits.
7. Build an `ExpenseDraft`.
8. Validate through Layer A (the same validators a UI form would use).
9. Show the user a preview.
10. User confirms.
11. Layer A saves the draft or posts according to company policy.
12. Audit trail records the action with the unityAI run id.

### 6.1 AI Action Levels

unityAI surfaces declare an action level. Higher levels require explicit company opt-in, stricter feature flags, and stricter audit.

| Level | Capability | MVP? |
|---|---|---|
| 0 | Read-only — explain, summarize, answer questions | yes |
| 1 | Suggest-only — recommend vendor / account / tax / report / task | yes |
| 2 | Create draft — invoice / bill / expense / journal-entry draft, never posted | yes (Phase 7) |
| 3 | Prepare posting — generate posting preview; user must confirm | future |
| 4 | Auto-post under policy — low-risk only, amount thresholds, owner approval, full audit | future, off by default |

Level 4 is **explicitly out of MVP** and out of V1. It is mentioned only so the layering does not have to be redesigned later.

## 7. unityAI Gateway and Model Routing

### 7.1 Gateway responsibilities

The gateway owns:

- provider selection
- model selection per task type
- prompt versioning (hash-pinned)
- structured output validation against task-specific schemas
- token / cost tracking per company and per task type
- timeout, retry, fallback (with provider downgrade)
- redaction (PII, customer names where the task does not require them)
- request logging, response logging
- safety policy (refusal categories, payload size limits)
- feature flag gating (a task disabled by flag returns a `Disabled` result, not an exception)

The gateway interface (Phase-2 scaffolding):

```csharp
namespace Citus.Modules.UnityAi.Gateway;

public interface IUnityAiGateway
{
    Task<UnityAiResult<TResponse>> ExecuteAsync<TRequest, TResponse>(
        UnityAiTaskType taskType,
        TRequest request,
        UnityAiInvocationContext context,
        CancellationToken cancellationToken);
}
```

`UnityAiInvocationContext` carries `CompanyId`, `UserId`, optional `ScopeLabel`, and a `RunId` so the gateway can correlate logs and audit events.

### 7.2 Model router

Tasks are routed by type. The router is the only place that maps task → model.

| Task class | Model class | Examples |
|---|---|---|
| Cheap / mid | small text models | unitysearch_learning_summary, report_usage_summary, widget_recommendation, task_wording, alias_suggestion, no_match_classification |
| Advanced | reasoning model | accounting_command_parse, financial_insight_summary, anomaly_explanation, multi_step_planning |
| Vision | vision model | receipt_ocr_extract, invoice_field_extract, bill_field_extract |
| Embedding / rerank | embedding model | semantic_search, document_matching, future knowledge retrieval |

### 7.3 Task type registry

`UnityAiTaskType` is a stable enum. New types may be added; existing types may not change semantics.

Initial set:

```
unitysearch_learning_summary
unitysearch_alias_suggestion
unitysearch_ranking_hint_generation
report_usage_summary
dashboard_widget_recommendation
dashboard_summary
task_priority_summary
business_action_suggestion
accounting_command_parse
receipt_ocr_extract
invoice_field_extract
bill_field_extract
bank_memo_parse
financial_insight_summary
anomaly_explanation
email_draft_generation
```

Phase 0–1 implements only the unitysearch_* types; the rest are reserved.

## 8. Data Boundary Rules

### 8.1 Global / system-level data

Allowed to be global (single row applies to every company):

- UnitySearch context definitions
- Ranking algorithm code
- AI task type definitions
- AI provider type definitions
- Prompt templates and versions
- Tool schemas
- Default Chart-of-Accounts templates
- Default tax-framework templates
- Module definitions
- Generic OCR capability definitions
- System default dashboard widget definitions

### 8.2 Company-owned data

Required to be company-scoped (`company_id NOT NULL`, indexed):

- Actual Chart of Accounts, customers, vendors, products/services, tax codes
- Invoices, bills, payments, journal entries
- Bank accounts, inventory records, receipts/documents
- UnitySearch events, usage stats, pair stats
- Report usage stats, dashboard widget preferences, dashboard suggestions
- Action Center tasks and task interaction history
- AI learning profiles, ranking hints, alias suggestions
- AI job runs, AI request logs
- Natural-language command history
- AI-generated draft suggestions

### 8.3 User preference vs user accounting behavior

Global user preferences (shared across companies):

- theme, language, layout preference, table density

Company-scoped user behavior (separate per company a user has access to):

- frequently selected vendors, accounts, reports
- task handling habits
- dashboard usage
- tax / account / payment habits

A user with access to multiple companies has **separate** learned behavior in each company.

### 8.4 Cross-company learning is forbidden in V1

V1 does not implement cross-company learning. A future V2 may permit it, but only with:

- anonymized data (no company names, no customer/vendor names, no raw transaction details)
- aggregated data (bucketed amounts, not raw values)
- explicit opt-in policy per company
- full disclosure to the operator and the audit log

For V1: every learning artifact is owned by exactly one company.

## 9. Non-Black-Box Requirements

unityAI is not allowed to be silent or mysterious. Every AI-generated and learning-generated artifact must answer:

- What generated this?
- When was it generated?
- Which company does it belong to?
- Which job run generated it?
- What evidence was used?
- What confidence score was assigned?
- Was it system-generated (`rule`), learning-generated (`learning`), or AI-generated (`ai`)?
- Is it pending, active, dismissed, rejected, or expired?
- Why was it accepted or rejected?

Required tables (proposed; concrete schemas land with the relevant phase):

- `ai_job_runs`
- `ai_request_logs` (redacted; retention bounded)
- `ai_decision_traces`
- `ai_learning_profiles`
- `unitysearch_decision_traces`
- `dashboard_widget_suggestions`
- `action_center_tasks`
- `report_usage_events`

Audit integration: every gateway call that influences a user-visible action emits a `PlatformAuditEvent` referencing the `RunId`. The existing audit page (`Citus.SysAdmin.Blazor/Components/Features/Audit/AuditPage.razor`) already renders these and gains new event types under the `unityai.*` action namespace.

## 10. Feature Flags

Flags are boolean configuration keys read from `IConfiguration` (env, appsettings) and exposed to all layers. Defaults are conservative.

| Flag | Default | Effect when off |
|---|---|---|
| `UNITYAI_GATEWAY_ENABLED` | off | Gateway short-circuits to `Disabled` result; no provider calls. |
| `UNITYSEARCH_LEARNING_ENABLED` | off | Behavior events are not recorded for unitysearch surfaces. |
| `UNITYSEARCH_AI_LEARNING_ENABLED` | off | AI summary worker does not run for unitysearch profiles. |
| `UNITYSEARCH_TRACE_ENABLED` | off (sampled when on) | Decision traces not persisted. |
| `REPORT_USAGE_LEARNING_ENABLED` | off | Report events not recorded. |
| `DASHBOARD_RECOMMENDATION_ENABLED` | off | Widget suggestions not generated. |
| `ACTION_CENTER_ENABLED` | on (rule tasks only) | Action Center hidden. |
| `ACTION_CENTER_AI_TASKS_ENABLED` | off | Only rule tasks visible. |
| `UNITYAI_OCR_ENABLED` | off | Document AI surface hidden. |
| `UNITYAI_COPILOT_ENABLED` | off | Natural-language draft builder hidden. |

A flag turning on a *learning* surface does not by itself enable AI calls — that still requires `UNITYAI_GATEWAY_ENABLED` plus a provider configuration.

## 11. Module / Project Layout

Following the strict naming convention from the authority doc (`Citus.Modules.<Module>.<Layer>`, `Citus.Engines.<Engine>`):

| Module | Purpose | Phase |
|---|---|---|
| `Citus.Modules.UnitySearch.*` | Existing — gains Learning + Output extensions | 1 |
| `Citus.Modules.UnityAi.Gateway.Domain.Shared` | Task types, action levels, result records | 2 |
| `Citus.Modules.UnityAi.Gateway.Application.Contracts` | `IUnityAiGateway`, `IUnityAiProvider`, `IUnityAiPromptRegistry`, `IUnityAiToolRegistry` | 2 |
| `Citus.Modules.UnityAi.Gateway.Application` | Router, validator, safety policy, audit integration | 2 |
| `Citus.Modules.UnityAi.Gateway.Providers.Noop` | NoopProvider — ships first, always disabled-ok | 2 |
| `Citus.Modules.Learning.*` | Behavior events, usage stats, pair stats, profiles, summary worker | 1, 3, 4 |
| `Citus.Modules.Dashboard.*` | Widget composition, widget suggestions, layout persistence | 4 |
| `Citus.Modules.ActionCenter.*` | Rule-based tasks + AI-assisted tasks, fingerprinting, status workflow | 5 |
| `Citus.Modules.Copilot.*` | Command parser, draft builder | 7 |
| `Citus.Modules.DocumentAi.*` | OCR / extraction pipelines | 8 |

The existing `Modules/Reports/` module is the truth for report definitions and execution; the Learning Module reads its events but does not own report logic.

The shell projects (`Citus.Business.Blazor`, `Citus.SysAdmin.Blazor`) consume these modules through the same `Application.Contracts` indirection used elsewhere.

## 12. Roadmap

| Phase | Scope | Layer activated |
|---|---|---|
| **0** | Architecture lock — this document, naming, non-goals | none |
| **1** | UnitySearch Learning + deterministic ranking + decision traces | C, D (unitysearch only) |
| **2** | unityAI Gateway foundation — gateway, router, prompt registry, NoopProvider, job runs, request logs, flags | B (Noop only) |
| **3** | Report Usage Learning + widget suggestion service | C, D (reports/dashboard) |
| **4** | Dashboard Intelligence — manual widgets, suggestions, dashboard preference learning | C, D (dashboard) |
| **5** | Action Center — rule-based tasks first, AI-assisted tasks behind flag | D |
| **6** | AI insight summaries (dashboard summary, priority summary, soft suggestions) | B + C + D |
| **7** | Accounting Copilot — natural-language command → draft | B + C + D, Action Level 2 |
| **8** | OCR / Document AI — receipts, invoices, bills, statements | B + D |
| **9** | Controlled automation — Action Level 3, then 4 only with explicit policy | B + A interface |

Each phase ships behind its feature flag. Phase 1 introduces no AI provider calls.

## 13. Explicit Non-Goals

Out of scope for V1, regardless of how easy a hack would be:

- AI auto-posting
- AI direct journal-entry creation
- AI direct tax filing
- AI direct bill payment
- AI changing dashboard composition without user approval
- Real-time AI calls inside the unitysearch hot path (search must stay deterministic and fast)
- Cross-company behavior learning
- Vector database
- Custom ML training
- Full OCR pipeline (Phase 8 will design it)
- Full natural-language accounting UI (Phase 7 builds the draft path only)
- Full AI chat interface
- Replacing existing accounting / posting / tax engines
- Bypassing backend validation
- Changing document lifecycle rules
- Large UI redesign

If any future task requests one of these, it must first amend this document.

## 14. Verification / Acceptance Criteria

For Phase 0 (this document):

- [ ] `AI_PRODUCT_ARCHITECTURE.md` exists at repo root.
- [ ] `CITUS_PRODUCT_ENGINEERING_AUTHORITY.md` is referenced as the parent authority.
- [ ] No naming conflict with existing modules (`Citus.Modules.UnitySearch`, `Citus.Modules.AR`, `Citus.Modules.AP`, `Citus.Modules.GL`, `Citus.Modules.Reports`).
- [ ] Layer A engines are referenced by their actual code paths.
- [ ] Cross-company learning is explicitly forbidden in V1.
- [ ] Every AI-derived artifact is required to expose source, evidence, and confidence.

For each subsequent phase, before merge:

- [ ] Feature flags default to off (or rule-only in the case of Action Center).
- [ ] Company isolation is verified by an integration test (`CompanyId` filter on every read and write).
- [ ] No AI provider call happens when `UNITYAI_GATEWAY_ENABLED` is off.
- [ ] NoopProvider does not perform network I/O.
- [ ] Pending AI hints do not mutate ranking until accepted.
- [ ] Dashboard suggestions require explicit user acceptance.
- [ ] Rule-based tasks have evidence; AI-suggested tasks carry confidence and `decision_trace_id`.
- [ ] No AI module bypasses backend validation (covered by integration tests against the same handlers a human user hits).
- [ ] No cross-company data access (covered by repository-level tests).

## 15. Glossary

- **unityAI** — the Citus AI capability brand. Public-facing name across UI, docs, and marketing.
- **UnitySearch** — Citus's universal search/picker module. Pre-existing; the canonical Learning + Output reference.
- **Learning Profile** — structured, company-scoped summary of habits used by Output surfaces for ranking and suggestion.
- **Ranking Hint** — a per-(company, user, context) record that a Learning surface emits and an Output surface consumes when ordering results.
- **Decision Trace** — structured record of the inputs, hints, model outputs, and rules that produced a specific recommendation or task.
- **Action Level** — declared capability tier of a unityAI surface (0 read-only … 4 auto-post under policy).
- **Action Center** — Citus surface that consolidates rule-based and AI-assisted tasks for the active user in the active company.
- **Business Truth Layer** — Layer A; the Posting / Tax / FX / lifecycle / permission / audit primitives that own accounting correctness.
- **NoopProvider** — the default unityAI provider that returns `Disabled` for every task. Lets the gateway exist without any external dependency.
