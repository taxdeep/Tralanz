# Blazor Phase 1 Merge Plan

This document defines the first-stage frontend merge plan after the Citus frontend direction was updated to:

- `.NET 11`
- `Blazor (C# full stack)`
- `MudBlazor`
- `YARP`

This plan is governed by:

- [CITUS_PRODUCT_ENGINEERING_AUTHORITY.md](./CITUS_PRODUCT_ENGINEERING_AUTHORITY.md)
- [WEBVELLA_CORE_ADAPTATION.md](./WEBVELLA_CORE_ADAPTATION.md)

## UI Theme Decision

`Volo.Abp.LeptonXLiteTheme.SourceCode` can be used as reference material, but it is not the primary Citus product UI system.

Use it for:

- ABP-governed account/admin surface references
- shell and layout density ideas
- theme-token organization
- selective source-level learning where it helps the Blazor/MudBlazor shell

Do not use it to replace:

- Citus accounting workflow navigation
- company-context and backend-readiness panels
- source-document review flows
- AR/AP control-layer UI
- MudBlazor-based Citus component primitives

The product UI direction remains Citus-owned Blazor Web App + MudBlazor, with LeptonX absorbed selectively rather than adopted as the controlling system.

## 1. Phase 1 Goal

Phase 1 is not a full accounting UI migration.

Phase 1 is:

- establishing the Blazor frontend base
- creating the SysAdmin control surface
- connecting the UI to `Citus.Platform.Core`
- formalizing company-aware shell behavior
- preparing the system for later accounting-module UI migration

Phase 1 is explicitly not:

- migrating invoice, bill, payment, or journal posting screens
- replacing the posting engine with generic CRUD screens
- importing WebVella's dynamic page-designer behavior into accounting flows

## 2. Authority Alignment

This phase exists because the authority requires:

- `Blazor (C# full stack)` as the frontend direction
- a separate `SysAdmin`
- `Company Isolation > Everything`
- `All formal accounting must go through the Posting Engine`
- a modular, control-layer-driven architecture

That means the first Blazor surface should be:

- SysAdmin first
- platform control first
- metadata and observability first
- accounting write screens later

## 3. What We Merge From WebVella

We should merge ideas and controlled patterns, not lift the old system whole.

Merge now:

- request-context and page-context thinking from `WebVella.Erp.Web`
- component-registry thinking from `WebVella.Erp.Web`
- host orchestration thinking from `WebVella.Erp.Site`
- Blazor app-shell direction from `WebVella.Erp.WebAssembly`

Do not merge now:

- generic record create/edit/manage pages
- dynamic Razor-page persistence to the file system
- EQL execution/debug pages
- browser-local JWT-first architecture
- any path that can bypass accounting engine rules

## 4. Phase 1 Deliverables

The first stage should produce these deliverables:

1. A new Blazor frontend host for SysAdmin.
2. A shared Citus UI shell that understands:
   - current system area
   - current company context
   - maintenance state
   - logged-in identity
3. A YARP gateway entrypoint.
4. MudBlazor-based base components for admin/control pages.
5. SysAdmin pages for platform and runtime control.
6. Company-aware routing and navigation primitives.
7. Authentication/authorization integration suitable for C# full-stack Blazor.

## 5. Proposed Project Structure

Add these projects to `backend/src` in Phase 1:

- `Citus.Gateway`
- `Citus.SysAdmin.Blazor`
- `Citus.Ui.Shared`

Keep and reuse:

- `Citus.SysAdmin.Api`
- `Citus.Platform.Core`
- `Citus.Platform.Infrastructure`
- `Citus.Accounting.Api`

### 5.1 `Citus.Gateway`

Purpose:

- public reverse-proxy entrypoint
- route business/admin traffic cleanly
- central place for edge policies later

Responsibilities:

- YARP route config
- `/sysadmin` -> `Citus.SysAdmin.Blazor`
- `/api/sysadmin` -> `Citus.SysAdmin.Api`
- `/api/accounting` -> `Citus.Accounting.Api`
- future auth/BFF edge behavior

Phase 1 scope:

- simple route forwarding
- local/dev-friendly configuration
- no advanced edge policy yet

### 5.2 `Citus.SysAdmin.Blazor`

Purpose:

- first production Citus Blazor UI
- official SysAdmin control surface

Recommended app type:

- `.NET 11 Blazor Web App`
- interactive server mode first

Why:

- better control than pure WASM
- better fit for admin surfaces
- easier integration with backend authority and secured server-side flows
- avoids phase-1 dependence on browser-local token truth

Recommended folders:

- `Components`
- `Layout`
- `Features`
- `Services`
- `Security`
- `State`
- `Navigation`
- `Theme`

Recommended feature folders:

- `Features/Overview`
- `Features/Modules`
- `Features/Entities`
- `Features/Companies`
- `Features/Users`
- `Features/Maintenance`
- `Features/Health`

### 5.3 `Citus.Ui.Shared`

Purpose:

- shared UI contracts and primitives
- shared shell models
- common navigation/state contracts

Keep this small in Phase 1.

It should contain:

- menu models
- page-header models
- company-context models
- status/health view models
- shared enums/constants used by Blazor hosts

It should not become:

- a dumping ground
- business logic
- accounting domain logic

## 6. SysAdmin Page Set For Phase 1

Build these pages first:

1. `Platform Overview`
2. `Modules`
3. `Entities`
4. `Companies`
5. `Users`
6. `Maintenance Mode`
7. `Runtime Health`

### 6.1 Platform Overview

Show:

- platform core status
- registered module count
- registered entity count
- service health summary
- maintenance mode summary

Data source:

- `Citus.SysAdmin.Api`
- existing `/core` and `/health` endpoints

### 6.2 Modules

Show:

- registered modules
- route prefixes
- system-module flags
- capabilities

Data source:

- `/core/modules`

### 6.3 Entities

Show:

- platform entities
- module ownership
- storage table
- company-scoped/system-scoped flags
- field counts

Data source:

- `/core/entities`
- `/core/entities/{name}`

### 6.4 Companies

Phase 1 expectation:

- read-only or light-control shell

Show:

- company list
- active/inactive state
- owner counts
- maintenance-relevant state

If company APIs are not ready yet, this page can ship as a controlled placeholder backed by mock contracts until the real service is added.

### 6.5 Users

Phase 1 expectation:

- admin visibility first

Show:

- users
- system role
- company memberships
- enabled/disabled state

If the backend is incomplete, structure the page and contracts now but gate mutations behind API readiness.

### 6.6 Maintenance Mode

Show:

- current maintenance status
- who enabled it
- when it changed
- impact banner

This aligns directly with the authority requirement that SysAdmin remains available while normal user writes/logins are blocked.

### 6.7 Runtime Health

Show:

- API health
- PostgreSQL connectivity status
- bootstrap state
- future cache/background-job metrics placeholders

This is the first UI step toward the observability direction in the authority.

## 7. Shared Shell Requirements

Phase 1 should create the official Blazor shell behavior.

Required shell elements:

- left sidebar navigation
- top identity/status bar
- company context display
- maintenance banner
- environment badge for non-production
- page header and breadcrumbs

Required shell rules:

- users must always know the current company context
- SysAdmin identity must be visually distinct from business context
- navigation must stay stable and business-like
- the shell must not imply that accounting write screens are generic CRUD

## 8. Company Context Rules In The UI

Even though SysAdmin is separate, Phase 1 must establish the company-context pattern now.

UI requirements:

- clear active company indicator
- switcher component contract
- safe empty state when no active company is selected
- page-level guard for company-scoped pages

Do not rely on client-only company truth.

The active company context must ultimately come from backend-authoritative session/user state.

## 9. Authentication Direction

Phase 1 authentication should follow the Blazor full-stack model, not the old WebAssembly local-storage-first model.

Recommended direction:

- server-controlled authentication
- cookie/BFF-friendly approach
- `AuthenticationStateProvider` integrated with server truth
- no accounting/authorization truth stored only in browser local storage

Why this is important:

- aligns with `Backend Authority > Frontend Assumptions`
- aligns with SysAdmin separation
- reduces token leakage and client-truth drift

## 10. MudBlazor Base Component List

Phase 1 should build a controlled component base, not a dynamic page designer.

Build first:

- `CitusAppShell`
- `CitusSidebarNav`
- `CitusTopBar`
- `CitusCompanyChip`
- `CitusMaintenanceBanner`
- `CitusPageHeader`
- `CitusStatCard`
- `CitusSectionCard`
- `CitusDataGrid`
- `CitusKeyValuePanel`
- `CitusEmptyState`
- `CitusStatusBadge`

Optional if time allows:

- `CitusConfirmDialog`
- `CitusHealthIndicator`
- `CitusAuditDrawer`

Do not build in Phase 1:

- free-form page composition designer
- generic record-edit field matrix for accounting documents
- generic "one component fits all modules" mutation forms

## 11. API Consumption Rules

Phase 1 UI calls should stay explicit and bounded.

Recommended clients in `Citus.SysAdmin.Blazor/Services`:

- `SysAdminHealthClient`
- `PlatformCoreClient`
- `CompanyAdminClient`
- `UserAdminClient`
- `MaintenanceClient`

Rules:

- typed clients only
- no loosely typed API dumping
- no UI-owned business validation
- backend remains source of truth

## 12. Page Migration Order Inside Phase 1

Implement in this order:

1. `Citus.SysAdmin.Blazor` host + shell
2. login/auth integration
3. `Platform Overview`
4. `Modules`
5. `Entities`
6. `Maintenance Mode`
7. `Runtime Health`
8. `Companies`
9. `Users`
10. `Citus.Gateway`

Why this order:

- it delivers visible progress fast
- it uses APIs that already exist first
- it validates the shell before deeper admin workflows
- it keeps accounting write screens out of the first migration wave

## 13. Explicit Non-Goals For Phase 1

These are postponed to later phases:

- Journal Entry Blazor posting UI
- Invoice/Bill Blazor posting UI
- customer/vendor operational screens
- reconciliation workspace
- reports UI migration
- AI assistant surfaces
- connector administration surfaces
- dynamic page builder

## 14. Output Of A Successful Phase 1

At the end of Phase 1, Citus should have:

- a real Blazor frontend base
- a real SysAdmin UI
- a real YARP entrypoint
- a stable shell and navigation model
- platform-core visibility and control
- a company-aware UI contract for later module work

At the end of Phase 1, Citus should not yet claim:

- full accounting UI migration
- Blazor replacement of all existing pages
- generic metadata-driven accounting operations

## 15. Recommended Next Step After This Plan

The next implementation step should be:

1. scaffold `Citus.SysAdmin.Blazor`
2. scaffold `Citus.Ui.Shared`
3. scaffold `Citus.Gateway`
4. add them to `backend/Citus.Accounting.sln`
5. build the shell and `Platform Overview` page first
