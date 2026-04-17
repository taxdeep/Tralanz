# Citus User Permission System Plan

This document records the product and engineering plan for the Citus User Permission System.

Authority order:

`CITUS_PRODUCT_ENGINEERING_AUTHORITY.md > MULTI_COMPANY_AUTH_AND_CONTROL_SPEC.md > this document > task-specific notes`

## 1. Definition

Citus User Permission System is not a single login module.

It is a four-layer control system:

`Identity + Company Context + Permission + System State`

The goal is to protect:

- company isolation
- owner governance
- backend authority
- SysAdmin separation
- maintenance safety
- auditable user and permission changes

## 2. Boundary Clarification

The user permission architecture is split into four cooperating boundaries:

1. Platform Identity / Account
2. Platform Billing / Subscription
3. CompanyAccess
4. Business App Domains

These boundaries must stay separate even when they are implemented in the same modular monolith.

### 2.0 Platform Identity / Account

Platform Identity / Account owns account-level identity truth.

It is responsible for:

- registration
- login
- logout/session basics
- password credentials
- email verification
- reset password
- account profile base data
- platform account status
- account lock / disable state

It is not responsible for:

- company membership
- active company
- company-scoped permissions
- owner/user assignment inside a company
- AP / AR / GL / Inventory / Tax / Reports authority
- posting or approval legality

Business modules may use account identifiers as actor references.

They must not copy identity truth into business-owned user tables.

### 2.1 Platform Billing / Subscription

Platform Billing / Subscription owns commercial entitlement truth.

It is responsible for:

- plan
- trial
- subscription
- renewal
- cancellation
- payment failure state
- seat entitlement
- feature entitlement
- usage limits
- edition / packaging controls

It may answer questions such as:

- Is this tenant/workspace entitled to multi-currency?
- Is this tenant/workspace entitled to AI assist?
- How many seats are allowed?
- Is the subscription suspended because payment failed?

It is not responsible for:

- company membership truth
- owner/user assignment
- active company
- accounting validity
- posting legality
- tax truth
- FX truth
- report semantics

Feature and subscription gates may enable, disable, or limit access to capabilities.

They must not rewrite posted accounting truth or replace domain legality.

### 2.2 CompanyAccess

CompanyAccess owns company-access truth.

It is responsible for:

- which account can enter which company
- owner / user membership
- active company
- company-scoped permissions
- company inactive / read-only gate
- company-context access legality
- at-least-one-owner invariant

CompanyAccess consumes account identifiers from Platform Identity.

CompanyAccess may consume entitlement signals from Platform Billing / Subscription.

CompanyAccess must remain the source of membership and active-company truth.

### 2.3 Business App Domains

Business App Domains own business truth.

Examples:

- AP
- AR
- GL
- Inventory
- Tax
- Reports
- Reconciliation
- Tasks

They are responsible for:

- business lifecycle
- domain validation
- posting readiness
- approval legality
- report semantics
- accounting and operational truth

Business domains consume:

- account actor reference from Platform Identity
- company context and permissions from CompanyAccess
- entitlement allow/deny signals from Platform Billing / Subscription

Business domains must not own:

- generic account identity
- generic user-management truth
- subscription billing truth
- company membership truth

Rule:

Entitlement allows access to a capability.

CompanyAccess allows access in a company.

Business domain rules decide whether the action is legal.

### 2.4 Business App Boundary

The Business App does not own an independent user-management business domain.

The Business App owns the `CompanyAccess` boundary only.

`CompanyAccess` is responsible for resolving an already-authenticated account into the current business context:

- account actor reference
- active company
- company membership
- company role
- company-scoped permission set
- company status gate
- business access legality in the current company context

Forbidden in Business App:

- root module named `Users`
- root module named `UserManagement`
- root module named `Identity`
- business logic centered around generic users instead of company membership

Allowed in Business App:

- account id / user id as actor reference
- account display labels for audit and UI
- company-scoped membership and permission checks

Rule:

Business App may reference platform accounts, but it must not become the owner of platform identity truth.

### 2.5 SysAdmin Boundary

SysAdmin is an independent PlatformOps / SysAdmin boundary.

It has:

- independent entry
- independent session
- platform governance responsibility

SysAdmin owns or coordinates:

- maintenance mode entry and exit
- company lifecycle governance
- system-level audit visibility
- emergency governance actions
- runtime/system visibility

SysAdmin may trigger:

- user lock
- user disable
- password reset
- membership adjustment
- owner/user assignment
- company inactive/delete lifecycle actions

But the authoritative truth belongs to the proper owner:

- account / password / login truth belongs to Account / Identity infrastructure
- company membership truth belongs to `CompanyAccess`
- posting and accounting authority belongs to Citus accounting modules and engines

Rule:

SysAdmin is a governance entry point, not a business membership truth owner.

SysAdmin must not bypass business membership and become a business posting user.

### 2.6 Identity Infrastructure Boundary

Identity infrastructure should align with an ABP-style Account / Identity capability over time.

It may own:

- login
- password credentials
- account profile base data
- account UI
- sessions
- basic permission infrastructure
- reset-password mechanics
- account lock / disable mechanics

It may support Citus by projecting identity facts into application services.

It may not become the source of business authorization truth.

The following truth must remain Citus-owned and company-scoped:

- company membership
- active company
- company-scoped permission
- at-least-one-owner rule
- company inactive read-only gate
- maintenance-mode business access gate
- business action legality
- posting authority
- approval authority
- accounting authority

Rule:

ABP-style infrastructure can be the login carrier, permission projection layer, and UI support layer.

It must not replace `CompanyAccess` or accounting-domain authority.

### 2.7 Backend Admin And Login Permission Design

This section defines how backend administration, login, and company-scoped authorization must work together.

Naming rule:

- `Account` means platform identity.
- `Membership` means the CompanyAccess relationship between an account and a company.
- `SysAdmin` means independent platform-operations identity.
- `User` may appear in UI copy, but it must not become a Business App root module.

Current schema alignment:

- the current SQL draft table named `users` should be treated as platform account storage, not as a Business App user-management domain
- `company_memberships.user_id` means account actor reference
- `company_memberships` is the authoritative CompanyAccess membership table
- `business_sessions` must resolve account plus active company plus membership context
- SysAdmin state and sessions must stay outside Business App membership truth

Future schema may rename physical account storage to `platform_accounts`.

Until then, code and documentation must preserve this semantic distinction:

`users table name in draft SQL != Business App Users module`

#### 2.7.1 Business Login Flow

Business login must follow this order:

1. Platform Identity verifies account credentials.
2. Platform Identity checks account status: active, not disabled, not locked.
3. Maintenance mode is checked before normal Business App session creation.
4. CompanyAccess resolves companies where the account has active membership.
5. The account selects or resumes an active company.
6. CompanyAccess resolves membership role, permission tokens, company status, and permission version.
7. Business session context is created with account actor reference and active company context.
8. Business APIs re-resolve or validate the context server-side before reads and writes.

If the account has no active company membership:

- normal business data access is blocked
- the UI may show an empty-state / invitation-required screen
- no fallback global business scope is allowed

If maintenance mode is active:

- normal business login is blocked or converted to maintenance-only read notice
- existing business sessions cannot continue writes
- SysAdmin remains available

#### 2.7.2 Business Authorization Decision Order

Every business action must be evaluated in this order:

1. Platform account is authenticated.
2. Platform account is active and not locked / disabled.
3. Maintenance mode allows the requested business access.
4. Active company is resolved.
5. Active membership exists for the account and company.
6. Company status allows the operation.
7. Platform Billing / Subscription entitlement allows the capability where applicable.
8. Company-scoped permission allows the attempt.
9. Business domain rules decide whether the action is legal.
10. Posting / Tax / FX / Reconciliation engines decide formal accounting outcomes where applicable.

Important distinction:

- permission allows an attempt
- entitlement enables a capability
- company status gates mutation
- domain rules decide business legality
- engines decide accounting truth

#### 2.7.3 Company Status Gate

Company status must affect backend behavior:

- `active`: reads and writes may proceed subject to permission and domain rules
- `inactive`: reads may proceed, writes are blocked
- `suspended`: reads and writes may be restricted by platform policy
- `archived`: normal writes are blocked; read/export behavior must be explicit

The read-only gate is backend-owned.

Frontend disabled buttons are only hints.

#### 2.7.4 SysAdmin Login And Governance Flow

SysAdmin login must be independent from Business App login.

SysAdmin sessions must not contain:

- active company context for business operations
- company membership role
- business posting authority
- AP / AR / GL / Inventory execution authority

SysAdmin may execute platform governance commands, such as:

- enable or disable maintenance mode
- set company active / inactive / suspended / archived
- lock or disable a platform account through Account / Identity authority
- request or force password reset through Account / Identity authority
- assign or remove company owner/user through CompanyAccess authority
- view platform runtime and system audit surfaces

SysAdmin must not:

- post journals
- approve business documents as a company user
- bypass CompanyAccess membership checks
- rewrite accounting truth
- edit business documents directly as a super-owner

Governance routing rule:

- account credential actions route to Platform Identity / Account
- subscription and feature actions route to Platform Billing / Subscription
- membership and owner assignment route to CompanyAccess
- company lifecycle actions route to company lifecycle governance
- posting and accounting outcomes route to business modules and engines

#### 2.7.5 Profile, Email, And Password Flow

Business profile can allow safe profile edits directly when they do not change security identity.

Email and password changes require verification:

1. Notification readiness is checked.
2. A single-use verification code is issued.
3. Code is stored as a backend hash, not plaintext.
4. User submits code case-insensitively.
5. Backend validates purpose, expiry, attempts, and single-use state.
6. Platform Identity applies the email or password change.
7. Audit records are written.

Notification readiness must be true before secure profile flows are offered.

Readiness requires:

- provider configuration exists
- test send previously succeeded
- readiness was not invalidated by config change

#### 2.7.6 Platform Billing / Subscription Gate

Platform Billing / Subscription is not the first implementation slice, but its gate position must be reserved.

Example decision:

- subscription allows multi-currency
- CompanyAccess says this account may enter Company A
- company permission says this account may edit AP mappings
- AP domain says the mapping change is legal
- Posting Engine still controls formal accounting truth

Subscription or feature state must not replace company membership or domain legality.

#### 2.7.7 Minimum Persistence Direction

Target persistence responsibilities:

- Platform Identity / Account: platform account, credential, verification, reset, lock/disable state
- Platform Billing / Subscription: plan, subscription, seat, feature entitlement, usage limit state
- CompanyAccess: membership, role, company-scoped permission tokens, owner invariant, active company context
- SysAdmin: independent sysadmin account/session or external admin realm, maintenance mode, platform runtime governance audit
- Business Domains: AP / AR / GL / Inventory / Tax / Reports / Reconciliation truth

Important schema direction:

- `company_memberships.permissions` should be a JSON array of permission tokens, not an arbitrary object blob
- permission tokens should be catalogued and versioned by CompanyAccess
- active company may be remembered as a preference, but the authoritative active company must be resolved against live membership and company status
- SysAdmin account identifiers must not be valid business membership substitutes

## 3. Business User Permission Model

Minimum company roles:

- `owner`
- `user`

Owner means governance authority inside a company.

Owner can:

- manage company memberships
- assign company-scoped permissions
- assign owner/user role
- govern company-level settings if permitted by domain rules
- ensure the company always retains at least one owner

User means a business operator with granular company-scoped permissions.

User permissions should support:

- AR
- AP
- approve
- reports
- settings access
- book governance
- mapping management
- reconciliation

Rule:

`owner` and `user` are not enough by themselves.

Backend authorization must evaluate:

`account_id + active_company_id + membership role + permission set + system state + domain legality`

## 4. Multi-Company Access Model

One account may belong to multiple companies.

One company may have multiple accounts.

Every Business App session must resolve:

- current account
- available companies
- active company
- active membership
- active role
- active permission set

No business read or write may execute without active company context.

Switching company must switch:

- data scope
- permission scope
- reports
- settings
- numbering
- templates
- currencies
- FX context
- cache namespace
- AI context

## 5. System State Gates

### 5.1 Company Inactive

If SysAdmin sets a company inactive:

- users may still view permitted historical data
- writes are blocked
- posting is blocked
- mutation workflows are blocked
- UI must show read-only state

Backend enforcement is mandatory.

Frontend hints are not enough.

### 5.2 Maintenance Mode

If maintenance mode is active:

- normal business users cannot log in for normal operations
- existing business sessions cannot continue writes
- business write APIs are blocked
- SysAdmin remains available
- maintenance state must be visible

Backend enforcement is mandatory.

## 6. Profile And Verification

Business user menu must include:

- Profile
- Log out

Profile may support:

- personal information
- email change
- password change

Email and password changes require verification.

Verification code rules:

- 6 characters
- letters and/or digits allowed
- case-insensitive
- single-use
- time-limited
- backend-validated

Precondition:

SMTP / notification readiness must be true before email or password change flows can be offered.

SMTP readiness requires:

- configuration exists
- test send succeeded
- readiness invalidates when config changes

## 7. Audit Requirements

Domain audit trail must record:

- membership created
- membership disabled
- role changed
- permission changed
- owner assignment changed
- last owner removal blocked
- company inactive/active changed
- maintenance mode changed
- user lock/disable requested by SysAdmin
- password reset requested by SysAdmin
- verification code issued
- verification succeeded
- verification failed where security-relevant

ABP/platform audit may record request-level activity.

Citus domain audit must record business-control truth.

## 8. Implementation Phases

### Phase 1: Business Session Truth

Goal:

Remove reliance on bootstrap user as real operator truth.

Deliver:

- CompanyAccess-backed account context
- available-company resolution
- active-company selection
- membership role and permission resolution
- business-session headers sourced from CompanyAccess truth
- Accounting API write guards using resolved session truth

### Phase 2: Permission Catalog

Goal:

Formalize company-scoped permission tokens.

Deliver:

- fixed permission catalog
- owner-governed assignment surface
- permission audit trail
- permission read model for shell and API

### Phase 3: Owner Governance

Goal:

Make owner assignment safe.

Deliver:

- at-least-one-owner backend invariant
- owner assignment / removal workflow
- last-owner removal blocked with audit

### Phase 4: SysAdmin Boundary

Goal:

Create independent SysAdmin governance session.

Deliver:

- SysAdmin login/session boundary
- maintenance mode controls
- company lifecycle controls
- user lock/disable/reset triggers
- no business posting bypass

Current skeleton:

- `PUT /control/companies/{companyId}/status`
- `PUT /control/accounts/{accountId}/status`
- `POST /control/accounts/{accountId}/password-reset-requests`
- `PUT /control/companies/{companyId}/memberships/{membershipId}/role`

Current shell surface:

- `Citus.SysAdmin.Blazor` Companies page can switch active company context and issue company status governance actions
- `Citus.SysAdmin.Blazor` Users page can issue platform account activate/inactive actions
- `Citus.SysAdmin.Blazor` Users page can trigger password-reset governance requests
- `Citus.SysAdmin.Blazor` Users page can list current active-company memberships and change owner/user role through CompanyAccess governance
- `Citus.SysAdmin.Blazor` Audit page can review recent company/account/password-reset/membership governance actions from `audit_logs`
- `Citus.SysAdmin.Blazor` Login page now supports governed first-SysAdmin provisioning when no operator exists
- `Citus.SysAdmin.Blazor` Security page now supports self-service SysAdmin secret rotation and session revocation
- protected SysAdmin accounts are shown as governance-protected, not editable through business-account actions
- `SysAdmin.Api` now exposes DB-backed `auth/login`, `auth/session`, and `auth/logout` for independent SysAdmin session flow
- `SysAdmin.Api` now exposes `auth/setup`, `auth/setup/first-account`, and authenticated `auth/rotate-secret`
- `SysAdmin.Blazor` now restores SysAdmin session token from browser session storage and requires authenticated session before entering protected shell pages
- `platform_runtime_state.notification_readiness` now gates whether password-reset verification may be issued
- password reset no longer proceeds when notification readiness is unverified
- password reset now issues a `password_reset` verification-code record, creates a platform notification dispatch record, and attempts provider-backed secure delivery immediately after commit

Rules preserved by the skeleton:

- company lifecycle changes update company status and write SysAdmin audit
- account status changes update Platform Identity storage and write SysAdmin audit
- password reset request records the governance trigger and then attempts provider-backed secure delivery after commit
- password reset dispatch now records `sent / failed` outcome on `platform_notification_dispatches` and appends audit events for delivery success/failure
- membership role changes are routed through CompanyAccess governance workflow
- demoting the last active owner is blocked by CompanyAccess persistence logic
- membership role changes write `membership_role_changed` domain audit records
- SysAdmin audit page now reads formal company/account/membership governance events from `audit_logs`
- non-development environments no longer rely on unconditional bootstrap credential seeding by default
- first SysAdmin provisioning now has a governed fallback path when no operator exists
- SysAdmin secret rotation revokes active sessions and pushes operator back through re-authentication
- SysAdmin login uses `sysadmin_accounts`, not Business App company membership
- SysAdmin protected endpoints require explicit `X-Citus-SysAdmin-Session`
- password reset governance checks notification readiness before issuing verification code
- SysAdmin endpoints do not create business sessions
- SysAdmin endpoints do not grant posting, approval, AP, AR, GL, or Inventory authority

Phase 4 closure note:

- the minimal SysAdmin boundary is now closed as a product-grade skeleton
- any future notification work should improve provider abstraction, template management, retry policy, and verified test-send UX, not reintroduce placeholder-only dispatch semantics

### Phase 5: System State Gates

Goal:

Make company inactive and maintenance mode enforceable.

Deliver:

- read-only company gate
- business write middleware/filter
- maintenance-mode business lockout
- SysAdmin allowlist
- business shell projection from persisted `CompanyAccess` truth, not static fallback, so active company status and read-only banners stay aligned with backend gating
- core draft / post / apply pages should disable primary write actions and explain blocked state before the backend rejection path is hit
- `Web.Business.*` pages must consume write-gate state through `ICompanyAccessShellSession` refresh/projection, not by directly depending on `Web.Shell` implementation types
- document detail, open-item control, and journal review surfaces should also hide or disable reverse / adjustment / lifecycle write affordances whenever company or platform state blocks writes
- top-level shell entry points such as home quick actions, source-document browser draft shortcuts, and navigation links should not keep advertising direct draft/editor entry when the active company or platform runtime is write-blocked
- company governance and CompanyAccess control surfaces such as book governance, open-item adjustment account mappings, and membership-permission assignment should stay readable but must disable governed mutation affordances and explain blocked state before save/deactivate/governance actions are attempted
- user-preference writes that live in the business shell, such as CompanyAccess system setup preferences, should follow the same gate: readable preview is allowed, but save/select mutation affordances must respect blocked company and maintenance state
- backend guard coverage should explicitly assert the paired rule set: maintenance and read-only company state block writes, but they still allow read-only requests unless a stricter policy is later introduced
- endpoint-oriented guard tests should use the real document/open-item HTTP request contracts for draft save, lifecycle reverse, and adjustment request/execute paths so blocked-state regressions are caught close to actual mutation endpoints
- settlement prepare/post, reverse-request submit/execute, and adjustment-account mapping save/deactivate contracts should also stay under endpoint-oriented blocked-state coverage so governance and settlement mutations cannot silently bypass the shared write gate

### Phase 6: Profile Verification

Goal:

Make email/password changes safe and real.

Deliver:

- notification readiness read model
- verification code store
- email change request/confirm
- password change request/confirm
- audit trail
- platform-owned profile workflow should keep direct display-name edit separate from email/password verification requests
- email/password self-service flows must remain platform-account truth, not CompanyAccess truth, while still being reachable from the Business App shell as an aggregated Profile surface
- email/password request actions should be blocked by maintenance mode and notification-readiness failure, but confirm flows and profile reads should remain auditable and backend-owned
- verification requests should store pending target state in governed verification payload rather than mutating `users` truth before confirmation
- the shell should expose a formal Profile page with current account summary, pending verification visibility, and governed request/confirm actions for email and password changes
- `Web.Shell` should also expose minimal profile HTTP contracts (`GET`, `display-name save`, `email/password request`, `email/password confirm`) so profile verification can be locked down with real endpoint contract tests rather than workflow-only tests
- after the profile HTTP contracts exist, the interactive `Profile` page should consume that same API surface instead of calling the workflow directly, so UI behavior and endpoint contract coverage stay on one path
- notification readiness should be surfaced into the Business App Profile page as a read-only platform signal, so blocked email/password actions can be explained with `config/test/verification-ready` detail instead of a generic failure banner
- SysAdmin Maintenance should provide a real notification test-send action backed by the configured provider, and test execution should refresh `test_status` / `last_tested_at` without silently granting `verification_ready`
- Business and SysAdmin notification-readiness surfaces should share one backend workflow so readiness reads, provider-configuration errors, and test-send side effects do not drift between shells

## 9. First Slice Recommendation

The first implementation slice should be:

`CompanyAccess-backed Business Session Truth`

Target outcome:

- Web.Shell no longer treats bootstrap account as production operator truth
- active company is resolved through CompanyAccess membership
- membership permissions feed business-session headers
- no active company means no business write path
- no active membership means no business write path

This slice should stay inside approved boundaries:

- `CompanyAccess`
- `SysAdmin`
- `Web.Shell`
- `Infrastructure.PostgreSQL`

It must not create forbidden root modules:

- `Users`
- `UserManagement`
- `Identity`
Phase 6 progress note:

- SysAdmin notification readiness test-send now has endpoint contract coverage at the real HTTP layer, not just workflow tests.
- Contract tests cover unauthorized access, successful authenticated test-send payload mapping, and `InvalidOperationException -> 400` behavior for `/control/notification-readiness/test-send`.
- SysAdmin notification readiness read/update endpoints are now also covered at the HTTP layer, including unauthorized access and `PUT` persistence-to-runtime-state plus workflow-summary response semantics.
- SysAdmin password-reset request is now covered at the HTTP layer for `401`, `202`, `404`, `400`, and `503`, including confirmation that the endpoint uses authenticated session identity rather than trusting a request-body `SysAdminAccountId`.
- SysAdmin company/account status governance endpoints are now covered at the HTTP layer for `401`, `200`, `404`, and `400`, including confirmation that both endpoints resolve actor identity from the authenticated session rather than request-body `SysAdminAccountId`.
- SysAdmin audit-events read and membership-role governance are now covered at the HTTP layer, including `401` gate behavior, summary/result payload mapping, and confirmation that membership role change also resolves actor identity from the authenticated session.
- Web.Shell now has a real business sign-in / resume / sign-out / active-company-switch HTTP surface at `/api/business/session/*`, backed by platform account truth plus CompanyAccess-resolved company context rather than bootstrap-only shell defaults.
- Business login now checks maintenance mode before session creation, resolves the active company through CompanyAccess membership truth, persists a `business_sessions` record, and restores shell state from browser session storage on reload.
- Web.Shell layout now gates protected business pages behind authenticated business session restore, exposes a real sign-in page, and surfaces a top-bar active-company switcher plus sign-out flow instead of assuming a default logged-in bootstrap operator.
- `WebShellBusinessSessionHeaderHandler` now only emits business session headers after authenticated session/context exists, so API clients do not leak empty GUID headers before login.
- Business session endpoint contract tests now cover sign-in success, maintenance lockout, resume/context read, persisted active-company switching, and sign-out revocation at the real HTTP layer.
- `Web.Shell` platform profile and notification-readiness APIs no longer trust `X-Citus-User-Id` or bootstrap fallback for authenticated use cases; they now require a valid `X-Citus-Business-Session`, resolve the actor through platform business-session validation, and return `401` when the business session is missing or invalid.
- `PlatformProfileClient` and the interactive Profile page now consume the authenticated business session token instead of manually pushing user-id headers, so profile verification flows are aligned with the same login/session truth as the rest of the shell.
- Profile/notification contract tests now cover `401` behavior for missing or invalid business sessions plus successful authenticated profile and readiness payload mapping at the real Web.Shell HTTP layer.
- `ShellLayout` now re-applies login-route gating after first render as well, so if a protected page clears the business session at runtime the shell redirects back to `/login` instead of sitting on a dead protected surface.
- The interactive Profile page now treats `Business sign-in is required.` as a session-expired signal: it clears browser-stored business auth state, clears `WebShellState`, and routes the user back to `/login` with a warning instead of leaving a stale verification form onscreen.
- `WebShellBusinessSessionClient` now exposes a context-probe result that distinguishes `401 / requires sign-in` from transient probe failure, so shell pages do not confuse a temporary context refresh problem with a hard logout condition.
- `WebShellSessionExpirationCoordinator` is now wired into `Home`, `NavMenu`, the Company governance pages, source-document browser/detail drill-down, open-item drill-down, draft editors, and the `WebShellCompanyAccessShellSession` adapter. Those surfaces now clear the stored business token and redirect to `/login` only when the shell probe returns an actual authentication-expired result.
- `ShellSourceDocumentDraftClient`, `ShellAccountingDocumentReviewClient`, and `ShellOpenItemDrillDownClient` now return an explicit authenticated API result model instead of collapsing `401` into `null`, generic failure tuples, or thrown request errors.
- Draft editors, source-document detail/reverse flows, and open-item adjustment flows now treat business-request `401` responses the same way as context-probe expiry: clear the stored business session, clear shell state, and route the user back to `/login` instead of leaving stale write surfaces open.
- `ShellOpenItemAdjustmentAccountMappingClient` and the company mapping-governance page now use the same authenticated API result model, so company policy writes also respect real business-session expiry instead of surfacing `401` as a generic governance error.
- `PlatformProfileClient` and the interactive Profile page now also use the shared authenticated API result model instead of tuple-plus-string auth checks, so profile reads and verification request/confirm actions follow the same `401 -> session expired -> clear token -> return to /login` path as the rest of the shell.
- Future MFA integration must remain challenge-first and session-second: primary credential validation may return `RequiresSecondFactor = true` plus challenge metadata, but no `business_sessions` token should be issued until the second factor succeeds.
- MFA must stay subordinate to existing truth boundaries: platform identity proves the actor, CompanyAccess still resolves active company and company-scoped authorization only after the business session is fully authenticated, and MFA may not bypass maintenance mode, company inactive/read-only gates, or company membership legality.
- Business sign-in now has a formal MFA challenge contract skeleton: when `users.mfa_mode = 'email_code'`, primary credential validation may return `AuthenticationStage = challenge_required` plus `MfaChallengeId`, `MfaChallengeExpiresAtUtc`, and available factor metadata instead of immediately issuing a business session.
- `POST /api/business/session/mfa/complete` is now the formal second step for business MFA. The session token is issued only after the challenge code is confirmed, which keeps the shell aligned with the “challenge-first, session-second” rule already recorded in authority.

Checkpoint summary (2026-04-16):

- Phase 4 is closed as a minimal SysAdmin control-plane skeleton: independent SysAdmin sign-in/session, first-operator provisioning, secret rotation, provider-backed notification test-send, password-reset governance, company/account/membership governance, and audit read surfaces are all in place and covered by HTTP contract tests.
- Phase 5 is largely closed as a business-state gate skeleton: maintenance mode and company read-only state now block writes across draft/post/apply/reverse/governance entry points, with both shell affordance control and endpoint-oriented guard coverage.
- Phase 6 is materially in progress: platform-owned profile verification, notification readiness, business sign-in/session persistence, active-company switching, and session-expiry handling are now running through real Web.Shell HTTP contracts instead of bootstrap-only assumptions.
- Business session truth now sits on the intended boundary: Platform Identity proves the actor, `business_sessions` restore shell state, and `CompanyAccess` still resolves active company, write gates, and company-scoped authorization after sign-in.
- The remaining shell-client polish work is now mostly about consistency: any remaining shell HTTP clients should converge on the same authenticated-result model so `401` produces one governed session-expiry path instead of mixed null/error behaviors.
- MFA is now a recorded product requirement in the authority document: challenge-first, session-second, platform-owned, auditable, and always subordinate to CompanyAccess truth plus maintenance/company-state gates.

- Platform-owned profile now surfaces and governs `mfa_mode` for the current account through the same authenticated business-session contract as other profile actions. The first supported modes are `none` and `email_code`.
- MFA enablement is now constrained at the platform profile boundary as well as the sign-in boundary: enabling email_code requires a verified email address plus verified notification readiness, and the interactive Profile page now makes those prerequisites visible before the user saves the mode.
- Platform profile now also surfaces the most recent MFA mode-change audit timestamp and previous mode, so MFA governance is not just configurable but visibly traceable to the current account owner.
- SysAdmin control now has a governed `account mfa reset` path: authenticated SysAdmin operators can force an account back to `mfa_mode = none`, revoke any active business MFA challenges, emit an `account_mfa_reset` audit event, and exercise that flow through real HTTP contract coverage (`401 / 200 / 404 / 400`) plus the SysAdmin users shell action.
- SysAdmin `control/users` now projects a real platform-account read model instead of only static shell seed data, and that summary includes current `mfa_mode` plus the most recent MFA reset timestamp/reason. The SysAdmin users page and the Business Profile page both surface that reset visibility so MFA recovery is not only executable but also reviewable.
- MFA recovery governance is now a real request/review/execute flow instead of only an emergency reset button. Business Profile can submit `/api/platform/profile/mfa-recovery/request`, SysAdmin can list open recovery requests, approve or reject them, and execute only approved requests through dedicated control endpoints.
- Recovery flow truth is now visible on both sides: the Business Profile page shows any active recovery request and latest review metadata, while SysAdmin Users now includes an MFA recovery queue with approve/reject/execute controls plus per-user pending-request hints.
- Direct SysAdmin `mfa-reset` remains available as an emergency path, but the governance repository now blocks that shortcut when the account already has an open recovery review. This keeps request-first recovery and emergency override from silently colliding.
- HTTP contract coverage now includes MFA recovery queue/list/review/execute on the SysAdmin side and authenticated request submission on the Business Profile side, so the new governance loop is covered at the real endpoint layer rather than only at workflow level.
- Business Profile now also exposes an MFA governance timeline backed by platform audit truth. The page reads a dedicated `/api/platform/profile/mfa-timeline` surface and shows mode changes, recovery request/review/execute events, reset events, actor labels, reasons, and timestamps in one account-scoped sequence.
- MFA profile actions that materially change governance state now refresh that timeline immediately after save/request, so users see their own current MFA governance history without needing to switch to SysAdmin audit views.
- SysAdmin Users now also exposes a per-account MFA timeline backed by the same audit truth through `GET /control/accounts/{accountId}/mfa-timeline`. Operators can open MFA history for a selected account, review mode changes, recovery request/review/execute events, reset events, actor labels, reasons, and timestamps, and the route is now covered by real HTTP contract tests (`401` unauthorized plus authenticated payload mapping).
- SysAdmin Audit now includes an account-scoped MFA timeline drill-down in addition to the flat governance event table. Operators can select a managed account directly inside the audit surface and review the same account-level MFA history without leaving the formal audit/read page.
- SysAdmin MFA governance surfaces now support account-scoped drill-through: `Users` and recovery-queue actions can jump directly into `Audit?accountId=...`, and the audit page keeps that selected account synchronized through the query string so the formal audit surface and the operational users surface no longer drift apart.
- SysAdmin `Users` now treats MFA recovery as an explicit three-stage governed flow instead of only a flat queue row. Each request can open a recovery-flow panel that shows requested, review, and execute stages with current status, review metadata, execution readiness, and direct drill-through to the formal audit page.
- SysAdmin `Users` now also reads formal account-scoped MFA recovery history from a dedicated control endpoint. The recovery-flow surface is no longer limited to open queue rows: rejected and executed requests now remain visible with request, review, and execution timestamps/reasons so operators can review past recovery outcomes, not only pending work.
- TOTP enrollment plus business-sign-in challenge skeleton is now materially closed: platform profile can start and confirm governed `totp_app` enrollment, pending enrollment truth is stored separately from active MFA mode, business sign-in can require a `totp_app` second-factor challenge before session issuance, and SysAdmin/emergency MFA reset paths revoke active TOTP enrollment state together with other MFA challenge truth.
- MFA recovery policy is now being made explicit across read surfaces instead of staying implicit in backend checks alone: Business Profile states that recovery is request-first and review-required, while SysAdmin `Users` projects whether an open governed recovery request is already blocking the emergency reset path for that account.
- Recovery-policy reasoning is now part of the serialized summary truth itself, not only local page logic: `PlatformAccountProfileSummary` and managed-user read models now emit policy-facing reason text for self-service recovery and emergency-reset availability, so Business and SysAdmin surfaces consume the same bounded governance semantics.
- The final identity hardening slice now also closes the three remaining security risks that were still blocking a confident return to business modules: new TOTP enrollment writes now protect `secret_base32` at rest, business sessions and MFA challenges now snapshot `users.security_stamp` and are rejected after password/MFA security changes, and MFA challenge completion now enforces a concrete failure threshold with temporary account lockout instead of infinite retry.
- `Web.Shell` business-session auth failures now preserve backend error bodies on `401`, so hardening outcomes such as temporary MFA lockout are visible to the operator instead of collapsing into a generic sign-in-required message.

### Phase 6 exit gate and next sequencing

Phase 6 should not expand indefinitely. The identity/MFA slice is now materially closed for the current phase:

1. `TOTP secret protection at rest`
2. `security_stamp`-driven business session and MFA challenge invalidation
3. MFA challenge retry threshold plus temporary lockout
4. recovery policy closure across Business Profile and SysAdmin read surfaces

After this minimal hardening closure, the next priority is to pivot back to business modules rather than continue broad identity expansion.

Planned return-to-business sequence:

1. resume `AR/AP` product hardening from `ARAP Project Plan.MD`
2. close remaining AR/AP source/settlement/policy polish items that still feel demo-like
3. only then open the first governed `Inventory` slice, with multi-warehouse assumptions from the start

Working rule:

- identity/MFA remains a platform-critical stream, but it should now be treated as a bounded enabling track
- business-module momentum should resume immediately after the remaining Phase 6 slice above
