# UI Library Migration Plan — AntDesign Blazor → Radzen.Blazor

This document is the executable migration plan for moving Citus's user-interface chrome from AntDesign Blazor to Radzen.Blazor. It is subordinate to [`CITUS_PRODUCT_ENGINEERING_AUTHORITY.md`](./CITUS_PRODUCT_ENGINEERING_AUTHORITY.md). If any statement here conflicts with the authority doc, the authority doc wins.

Authority order:

`CITUS_PRODUCT_ENGINEERING_AUTHORITY.md > this document > module-level READMEs > task notes > temporary implementation habits`

Sibling specs that this document coordinates with:

- [`UI_NAVIGATION_AND_DESIGN_SYSTEM_SPEC.md`](./UI_NAVIGATION_AND_DESIGN_SYSTEM_SPEC.md) — visual tokens, density rules, dark-mode philosophy. Must keep working through every phase.
- [`AI_PRODUCT_ARCHITECTURE.md`](./AI_PRODUCT_ARCHITECTURE.md) — UnitySearch picker / topbar / Action Center surfaces; their behavioural contracts must not regress during the swap.
- [`AP_AR_LIFECYCLE_CONTROL_SPEC.md`](./AP_AR_LIFECYCLE_CONTROL_SPEC.md) — document-state UI gates (read-only / draft / posted / void). Visual representation may change; semantic gating must not.

## 1. Scope and intent

### 1.1 Why migrate at all

AntDesign Blazor 1.6.1 has been the chrome of choice since the Blazor rewrite. It works, but the project has hit recurring friction:

- **Framework lag** — AntDesign 1.6.1 references types removed in .NET 11 preview (`WebEventCallbackFactoryEventArgsExtensions`), which forced the whole solution back to net10 to keep `Input<T>` from circuit-killing.
- **Hard-coded `Theme="Light"`** on the AntDesign Sider made dark-mode menu text invisible until a deep `[data-theme="dark"]` override block was layered on top of every level AntDesign paints text at (`.ant-menu-item`, `.ant-menu-title-content`, `<a>`, `__item`, `__label`).
- **Razor parser collisions** — Razor 10's stricter parsing treats `@section.X` as the `@section` directive; AntDesign's idiomatic loop variable name forced renames in three razor files.
- **Density** — AntDesign defaults to a consumer-SaaS density. Every dense surface in Citus (journal-entry lines, report tables) carries a custom `.citus-*` CSS block to compress it back into ERP shape.
- **DataGrid gap** — accounting users routinely scroll 1k–10k row reports. AntDesign's `<Table>` is adequate; Radzen's DataGrid is the industry standard in the Blazor space (virtualisation, column resize, paging, filter UI all in the box).

Radzen.Blazor fits the LOB / ERP shape better, drives every colour through CSS variables natively, and has not (so far) lagged a .NET preview release. The migration is not about replacing what works; it is about gradually shifting the surface area where Radzen is meaningfully better, while keeping AntDesign on the parts that are working fine until the reward is worth the risk.

### 1.2 Hard rules during the migration

These hold from the day this document lands until phase 9 closes:

1. **No new AntDesign-only pages once Phase 1 lands.** New report tables, charts, and high-volume grids are built on Radzen.
2. **No global Radzen theme.** Radzen is wired with `default-base.css` (the unstyled base) plus a token bridge in `shell.css`. Any Radzen-shipped theme stylesheet (Material, Standard, Software) is forbidden — they would fight the citus token system.
3. **No mixed-library composite primitives.** A Citus atom (`CitusButton`, `CitusInput`, `CitusIcon`, `CitusPageHeader`, …) wraps exactly one library. When an atom flips, all of its callers flip in the same commit.
4. **Posting / accounting / lifecycle behaviour is invariant.** No phase of this migration changes a backend contract, lifecycle state machine, or audit-trail schema. Visual chrome only.
5. **Each phase ships behind a feature flag where the user-visible surface meaningfully changes** (Phase 4, 7, 8). Trivial-surface phases (1, 2, 3, 5) ship straight.
6. **Each phase has a one-commit revert path.** The plan is graduated, but the project must always be able to walk back to the last known-good shape on a single `git revert` if a phase regresses production.

## 2. Current state (Phase 0 done)

### 2.1 What shipped in the spike

Commit [`622ee00`](https://github.com/taxdeep/Citus/commit/622ee0038ae242506f4e0ed18ba1cb8800417404) is the spike baseline:

- `Radzen.Blazor` 5.0.0 added to `Citus.Business.Blazor.csproj` with a `[5.0.0,7.0.0)` range.
- `_content/Radzen.Blazor/css/default-base.css` and `Radzen.Blazor.js` loaded from `App.razor`.
- `builder.Services.AddRadzenComponents()` added to `Citus.Business.Blazor/Program.cs` next to `AddAntDesign()`.
- The Trial Balance section of `Components/Features/Reports/ReportsPage.razor` swapped from a plain HTML table to `<RadzenDataGrid>` with sort / filter / paging / column resize on the same six columns.
- `.citus-radzen-grid` CSS bridge added to `Citus.Ui.Shared/Styles/shell.css` so the grid picks up `--citus-color-*` tokens in both light and dark modes.

### 2.2 Open caveats from the spike

These travel forward through the plan; each later phase resolves at least one:

- **`NU1903` System.Linq.Dynamic.Core 1.3.7 CVE.** Transitive dep of Radzen 5.0.0; used by the grid's runtime LINQ filter expressions. Phase 2 resolves this by upgrading Radzen to a patched 5.x build (or pinning the patched dep directly).
- **`default-base.css` is intentionally minimal.** Some Radzen components that need their own tokens (DropDown, Calendar, Dialog) will look unstyled until each one's tokens are bridged. Phase 5 expands the bridge as needed.
- **No Radzen DI services beyond DataGrid are exercised yet.** `DialogService`, `NotificationService`, `ContextMenuService` ship from `AddRadzenComponents()` but Citus still uses the AntDesign equivalents. Phase 7 swaps those.

## 3. Phase ladder

Each phase has: scope, deliverables, exit criteria, rollback, and effort estimate. Effort is engineering-day approximations from the current baseline; reality drifts.

### Phase 1 — Read-heavy report tables (≈1 day)

**Scope:** every read-only report grid in `Components/Features/Reports/ReportsPage.razor`:

- Trial Balance ✓ (Phase 0)
- Profit & Loss
- Balance Sheet
- AR Aging
- AP Aging
- Sales Tax Filing detail (when it lands)

**Deliverables:**

- `<table class="citus-data-table">` blocks → `<RadzenDataGrid>` blocks with `Sortable / Filterable / Resizable` per column.
- Each column's `Property=` matches the existing summary record shape; no DTO changes.
- `.citus-radzen-grid` CSS bridge stays the only Radzen-aware CSS.
- Page still renders a single `<CitusPageHeader>` and `<CitusStatCard>` row per report — these remain AntDesign-tinged through their wrapper.

**Exit criteria (all must be true to move to Phase 2):**

1. All 5 report grids visually parse in light + dark mode without manual CSS tweaks per grid.
2. Sort / filter / paging round-trip without console errors on a real ledger with ≥ 500 accounts.
3. The page's existing CSV-export path still works (Radzen has its own export; for V1 we keep our own to avoid duplicating wire shape).

**Rollback:** revert the single commit; `<RadzenDataGrid>` blocks → original `<table>` blocks.

### Phase 2 — Patch the CVE, formalise the bridge (≈0.5 day)

**Scope:** make the Radzen footprint security-clean and document-clean.

**Deliverables:**

- Bump `Radzen.Blazor` to the lowest 5.x release that pulls a patched `System.Linq.Dynamic.Core` (≥ 1.6.4 per the GHSA advisory). If no Radzen 5.x carries a patched version, pin `<PackageReference Include="System.Linq.Dynamic.Core" Version="..." />` directly in `Citus.Business.Blazor.csproj` to override the transitive resolution.
- `dotnet list package --vulnerable` exits clean.
- Promote `.citus-radzen-grid` from a comment-block in `shell.css` to a clearly demarcated section ("Radzen bridge — extend per component") with a precedent for adding sibling blocks (`.citus-radzen-chart`, `.citus-radzen-dropdown`, …) without rewriting.

**Exit criteria:**

1. `NU1903` warnings gone from build output.
2. CI build (when it lands) emits 0 vulnerability warnings.
3. The bridge section in `shell.css` has a single, scannable surface area that the next contributor can extend without reading this doc.

**Rollback:** the package bump is a one-line revert; CSS reorg is non-breaking.

### Phase 3 — Charts (≈1 day)

**Scope:** introduce data visualisation on dashboard / report surfaces using `<RadzenChart>` rather than mounting a third library.

**Deliverables:**

- Dashboard widgets that today show numbers gain a small trend chart variant (cash balance trailing-30, AR aging buckets, P&L month-over-month).
- Reports page gains an optional chart panel above each report grid.
- Chart colours come from `--citus-color-primary / -primary-bg / -info / -warning / -danger / -success` tokens via the bridge — series palette must hot-swap on theme toggle.

**Exit criteria:**

1. At least one chart renders on the dashboard and one renders on the reports page in both modes.
2. No theme-flicker on toggle (chart re-renders with new palette within one frame).
3. No external chart-library dependency added.

**Rollback:** charts are additive; revert leaves grids alone.

### Phase 4 — Document list grids, behind a flag (≈2 days)

**Scope:** the high-volume list pages — `Invoices`, `Bills`, `Journal Entries`, `Transactions`. These are the surfaces where DataGrid quality actually moves the needle on user productivity.

**Deliverables:**

- Each list page gets a parallel Radzen rendering.
- A new feature flag `UI_RADZEN_LISTS_ENABLED` (config key, default off) controls which rendering the page uses. Operators flip it per company / per environment.
- Bulk operations (status filters, multi-select, action menus) ride on Radzen's selection model.
- Existing CSV export and per-row navigation contracts are preserved.

**Exit criteria:**

1. With the flag on, all four lists work end-to-end (open, filter, sort, page, drill-in, export).
2. With the flag off, every page renders unchanged from Phase 3.
3. Performance smoke test: 5k-row Journal Entry list scrolls at 60fps in both modes.

**Rollback:** flip the flag off. Code stays for the next attempt.

### Phase 5 — Form primitives, atom-by-atom (≈3 days) ✅

**Scope:** the `Citus.Ui.Shared/Atoms/` wrappers — `CitusInput`, the implicit `<AntDesign.DatePicker>` / `<AntDesign.Select>` / `<AntDesign.InputNumber>` / `<AntDesign.Tag>` usages — flip to Radzen primitives one at a time.

**Order of operations** (each is its own commit, each independently revertable):

1. ✅ **5a** `CitusInput` rewritten to wrap `<RadzenTextBox>` while keeping the `@typeparam TValue` declared (unused) for back-compat with the 60+ `TValue="string"` callsites. The `Input<T>` JIT incident that triggered the .NET 10 retarget is now a non-issue because the underlying primitive is gone.
2. ✅ **5b** `<AntDesign.DatePicker>` → `<RadzenDatePicker>` across 8 razor pages (16 occurrences). Bulk sed; both libraries accept the same `TValue` / `Value` / `@bind-Value` shape. Required adding `@using Radzen` + `@using Radzen.Blazor` to both Blazor app `_Imports.razor` so component resolution succeeds without per-file imports.
3. ✅ **5c** `<AntDesign.Select>` → `<RadzenDropDown>` across 5 files (8 dropdowns). Two shape changes per callsite: `DataSource`→`Data`, `ItemValue` lambda → `ValueProperty="<name>"` (Radzen takes property-name strings, not lambdas), and `ItemLabel` lambda → `<Template Context="ctx">@Format((T)ctx)</Template>` for derived labels. Raw string/int lists drop the label hook entirely (Radzen falls back to ToString).
4. ✅ **5d** `<AntDesign.InputNumber>` → `<RadzenNumeric>` across 7 files (15 occurrences). Bulk sed; the API surface used here is identical (`TValue`, `Value`/`ValueChanged`/`@bind-Value`, `Min`, `Max`, `Disabled`, `Style`).
5. ✅ **5e** `<AntDesign.Tag>` → `<CitusBadge>` (Radzen-backed) across 26 razor files (~80 occurrences). Introduced a `CitusBadge` atom that keeps AntDesign's color-string vocabulary so dynamic `Color="@(...)"` expressions carry over unchanged; the atom maps internally to `Radzen.BadgeStyle`.

**Exit criteria:**

1. ✅ All five primitives migrated.
2. ✅ The `CitusInput` wrapper file has no AntDesign reference left.
3. Journal Entry, Invoice / Bill create, Account form, Tax Rates form, Profile form all work in both modes. *(Build green; manual UI smoke test still owed before Phase 6 kicks off.)*

**Rollback:** atom-level. Each atom commit reverts independently.

**Notes carried into Phase 6:**

- `Bordered="@false"` and `Size="@AntDesign.InputSize.Small"` on the company switchers were dropped during the Phase 5c port. If the topbar dropdowns look heavy, the fix is a `.citus-radzen-dropdown--inline` CSS bridge in `shell.css`, not adding props back.
- The `CitusBadge` atom is the only Citus-side wrapper introduced in Phase 5. Phases 6+ should not add new wrappers; raw Radzen primitives in pages are fine now that the namespace is globally imported.

### Phase 6 — Heavy form layouts (≈1 day) ✅

**Scope:** the create / edit pages whose chrome is mostly form: `Invoice`, `Bill`, `Account`, `Tax Rates`, `Profile`, `Account Profile`. Most of the body is already form fields; this phase replaces only the remaining `<AntDesign.*>` references with Radzen equivalents.

**What actually shipped:**

- ✅ **Alert** (75 callsites, 30 files): introduced `CitusAlert` atom + `CitusAlertSeverity` enum that wraps `RadzenAlert`. Severity values mirror AntDesign's (`Info` / `Warning` / `Error` / `Success`); the atom maps `Error` to `AlertStyle.Danger` internally. Sed handled tag rename + `Type=" -> Severity="` + `AntDesign.AlertType.X -> CitusAlertSeverity.X`. Three ternary `Type=@(...)` callsites needed hand fixes. The 14 existing `<MessageTemplate>` blocks carried over via the atom's matching `MessageTemplate` parameter.
- ✅ **Progress** (28 callsites): every callsite was the same indeterminate `Type=Line Percent=100 Status=Active ShowInfo=false` shape, so extracted a parameterless `CitusLoadingBar` atom that wraps `RadzenProgressBar` in indeterminate mode. Sed replaced one-liners; three multi-line callsites (ChartOfAccountsPage, TaxRatesPage, CitusAsyncBoundary) hand-edited.
- ✅ **InputPassword** (8 callsites): `<AntDesign.InputPassword>` → `<RadzenPassword>`. Sed also dropped the AntDesign-only `Size="@AntDesign.InputSize.X"` attribute (Radzen sizes via Style/Class).
- ✅ **Switch** (6 callsites): identical `Value`/`@bind-Value` surface; pure tag-rename sed.
- ✅ **Checkbox** (3 callsites): tag rename plus `@bind-Checked` → `@bind-Value` (Radzen's parameter name differs).
- ✅ **TextArea** (1 callsite): pure tag-rename sed.

**Deliverables:**

- ✅ Each form-heavy page imports zero `AntDesign.*` types when the phase ships.
- ✅ The Citus atom layer (`CitusButton`, `CitusPageHeader`) survives unchanged — they continue to wrap whatever the underlying primitive is.

**Exit criteria:**

1. ✅ `grep -rhoE "<AntDesign\.[A-Za-z]+" backend/src --include="*.razor"` now only returns shell-chrome components (Layout/Sider/Menu/MenuItem/MenuDivider/MenuItemGroup/Header/Content/AntContainer/Dropdown) — all of which are Phase 8 territory.
2. Six pages still pass their existing tests. *(Build green; manual UI smoke test still owed before Phase 8 kicks off.)*

**Rollback:** per-substep commits; the `CitusAlert` and `CitusLoadingBar` atoms revert independently of the page sweeps.

**Notes carried into Phase 7+:**

- Two new atoms (`CitusAlert`, `CitusLoadingBar`) joined `CitusBadge` in the Citus-side wrapper layer. These are appropriate for Phase 9 cleanup: when AntDesign is gone, the AntDesign-shaped surface stays — only the internal Radzen call changes if Radzen ever shifts.
- `RadzenPassword` / `RadzenSwitch` / `RadzenCheckBox` / `RadzenTextArea` are used directly, not wrapped — their APIs match Radzen's defaults closely enough that a Citus-side adapter would only add noise.

### Phase 7 — Service migration (Toast/Message) (≈1 day) ✅

**What actually shipped:**

- New `CitusToastService` in `Citus.Ui.Shared/Services` — registered scoped, wraps `Radzen.NotificationService`, exposes `InfoAsync` / `WarningAsync` / `SuccessAsync` / `ErrorAsync` with the same shape as the old `AntDesign.IMessageService`. Errors render with a 6s duration, the rest with 4s.
- Sed across ~25 razor pages: `@inject AntDesign.IMessageService Message` → `@inject CitusToastService Message`. Variable name `Message` and all ~105 call sites stayed unchanged.
- Both shells gained `<RadzenComponents />` next to `<AntDesign.AntContainer />` in `MainLayout.razor` so notifications could render. AntContainer was kept temporarily (Phase 8 needed it for Dropdown/Menu) and removed in Phase 8e.
- `Citus.SysAdmin.Blazor/Program.cs` gained `AddRadzenComponents()` (Business.Blazor already had it).

**Deferred from the original plan:**

- `Radzen.DialogService` / `Radzen.ContextMenuService` were not wired up because the codebase has zero `<AntDesign.Modal>` confirm callers and zero context-menu callsites. If/when those features get added, the registration is one line each in `Program.cs`.

### Phase 8 — Shell chrome (Layout / Sider / Menu / TopBar / Dropdown) (≈3 days) ✅

**What actually shipped:**

The plan originally flagged this for a feature flag + bake; we proceeded straight per direct instruction. The build is green; runtime smoke test is owed before any real release.

- **Layout / Sider / Header / Content (both MainLayouts)**: `<AntDesign.Layout>` / `<Sider Collapsible>` / `<Header>` / `<Content>` → plain `<div class="citus-shell">` + `<aside class="citus-shell__sider">` + `<header class="citus-shell__header">` + `<main class="citus-shell__content">`. The existing `.citus-shell__*` CSS already drove the styling, so the BEM rewrite kept rendering 1:1. Sider's auto-collapse-on-breakpoint and `CollapsedChanged` callback dropped — manual toggle via the topbar drawer button is the only entry point now. The dead `OnCollapsedChanged` handlers were removed from both shells.
- **NavMenus (BusinessNavMenu, SysAdminNavMenu)**: `<AntDesign.Menu Mode="Inline">` + `<MenuItemGroup>` + `<MenuItem RouterLink>` → plain `<nav>` + `<ul>` + Blazor `<NavLink Match="Prefix">`. NavLink's built-in auto-toggled `.active` class replaced the manual `SelectedKeys` + `LocationChanged` subscription — dropped ~20 LOC of routing-tracking code per file. `shell.css`'s `.citus-nav-menu .ant-menu-*` selectors were rewritten to BEM-style `.citus-nav-menu__group` / `__link` / `__link.active`.
- **TopBar user dropdowns**: `<AntDesign.Dropdown>` + `Overlay/Menu/MenuItem` → plain `<details>` + `<summary>` popover with new `.citus-user-menu*` CSS. Click-outside-closes is not reproduced (acceptable trade-off for a low-traffic menu). Avoids any JS for the dropdown.
- **AntContainer**: dropped from both `MainLayout.razor` files; no longer needed.
- **Stragglers**: `AntDesign.PresetColor.X.ToString().ToLowerInvariant()` callsites replaced with `"blue"` / `"orange"` / `"green"` literals.

**Bookmarked for Phase 9** (because they still wrapped AntDesign at this point):

- `CitusButton` (wrapped `<AntDesign.Button>`)
- `CitusSelect` (wrapped `<AntDesign.Select>` — turned out to have zero callsites)
- `CitusTable` (wrapped `<AntDesign.Table>` — also zero callsites)

### Phase 9 — Decommission AntDesign Blazor (≈0.5 day) ✅

**What actually shipped:**

- **CitusButton rewritten** as a plain `<button>` with class composition. The full visual surface (primary / ghost / link / text / danger / block / sm / lg / loading) lives in `shell.css`'s new `.citus-button*` rules.
- **CitusSelect deleted** — zero callsites; pages migrated directly to `<RadzenDropDown>` in Phase 5c.
- **CitusTable + CitusTableDensity deleted** — zero callsites; report pages use `<RadzenDataGrid>` (Phase 1 / 4) and create/edit forms use plain HTML tables.
- **AntDesign package removed** from `Citus.Ui.Shared.csproj`.
- **AddAntDesign()** removed from both `Program.cs` files.
- **`<link rel="stylesheet" href="_content/AntDesign/css/...">`** dropped from both shells' `App.razor`. SysAdmin.Blazor's `App.razor` also gained the Radzen base CSS + JS that Business.Blazor already had.
- **`@using AntDesign`** and **`@using AntDesign.TableModels`** stripped from `Citus.Ui.Shared/_Imports.razor`.
- **Dark-mode `.ant-menu-*` overrides** in `shell.css` (~60 lines) collapsed to a single `.citus-shell__sider` background tweak; the new BEM nav selectors pull from `--citus-color-*` tokens directly so dark mode flips automatically.
- **`Citus.Business.Blazor/Styles/app.css`** lost its `.citus-table .ant-table-*` rules (CitusTable atom is gone).

**Exit criteria:**

1. ✅ Solution builds with 0 errors and 0 AntDesign-related warnings.
2. ✅ `grep -rn "AntDesign" backend/src --include="*.csproj" --include="*.razor" --include="*.cs"` returns only doc comments and historic README-style notes.
3. ✅ Bundle size shrinks: AntDesign 1.6.1 contributed roughly 3.1 MB of runtime assets (CSS + locale resources + Blazor DLL).

## 4. Coexistence rules during the transition

These apply for every Phase 0 → Phase 8 build that ships:

1. **Namespace discipline.** AntDesign types use the `AntDesign.` qualifier; Radzen types use the `Radzen.` qualifier. Plain `<DataGrid>` is a Radzen control by convention; plain `<Table>` is AntDesign. Mixing in the same razor without qualification is a smell.
2. **CSS scoping.** Radzen styling lives under `.citus-radzen-*` selectors only. Any styling that touches `.rz-*` selectors directly is treated as a regression unless it is inside a `.citus-radzen-*` parent.
3. **DI ordering.** `AddAntDesign()` first, `AddRadzenComponents()` second. The reverse order has not been tested and should not be merged blind.
4. **Feature-flag plumbing for staged rollouts.** Phases 4 and 8 carry user-visible surface changes; both must land behind a `UI_RADZEN_*_ENABLED` flag and bake for at least one upgrade cycle before the AntDesign branch is removed.
5. **No new AntDesign components after Phase 1.** A new feature that wants a control AntDesign offers but Radzen doesn't is a signal to add the bridge for the Radzen equivalent; not a signal to extend the AntDesign surface.

## 5. Token / theming guidance

The `--citus-color-*` token system in `tokens.css` stays the source of truth across the migration. The contract:

- AntDesign components are bridged into the token system through deep `[data-theme="dark"] .ant-*` overrides in `shell.css`. These shrink as phases advance and disappear in Phase 9.
- Radzen components are bridged into the token system through `.citus-radzen-*` selector blocks in `shell.css`. These grow as phases advance and stabilise in Phase 8.
- Citus atoms (`CitusButton`, `CitusInput`, `CitusIcon`, `CitusPageHeader`, `CitusStatCard`) read tokens directly. They are library-agnostic. Their internals may flip without their callers noticing.

## 6. Testing and verification expectations

Each phase passes the same gate before merge:

1. `dotnet build backend/Citus.Accounting.sln -c Release -p:SkipCssBuild=true` exits 0.
2. `dotnet test backend/tests/Citus.Modules.UnityAi.Tests/` 32+/N green (the unityAI suite is the most stable cross-cutting check we have today).
3. Manual smoke on `/reports`, `/journal-entry/new`, `/chart-of-accounts`, `/settings/tax-rates`, `/account/profile` in light + dark, both with and without the migrated subset.
4. `grep -rn "AntDesign" backend/src/Citus.Business.Blazor` count must monotonically decrease commit-over-commit once Phase 1 lands.

When the test surface for Reports / forms grows, the gate grows with it.

## 7. Explicit non-goals

The following are **out of scope** for this migration regardless of how convenient they seem along the way:

- Migrating SysAdmin (`Citus.SysAdmin.Blazor`) and Business (`Citus.Business.Blazor`) shells in different libraries. Both move together at every phase that touches shell chrome.
- Adopting a paid Radzen.Blazor.Studio package or Radzen Pro. The migration is to the MIT `Radzen.Blazor` library only.
- Running both libraries' DataGrid on the same page. Each surface is owned by exactly one library at a time.
- Building a third "Citus design system" library that wraps both. The Citus atoms in `Citus.Ui.Shared/Atoms/` already do that; adding another layer is over-engineering.
- Translating the visual identity. Spacing, density, dark-mode palette, and typography stay as defined in `UI_NAVIGATION_AND_DESIGN_SYSTEM_SPEC.md`.

## 8. Roadmap snapshot

| Phase | Scope | Effort | Gating flag |
|---|---|---|---|
| **0** ✓ | Spike — Radzen DataGrid on Trial Balance | 0.5 day | none |
| 1 | Remaining report tables (P&L, BS, AR/AP Aging) | 1 day | none |
| 2 | CVE patch + bridge formalisation | 0.5 day | none |
| 3 | Charts (dashboard + reports) | 1 day | none |
| 4 | Document list grids (Invoices, Bills, JE, Transactions) | 2 days | `UI_RADZEN_LISTS_ENABLED` |
| 5 | Form primitives (Input → Tag) | 3 days | none (atom-level revertable) |
| 6 | Heavy form layouts (Invoice / Bill / etc.) | 1 day | none |
| 7 | Services (Notification / Dialog / ContextMenu) | 1 day | none |
| 8 | Shell chrome (Sider / Menu / TopBar / Layout) | 3 days | `UI_RADZEN_SHELL_ENABLED` |
| 9 | Decommission AntDesign | 0.5 day | none |

Total: ~13 engineering days. Distributed across however many calendar weeks the rest of the roadmap allows; not a single sprint.

## 9. Acceptance for the migration as a whole

The migration is complete when:

1. `Citus.Ui.Shared.csproj` and `Citus.Business.Blazor.csproj` carry no `AntDesign` package reference.
2. `App.razor` does not load `_content/AntDesign/css/*`.
3. `grep -rn "AntDesign" backend/src` returns zero hits outside historic comments.
4. The shell renders in both modes with `shell.css` ≤ its current line count (the dark-mode AntDesign override block is gone, the Radzen bridge is bounded).
5. No regression on the test gate from Phase 0.
6. SysAdmin and Business shells visually match their Phase 0 baseline in spacing, density, dark-mode palette, typography (this doc subordinates to `UI_NAVIGATION_AND_DESIGN_SYSTEM_SPEC.md`, not the other way around).

When all six are true, this document is closed; a short retrospective lands as `UI_LIBRARY_MIGRATION_RETRO.md` and the plan file moves to an `Archive/` subdirectory if the repo grows one.
