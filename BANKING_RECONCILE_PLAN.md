# Banking Reconcile — V1 Product Plan and Control Spec

Status: Draft v1 — ready to start R-1
Date: 2026-05-23

Authority order:

`TRALANZ_PRODUCT_ENGINEERING_AUTHORITY.md > this document > AP_AR_LIFECYCLE_CONTROL_SPEC.md > POSTING_ENGINE_MULTICURRENCY_DESIGN.md > task notes`

---

## 1. Purpose and Scope

This document defines the executable rules for Bank Reconciliation V1 in
Tralanz. It captures the eight business decisions (Q1–Q8) and three
boundary decisions confirmed by the operator on 2026-05-23 during the
planning review. The implementation phases (R-1 → R-5) reference this
doc as the source of truth.

Bank Reconciliation is a control surface on top of the GL ledger. It
does NOT create new financial truth — it asserts which already-posted
ledger entries cleared a particular bank statement, and locks those
entries against silent mutation once an operator has signed off on the
statement.

V1 ships a working flow end-to-end: open a draft, mark cleared / not
cleared, save for later, resume, complete with zero difference, undo
LIFO. V1 does NOT ship inline transaction editing, variance write-off,
attachment upload, or post-completion report — those are V2 (see
Section 4).

---

## 2. Control Position

Bank Reconcile is not a status-only UI layer.

It is a control surface built on:

- `ledger_entries` (the authoritative posted-transaction stream)
- `accounts` (the chart-of-accounts entry that defines a bank /
  cash / credit-card account)
- `bank_reconciliations` (the operator's signed statement record)
- Posting-period rules (independent — see Section 11)

It owns:

- which ledger entries cleared the bank
- which draft sessions are in flight per (company, account)
- the LIFO ordering of completed reconciliations per account
- the JE-lock predicate: "is this ledger entry inside a completed
  reconciliation?"

It does NOT own:

- the underlying ledger entry's amount / date / account (those are
  owned by the document that posted them — Invoice / Bill / JE /
  Bank Transfer / etc.)
- FX revaluation (independent — see Section 9)
- Posting-period close (independent — see Section 11)

---

## 3. V1 Goals

V1 closes the gap between the current `/reconciliation` skeleton
(completion-only, no in-progress state, no register page) and a
QuickBooks-Desktop-equivalent reconciliation flow.

In scope:

- **In-progress / draft persistence.** Operators can mark cleared
  rows, change their mind, leave the page, come back tomorrow, and
  resume exactly where they were. One draft per (company, account)
  maximum.
- **Three explicit lifecycle states.** `in_progress`, `completed`,
  `abandoned`. State transitions are enforced server-side.
- **Carry-forward of beginning balance.** A new draft auto-fills
  Beginning Balance from the previous completed reconciliation's
  ending balance. Operator can override but the override is audited.
- **LIFO Undo of completed reconciliations.** Operator with the
  reconciliation permission can undo a completed reconciliation, but
  only the most-recent completed reconciliation for that account.
  Undoing an earlier one requires undoing every later one first.
- **JE locking on completion.** Once a ledger entry is referenced by
  a completed reconciliation, its amount, posting date, and
  account_id become immutable; memo and ref_no remain editable. JE
  Void / Reverse against a reconciled JE is allowed (because they
  emit a new compensating JE rather than mutating the original) but
  the new JE will surface as an unreconciled candidate in the next
  draft.
- **Bank Register page.** A per-account ledger view with cleared /
  uncleared visual marking and an entry-point to the Reconcile flow.
  Replaces the current direct `/reconciliation` route.
- **Edit-info side drawer.** Mid-session, the operator can change the
  statement ending balance and ending date without losing their
  cleared selections.
- **Three-state finish menu.** Top-right button reflects state:
  `Finish now` (when DIFFERENCE = 0), `Save for later`, and
  `Close without saving` (which discards the draft).
- **Completion modal.** "You reconciled this account" with Done and
  (placeholder) Attach statement / View report buttons. The placeholders
  exist as disabled "Coming in V2" affordances so the modal layout
  matches the V2 surface.
- **Multi-currency.** Reconciliation operates entirely in the bank
  account's transaction currency. The existing tx_debit/credit +
  debit/credit double-axis ledger model is preserved unchanged.
- **Performance.** Bank Register and reconcile candidate query
  support large accounts (> 500 entries/period) via cursor
  pagination, virtual scroll, and a partial index on unreconciled
  entries.

## 4. Non-Goals (explicit V2)

These are intentionally NOT in V1. Listed here so future readers
don't try to retrofit them into R-1..R-5.

- **Inline transaction edit.** No editing of ledger entry amount /
  account / date inside the reconciliation surface. To change a JE
  mid-session, the operator saves the draft for later, opens the JE
  in its own editor, posts the change, and returns to resume. (Q3)
- **Attach statement.** No file upload in V1. The Completion modal
  shows the button as a disabled "Coming in V2" hint. (Q5)
- **Reconciliation report.** Post-completion report and PDF/CSV
  export are V2. V1 displays the completed reconciliation summary
  in the Bank Register / History view only. (Q6)
- **Variance / write-off / adjusting entry.** DIFFERENCE ≠ 0 is the
  only barrier to Finish; V1 does not offer a "Create Adjusting
  Entry" path. The operator must find and fix the discrepancy or
  Save for later. (Q8)
- **FX revaluation interaction.** V1 keeps FX revaluation strictly
  outside bank reconciliation. The bank account's reconciliation
  operates in transaction currency only; base-currency revaluation
  adjustments do not appear in the candidate list. (Boundary Q3)
- **Multi-statement-per-period.** V1 assumes one bank statement per
  reconciliation cycle. Mid-cycle statements (interim, supplemental,
  amended) are V2.
- **Undo to an arbitrary historical reconciliation.** V1 enforces
  strict LIFO. There is no "force undo" override. (Q2)
- **Automated bank feed / OFX / CSV import.** Operators enter
  cleared state by hand. Bank-feed ingestion is a separate future
  module.

---

## 5. Current State (2026-05-23 audit)

Read this before changing anything in R-1.

### 5.1 Existing tables

- `bank_reconciliations` (migration `2026-05-12-...`): header table.
  Status column today has CHECK `status = 'completed'` only —
  effectively "always completed". Fields: opening_balance,
  ending_balance, cleared_increase, cleared_decrease,
  calculated_ending_balance, difference, line_count, notes,
  completed_by_user_id, completed_at.
- `bank_reconciliation_lines`: per-line snapshot of cleared entries
  for a completed reconciliation. Stores both
  `tx_debit/tx_credit/debit/credit` and `signed_amount_base /
  signed_amount_transaction`. One-to-many to
  `bank_reconciliations`. Unique on `ledger_entry_id` (a ledger
  entry can be in at most one completed reconciliation).

### 5.2 Existing API

- `GET /accounting/reconciliation/ledger?accountId=...&statementDate=...`
  — lists unreconciled ledger entries up to a date. Limit 500
  (hard-coded; will be removed in R-5).
- `POST /accounting/reconciliation/complete` — atomic complete with
  serializable transaction isolation. Validates difference < 0.005
  tolerance, refuses if any ledger_entry_id is already in a
  completed reconciliation_lines snapshot.

### 5.3 Existing UI

- `/reconciliation` route, single page, no draft state. Form fields
  (opening / ending balance / date / notes) embedded inline. Table
  is read-only with checkbox column. Complete button enabled only
  when difference = 0.

### 5.4 Existing accounts model

- `accounts.root_type ∈ {asset, liability, equity, revenue,
  cost_of_sales, expense}`
- `accounts.detail_type` includes `'bank'`, `'cash'`,
  `'credit_card'` (the three reconcilable account kinds)
- `accounts.currency_code` — per-account currency. The
  reconciliation statement is denominated in this currency.

### 5.5 Existing concurrency model

- Reconciliation completion uses SERIALIZABLE isolation +
  `SELECT FOR UPDATE` on `bank_reconciliations` lookup. Two
  operators cannot race-complete the same account.

### 5.6 Existing permissions

- `RequireBankReconciliationAuthority()` gate — owner,
  reconciliation user, or accounting-governance role. This is the
  only relevant token today. V1 keeps the gate but applies it to
  every new endpoint added below.

### 5.7 Missing from current state

- No draft / in-progress state.
- No carry-forward beginning balance.
- No LIFO undo.
- No JE locking — a completed reconciliation does not protect its
  referenced ledger entries from being edited via the JE editor.
- No Bank Register page.
- No nav entry.
- No reconciliation_id / reconciliation_draft_id columns on
  `ledger_entries` — cleared status is inferred by joining to
  `bank_reconciliation_lines`. (R-1 changes this.)

---

## 6. Data Model — Schema Changes (R-1)

### 6.1 `ledger_entries` — add two FK columns

Per Boundary Decision 1, reconciliation status is carried at the
LEDGER ENTRY (line) level, not at the journal-entry-header level.

Rationale: a single journal entry may post lines to multiple bank
accounts (e.g. a Bank Transfer JE: line 1 debits Bank A, line 2
credits Bank B), or it may post one line to a bank account and
another to a non-bank account (e.g. an Invoice payment: bank +
AR). Those lines belong to different reconciliation cycles and
must carry their own status independently.

```sql
alter table ledger_entries
  add column reconciliation_draft_id uuid
    references bank_reconciliations(id) on delete set null,
  add column reconciliation_id uuid
    references bank_reconciliations(id) on delete restrict;
```

Both columns are nullable. `on delete set null` on the draft FK lets
the "Close without saving" path drop the draft row and clear the
back-pointers in one cascade. `on delete restrict` on the completed
FK prevents accidental deletion of a completed reconciliation row
that still has live ledger references — undo must go through the
LIFO workflow.

#### CHECK constraint

```sql
alter table ledger_entries
  add constraint chk_ledger_entries_reconciliation_xor
  check (
    not (reconciliation_id is not null and reconciliation_draft_id is not null)
  );
```

A ledger entry is either unreconciled, draft-marked, or completed.
Never two of those at the same time. Per Boundary Decision 1, no
additional `is_reconciled` boolean — `reconciliation_id IS NOT NULL`
already represents the completed/cleared state.

#### Indexes

```sql
create index ix_ledger_entries_company_account_unreconciled
  on ledger_entries (company_id, account_id, posting_date)
  where reconciliation_id is null;

create index ix_ledger_entries_reconciliation_draft
  on ledger_entries (reconciliation_draft_id)
  where reconciliation_draft_id is not null;

create index ix_ledger_entries_reconciliation_id
  on ledger_entries (reconciliation_id)
  where reconciliation_id is not null;
```

The first (partial) index is the hot-path: "list unreconciled
ledger entries for account X in date order". This replaces the
existing `LEFT JOIN bank_reconciliation_lines` pattern in the
current query.

### 6.2 `bank_reconciliations` — extend status enum

```sql
alter table bank_reconciliations
  drop constraint bank_reconciliations_status_chk,
  add constraint bank_reconciliations_status_chk
    check (status in ('in_progress', 'completed', 'abandoned'));

alter table bank_reconciliations
  add column last_modified_at timestamptz not null default now(),
  add column abandoned_at timestamptz,
  add column abandoned_by_user_id char(7) references users(id);
```

Existing rows (all `'completed'`) remain valid under the new CHECK.

#### Unique constraint — at most one draft per account

```sql
create unique index ux_bank_reconciliations_in_progress_per_account
  on bank_reconciliations (company_id, account_id)
  where status = 'in_progress';
```

This is the database-level enforcement of "one in-flight draft per
(company, account)". Two operators cannot race-open conflicting
drafts on the same account.

### 6.3 `bank_reconciliation_lines` — retained, role refined

V1 keeps `bank_reconciliation_lines` and writes to it on completion
as before. With Q1 = A (line-level columns on `ledger_entries`), the
lines table is now technically a denormalized SNAPSHOT — the
authoritative cleared state is on `ledger_entries.reconciliation_id`.

Why keep the snapshot:

- audit trail of the exact ledger-entry state at completion time
  (signed amounts, tx vs base, account snapshot)
- fast retrieval of "show me the reconciliation report for
  reconciliation X" without re-joining to `ledger_entries`
- defensive copy: if a downstream bug allows a reconciled
  ledger_entry to be improperly mutated, the snapshot still has the
  truth

The snapshot does NOT participate in V1 decision logic — every
real query (list candidates, lock check, LIFO check) uses
`ledger_entries.reconciliation_id`.

### 6.4 Statement attachment placeholder

`bank_reconciliations.statement_attachment_id uuid` column is NOT
added in V1. The Attach Statement button on the completion modal is
a V2 placeholder. Adding the column now would invite premature
schema commits; V2 will add it when the attachment subsystem is in
place.

---

## 7. State Machine

```
                  POST /draft
                  ┌──────────┐
                  ▼          │
            ┌───────────┐    │
   nil  ───►│in_progress│◄───┤  PUT /draft/{id}        (toggle cleared / patch statement info)
            └─────┬─────┘    │
                  │          │
        ┌─────────┼──────────┴──────┐
        │         │                 │
        │         │ POST /complete  │ DELETE /draft/{id}
        │         │ (diff = 0)      │ "Close without saving"
        │         ▼                 ▼
        │   ┌──────────┐      ┌──────────┐
        │   │completed │      │abandoned │
        │   └─────┬────┘      └──────────┘
        │         │
        │         │ POST /reconciliation/{id}/undo
        │         │ (LIFO check passes)
        │         ▼
        │   ┌──────────┐
        └──►│abandoned │
            └──────────┘
```

Notes:

- `in_progress → in_progress` (the PUT loop) is by far the most
  common transition. Each toggle of a cleared checkbox is a PUT.
- `abandoned` is a terminal state. An abandoned reconciliation row
  is never reused; a new reconciliation is a fresh row.
- "Close without saving" path: DELETE → set
  `ledger_entries.reconciliation_draft_id = null` for every entry
  pointing at this draft, then UPDATE
  `bank_reconciliations.status = 'abandoned'`. The row itself is
  retained for audit (who opened it, when, what fields). It is NOT
  hard-deleted.
- The Undo path also lands in `'abandoned'`, not `'completed'` and
  not delete. Same reason: audit trail.

---

## 8. JE Locking Rule

Per Q2.

### 8.1 What gets locked

When a `ledger_entries` row has `reconciliation_id IS NOT NULL` (it
belongs to a completed reconciliation):

| Field on the parent journal_entry / journal_entry_line | Lockable? |
|---|---|
| amount (`debit`, `credit`, `tx_debit`, `tx_credit`)       | **Locked** |
| posting_date                                              | **Locked** |
| account_id (on the same line)                             | **Locked** |
| `description` / `memo` / `ref_no`                         | Editable  |
| `posting_role` (system field)                             | Editable  |

The intent: the financial fact ("$N on date D against account A
cleared the bank") is fixed; cosmetic edits to help operators
remember why are allowed.

### 8.2 Where the check enforces

The lock predicate fires at the **JE write boundary**, not at the
reconcile boundary. Every code path that writes to `ledger_entries`
or `journal_entry_lines` for an existing JE must call the check
predicate before update.

Write paths affected (R-1 enumerates all):

- `PostgresJournalEntryWriter` — direct JE create/update path
- `JournalEntryLifecycleWorkflow.UpdateAsync` (if it exists; if
  not, the path that backs JE-edit pages)
- `PostgresAccountingDocumentReviewRepository.ReverseAsync` — see
  below for the Reverse exception
- AR/AP open-item edit paths that mutate the cash leg's ledger
  entry (e.g. ReceivePayment edit, PayBill edit, RefundReceipt
  edit)
- Bank Transfer post / void / reverse (its JE has both legs on
  bank accounts and is the most reconcile-sensitive)

### 8.3 Reverse vs Mutate

A JE Reverse does NOT mutate the original JE — it emits a new
COMPENSATING JE with swapped Dr/Cr and a back-reference. The
original `ledger_entries` rows stay untouched. So Reverse is
**allowed against a reconciled JE**.

Consequence: the compensating JE itself produces fresh
`ledger_entries` rows that are unreconciled (`reconciliation_id =
null`). They will appear in the next reconcile candidate list. The
operator marks them cleared when the bank clears the reversal.

### 8.4 Void

A JE Void on a reconciled JE is **also allowed**, with the same
shape as Reverse — Void emits a compensating reversal under the
hood (per the existing AP_AR lifecycle spec). Same rule applies:
the original ledger lines retain their reconciliation_id; the
compensating lines are fresh and unreconciled.

If a code path implements Void as "mutate the original to zero" (it
should not, per the spec — but check), R-1 fixes that.

### 8.5 Error shape

The lock predicate raises a domain error:

```
ledger_entry_reconciled_immutable:
  "Cannot modify ledger entry {id}: it is reconciled in
   {reconciliation.display_number}. Undo the reconciliation first."
```

UI surfaces this as a blocking error with a link to the
reconciliation in question.

---

## 9. LIFO Undo Rule

Per Q2 operator clarification.

### 9.1 Statement

A completed reconciliation R may be undone **only if it is the
most-recently-completed reconciliation for its account**. Equivalent
SQL predicate at the API boundary:

```sql
not exists (
  select 1
  from bank_reconciliations later
  where later.company_id = R.company_id
    and later.account_id = R.account_id
    and later.status = 'completed'
    and later.statement_ending_date > R.statement_ending_date
);
```

If a later completed reconciliation exists, the undo request is
rejected with:

```
reconciliation_undo_not_latest:
  "Cannot undo {R.display_number} ({R.statement_ending_date}).
   A later reconciliation exists ({later.display_number},
   {later.statement_ending_date}). Undo it first."
```

The error names the immediate-next reconciliation that must be
undone first, not the full chain. The operator works backwards one
step at a time.

### 9.2 What "tied date" means

If two completed reconciliations have the same
`statement_ending_date` (operationally rare but possible — a
supplemental statement on the same period close), tie-break by
`completed_at` descending. The strictly-later one must be undone
first.

### 9.3 Effect of an Undo

`POST /accounting/reconciliation/{id}/undo`:

1. LIFO check (above). Fails fast on violation.
2. SERIALIZABLE transaction:
   - `UPDATE ledger_entries SET reconciliation_id = null WHERE reconciliation_id = R.id`
   - `DELETE FROM bank_reconciliation_lines WHERE reconciliation_id = R.id`
   - `UPDATE bank_reconciliations SET status = 'abandoned', abandoned_at = now(), abandoned_by_user_id = $actor WHERE id = R.id`
3. Audit row: who undid, when, the prior `status='completed'`
   header values frozen for forensics.
4. Return summary.

After undo, those previously-cleared ledger entries become
unreconciled candidates again. A new reconciliation for the same
period can be opened from scratch.

### 9.4 Why not soft-undo / "draft from completed"

We deliberately do not have an "edit completed reconciliation"
mode. Undo is a full revert. A subsequent re-reconciliation is a
fresh draft → completion cycle, with its own audit row. The audit
trail records the abandoned old reconciliation as well as the new
one, so a discrepancy investigation can see both.

---

## 10. API Surface

All endpoints under `/accounting/reconciliation` (existing prefix).
All require `RequireBankReconciliationAuthority`. All accept
`Idempotency-Key` HTTP header (per H3 convention).

### 10.1 Bank Register query

```
GET /accounting/bank-register/{accountId}
    ?from=<date>&to=<date>&cursor=<opaque>&limit=<int>
```

Returns ledger entries for the account in posting_date desc, with
cleared status flag derived from `reconciliation_id IS NOT NULL`.
Cursor-based pagination. R-5 wires the partial index.

### 10.2 Draft lifecycle

```
POST /accounting/reconciliation/draft
  body: { accountId, beginningBalance, endingBalance, endingDate }
  → 201 { reconciliationId, ... }
  errors: draft_already_open_for_account, posting_period_closed
```

Beginning balance is server-validated: must equal the previous
completed reconciliation's `ending_balance`, OR be the account's
opening balance if no prior reconciliation exists. Operator can
override but the override is recorded in
`bank_reconciliations.notes` automatically.

```
PUT /accounting/reconciliation/draft/{id}/cleared
  body: { ledgerEntryId, cleared: true | false }
  → 200 { difference, ... }
  errors: not_in_progress, ledger_entry_not_in_account,
          ledger_entry_already_completed_elsewhere
```

Toggle a single ledger entry's draft mark. Returns the updated
totals so the UI can redraw the summary without a separate read.

```
PATCH /accounting/reconciliation/draft/{id}
  body: { endingBalance?, endingDate?, notes? }
  → 200 { difference, ... }
  errors: not_in_progress
```

Update statement info mid-session (the "Edit info" drawer).

```
DELETE /accounting/reconciliation/draft/{id}
  → 204
  errors: not_in_progress
```

"Close without saving". Clears all
`ledger_entries.reconciliation_draft_id` pointing at this draft,
sets `bank_reconciliations.status = 'abandoned'`.

### 10.3 Completion

```
POST /accounting/reconciliation/draft/{id}/complete
  body: {}
  → 200 { reconciliationId, displayNumber, completedAt }
  errors: not_in_progress, difference_nonzero,
          posting_period_closed (if statement_ending_date is in
                                 a closed period)
```

Validates `difference < 0.005` (existing tolerance). Inside one
SERIALIZABLE tx:

- Copies each drafted ledger entry into
  `bank_reconciliation_lines` (snapshot).
- `UPDATE ledger_entries
     SET reconciliation_id = $id, reconciliation_draft_id = null
   WHERE reconciliation_draft_id = $id`
- `UPDATE bank_reconciliations
     SET status = 'completed', completed_at = now(),
         completed_by_user_id = $actor
   WHERE id = $id`

### 10.4 Undo

```
POST /accounting/reconciliation/{id}/undo
  body: {}
  → 200 { reconciliationId, abandonedAt }
  errors: not_completed, reconciliation_undo_not_latest
```

LIFO check first. Then the inverse of complete (Section 9.3).

### 10.5 History / list

```
GET /accounting/reconciliation?accountId=<id>&status=<state>&cursor=<...>
  → { items: [{ id, displayNumber, statementEndingDate, status, ... }],
      nextCursor }
```

Used by the "History by account" UI on the entry page.

---

## 11. Period-Close Independence

Per the audit's Section 5 finding: reconciliation completion does
NOT trigger a posting-period close, and posting-period close does
NOT auto-mark anything as reconciled. They are two independent
control surfaces with different operator roles.

What they DO share:

- A completion attempt where
  `statement_ending_date > posting_period.closed_through` is allowed
  (you can reconcile AFTER a period close; the statement is
  about the bank, not the books).
- A completion attempt where the statement's posting period is
  closed AND any drafted ledger entry is INSIDE that closed period
  is allowed (those entries were already posted before close).
- The JE-locking rule from Section 8 is the ONLY automatic effect
  of completion on the GL. It does not lock the period.

What they do NOT share:

- Period close does not lock unreconciled bank lines. (They remain
  candidate for a future reconciliation, which is the outstanding
  cheque case — see Boundary Decision 2.)
- Reconciliation undo does not affect period close.

---

## 12. FX and Multi-Currency

Per Boundary Decision 3.

- Bank reconciliation operates entirely in the bank account's
  **transaction currency** (`accounts.currency_code`). A USD bank
  account reconciles against a USD statement.
- The summary totals (Beginning, Cleared, Statement Ending,
  Difference) are all displayed in the account currency.
- The Statement Ending Balance the operator enters is in the
  account currency.
- The `tx_debit / tx_credit` columns on `ledger_entries` are the
  fields the reconcile calculation uses. The `debit / credit`
  (base-currency) columns are ignored by the reconcile UI but are
  preserved in the snapshot for audit / GL-consistency checks.

### 12.1 FX revaluation entries

When the FX engine creates a revaluation JE that adjusts the
base-currency carrying value of a bank account, that JE writes a
ledger entry whose `tx_debit = tx_credit = 0` (the explicit
TX-currency-zero pattern documented in
`POSTING_ENGINE_MULTICURRENCY_DESIGN.md`, with
`posting_role='fx:realized_*'` or `'fx:unrealized_*'`).

These entries:

- DO NOT appear in the bank reconciliation candidate list — they
  have no transaction-currency amount to clear.
- Are filtered out by the candidate query:
  `where tx_debit <> 0 or tx_credit <> 0`.
- Stay on the bank account's ledger and contribute to the base
  carrying value, but the reconciliation system ignores them.

This is the "FX revaluation outside bank reconciliation" rule:
reconcile what the bank statement shows in account currency; let
the FX engine handle base-currency adjustments separately.

### 12.2 What if the operator's statement is in a different currency

Out of scope. V1 refuses (UI doesn't even offer the option — the
statement-currency display is hard-bound to the account's
currency). If an operator has a USD account that the bank charges
in CAD for some reason, that's a documented limitation; they need
a separate CAD bank account.

---

## 13. Concurrency, Isolation, and Idempotency

V1 keeps the existing SERIALIZABLE-isolation pattern for the
completion path. New protections:

- **DB-level "one draft per account"**: the partial unique index
  on `(company_id, account_id) WHERE status='in_progress'`
  guarantees only one in-flight draft exists per account, even if
  two operators race. The second `POST /draft` gets a 409 from
  the unique violation, surfaced as `draft_already_open_for_account`.
- **Per-toggle PUT**: each `PUT /draft/{id}/cleared` is a small
  transaction that does the toggle in a single round-trip
  (`UPDATE ledger_entries SET reconciliation_draft_id = ... WHERE id = ?`).
  No need for SERIALIZABLE here; READ COMMITTED is fine because
  the only contended row is the ledger entry, and toggling is
  idempotent given the same intent.
- **HTTP Idempotency-Key** on `POST /draft`, `POST /complete`,
  `POST /undo` — same convention as the inventory P0-5 cluster.
  A retried completion with the same key replays the existing
  result rather than double-completing.

---

## 14. Permissions and Routing

### 14.1 Permission tokens

V1 keeps the existing `RequireBankReconciliationAuthority` gate
without adding new tokens. All five new endpoints (Section 10) and
the existing complete endpoint share it.

Rationale: introducing fine-grained draft / complete / undo tokens
adds onboarding friction for the operator (currently one role for
the whole flow). V2 may split if a real customer asks for
"reviewer who can mark cleared but not finalize".

### 14.2 Routing changes

| Route                                      | Status      | Purpose |
|--------------------------------------------|-------------|---------|
| `/banking/register/{accountId}`            | NEW in R-2  | Bank Register page |
| `/banking/register/{accountId}/reconcile`  | NEW in R-3  | Reconcile flow (replaces /reconciliation) |
| `/reconciliation` (current)                | KEEP for one release as alias → 301 to new path |
| `/banking/reconciliation/{id}/report`      | NEW in R-4 (V2) | Stub page in V1 with "Coming in V2" |

The 301 alias is a one-release courtesy. V2 removes the alias.

### 14.3 Nav

- Business nav adds a "Banking" section with "Bank register" as
  the entry. The Reconcile flow is reached by clicking an account
  from the register, not from a top-level nav.

---

## 15. Implementation Phases

### R-1: Schema + draft lifecycle + LIFO undo + JE locking

Scope:

- Migration 6.1, 6.2, 6.3 (ledger_entries columns + indexes,
  bank_reconciliations status extension + draft uniqueness).
- New domain types: `BankReconciliationDraft`,
  `BankReconciliationDraftLine`, `LedgerEntryReconciliationLock`.
- New store methods on `IBankReconciliationStore`:
  `OpenDraftAsync`, `ToggleLineAsync`, `PatchStatementInfoAsync`,
  `AbandonDraftAsync`, `LoadDraftAsync`, `UndoCompletedAsync`,
  `CheckLineLockAsync`.
- API endpoints from Section 10.2 + 10.4.
- JE-locking predicate (Section 8) wired into every write path
  enumerated there. This is the largest single concern.
- Tests:
  - Open / toggle / patch / abandon / resume drafts.
  - One-draft-per-account uniqueness under race.
  - LIFO undo accepted on latest, rejected on earlier.
  - JE-write predicate fires on amount / date / account_id
    change; passes on memo / ref_no change.
  - Reverse / Void emit compensating JE successfully against a
    reconciled JE.

Out of scope:

- UI work (still uses current `/reconciliation` page; that page is
  upgraded in R-3).

Estimate: large. One PR. Build + test green.

### R-2: Bank Register page + nav

Scope:

- New Blazor page `/banking/register/{accountId}`.
- Account filter dropdown (cash / bank / credit_card).
- Ledger entry table with cleared-status visual marker (green
  check / dash).
- Cursor pagination.
- Click an entry → opens the JE editor.
- Click "Reconcile" → navigates to `/banking/register/{accountId}/reconcile`.
- BusinessShellState nav adds "Banking → Bank register".

Estimate: small. One PR.

### R-3: Reconcile UX overhaul

Scope:

- Replace existing `/reconciliation` page with the new flow:
  - Entry: detect in-progress draft; show "Resume reconciling"
    OR "Start reconciling" form.
  - In-progress workspace: 4-card summary + difference
    indicator + cleared checkbox column + three-state finish
    menu.
  - Edit info side drawer.
  - Completion modal with placeholder buttons (Attach
    statement disabled "Coming in V2", View report
    disabled "Coming in V2", Done active).
  - 301 alias from `/reconciliation` to the new URL.
- All API calls go through the R-1 endpoints.

Estimate: medium. One PR.

### R-4: Carry-forward + report stub

Scope:

- `POST /accounting/reconciliation/draft` server-validates
  beginning balance against previous completed
  `ending_balance`.
- Reconciliation summary panel on the Bank Register page (last
  N completed for this account).
- Completion modal "View report" link active → opens a stub page
  `/banking/reconciliation/{id}/report` that lists the snapshot
  from `bank_reconciliation_lines`. PDF / CSV export is V2.

Estimate: small-medium. One PR.

### R-5: Performance + JE locking coverage audit

Scope:

- Remove the hard `LIMIT 500` on the candidate-list query.
- Cursor-based pagination + virtual scroll on both Bank Register
  and reconcile workspace.
- Partial index from 6.1 verified used by EXPLAIN.
- Comprehensive JE-write-path audit: enumerate EVERY write path
  that touches `ledger_entries.debit / credit / posting_date /
  account_id` and confirm each calls the lock predicate. Add
  missing ones.
- Smoke test with a synthetic 10,000-entry account.

Estimate: medium. One PR (or 2 if the JE-write audit finds many
gaps).

---

## 16. Risks

### 16.1 R-1 schema migration on an existing prod DB

Both new FK columns on `ledger_entries` start NULL. Existing
completed reconciliations have rows in `bank_reconciliation_lines`
but `ledger_entries.reconciliation_id = NULL` until backfilled.

Backfill plan inside the migration:

```sql
update ledger_entries le
   set reconciliation_id = brl.reconciliation_id
  from bank_reconciliation_lines brl
 where brl.ledger_entry_id = le.id
   and le.reconciliation_id is null;
```

This is idempotent (re-runnable) and locks only the
already-reconciled rows. New deployments start with 0 rows; the
backfill is a no-op there.

If the test server has many existing reconciliations, the backfill
may take seconds. Pre-deploy verification on a test snapshot
recommended.

### 16.2 JE-write-path enumeration gap

If R-1 misses a write path (e.g. a future code path that
mutates ledger_entries directly), an operator can silently break
a reconciled entry. Mitigation:

- The CHECK on `ledger_entries` cannot enforce immutability
  (CHECK can't reference the OLD row in postgres triggers
  cleanly without overhead).
- Instead, R-1 adds an AFTER UPDATE trigger on
  `ledger_entries` that raises if any of {debit, credit,
  tx_debit, tx_credit, posting_date, account_id} changed while
  `reconciliation_id IS NOT NULL`. This is the
  belt-and-suspenders backup to the application-layer
  predicate.

```sql
create function bank_recon_le_immutability_guard()
returns trigger as $$
begin
  if old.reconciliation_id is not null then
    if old.debit         <> new.debit         or
       old.credit        <> new.credit        or
       old.tx_debit      <> new.tx_debit      or
       old.tx_credit     <> new.tx_credit     or
       old.posting_date  <> new.posting_date  or
       old.account_id    <> new.account_id    then
      raise exception 'ledger_entry % is reconciled (%) and its '
                      'financial fields are immutable',
                      new.id, old.reconciliation_id;
    end if;
  end if;
  return new;
end;
$$ language plpgsql;

create trigger trg_bank_recon_le_immutability
  before update on ledger_entries
  for each row
  when (old.reconciliation_id is not null)
  execute function bank_recon_le_immutability_guard();
```

This trigger does NOT block deletes (a JE delete is allowed in
the existing model only via Reverse/Void, not direct delete). It
does NOT block memo/ref_no changes on the parent
`journal_entry_lines` because that table is separate.

### 16.3 LIFO Undo and concurrent reconciliations

If operator A is in the middle of completing a new reconciliation
(period N+1) while operator B requests an undo of period N, the
LIFO check might see no later reconciliation (B's perspective)
but A is about to insert one. SERIALIZABLE isolation on both ops
handles this: one will fail with a serialization conflict and the
client retries.

### 16.4 Migration ordering vs PR #74 / #75

This PR lands the doc only — no schema or code. R-1 lands the
schema. The schema migration is appended after
`2026-05-22-m13-row-level-security.sql` so it sequences cleanly
with the existing migration chain. RLS policies on
`ledger_entries` from M13 need a one-line addition to allow the
reconciliation columns through the bypass — verified in R-1.

### 16.5 Operator confusion about Save for later vs Close without saving

UX risk: operator clicks "Close without saving" thinking it's
"close the page". Mitigation in R-3:

- Render "Close without saving" in a destructive (red-text or
  warning-icon) styling.
- Show a confirm modal: "Discard your in-progress reconciliation?
  Your cleared selections will be lost. Save for later if you
  want to come back to them."

---

## 17. Open Questions for Future (post-V1)

These are flagged here so V2 / V3 planning has a starting list.
None block V1.

1. **Multi-statement period.** Some banks issue mid-month interim
   statements. Should V2 support more than one statement per
   reconciliation cycle, or do operators just stagger their
   cycles?
2. **Variance / write-off path (Q8 V2).** When operator and bank
   agree the difference is unrecoverable (e.g. a $0.01 bank
   rounding), an "Adjust and Finish" path that posts a small
   correcting JE to a designated variance account would help.
   Needs: which account, who approves, what the JE looks like.
3. **Attach statement (Q5 V2).** Where do attachments live? Local
   filesystem (simplest, works for single-tenant Ubuntu installs)
   or S3/MinIO (multi-tenant SaaS)? What max size / virus scan?
4. **PDF / CSV report (Q6 V2).** Rendering pipeline. Re-uses
   `bank_reconciliation_lines` snapshot.
5. **Bank-feed import.** OFX, QFX, CSV, or Open Banking? Out of
   product scope until enough customers ask.
6. **Inline transaction edit (Q3 V2).** Should it edit-in-place
   or pop a modal with the full JE editor?
7. **Reviewer / approver split.** A two-person workflow where
   one operator marks cleared and a different one approves the
   finish. Requires new permission tokens.
8. **Reconciliation against ledger_entries posted via inventory.**
   Inventory's GL handlers (Adjustment / Manufacturing / Transfer)
   emit ledger_entries on inventory-asset accounts, not bank
   accounts, so they don't intersect V1. But Goods Receipt /
   Sales Issue paths may post to AP / AR clearing accounts that
   are reconciled monthly; verify those are handled by the
   normal "AR/AP reconciliation" V2 feature (separate from bank
   reconciliation).

---

## 18. Summary of Decisions Locked In

| # | Decision | Source |
|---|---|---|
| Q1 | Reconciliation status is stored on `ledger_entries` (line level), via two FK columns + CHECK xor | Operator 2026-05-23 |
| Q2 | Completion locks amount / date / account_id on the JE; memo / ref_no editable. Undo is strict LIFO | Operator 2026-05-23 |
| Q3 | Inline transaction edit is V2 | Operator 2026-05-23 |
| Q4 | "Close without saving" discards draft only; completed history untouched | Operator 2026-05-23 |
| Q5 | Attach statement is V2 (UI placeholder shown in V1 completion modal) | Operator 2026-05-23 |
| Q6 | Reconciliation report is V2 | Operator 2026-05-23 |
| Q7 | Performance work (LIMIT 500 removal, partial index, virtual scroll) lands in R-5 | Operator 2026-05-23 |
| Q8 | V1 keeps 0 difference threshold; no variance / write-off path | Operator 2026-05-23 |
| B1 | Reconciliation status at ledger-entry line level, not at JE header level. Bank Transfer / split JE cases drive this | Operator 2026-05-23 |
| B2 | Outstanding-cheque case is normal: an old-dated unreconciled entry stays in the candidate pool until it clears the bank in a future statement | Operator 2026-05-23 |
| B3 | FX revaluation entries are excluded from bank reconciliation (`tx_debit = tx_credit = 0` rows filtered out) | Operator 2026-05-23 |

Any future spec change to bank reconciliation must reconcile (no pun
intended) with this list, or this list must be updated first in a
documented amendment.
