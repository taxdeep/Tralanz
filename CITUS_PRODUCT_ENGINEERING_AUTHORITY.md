# Citus Product And Engineering Authority

This document is the highest-priority product and engineering authority for Citus.

All code, database schema, APIs, services, UI, reports, permissions, admin behavior, AI behavior, FX behavior, cache behavior, and future modules must comply with this guide.

If any conflict exists, follow this priority order:

`This document > other requirement docs > task-specific notes > temporary implementation habits`

Any implementation that conflicts with this document must be corrected.

Related executable specifications currently include:

- [MULTI_COMPANY_AUTH_AND_CONTROL_SPEC.md](./MULTI_COMPANY_AUTH_AND_CONTROL_SPEC.md)
- [POSTING_TAX_FX_ENGINE_EXECUTION_SPEC.md](./POSTING_TAX_FX_ENGINE_EXECUTION_SPEC.md)
- [AP_AR_LIFECYCLE_CONTROL_SPEC.md](./AP_AR_LIFECYCLE_CONTROL_SPEC.md)
- [UI_NAVIGATION_AND_DESIGN_SYSTEM_SPEC.md](./UI_NAVIGATION_AND_DESIGN_SYSTEM_SPEC.md)
- [POSTING_ENGINE_MULTICURRENCY_DESIGN.md](./POSTING_ENGINE_MULTICURRENCY_DESIGN.md)
- [POSTGRESQL_CORE_SCHEMA_SPEC.md](./POSTGRESQL_CORE_SCHEMA_SPEC.md)
- [CSHARP_DOMAIN_AND_APPLICATION_SKELETON.md](./CSHARP_DOMAIN_AND_APPLICATION_SKELETON.md)

## 1. Product Definition

### 1.1 Core Definition

**Citus = a strictly isolated multi-company accounting system + a strong-rule core engine + a control layer + an AI suggestion layer + modular business capabilities.**

### 1.2 Product Nature

Citus is not a loose bookkeeping tool and not a feature pile.

It is:

- a multi-company accounting and business system
- a correctness-first accounting engine
- a control-oriented financial system
- an AI-assisted understanding system, not an AI execution system
- a modular, engine-centric, long-term platform

### 1.3 Product Goal

Citus aims to provide a system that is:

- suitable for small businesses
- controllable for bookkeepers and accountants
- naturally ready for multi-company use
- disciplined in AI usage
- stable enough for long-term expansion

### 1.4 核心技术栈（ABP 治理版，适合 AI 主导开发）
- 框架：.NET 10 (ASP.NET Core)
- **总体架构**：**ABP-based Modular Monolith（基于 ABP 的模块化单体） + DDD（领域驱动设计） + CQRS（读写分离） + Vertical Slice Architecture（按功能切片）**
  - ABP 负责平台治理与通用基础设施。
  - Citus 自研模块负责 accounting truth、posting/tax/FX/reconciliation 等核心业务真相。
  - DDD 应用于规则密集的核心域；简单后台管理与配置型模块保持务实，不为了模式而模式。
  - 首期不强制按每个模块拆成大量独立项目；先在单体内保持清晰边界，等模块稳定后再提升可复用度。
- 数据库：PostgreSQL
- ORM：EF Core 为主，Dapper 为辅
  - 写操作（Commands）：统一使用 EF Core，负责事务、Change Tracking、迁移、领域验证。
  - 读操作（Queries）：默认使用 EF Core（AsNoTracking / Projection / Raw SQL）。
  - 只有在报表、大分页、复杂聚合、性能热点被确认后，再引入 Dapper。
- 前端：Blazor Web App + MudBlazor
  - 优先面向内部 ERP / Back Office 场景，保持 C# 全栈，降低学习与维护成本。
  - 首期不把 React/Vue + TypeScript 作为主路线。
  - 若未来需要独立客户门户、公开站点、移动端配套或前端团队独立演进，再评估 React/Vue + TypeScript。
- 身份认证 / 账户：优先复用 ABP Identity / Account
  - 若未来出现独立 AuthServer、第三方 API 客户端、SSO、单点登出等需求，再引入 OpenIddict 体系。
- 平台治理基础设施：优先复用 ABP / ABP Commercial 模块
  - Permission Management
  - Setting Management
  - Feature Management
  - Audit Logging
  - Background Jobs / Background Workers
  - Blob Storing
  - Text Template Management
  - Tenant Management / SaaS（仅在 SaaS 化时启用）
- API 网关：首期不强制引入
  - 单体阶段可不使用独立网关。
  - 当系统拆分为多个独立服务、需要统一入口、转发、限流或鉴权编排时，再引入 YARP。
- 缓存：分阶段引入
  - Phase 1：IMemoryCache / ABP Distributed Cache（配置、字典、低频变化主数据）
  - Phase 2：HybridCache + Redis（高频只读、SmartPicker、报表加速）
  - 当 ABP multi-tenancy 启用时，所有缓存 key 必须同时带 `tenant_id` 与 `company_id`；未启用时至少带 `company_id`。
  - 缓存只加速，不作为 truth；写操作必须主动失效。
- 异步 / 后台任务：**ABP Background Jobs / Background Workers + Outbox 模式优先**
  - 用于报表生成、通知发送、审计日志同步、FX rate refresh 等非实时任务。
  - 首期不强制引入 MassTransit / RabbitMQ。
  - 只有在出现多服务可靠投递、复杂工作流或 Saga 需求后，再升级到 MassTransit。
- 监控 / 可观测性：`ILogger` + HealthChecks + OpenTelemetry
  - 首先保证错误日志、请求链路、健康检查可见。
  - Prometheus / Grafana 作为第二阶段增强，而不是首发硬依赖。


## 2. Core Principles

The following principles are non-negotiable.

### 2.1 Immutable Principles

- Correctness（正确性） > Flexibility（灵活性）
- Backend Authority（后端权威） > Frontend Assumptions（前端假设）
- Structure（结构） > Convenience（便利）
- Auditability（可审计性） > Performance Tricks（性能小技巧）
- Company Isolation（公司隔离） > Everything（一切）
- Engine Truth（引擎真相） > UI Presentation（界面展示）
- Historical Honesty（历史诚实性） > Cosmetic Neatness（外观整洁）
- Cache（缓存） = Acceleration ONLY（仅用于加速）
- AI = Suggestion Layer ONLY（仅作为建议层）

### 2.2 Principle Clarifications

#### Correctness > Flexibility

The system may limit user freedom in order to protect accounting correctness.

#### Backend Authority > Frontend Assumptions

Validation, numbering, lifecycle, posting, FX conversion, and accounting truth must be decided by the backend.

#### Structure > Convenience

Stable structure and consistent logic are more important than local convenience.

#### Auditability > Performance Tricks

No shortcut is allowed if it weakens traceability.

#### Company Isolation > Everything

Multi-company isolation is the highest operational boundary.

#### Historical Honesty > Cosmetic Neatness

If historical truth cannot be reconstructed with confidence, the system must show it honestly as unavailable / unknown / legacy-unavailable rather than invent a cleaner story.

#### Cache = Acceleration ONLY

Cache may accelerate reads, ranking, and reports. Cache may not become accounting truth, authorization truth, or validation truth.

#### AI = Suggestion Layer ONLY

AI may suggest, explain, rank, warn, and summarize.
AI may not post, reconcile, or alter books directly.

## 3. System Architecture

### 3.1 Two-Layer System

#### 1) Business App

The main product used by business users.

This is where accounting, reporting, reconciliation, customers, vendors, invoices, bills, payments, tax, templates, settings, notifications, and operational workflows belong.

#### 2) SysAdmin / Host Admin

A fully separate administration system.

It has independent operational responsibility and does not participate in normal business posting flows.

SysAdmin / Host Admin controls:

- tenant / workspace lifecycle
- company lifecycle
- users
- system mode / maintenance mode
- runtime observability
- system-level administration

### 3.2 Architecture Direction

Citus must remain:

- engine-centric
- module-based
- connector-ready
- AI-assisted, not AI-driven
- ABP-governed for platform concerns, domain-sovereign for accounting truth

Core truth belongs to engines.
Business workflows belong to modules.
External integrations belong to connectors.
AI belongs to the suggestion layer.
ABP belongs to platform governance and reusable infrastructure.

### 3.3 Shared Architecture Layers

The platform should progressively standardize into these reusable layers:

#### Core Engines

- Posting Engine
- Tax Engine
- FX Conversion Engine
- Numbering Engine
- Reconciliation Control Engine

#### Business Modules

- Invoices
- Bills
- Customers
- Vendors
- Journal Entry
- Reports
- Tasks
- Payment / Collection flows

#### Platform / Infrastructure Modules

- Identity / Account
- Permission Management
- Setting Management
- Feature Management
- Audit Logging
- Background Jobs
- Blob Storing
- Text Template Management
- Shared Cache Infrastructure
- AI Assist Platform
- SmartPicker Acceleration
- Report Acceleration

#### Connector Layers

- payment providers
- channels
- external rate providers

### 3.4 ABP Governance Boundary

ABP / ABP Commercial should be treated as the platform governance layer.

It may own:

- authentication / account UI
- permission persistence and management
- feature flags / edition controls
- setting persistence and hierarchy
- request/action/entity audit logs
- background jobs and workers
- blob storage abstraction
- text template editing
- SaaS / tenant administration where applicable

Citus domain modules must remain the authority for:

- posting truth
- tax truth
- FX snapshot truth
- reconciliation truth
- accounting lifecycle truth
- report semantics
- company-level accounting rules

No ABP module may bypass the Posting Engine or replace accounting domain rules.

### 3.5 Official Code Boundary Names

User-facing navigation labels and code boundary names are not the same thing.

Navigation may use business-friendly labels such as Dashboard, Journal Entry, Receive Payment, Pay Bills, and Settings.

Code and project boundaries must use approved root names only.

Approved root business modules:

- `Company`
- `CompanyAccess`
- `GL`
- `AR`
- `AP`
- `Reconciliation`
- `Reports`
- `Tasks`

Approved root engines:

- `Posting`
- `Tax`
- `FX`
- `Numbering`
- `ReconciliationControl`

Approved root infrastructure areas:

- `AIAssist`
- `Notifications`
- `Caching`
- `SmartPicker`
- `Reporting`

Mapping rules:

- Journal Entry, Chart of Accounts, and related general-ledger workflows belong to `GL`.
- Customers, Invoices, and Receive Payment belong to `AR`.
- Vendors, Bills, and Pay Bills belong to `AP`.
- Company-level controlled areas such as Profile, Templates, Sales Tax, Numbering, Notifications, Security, and Currencies belong to `Company`.
- Company membership, invitations, owner/user assignment, active company context, and company-scoped authorization belong to `CompanyAccess`.
- Dashboard is a host-level product surface, not an independent root module.
- Settings is a navigation surface, not a dumping-ground root module.

## 4. Multi-Company Architecture

### 4.1 Boundary Model

Citus must explicitly distinguish three boundaries:

- **Host / Platform** = the system owner and platform administration boundary
- **Tenant / Workspace** = the SaaS customer or workspace boundary managed by ABP multi-tenancy when enabled
- **Company** = the legal accounting entity boundary inside a tenant / workspace

**Default future direction:** `tenant/workspace != company`

One tenant / workspace may contain multiple companies.
A company is not the same thing as an ABP tenant by default.

### 4.2 Membership Model

- one user may belong to multiple companies
- one company may have multiple users
- when ABP multi-tenancy is enabled, these memberships are expected to be **within the same tenant / workspace** unless explicitly governed otherwise
- every authenticated business session must have a clear active company context

Session must include:

- `active_company_id`

If the system later supports multiple workspaces for the same user, the runtime must also have a clear tenant / workspace context before company selection.

### 4.3 Mandatory Data Rules

All core accounting and business objects must have:

- `company_id NOT NULL`

When ABP multi-tenancy is enabled, all tenant-owned business objects should also be tenant-aware through `TenantId` / `tenant_id`.

All reads, writes, relations, reports, exports, caches, and AI context must be company-scoped.
When multi-tenancy is enabled, they must also be tenant-scoped first.

This applies to, at minimum:

- accounts
- journal entries
- journal lines
- ledger entries
- invoices
- bills
- customers
- vendors
- taxes / tax codes
- numbering configs
- templates
- reconciliations
- audit logs
- tasks
- products/services
- currencies
- exchange rates
- notification configs
- security configs

### 4.4 Mandatory Write Validation

Every write path must validate both tenant/workspace consistency (when enabled) and company consistency, including:

- `document.company_id == session.active_company_id`
- `account.company_id == session.active_company_id`
- `tax.company_id == session.active_company_id`
- `customer/vendor.company_id == session.active_company_id`
- `journal_entry.company_id == source.company_id`
- `party.company_id == session.active_company_id`

When ABP multi-tenancy is enabled:

- runtime `CurrentTenant.Id` must match the tenant ownership of the target data
- tenant switch is not equivalent to company switch

Any cross-company reference must be rejected.
Any cross-tenant reference must be rejected.

### 4.5 Forbidden by Default

The following are forbidden:

- cross-company journal entries
- cross-company ledger entries
- cross-tenant access to business truth
- shared chart of accounts across companies
- shared customers across companies
- shared vendors across companies
- shared tax objects across companies
- shared business documents across companies
- business documents referencing accounting objects from another company
- treating ABP tenant features/settings as a substitute for company-level accounting ownership

### 4.6 UI Behavior

Users must always know which company they are in.

The UI must clearly provide:

- current company display
- company switcher
- full company-context switching

If multi-workspace is later enabled, the UI must also clearly show current workspace / tenant.

When switching company:

- UI shell may stay stable
- all data, permissions, reports, settings, numbering, templates, currencies, and FX context must switch


## 5. Authorization, Roles, and System Control

### 5.1 Business Roles

The Business App must support at least:

- `owner`
- `user`

Rules:

- each company must always have at least one owner
- owners can manage company users and permissions
- user permissions should be configurable by domain

Minimum recommended permission domains:

- AR
- AP
- approve
- reports
- settings access
- reconciliation-related access

### 5.2 ABP Permission Boundary

ABP Permission Management should be the canonical platform store for permission values and grant management.

Recommended use:

- ABP permissions control whether a user can access an operation, page, endpoint, or menu
- Citus domain policies control whether a business action is valid in the current company, state, period, and workflow

This means:

- permission allows an attempt
- domain rules decide whether the attempt is legal

Approval logic, posting authority, period-close restrictions, and reconciliation completion rules must remain domain-owned, not only permission-owned.

### 5.3 Feature / Edition Control

When ABP SaaS / Feature Management is enabled, feature flags and editions may be used to control commercial packaging and tenant/workspace-level capability rollout.

Examples:

- multi-currency enabled
- AI assist enabled
- advanced reports enabled
- attachments enabled
- customer portal enabled

Feature flags may enable or disable capabilities.
Feature flags may not rewrite historical accounting truth or bypass engines.

### 5.4 SysAdmin / Host Admin Role

SysAdmin / Host Admin is not a business-company extension.

It is a separate platform identity and must not reuse the business user model for company write operations.

SysAdmin / Host Admin capabilities include:

- tenant / workspace lifecycle control
- company delete / inactive / lifecycle control
- user edit / disable / reset password / role management
- maintenance mode
- runtime/system error visibility
- platform-level administration

### 5.5 Identity, Membership, and Control Boundary

Platform identity is platform-governed.
Company membership and company-scoped authorization are business-module-governed.

Rules:

- authentication, password, login flows, and platform identity infrastructure belong to the platform layer
- company membership, invitations, owner/user assignment, active company resolution, and company-scoped authorization belong to `CompanyAccess`
- global user disable, password reset, maintenance control, and platform lifecycle actions belong to `SysAdmin`
- a generic business module named `Users`, `UserManagement`, or `Identity` is forbidden unless explicitly approved as a platform module

This boundary exists to keep platform identity logic separate from company-scoped business control.

### 5.6 Maintenance Mode

The system must support maintenance / restart mode.

When enabled:

- normal users cannot log in or perform writes
- maintenance state must be visible
- SysAdmin / Host Admin remains available


## 6. Posting Engine

### 6.1 Single Official Entry Path

All formal accounting must go through the Posting Engine.

Standard flow:

**Document -> Validation -> Tax Calculation -> FX / Currency Resolution -> Posting Fragments -> Aggregation -> Journal Entry -> Ledger Entries**

### 6.2 Prohibited Behavior

The following are forbidden:

- bypassing the Posting Engine
- writing formal ledger entries directly
- letting source documents change without keeping JE in sync
- creating formal JE without source linkage
- using provider data or UI preview as ledger truth

### 6.3 Journal Entry Requirements

Journal Entry must include at least:

- `company_id`
- `status`
- `source_type`
- `source_id`
- totals / summary fields
- posting metadata
- auditability metadata

Required JE statuses:

- `draft`
- `posted`
- `voided`
- `reversed`

Business document lifecycle remains the source of truth.
JE status must stay consistent with the source lifecycle.

### 6.4 Concurrency and Atomicity

Posting must run in a DB transaction and must ensure:

- source row locking
- duplicate-post prevention
- atomic source status / JE / ledger creation
- full rollback on failure

## 7. Data Identity and Numbering

### 7.1 Entity Number

System identity uses:

**`ENYYYY########`**

Rules:

- globally unique
- immutable
- backend-generated
- cannot be overridden by frontend
- unaffected by rename / void / reverse

### 7.2 Display Number

Display numbers are human-facing business numbers, not identity truth.

Examples include:

- invoice number
- bill number
- customer ID
- vendor ID
- receipt number
- payment number
- JE display number

Rules:

- configurable
- duplicate-detectable
- not identity
- cannot replace internal references

### 7.3 Numbering Settings

Numbering is a formal company-level capability.

It should support:

- prefix
- next number
- padding
- preview
- enabled/suggestion behavior

Entity number and display number must never be confused.

## 8. Chart of Accounts

### 8.1 Positioning

The COA is structured accounting infrastructure, not a free-form list.

### 8.2 Root Account Types

Root types are fixed:

- asset
- liability
- equity
- revenue
- cost_of_sales
- expense

### 8.3 Detail Account Types

Detail types exist under root types to support:

- recommendations
- reporting semantics
- AI suggestions
- default system behavior

Detail types may not break root-type accounting meaning.

### 8.4 Code Rules

Account code must follow structured rules.

Default directional mapping:

- `1xxxx` -> asset
- `2xxxx` -> liability
- `3xxxx` -> equity
- `4xxxx` -> revenue
- `5xxxx` -> cost_of_sales
- `6xxxx` -> expense

Company-level code length rules must be enforced consistently.

### 8.5 System-Reserved Accounts and Codes

Some account-code ranges and some accounts are reserved for system use.

This is required for:

- system control accounts
- foreign-currency AR/AP control accounts
- future FX gain/loss / rounding / revaluation accounts
- other governed accounting infrastructure

Rules:

- users must not create accounts in reserved code ranges
- users must not repurpose system-reserved accounts
- system identity must not rely on code string alone

System-owned accounts should be identified by durable backend fields such as:

- `is_system`
- `system_key`
- `system_role`
- `currency_code` where applicable
- `allow_manual_posting`

### 8.6 Delete and Status Rules

Historical accounting accounts should not be hard-deleted.

- delete with history is forbidden
- inactive with history is allowed

System-owned control accounts should not be user-deletable or user-inactivatable.

### 8.7 COA Template

The system must support a system-default COA template.

New companies may be provisioned from that template.

System default records should be clearly marked, for example:

- `is_system_default = true`

## 9. Tax Engine

### 9.1 Core Principle

**Tax = line-level calculation -> account-level aggregation**

Tax truth starts at the line level and is then aggregated.

### 9.2 Sales Side

For sales:

- revenue posts to revenue
- tax posts to tax payable

### 9.3 Purchase Side

For purchases:

- recoverable tax -> receivable / recoverable tax account
- partially recoverable tax -> split behavior
- non-recoverable tax -> absorbed into expense or inventory as appropriate

### 9.4 Consistency Rules

Tax logic must be:

- backend-owned
- posting-engine aligned
- consistent across invoice, bill, JE, and reports
- never invented by UI

## 10. Journal Entry and FX Rules

### 10.1 Aggregation Principle

Formal JE should be aggregated by account / account-code semantics.

Citus should produce JE that is:

- readable
- reviewable
- traceable

### 10.2 Source Link Principle

JE must stay strongly linked to source:

- source_type
- source_id
- company consistency
- lifecycle synchronization

### 10.3 Prohibited

- JE without source
- source changed but JE unchanged
- hard deletion of posted truth
- accounting truth detached from business truth

### 10.4 Multi-Currency Journal Entry Rules

Journal Entry must support a single transaction currency per JE.

Rules:

- every JE must persist the actual `transaction_currency_code`
- base-currency JE must still persist explicit base ISO code
- JE header must persist a snapshot of:
  - `exchange_rate`
  - `exchange_rate_date`
  - `exchange_rate_source`
- JE lines must persist both:
  - transaction-currency amounts (`tx_debit`, `tx_credit`)
  - base-currency amounts (`debit`, `credit`)
- base debit/credit remain ledger truth
- tx amounts are the source amounts used to derive base truth

### 10.5 FX Source Semantics

Exchange-rate storage semantics and JE snapshot semantics must be normalized and separated.

Recommended row-origin semantics for stored exchange-rate rows:

- `manual`
- `provider_fetched`
- `legacy_unknown` when old provenance cannot be reconstructed honestly

Recommended JE snapshot semantics:

- `identity`
- `manual`
- `company_override`
- `system_stored`
- `provider_fetched`

UI labels such as "Latest" or "Manual" are display labels only and must not become drifting accounting truth.

### 10.6 Save-Time FX Rules

At JE save/post time:

- live provider calls are forbidden
- backend must validate an acceptable locally stored snapshot or a manual override
- backend must derive base amounts from tx amounts
- client-submitted base amounts must not be ledger truth

For non-manual foreign-currency saves:

- validation must be against the exact locally shown / accepted snapshot identity, or an explicitly allowed equivalent local snapshot state
- validation must not be based on "current latest rate" equality

### 10.7 Rounding Policy

Phase 1 policy:

- convert each line individually using banker's rounding to 2 decimals
- if resulting base totals do not balance exactly, block save

This is intentional.

Controlled auto-rounding may only be considered later, and only after a governed system-owned FX rounding account exists.

### 10.8 Historical Honesty

Historical FX truth must be shown honestly.

Rules:

- if historical FX semantics can be reconstructed with confidence, they may be displayed as resolved truth
- if they cannot be reconstructed, they must be shown as unavailable / unknown / legacy-unavailable
- the system must not cosmetically relabel uncertain historical FX truth as identity/base truth

### 10.9 Posted JE FX Read Path

Every posted JE must have an immutable read-only FX snapshot display path.

This path must show, where applicable:

- transaction currency
- exchange rate
- effective date
- source label
- transaction/base amounts
- any legacy-unavailable marker when historical truth cannot be reconstructed

List, detail, and reversal flows must not disagree about legacy FX truth.

## 11. Multi-Currency Architecture Beyond JE

### 11.1 Multi-Currency Positioning

Multi-currency is not a page feature.
It is a governed accounting capability.

It must be implemented through reusable modules and engines, not duplicated across forms.

### 11.2 Core Multi-Currency Modules

#### MultiCurrencyModule

Owns:

- company base currency
- multi-currency enablement
- allowed transaction currencies
- base vs foreign determination
- reusable FX form/read context

#### ExchangeRateModule

Owns:

- local-first exchange-rate lookup
- company override vs system precedence
- provider fetch/store lifecycle
- provider adapter(s)
- source semantics
- refresh behavior
- fallback behavior

#### FXConversionEngine

Owns:

- tx -> base conversion
- line-level conversion
- totals conversion
- rounding policy
- save-time balance enforcement

### 11.3 External Provider Rule

Frankfurter may be used as the default free rate provider.

Rules:

- provider is for lookup / refresh only
- provider is never accounting truth
- provider result becomes usable only after local persistence and JE snapshot persistence
- manual override must never mutate shared rate tables

## 12. AR/AP Multi-Currency Control Accounts

### 12.1 Default Single-Currency Behavior

When multi-currency is not in use:

- Sales / Invoices post to the company default `AR`
- Bills post to the company default `AP`

### 12.2 Foreign-Currency Control Accounts

When a foreign currency such as USD is enabled:

- Citus automatically creates the corresponding foreign-currency control accounts, for example:
  - `AR-USD`
  - `AP-USD`

These are system-owned control accounts.

### 12.3 Customer/Vendor Routing Rules

Customer and Vendor each have exactly one default transaction currency.

Rules:

- if a customer's default transaction currency is USD, new sales / invoices route to `AR-USD`
- if a vendor's default transaction currency is USD, new bills route to `AP-USD`
- base-currency customers/vendors continue to use default `AR` / `AP`

### 12.4 Edit Rules

- a customer/vendor may change default transaction currency only if they have no historical transaction records
- once historical records exist, default transaction currency becomes locked

### 12.5 System Ownership Rules

System-owned foreign-currency control accounts must be:

- auto-created by system workflow
- mapped by backend control-account mapping, not guessed from UI text
- protected from user deletion / repurposing
- not freely selectable for arbitrary manual posting unless explicitly allowed by governed system behavior

## 13. Business Modules and Product Scope

### 13.1 Current Core Product Areas

Current formal product direction includes:

- Dashboard
- Journal Entry
- Invoices
- Bills
- Customers
- Vendors
- Receive Payment
- Pay Bills
- Reconciliation
- Reports
- Settings

### 13.2 Task Module Position

The Task module currently serves as:

- a business-work tracking layer
- a billable-work / billable-expense support layer
- a bridge into invoice / AR visibility
- a support layer for customer workspace

Current status:

- Task main flow is basically complete
- future Task / Quote boundary must be reconsidered together
- long-term semantic overlap must not be allowed to drift

### 13.3 Invoice Direction

Invoice is one of the most important future product lines.

It must continue to improve in:

- editable templates
- sending capability
- product/service integration
- revenue-account linkage
- sales-tax integration
- AR lifecycle consistency
- future compatibility with foreign-currency AR routing

### 13.4 Payment Gateway Layer

Citus should evolve toward a provider-agnostic payment gateway layer.

Planned direction includes:

- Stripe
- PayPal
- other providers

Rules:

- connectors are modular
- accounting truth remains system-owned
- payment integration must not corrupt AR or posting consistency

### 13.5 Channel / Integration Strategy

External channel integration must remain platform-agnostic.

Target directions include:

- Shopify
- Temu
- WooCommerce / WordPress
- other sales channels

Rules:

- channel-specific connectors
- shared engine truth
- no pollution of core accounting engine by connector logic

## 14. Reconciliation

### 14.1 Product Meaning

**Reconciliation = Accounting Control Layer**

It is not merely a checkbox workflow.

### 14.2 Recommended Status Flow

- `draft`
- `in_progress`
- `completed`
- `reopened`
- `cancelled`

### 14.3 Matching Capability

The system must support:

- one-to-one
- one-to-many
- many-to-one
- split

### 14.4 Completion Rule

Reconciliation may only complete when:

- `difference == 0`

### 14.5 UI Direction

Reconciliation UI should be:

- QuickBooks-like in clarity
- control-oriented
- summary-bar driven
- inflow / outflow separated

## 15. Void Reconciliation

Only the latest completed reconciliation may be voided.

Voiding is not deletion.

Required fields include:

- `is_voided`
- `voided_by`
- `voided_at`
- `void_reason`

Void means rollback of control state while preserving history.

## 16. Audit and Observability

### 16.1 Audit Is Two-Layered

Citus auditability must distinguish between:

#### 1) Platform Audit (ABP Audit Logging)

Used for:

- request / response traces
- executed actions and application-service calls
- entity change visibility where supported
- exception visibility
- request duration and operational diagnostics

#### 2) Domain Audit (Citus Business Event Trail)

Used for:

- match / unmatch
- suggestion accept / reject
- reconciliation finish
- reconciliation void
- auto-match run
- posting events
- status transitions
- sensitive settings changes
- sysadmin actions
- FX snapshot selection / override where appropriate
- legacy reversal block decisions where applicable

ABP audit logging does not replace the business event trail.
The business event trail does not replace platform request audit.

### 16.2 Observability

The platform should progressively support:

- runtime error logs
- maintenance-state visibility
- system health visibility
- future CPU / storage / attachment observability
- cache source / invalidation visibility
- provider / FX lookup visibility
- job queue / retry visibility
- report-generation latency visibility


## 17. Notifications and Communication Infrastructure

### 17.1 Positioning

Notifications are formal infrastructure, not a small utility.

They support:

- verification codes
- password/email changes
- invoice sending
- system notifications
- future SMS capabilities

### 17.2 Required State

At minimum, the system should track:

- config presence
- test_status
- last_tested_at
- verification_ready

### 17.3 Rules

- SMTP not verified -> verification sending is blocked
- config changed -> previous readiness becomes invalid
- sensitive flows depend on real notification readiness

## 18. User Security

### 18.1 Required Verification

The following actions must require verification:

- email change
- password change

### 18.2 Verification Code Rules

Verification codes must be:

- 6 characters
- case-insensitive
- single-use
- time-limited
- validated on the backend

### 18.3 Security Settings Direction

Settings should reserve room for future rules such as:

- unusual IP login alert
- more security policies
- notification readiness dependency

## 19. Settings Architecture

### 19.1 Principle

Settings is a structured control surface, not a dumping ground.

### 19.2 ABP Setting Hierarchy vs Citus Domain Settings

The system should distinguish four configuration layers:

#### 1) Host / Global Settings (ABP Global)

Used for platform-wide behavior, such as:

- maintenance mode
- platform notification provider defaults
- global audit retention
- global AI provider defaults
- system SMTP defaults

#### 2) Tenant / Workspace Settings (ABP Tenant Settings)

Used for workspace-level behavior, such as:

- enabled integrations for a customer workspace
- tenant notification branding
- tenant-level feature defaults
- workspace-level security policies

#### 3) User Preferences (ABP User Settings)

Used for user-specific behavior, such as:

- theme
- locale
- table density
- personal dashboard preferences

#### 4) Company Accounting Settings (Citus Domain Tables)

Used for accounting truth and company-owned business control, such as:

- base currency
- numbering rules
- tax setup
- document templates
- posting defaults
- AR/AP account mappings
- multi-currency control behavior

**Important rule:** company accounting settings must not be hidden inside generic ABP setting storage if they are part of accounting truth or posting behavior.

### 19.3 Company Settings Direction

Settings > Company should progressively organize into clear domains such as:

- Profile
- Templates
- Sales Tax
- Numbering
- Notifications
- Security
- Currencies / Multi-Currency controls

These are company-level controlled areas.

### 19.4 User Menu

User menu should provide:

- Profile
- Log out

Profile changes involving email/password must go through verification.
### 19.5 Settings Boundary Clarification

Settings is a structured entry surface, not a root dumping-ground module.

Rules:

- Settings may aggregate pages from `Company`, `CompanyAccess`, user profile, and platform-governed capabilities
- company business settings must remain in `Company`
- company membership and company-scoped permission settings must remain in `CompanyAccess`
- platform identity and global system control settings must remain in platform or `SysAdmin`
- creating a catch-all root module named `Settings` is forbidden

## 20. UI / UX Design Principles

### 20.1 Overall Style

Citus must feel:

- clean
- stable
- business-first
- professional
- restrained

No flashy, noisy, or game-like UI direction.

### 20.2 Core UX Rules

- left sidebar is the main navigation anchor
- Dashboard is an operational overview, not heavy BI
- Reports is the standard reporting home
- users must always know current company context
- tables and forms must support long-duration work
- multi-currency surfaces must make transaction currency vs base currency clear without turning forms into clutter

### 20.3 Long-Use Comfort

The design system should progressively support:

- low glare
- stable hierarchy
- report readability
- table readability
- eye-friendly dark mode

Dark mode should not be simple inversion.
It should be a professional low-glare theme suitable for accounting workflows.

## 21. Sidebar and Navigation

The sidebar must remain business-driven.

### 21.1 Official Structure

#### Core

- Dashboard
- Journal Entry
- Invoices
- Bills

#### Sales & Get Paid

- Customers
- Receive Payment

#### Expense & Bills

- Vendors
- Pay Bills

#### Accounting

- Chart of Accounts
- Reconciliation
- Reports

#### Settings

Settings remains a distinct entry point, with structured internal subsections.

### 21.2 Explicitly Forbidden

- reintroducing top-level Contacts is forbidden
- reintroducing top-level Banking is forbidden
- moving Reports elsewhere is forbidden
- breaking business meaning in navigation is forbidden

## 22. SmartPicker and Acceleration Infrastructure

### 22.1 SmartPicker Positioning

SmartPicker is the legal-candidate entry surface for controlled selection fields.

It must remain responsible for:

- entity/provider resolution
- company scope enforcement
- context filtering
- active/type guard
- Search / GetByID legality semantics

It must not become the home of unrelated AI or persistence truth.

### 22.2 SmartPicker Acceleration

SmartPicker Acceleration is a separate enhancement layer.

It may own:

- recent retrieval
- hot-candidate retrieval
- short TTL query cache
- usage signal collection
- ranking
- picker metrics

Rules:

- ranking only within backend-supplied legal candidates
- cache only accelerates
- backend legality remains authoritative
- write-side invalidation is required after relevant master-data changes

### 22.3 Shared Cache Infrastructure

Shared cache infrastructure should support:

- namespacing
- versioning or equivalent invalidation primitives
- company-safe invalidation
- acceleration semantics for picker and reports

Global flush should be avoided as a default company-scoped invalidation strategy.

## 23. Reports and Report Acceleration

### 23.1 Reporting Is a Product Output

Reports are not temporary pages.

They must have:

- consistent logic
- alignment with engine truth
- alignment with business status
- semantic consistency across HTML / print / CSV / export

### 23.2 AR Reporting Direction

A/R Aging has entered the formal product-grade path and should continue improving in:

- summary/detail consistency
- export consistency
- print readability
- customer finance visibility support

### 23.3 General Rule

Report truth must be generated in backend services.
Templates may render but must not invent accounting meaning.

### 23.4 Report Acceleration

Report acceleration is allowed as a separate layer.

It may own:

- result cache
- aggregate cache
- export cache
- drill-down cache
- invalidation hooks
- freshness/source semantics
- warmup / prediction scaffolding

Rules:

- report acceleration must not replace report truth
- write-side invalidation is required on all relevant mutation paths
- cached/source/freshness semantics must be visible on supported report surfaces

### 23.5 Report Type / Accounting Basis Selection
Citus 必须支持多种报表会计基础（Report Type），以满足不同用户、税务申报和内部管理的需求。
Report Type 下拉选项（必须实现）：

- Accrual (Paid & Unpaid)（默认推荐）：采用权责发生制（Accrual Basis）。收入在赚取时确认，费用在发生时确认，无论是否实际收付。这应该是大多数正式财务报表（Profit & Loss、Balance Sheet、Aging Reports 等）的默认选项，提供最完整的财务状况视图。
- Cash Basis (Paid)：采用收付实现制（Cash Basis）。仅显示已实际收到或支付的金额。适合现金流管理、税务申报（部分小型企业或特定税种）。
- Cash Only：更严格的现金基础，仅基于现金账户变动（可能排除部分银行调节项）。适合极简现金流视图。

#### 实现规则（必须遵守）：

Report Type 是报表级参数，而非公司全局默认会计方法（公司可有默认偏好，但用户生成报表时可切换）。
所有报表（尤其是 AR Aging、AP Aging、Profit & Loss、Balance Sheet 等）必须支持这三种 Report Type。
Backend Authority：报表的计算逻辑必须由后端引擎决定（使用 Dapper 或专用 Report Service），前端只负责传递选择参数和展示结果。不能让前端自行计算差异。
- 一致性：同一 Report Type 下，不同报表（例如 Invoice 列表 vs P&L）必须使用相同的确认规则。
- 公司隔离：Report Type 选择必须在当前 active company 上下文中生效。
- 审计与历史诚实性：生成报表时应记录使用的 Report Type、生成时间和参数快照（便于以后审计）。
- 默认值：新公司默认使用 Accrual (Paid & Unpaid)，可在 Company Settings 中配置默认 Report Type。
- UI 位置：这个下拉框应出现在 Reports 主页、具体报表参数面板中（例如 AR Aging Report、Profit & Loss 等页面顶部），并带有帮助提示（?）解释每种类型的含义。

与现有原则的对齐：

- 符合 “Engine Truth > UI Presentation” —— 报表真相由后端 Posting Engine 和查询逻辑决定。
- 符合 “Historical Honesty” —— 如果数据来自不同期间，应清晰显示使用的会计基础。
- 与 ABP 集成：可将 Report Type 作为查询参数传入 Application Service，或使用 ABP 的 Setting Management 保存公司默认值。

#### 可选扩展（未来可考虑）：

- 支持用户保存常用报表模板（含 Report Type 设置）。
- 在 Dashboard 或关键报表中显示当前使用的 Report Type。
- 提供 “Compare Accrual vs Cash” 并排视图（高级功能）。

## 24. AI Layer

### 24.1 Definition

**AI = advisor / external accountant style assistant, not executor**

AI should help:

- supervise bookkeeping
- explain business
- interpret reports
- identify anomalies
- support better decisions

### 24.2 Strictly Forbidden

- AI changing books
- AI auto-posting
- AI auto-completing reconciliation
- AI bypassing validation
- AI becoming accounting truth

### 24.3 Currently Allowed AI Capabilities

- suggestions
- rankings
- explanations
- anomaly hints
- report interpretation
- tax reasonableness hints
- account recommendations
- writing assistance for controlled text fields

### 24.4 AI Assist Platform

AI access should be centralized through an AI Assist Platform.

This layer may own:

- provider abstraction
- prompt registry
- safety rules
- audit logging
- fallback behavior
- latency / timeout / retry governance

### 24.5 Long-Term AI Vision

The long-term AI direction is closer to an **AI CFO / external accountant layer** than to OCR automation.

It should help small business owners understand their business more deeply.

## 25. AI for Reconciliation

### 25.1 Suggested Structure

**Rules -> Scoring -> AI Enhancement**

### 25.2 Suggestion Entities

Formal suggestion records should exist as dedicated entities, such as:

- `reconciliation_match_suggestions`
- `suggestion_lines`

### 25.3 User Control

- Accept -> perform match
- Reject -> no accounting truth change

Every suggestion must be explainable.

### 25.4 Reconciliation Memory

The system may learn historical behavior to improve suggestion quality, but must remain:

- explainable
- auditable
- non-black-box
- subordinate to user control

## 26. Intercompany Strategy

### 26.1 Current Stage

Currently forbidden:

- intercompany transactions
- cross-company posting
- due to / due from automation
- group consolidation accounting

### 26.2 Future Unlock Conditions

Intercompany may only be considered after:

- Posting Engine is stable
- Reconciliation is mature
- Audit is complete
- Company isolation is robust
- report/control consistency is stable

### 26.3 Possible Future Direction

Later possibilities may include:

- intercompany JE links
- due to / due from pairing
- mismatch alerts
- group reporting
- elimination entries
- consolidation assist

This is strictly later-stage work.

## 27. Data Principles

### 27.1 Must Always Hold

- company_id isolation
- entity_number immutability
- backend authority
- JE traceability
- source-linked accounting truth
- auditability
- explicit lifecycle
- FX snapshot honesty
- system-owned account governance

### 27.2 Never Allowed

- deleting historical truth
- AI changing books
- bypassing validation
- JE detached from business truth
- cross-company contamination
- frontend state replacing backend truth
- provider data being treated as accounting truth
- cosmetically hiding historical uncertainty as false certainty

## 28. Implementation Discipline

### 28.1 Required Development Checklist

Before implementing any feature, verify:

1. does it respect company isolation
2. does it preserve engine truth
3. does it avoid bypassing posting rules
4. does it preserve auditability
5. does it prevent UI from becoming source of truth
6. does it avoid polluting unrelated modules
7. does it preserve historical honesty when data is uncertain
8. does it keep cache / AI / provider layers subordinate to backend truth
9. does it keep ABP governance concerns separate from accounting truth
10. does it preserve upgradeability of ABP modules

### 28.2 Default Build Order

Recommended implementation order:

**Data model -> Validation -> Engine/service -> Handler/API -> View model -> UI -> Tests**

### 28.3 Testing Requirements

Important capabilities should cover:

- happy path
- status transitions
- partial payment / partial state
- void / reverse exclusion
- cross-company rejection
- cross-tenant rejection where applicable
- export / HTML / CSV consistency
- nil / empty safety
- ordering stability
- provider contract correctness where applicable
- no-live-provider-at-save where applicable
- honest legacy read semantics where applicable

### 28.4 AI-First Development Rules

For AI-assisted development, the following rules are mandatory:

- AI may draft code, tests, SQL, UI, and refactors, but human review remains required for accounting correctness.
- Any feature touching company isolation, posting, tax, FX, reconciliation, permissions, numbering, or auditability must be implemented together with tests.
- Prompts must reference this document and the related executable specifications.
- AI should work slice-by-slice, not through large unbounded rewrites.
- Each task should preferably target one use case / one screen / one command-query pair.
- Generated code must preserve naming, folder conventions, and module boundaries.
- Generated migrations and SQL must be manually reviewed before execution on shared environments.
- AI may assist implementation, but engine rules and tests remain the final authority.

All new projects, namespaces, folders, and files must follow the approved naming grammar.

Project naming grammar:

`Citus.<Category>[.<RootName>][.<Layer>]`

Allowed categories:

- `Web`
- `SysAdmin`
- `DbMigrator`
- `SharedKernel`
- `Modules`
- `Engines`
- `Infrastructure`
- `Connectors`
- `Tests`

Approved root business modules:

- `Company`
- `CompanyAccess`
- `GL`
- `AR`
- `AP`
- `Reconciliation`
- `Reports`
- `Tasks`

Approved root engines:

- `Posting`
- `Tax`
- `FX`
- `Numbering`
- `ReconciliationControl`

Approved root infrastructure names:

- `AIAssist`
- `Notifications`
- `Caching`
- `SmartPicker`
- `Reporting`

Approved connector root names include patterns such as:

- `Payment.<Provider>`
- `Channel.<Provider>`
- `Rates.<Provider>`

Examples:

- `Citus.Modules.GL.Domain`
- `Citus.Modules.GL.Application`
- `Citus.Modules.CompanyAccess.Blazor`
- `Citus.Engines.Posting`
- `Citus.Infrastructure.AIAssist`
- `Citus.Connectors.Payment.Stripe`

Allowed layers for business modules:

- `Domain.Shared`
- `Domain`
- `Application.Contracts`
- `Application`
- `EntityFrameworkCore`
- `Blazor`

Forbidden root or utility names:

- `Users`
- `UserManagement`
- `Identity`
- `AccountingCore`
- `LedgerEngine`
- `Common`
- `Utils`
- `Helpers`
- `Misc`
- `Temp`
- `Manager`
- `Processor`

Rules:

- AI must not invent new root categories, root module names, or layer names without explicit approval
- file name must match the primary type name exactly
- one public type per file is the default rule
- new use cases must stay inside an approved root module boundary
- Journal Entry code must live under `GL`, not under a standalone `JournalEntry` root module
- company membership and company-scoped authorization code must live under `CompanyAccess`, not under a generic `Users` root module
- before generating code, AI must first list the exact target file paths it plans to create or modify
- if no approved target path exists, AI must stop and report: `No approved target path found.`

## 28.6 Module Naming and File Placement Rules

All new projects, folders, namespaces, and files must follow the approved naming grammar.

### Project name grammar
`Citus.<Category>[.<Module>][.<Layer>]`

Allowed categories:
- Web
- SysAdmin
- DbMigrator
- SharedKernel
- Modules
- Connectors
- Tests

Allowed module names:
- Company
- GL
- AR
- AP
- Tax
- FX
- Reconciliation
- Reports
- Tasks
- Notifications
- Identity

Allowed layers for Modules:
- Domain.Shared
- Domain
- Application.Contracts
- Application
- EntityFrameworkCore
- Blazor

Forbidden names:
- Common
- Helpers
- Utils
- Temp
- Misc
- Manager
- Processor
- ServiceImpl

Rules:
- AI must not invent new project categories.
- AI must not invent new module names without explicit approval.
- AI must not create files outside approved module boundaries.
- One public type per file.
- File name must match the main type name exactly.
- Vertical Slice use cases must be grouped by feature/use case folder.


## 29. Performance Strategy and Constraints

Performance must be designed, measured, and observed.
It must not be assumed merely because a certain stack or pattern is present.

### 29.1 Write Path Discipline

ERP write paths must prioritize correctness and transaction safety.

Rules:

- transactional writes use EF Core + Unit of Work semantics
- posting path must stay synchronous, atomic, and local to the transaction
- live provider calls are forbidden on save/post
- report generation, notifications, and heavy secondary work must be offloaded

### 29.2 Read Path Strategy

Default read strategy:

- start with EF Core projections and `AsNoTracking`
- use Dapper only for proven hot paths
- create report-specific read models only when needed
- prefer materialized views / summary tables only after semantics are stable

### 29.3 Cache Strategy

Cache is acceleration only.

Rules:

- cache keys must be namespaced
- when multi-tenancy is enabled, keys should include both `tenant_id` and `company_id`
- query/result versioning or equivalent invalidation primitives should be used
- write-side invalidation is mandatory
- cached data must never become accounting truth

### 29.4 Async Strategy

Preferred path:

- ABP Background Jobs / Workers for non-real-time work
- Outbox for reliable post-commit processing
- MassTransit / RabbitMQ only after real complexity justifies it

Typical async candidates:

- report generation
- invoice email sending
- notification dispatch
- FX rate refresh
- audit-log cleanup / archival
- AI summary generation

### 29.5 Database Strategy

Performance work should typically prioritize:

- proper indexes
- filtered / partial indexes where appropriate
- query-shape review
- projection trimming
- duplicate-post prevention indexes
- concurrency control for drafts and hot master data
- partitioning / materialized views only after real evidence

### 29.6 UI Read Strategy

Blazor pages must avoid over-fetching.

Rules:

- lists should paginate
- large tables should virtualize where appropriate
- detail pages should load focused view models, not giant aggregates
- posting preview and audit panels may use separate optimized read models

## 30. ABP Integration and Upgrade Governance

### 30.1 Adoption Boundary

ABP / ABP Commercial should primarily govern platform concerns:

- identity / account
- tenant / workspace management
- permission management
- feature management
- setting management
- audit logging
- background jobs
- blob storage
- text templates
- optional OpenIddict-based auth infrastructure

Citus-owned modules should govern business truth:

- GL
- AR
- AP
- FX
- tax
- reconciliation
- reports semantics
- company accounting settings

### 30.2 Tenant / Workspace Strategy

For future SaaS control:

- use **tenant / workspace** as the commercial and deployment boundary
- use **company** as the accounting/legal boundary inside that workspace
- use editions/features for packaging and rollout
- do not collapse tenant and company into the same concept unless the deployment model truly requires it

### 30.3 Extension Strategy

Preferred customization order:

1. configuration
2. module options
3. extension points / extra properties for ABP-owned objects
4. replaceable services / adapters
5. source inclusion or fork as the last resort

### 30.4 Source-of-Truth Rule

ABP may provide infrastructure, UI, and administration.
ABP may not redefine accounting truth.

Therefore:

- ABP settings may configure behavior, but may not rewrite posted history
- ABP permissions may gate access, but may not decide accounting legality alone
- ABP features may enable modules, but may not bypass posting/tax/FX engines
- ABP audit logs may record operations, but may not replace the accounting event trail

### 30.5 Upgradeability Rule

To preserve future control:

- keep business rules in Citus modules, not inside ABP package internals
- isolate overrides behind interfaces/adapters
- record all non-trivial ABP customizations
- prefer package updates over long-lived source forks wherever possible

## 31. Final Product Summary

Citus is:

- a strictly isolated multi-company system
- a strong-rule accounting engine
- a control-layer-driven finance platform
- a modular business application
- an ABP-governed platform shell for cross-cutting concerns
- an AI suggestion layer, not an AI execution layer
- a long-term extensible architecture

It must simultaneously preserve:

- accounting correctness
- company isolation
- tenant/workspace isolation where applicable
- business/accounting consistency
- auditability and control
- modular extensibility
- disciplined AI integration
- historical honesty
- governed multi-currency behavior
