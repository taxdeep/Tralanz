# Aiseworks Desktop Hybrid Migration Report

Generated: 2026-05-27

## Outcome

The desktop client now has a low-risk WPF + Blazor Hybrid path alongside the
existing WebView2 shell. The existing service-hosted Blazor UI still runs as
before, while the new hybrid host proves that WPF can load a Razor component
from the shared Razor Class Library and reach desktop-only capabilities through
interfaces.

## Phase 1 - WPF Shell + BlazorWebView

- Added `HybridHostWindow` to `Aiseworks.DesktopShell`.
- Added a local `wwwroot/index.html` with `_framework/blazor.webview.js`.
- Added the WPF menu entry `System -> Hybrid host` and shortcut `Ctrl+Shift+H`.
- Kept the existing WebView2 navigation shell intact for production continuity.

Revalidation: `dotnet build src\Aiseworks.DesktopShell\Aiseworks.DesktopShell.csproj -p:SkipCssBuild=true`.

## Phase 2 - Razor Class Library Extraction

- Reused the existing `Citus.Ui.Shared` Razor Class Library as the shared UI home.
- Added `DesktopHybridApp.razor` and scoped CSS in `Citus.Ui.Shared`.
- Added desktop capability contracts to `Citus.Ui.Shared.DesktopHybrid`.

The first migrated surface is intentionally a validation cockpit, not an
accounting workflow. Existing invoice, bill, journal, payment, reconciliation,
and report pages should move only after their browser/server dependencies are
isolated behind service abstractions.

Revalidation: `dotnet build src\Citus.Ui.Shared\Citus.Ui.Shared.csproj`.

## Phase 3 - Unified API Client

- Added `Aiseworks.ApiClient`.
- Added `IAiseworksSystemHealthClient` and typed DI registration.
- Wired `Aiseworks.DesktopShell` and `Citus.Ui.Shared` to consume API contracts
  without referencing server engines or database implementations.

This is the starting point for moving scattered UI `HttpClient` usage into
typed clients shared by Web and Desktop.

Revalidation: `dotnet build src\Aiseworks.ApiClient\Aiseworks.ApiClient.csproj`.

## Phase 4 - Desktop Enhanced Capabilities

- Added WPF implementations for file picking, printing, local cache,
  notification, update-check placeholder, and host context bridge.
- Desktop capabilities are exposed through interfaces consumed by Razor UI.
- The print path explicitly treats server-generated reports/PDFs as the
  authoritative content source.

Revalidation: desktop shell build plus manual entry point `System -> Hybrid host`.

## Phase 5 - Cleanup Guardrails

- Added `Aiseworks.DesktopArchitecture.Tests`.
- Tests assert that the desktop shell hosts Blazor Hybrid and shared UI.
- Tests assert that desktop/shared UI/API client projects do not reference
  server-owned engine, infrastructure, or API implementation projects.

Revalidation: `dotnet test tests\Aiseworks.DesktopArchitecture.Tests\Aiseworks.DesktopArchitecture.Tests.csproj`.

## Current Project Boundaries

- `Aiseworks.DesktopShell`: WPF shell, WebView2 compatibility host,
  BlazorWebView hybrid host, menus, shortcuts, desktop services.
- `Citus.Ui.Shared`: reusable Razor components, shared UI patterns, DTO-like UI
  summaries, and hybrid desktop capability abstractions.
- `Aiseworks.ApiClient`: typed HTTP client contracts and DI registration for UI
  hosts.
- Server-owned projects: accounting application, posting/FX/numbering engines,
  infrastructure, APIs, reporting, permissions, audit, reconciliation, tax, and
  email delivery.

## Next Migration Candidates

1. Move low-risk read-only pages into RCL first: dashboard, reports parameter
   pages, account/customer/vendor list surfaces.
2. Move shared form primitives next: pickers, address editor, payment method
   selector, tax code picker, write-flow result alert.
3. Move transaction pages only after write operations go through shared API
   clients and every server mutation enforces permission, audit, idempotency,
   and optimistic concurrency.

## Architecture Constraints

- The desktop client must never reference posting, tax, reporting,
  reconciliation, database, or server infrastructure implementation projects.
- The desktop client may cache only operator preferences, drafts, and read-only
  snapshots. Posting and reporting must revalidate on the server.
- UI permission hints are allowed, but authorization remains server-enforced.
- Report generation remains server-owned so Web/Desktop results stay identical.
- Invoices, statements, audit logs, email delivery, banking integrations, and
  accounting facts remain server-owned.
