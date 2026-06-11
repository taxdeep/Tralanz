# Tralanz document canvas (`tz-doc-*`)

A shared **continuous editing surface** for document editor pages (invoice, quote,
sales order, bill, …). It deliberately breaks from the stacked-card
(`CitusFormSection`) look used elsewhere: one flat surface, regions divided by thin
lines, dense inputs — an accountant-style editing workbench, not an admin CRUD form.

> Do **not** convert settings/master-data forms or list pages — the card pattern is
> right there. This is only for multi-region *document editors*.

## CSS vocabulary (defined in `Citus.Ui.Shared/Styles/shell.css`)
Reuses `--citus-*` tokens (no new colors). Compact ~30px inputs + 11px uppercase labels.

| Class | Role |
|---|---|
| `tz-doc-editor-page` | page wrapper (light-gray bg, scopes compact input height) |
| `tz-doc-surface` | the continuous white editing surface |
| `tz-doc-region` | a horizontal zone inside the surface; thin bottom divider, **no card** |
| `tz-doc-header-grid` + `tz-doc-zone` | multi-column header grid (each page fills its own fields) |
| `tz-doc-field` + `tz-doc-field__label` | label-over-control, 11px label |
| `tz-doc-field-row` | two fields side-by-side |
| `tz-doc-address-box` (`--empty`) | textarea-style bill/ship box |
| `tz-doc-line-grid` + `__scroll` / `__tools` / `__tool` | line-grid wrapper + bottom Add/Clear row (the `<table>` itself keeps `citus-data-table citus-data-table--compact`) |
| `tz-doc-lower-grid` (`__left`) | notes (left) + totals (right) |
| `tz-doc-totals-*` | right-aligned totals summary (`--grand` for the emphasized total) |
| `tz-doc-toolbar*`, `tz-doc-action-bar*` | top toolbar + sticky bottom bar (flat, no radius/shadow) |
| `tz-doc-todo` | de-emphasis for mockup-only fields with no backing model |

## Shared components (`Components/Shared/TzDoc*.razor`)
- `TzDocToolbar` — `Title`, `Subtitle?`, `BackHref`, `Actions` fragment.
- `TzDocActionBar` — `PrimaryLabel`, `IsSaving`, `Disabled`, `ErrorHint?`, `OnSave`, `LeftActions`/`RightExtras` fragments.
- `TzDocTotalsPanel` — `Rows` = `IReadOnlyList<(string Label, string Value, bool Emphasis, bool Todo)>` (the page builds its rows).
- `TzDocAddressSection` — `Billing`, `Shipping`, `ShippingMode`, `OnEditAddress` (the AddressEditor drawer stays on the page).

The **reference implementation** is `Components/Features/Invoices/InvoiceEditor.razor` (+ its
invoice-specific `InvoiceHeaderSection` / `InvoiceLineGrid` / `InvoiceNotesSection` /
`InvoicePaymentOptions`, which use the `tz-doc-*` classes but bind invoice fields).

## Per-page migration recipe (markup + CSS only — never touch `@code`/bindings/logic)
1. Wrap the body in `<div class="tz-doc-editor-page">`.
2. `CitusPageHeader` → `<TzDocToolbar Title=... BackHref=...>`.
3. Each `<CitusFormSection Title="X">` → `<div class="tz-doc-region">` (no card); header fields use
   `tz-doc-header-grid`/`tz-doc-zone`/`tz-doc-field`/`tz-doc-field__label` instead of `CitusFormGrid`/`CitusFormField`.
4. Line `<table>`: keep `citus-data-table citus-data-table--compact`, wrap in `<div class="tz-doc-line-grid">`,
   move Add/Clear to a `tz-doc-line-grid__tools` row below.
5. Summary/totals → `<TzDocTotalsPanel Rows="@(...)">` (build the rows in `@code`).
6. Address display → `<TzDocAddressSection>`; keep the `AddressEditor` drawer on the page.
7. Sticky submit (`CitusFormActions`) → `<TzDocActionBar PrimaryLabel=... OnSave="...">`.
8. Preserve every `@bind`/`ValueChanged`/`EventCallback`, validation, tax computation, and submit/post call verbatim.
