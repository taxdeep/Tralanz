# UI Design Language

Operational companion to `UI_NAVIGATION_AND_DESIGN_SYSTEM_SPEC.md`. The
spec defines the **principles** (clean, stable, business-first); this
document defines the **concrete rules and patterns** that every page is
expected to follow, and tracks page-by-page migration toward those rules.

Authority chain:
`CITUS_PRODUCT_ENGINEERING_AUTHORITY.md > UI_NAVIGATION_AND_DESIGN_SYSTEM_SPEC.md > this document > task notes`.

## How to amend

The user may propose changes in **English or 中文** — either is fine.
Each amendment should:

1. Update §3–§9 with the new rule (or refine an existing one).
2. Add a row in §10's migration log if any pages need to be retrofitted.
3. Land in its own commit titled `UI design language: <change>` so the
   doc's history is easy to scan.

This file is the **living source of truth** for the look-and-feel
working agreement between operator and engineering. Pages that diverge
from §3–§9 are bugs, not creative liberty.

---

## §1. Tokens (current)

Source: [`backend/src/Citus.Ui.Shared/Styles/tokens.css`](backend/src/Citus.Ui.Shared/Styles/tokens.css).

- **Surface**: `--citus-color-bg`, `-bg-elevated`, `-bg-muted`, `-bg-subtle`, `-border`, `-border-strong`
- **Foreground**: `--citus-color-fg`, `-fg-muted`, `-fg-subtle`, `-fg-on-primary`
- **Brand**: `--citus-color-primary` + `-hover` / `-active` / `-bg` / `-border`
- **Status**: `success`, `warning`, `danger`, `info` — each with a paired `-bg` low-saturation surface
- **Spacing scale** (4 px base): `--citus-space-{0,1,2,3,4,5,6,8,10,12,16}` (rem)
- **Type scale**: `xs 12px → 3xl 28px`, three line-heights (`tight 1.25`, `normal 1.5`, `relaxed 1.65`)
- **Radius**: `sm 4`, `md 6`, `lg 8`, `xl 12`
- **Shadow**: three elevations, `oklch` alpha so they stay subtle on dark
- **Density**: row heights `compact 36`, `default 44`, `comfortable 52`
- **Font**: Inter, falling back to PingFang SC / 微软雅黑 for CJK
- **Tabular numerals**: `var(--citus-tabular-nums)` — required for any numeric column

Dark mode is purpose-designed (not an inversion). Status colors are
desaturated except `warning`, which keeps energy because finance users
rely on it for past-due cues.

---

## §2. Atoms (current inventory)

Source: [`backend/src/Citus.Ui.Shared/Atoms/`](backend/src/Citus.Ui.Shared/Atoms/).

| Component        | Purpose                                                                         |
|------------------|---------------------------------------------------------------------------------|
| `CitusButton`    | The only button. Variants: `Default / Primary / Ghost / Link / Text`. Sizes: `Small / Medium / Large`. Modifiers: `Danger`, `Block`, `Loading`, `LeadingIcon`. |
| `CitusInput`     | Standard form text input. 32 px height tied to global form-input normalization. |
| `CitusBadge`     | Status pill. `Color="green/blue/orange/red/default/..."`, mapped to status tokens. |
| `CitusAlert`     | Inline message. `Severity = Info / Success / Warning / Error`. Optional icon, optional `MessageTemplate`. |
| `CitusEmpty`     | Empty-state placeholder. Use inside `CitusAsyncBoundary` rather than ad-hoc copy. |
| `CitusSkeleton`  | Loading placeholder. Variants: `List / Detail / Form`. Pick the one that matches the page shape. |
| `CitusLoadingBar`| Top-of-page progress bar. Reserved for shell-level transitions. |

Rule: **never** introduce a Radzen / AntDesign primitive directly when
an atom exists. If the atom is missing a feature, extend the atom in a
separate commit before using it.

---

## §3. Patterns (current inventory)

Source: [`backend/src/Citus.Ui.Shared/Patterns/`](backend/src/Citus.Ui.Shared/Patterns/).

| Component                  | Where it goes                                                  |
|----------------------------|----------------------------------------------------------------|
| `CitusPageHeader`          | First element on every page. Owns `Eyebrow / Title / Subtitle` + an `Actions` slot for top-level page actions (e.g. Refresh, Export, "New X"). |
| `CitusAsyncBoundary`       | Wraps any data-bound section; pairs loading + empty + content states. |
| `CitusEmptyState`          | Larger empty-state pattern with optional icon and CTA. Used inside `CitusAsyncBoundary`. |
| `CitusStatCard`            | KPI tile. `Label / Value / Description / Icon / Tone`. Always in a 4-up grid on the dashboard. |
| `CitusStatusBadge`         | Domain-aware status mapping (e.g. invoice statuses). Prefer over raw `CitusBadge` when the value comes from a posting state. |
| `CitusCompanyAccessBanner` | Renders the active-company chrome above the page content. Owned by the shell. |
| `CitusMaintenanceBanner`   | Renders maintenance-mode warning. Owned by the shell. |
| `ThemeToggle`              | Light / dark / system. Sits in the top bar. |

Page-level CSS class kit (lives in `shell.css`):

- `.citus-panel` + `.citus-panel__title` — section container with title
- `.citus-data-table` — the standard table style; pair with `text-right` + `font-variant-numeric: tabular-nums` for numeric columns
- `.citus-form-field` + `.citus-form-field__label` + `.citus-form-field__input` — form row primitive (32 px input height, label above)
- `.citus-link` — interactive text link (table cells, descriptions)
- `.citus-inline-code` — entity numbers, codes
- `.citus-bullet-list`, `.citus-definition-list` — text-level layout primitives

---

## §4. Page layout

Every page renders in this order:

```
CitusPageHeader (Eyebrow + Title + Subtitle + Actions)
  ↓
[Optional CitusAlert for transient messages]
  ↓
Content sections — each in <section class="citus-panel">
  ↓ (form pages only)
Action footer — see §7
```

Hard rules:

- **No raw `<h1>`** outside `CitusPageHeader`.
- **No raw `<button>`** — always `CitusButton`. Anchors styled as
  buttons (e.g. download links opening in a new tab) use the
  `citus-button citus-button--ghost citus-button--sm` class composition.
- Section titles are `<h2 class="citus-panel__title">` — no other size.
- Sub-sections inside a panel use `<h3 class="citus-panel__title text-base mt-4">`.
- **Spacing between top-level sections is `mb-6`** (24 px). Inside a
  panel, `mb-3` (12 px) between header → body, `mt-4` (16 px) between
  sub-blocks.

Grid choices:

- Two-column dashboards: `grid grid-cols-1 lg:grid-cols-2 gap-6`
- Detail / form layouts: `grid grid-cols-1 lg:grid-cols-12 gap-6` with main `col-span-7` + aside `col-span-5`
- KPI tiles: `grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4`

---

## §5. Form fields

- Every input lives inside `<label class="citus-form-field">` with the
  label as the first child (`<span class="citus-form-field__label">`).
- Input heights are normalized to 32 px globally — do not override per
  page.
- Required fields show a `*` after the label text — no other indicator.
- Multi-currency writes (any page with a `transaction currency` field)
  must pair the field with `<FxRateInline>` showing the recommended D-1
  rate. This is not optional.
- Numbers are rendered with `font-variant-numeric: tabular-nums`. Always.

---

## §6. Tables

- `<table class="citus-data-table">` everywhere. No bespoke table
  styling.
- Numeric columns: `<th class="text-right">` + `<td class="text-right">`
  with `style="font-variant-numeric: tabular-nums;"`.
- Long lists with sortable columns: header click toggles asc/desc;
  current sort is shown with `▲ / ▼` after the column label.
- Empty state lives inside the panel, not the table — render
  `<CitusAsyncBoundary IsEmpty="...">` around the `<table>`.
- Maximum density is `default` (44 px). Use `compact` only for dense
  read-only reports.

---

## §7. Form action footer (CURRENT RULE)

> **Adopted 2026-04-29.**
> Cancel sits on the **left**, save / submit / post actions sit on the
> **right**. All actions live at the **bottom of the page** (after the
> last content section), not floating in the middle.

Layout:

```html
<div class="citus-form-actions">
  <CitusButton Variant="CitusButtonVariant.Ghost" OnClick="Cancel">Cancel</CitusButton>
  <div class="citus-form-actions__primary">
    <!-- one or more positive actions, primary on the right -->
    <CitusButton Variant="CitusButtonVariant.Default" OnClick="@(() => SaveAsync(post: false))">Save</CitusButton>
    <CitusButton Variant="CitusButtonVariant.Primary" OnClick="@(() => SaveAsync(post: true))">Save &amp; Post</CitusButton>
  </div>
</div>
```

CSS contract (live in `shell.css`):

- `.citus-form-actions` — flex row, `justify-content: space-between`,
  `gap: 0.75rem`, `padding-top: 1rem`, top border using
  `--citus-color-border` to separate from the form body.
- `.citus-form-actions__primary` — flex row, `gap: 0.5rem`, holds 1+
  positive actions. The **rightmost** action is the primary.

When there are multiple positive actions (e.g. `Save` and `Save & Post`):

- The most-irreversible one is on the **right** (e.g. `Save & Post`),
  styled `Primary`.
- The reversible draft action is to its **left**, styled `Default`
  (not Ghost — Ghost is reserved for Cancel-class actions).

Destructive actions on edit pages (e.g. `Delete draft`) sit on the
**left** next to Cancel, with `Danger=true`.

Migration: §10 lists every create / edit page and its current state
versus this rule.

---

## §8. Status colors

Mapping from accounting state → `CitusBadge Color`:

| Domain state         | Color    | Notes                                  |
|----------------------|----------|----------------------------------------|
| draft                | `default`| neutral pill                           |
| pending / open / running | `blue` | in-flight                            |
| posted / succeeded / approved | `green` | terminal-success            |
| void / failed / rejected | `red` | terminal-failure                     |
| past-due / overdue   | `orange` | finance users rely on this color     |
| info / informational | `blue`   | matches Alert info                    |

Tone rules:

- Never use raw color hex in components. Always token / Color="…".
- Never combine two status colors in one row — pick the dominant one.

---

## §9. Money + numbers

- Always render with a currency prefix (`Can$1,234.56`, `US$0.00`,
  `RMB48,985.01`) so readers don't have to infer the currency from
  context.
- Negative amounts render with a leading `-`, never with `(parens)`.
- Zero amounts render as `Can$0.00`, not `—`. Em-dashes only mean
  "data not yet available," not "value is zero."
- Forecast / projected values get a tinted background
  (`rgba(45, 209, 247, 0.06)` — see Cash Flow band) and a small
  `Forecast` annotation in the column header. Never relabel the value.
- Multi-currency totals fall back to **base currency** when the
  transaction-currency mix isn't dominant; the cell label stays the
  customer's transaction currency in single-currency cases.

---

## §10. Migration log (page-by-page)

Status legend: ✅ matches §3–§9 · ⚠️ partial · ❌ violates rule(s).

| Page | Path | §7 footer | §6 tables | §5 forms | Notes |
|---|---|---|---|---|---|
| Invoice — New | `Components/Features/Invoices/InvoiceCreatePage.razor` | ❌ | ✅ | ⚠️ | "Post invoice" lives in a right-column panel as a Block button; no Cancel. Migrate to footer pattern. |
| Bill — New | `Components/Features/Bills/BillCreatePage.razor` | ❌ | ✅ | ✅ | Footer row exists but Cancel is on the right. Flip per §7. |
| Expense — New | `Components/Features/Expenses/ExpenseCreatePage.razor` | ❌ | ✅ | ✅ | Same as Bill — Cancel on the right. Flip. |
| Purchase Order — New | `Components/Features/PurchaseOrders/PurchaseOrderCreatePage.razor` | ❌ | ✅ | ✅ | Cancel on right. Flip. |
| Quote — New | `Components/Features/Sales/QuoteCreatePage.razor` | ❌ | ✅ | ✅ | Cancel on right. Flip. |
| Sales Order — New | `Components/Features/Sales/SalesOrderCreatePage.razor` | ❌ | ✅ | ✅ | Cancel on right. Flip. |
| Journal Entry — New | `Components/Features/JournalEntry/JournalEntryCreatePage.razor` | ⚠️ | ✅ | ✅ | No Cancel; uses "Reset draft" instead. Add a Cancel that returns to `/journal-entry` and keep Reset. |
| Customer — Profile / Edit | `Components/Features/Counterparties/CustomerProfilePage.razor` | — | ✅ | ✅ | Inline-edit panel; revisit when edit mode is opened. |
| Vendor — Profile / Edit | `Components/Features/Counterparties/VendorProfilePage.razor` | — | ✅ | ✅ | Same as customer. |
| Settings — Invoice template editor | `Components/Features/Settings/InvoiceTemplateEditorPage.razor` | ⚠️ | — | ✅ | Save / Cancel placement to be confirmed against §7. |
| Receive Payment | `Components/Features/Payments/ReceivePaymentPage.razor` | — | ✅ | ✅ | Workflow page, not a single form — re-evaluate footer rule when opened. |

Each migration is one commit, message format:

```
UI footer §7: <PageName> — Cancel left, primary right
```

Pages not listed above either match §3–§9 today or are read-only and
don't need the form footer rule applied.

---

## §11. Open questions

Items the user has flagged but not yet resolved into rules. Land here
before promoting to §3–§9.

- _(none right now — populate as questions arise)_
