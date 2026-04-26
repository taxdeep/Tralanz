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
- [AI_PRODUCT_ARCHITECTURE.md](./AI_PRODUCT_ARCHITECTURE.md)

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
- 框架：.NET 11 + C# 15（当前按 preview SDK / preview language features 基线推进）(ASP.NET Core)
- **总体架构**：**Modular Monolith + DDD（领域驱动设计） + CQRS（读写分离） + Vertical Slice Architecture（按功能切片）**

  - Citus 自研模块负责 accounting truth、posting/tax/FX/reconciliation 等核心业务真相。
  - 采用模块化单体结构（Modular Monolith），每个业务领域形成清晰的模块边界（例如 Accounting Module、Multi-Book Module、Reconciliation Module 等）
  - 核心规则密集域（Posting Engine、Tax Engine、FX Conversion Engine、Reconciliation Control）采用严格 DDD（Aggregate Root、Domain Service、Value Object、Domain Events）
  - 简单配置型模块（Settings、Profile、基本 CRUD）采用轻量 Vertical Slice，避免过度抽象
  - 模块之间通过明确接口和领域事件解耦，保持高内聚、低耦合，便于未来按需拆分为微服务。
  - 首期在单体内实现清晰物理和逻辑隔离，待核心引擎稳定后再逐步提升模块独立性。
- 数据库：PostgreSQL
- ORM：EF Core 为主，Dapper 为辅
  - 写操作（Commands）：统一使用 EF Core，负责事务、Change Tracking、迁移、领域验证。
  - 读操作（Queries）：默认使用 EF Core（AsNoTracking / Projection / Raw SQL）。
  - 只有在报表、大分页、复杂聚合、性能热点被确认后，再引入 Dapper。
- 前端：Blazor Web App + Ant Design Blazor（C# 全栈基线）
  - 保持 C# 全栈，最大化模型共享、类型安全和维护性，降低学习与长期维护成本。
  - 优先服务内部 ERP / Back Office 场景
  - 首期不把 React/Vue + TypeScript 作为主路线。若未来需要独立客户门户、公开站点、移动端配套或前端团队独立演进，再评估 React/Vue + TypeScript。

  - `AR` 正式采用 `Ant Design Blazor` 作为主组件体系，`Tailwind CSS` 作为辅助布局/间距/密度样式层，图标统一采用 `Tabler Icons`。
  - `AP` 在进入下一轮正式业务面交付时，应遵循与 `AR` 相同的 UI/UX 技术基线，避免 AR/AP 在 operator-facing surface 上出现两套长期并存的设计语言。
  - 该决定优先适用于 AR/AP 新交付面与重做面，不要求一次性重写 GL、Company Setup、Inventory、SysAdmin 或其他既有页面。
- 身份认证 / 账户：借鉴 ABP 的权限与多租户理念
  - 实现细粒度权限系统（支持 AR、AP、Reports、Approval 等领域权限）。
  - 严格的多公司隔离（Company Isolation）：每个会话必须有明确的 active_company_id，所有数据操作必须强制过滤。
  - 使用基于策略的授权（Policy-based Authorization），便于未来扩展复杂业务规则权限
  - 若未来出现独立 AuthServer、第三方 API 客户端、SSO、单点登出等需求，再引入 OpenIddict 体系。
- 平台治理基础设施：借鉴 ABP 核心理念，自行实现或轻量引入
  - 审计日志：所有关键操作（Posting、Reconciliation、FX Override、状态变更）必须记录完整审计轨迹（Actor、Before/After、CompanyId、Reason）。
  - 设置管理：公司级和系统级设置统一管理（Numbering、Currencies、Multi-Book 配置、Report Default 等）。
  - 后台任务：使用 Background Jobs + Outbox 模式处理非实时任务（报表生成、通知、FX rate refresh、审计同步）。
  - 多租户隔离：在基础设施层强制实现公司级数据隔离（可参考 ABP 的 Tenant Filter 思想，使用 EF Core Global Query Filter 或自定义过滤中间件）。
  - 缓存管理：分层缓存（HybridCache + Redis），所有缓存 Key 必须携带 company_id，严格遵守 Cache = Acceleration ONLY 原则，写操作必须主动失效。
- API 网关：首期不强制引入
  - 单体阶段可不使用独立网关。
  - 当系统拆分为多个独立服务、需要统一入口、转发、限流或鉴权编排时，再引入 YARP。

- 异步与后台任务：BackgroundService + Outbox 模式优先
  - 用于报表生成、通知发送、审计日志同步、FX rate refresh 等非实时任务。
  - 首期不强制引入 MassTransit / RabbitMQ。
  - 只有在出现多服务可靠投递、复杂工作流或 Saga 需求后，再升级到 MassTransit。
- 监控 / 可观测性：`ILogger` + HealthChecks + OpenTelemetry
  - 首先保证错误日志、请求链路、健康检查可见。
  - 后续逐步引入 Prometheus / Grafana 等可视化工具。

### 1.5 Multi-Book Accounting 支持（single-book first, multi-book capable；NetSuite 风格，但术语更严格，IFRS / US GAAP / ASPE 友好）

Citus 支持 **Multi-Book Accounting**，但默认产品体验应是 **single-book first, multi-book capable**：

- 大多数用户日常只操作 **Primary Book**，不应被迫每天显式理解并维护多个平行账簿。
- **Multi-Book** 是受控扩展能力，用于标准差异、税务口径、管理口径、集团列报或监管需求。
- 用户界面可以默认只呈现一套主要会计标准；系统底层仍必须保留多账簿与多准则能力。

Citus 在多账簿场景下，必须严格区分四个层次：

- **Source Transaction（源交易）**
- **Book Measurement / Posting（账簿计量与过账）**
- **Period-End Remeasurement（期末重估）**
- **Presentation / Consolidation Translation（列报 / 合并折算）**

核心规则：

- 一笔源交易只保留一份业务真相；**Posting Engine** 基于该真相并行生成各 Book 的会计结果。
- 每个 Book 都有自己的 accounted amounts、period close、revaluation journal、book-specific adjustments 与完整审计轨迹。
- 支持 **Adjustment-Only Book** 作为受控能力，用于只记录相对 Primary Book 的差异调整。
- **Presentation Currency translation** 属于报表 / 合并层，不得回写或污染 transactional books。
- 对同一 reporting entity 而言，**Functional Currency 是经济事实**，不应被普通用户随意按 Book 改写；若某 Book 需要不同的 ledger/base currency，应明确命名为 **Book Base Currency**，而不是默认等同 Functional Currency。
- **Accounting Standard selection 是 company / book policy**，不是普通用户偏好，也不是仅在报表页切换的显示参数。
- 新建加拿大私人企业模板时，**Primary Book 默认应为 ASPE**；若实体需要或选择 IFRS / US GAAP / Tax / Management 口径，则通过受控方式增加或切换 Books。
- 只有 **owner** 或被授予 **Company Settings / Book Governance** 权限的用户，才能创建 / 修改 Books、Accounting Standard、Functional Currency binding、Revaluation Policy、Rate Type Policy 等治理性设置。
- 一旦存在 posted history，变更 **Accounting Standard、Functional Currency binding、Book-governing FX policy** 时，必须走 **effective-dated governed change flow**、新建 secondary / adjustment book，或受控 migration；不得原地重写历史已过账真相。

**Accounting Standard / Primary Book 变更治理（四档规则，必须遵守）**：

1. **无任何 posted history**：允许直接修改 Primary Book / Primary Accounting Standard。
2. **已有 posted history，但尚未关账 / 未出正式报表**：禁止原地覆盖，必须走 **migration wizard**，生成新 book 或 future-dated cutover。
3. **已有 closed periods / 已出报告 / 已报税**：禁止 in-place change，只能新建 secondary / adjustment book，或从新财年首日切换并保留审计轨迹。
4. **仅报表列报 / 展示参数**：允许自由切换（例如选择展示哪个 book / presentation currency），但这不改变任何 book truth。

**现实合规提示（加拿大）**：

- Publicly accountable enterprises 必须使用 IFRS。
- Private enterprises 可选择 IFRS 或 ASPE。
- 若发生 IFRS 首次采用，必须遵循 **IFRS 1**：生成 opening IFRS statement of financial position，并在 transition date 对 equity 作受控调整，保留 reconciliation 与系统变更文档。

因此，“标准切换”必须被定义为 **framework migration**，而非普通设置开关。

**支持的 Accounting Standard Profiles（由 owner / 治理级管理员配置）**：

- **ASPE** —— 以 Section 1651 为基准；外币交易按 temporal method 进入当期损益；foreign operations 按 integrated / self-sustaining 分类处理。
- **IFRS** —— 以 IAS 21 为基准；强调 functional currency 判断、monetary vs non-monetary 区分，以及 presentation currency translation / OCI。
- **US GAAP** —— 以 ASC 830 为基准；强调 remeasurement into functional currency、translation adjustments / CTA、以及多实体环境下的并行账簿支持。
- **Management / Tax Book**（可选）—— 用于内部管理报表、税务申报或监管口径。

**配置方式**：

- 在 **Company Settings > Multi-Book Configuration** 中，owner 或被授权的治理用户可以：
  - 创建 / 管理多个 Accounting Books（Primary / Secondary / Adjustment-Only / Tax / Management）。
  - 为每个 Book 选择 **Accounting Standard**（ASPE / IFRS / US GAAP 等）。
  - 配置 **Book Role**、**Book Base Currency**、**Presentation Currency（可选）**、**Rate Type Policy**、**Revaluation Profile**、**FX Rounding Policy**、**Account Mapping Strategy**、**Effective From** 等治理字段。
  - 为 standards book 绑定实体级 Functional Currency 判断结果，而不是把 Functional Currency 当成任意可改的 UI 选项。
  - 在存在历史已过账数据后，发起 **future-dated change**、**new-book rollout** 或 **governed migration**，而不是直接覆盖历史设置。
- 用户在生成报表或查看 JE 时，可以选择查看**特定 Book** 的数据（或并排对比多个 Books）。
- 所有 Books 共享同一个 source transaction，但**不共享最终的 accounted truth**；每本账簿都独立生成自己的 JE / revaluation / adjustment trail。

这确保 Citus 对加拿大私人企业（ASPE）、需要国际报告的企业（IFRS）和有美国业务的用户（US GAAP）都高度友好，同时严格遵守 “Engine Truth”、“Historical Honesty” 和 “Backend Authority” 原则。

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
- Settlement FX Engine
- Remeasurement Engine
- Presentation Translation Engine
- Costing Engine
- Numbering Engine
- Reconciliation Control Engine

> Book management、accounting standard selection、effective-dated accounting policy governance、以及 inventory costing policy 属于 **company-owned controlled capability**。它们可以调用引擎，但不应被降级成普通 UI 偏好设置。

#### Business Modules

- Company
- CompanyAccess
- GL
- AR
- AP
- Inventory
- PaymentGateway
- Reconciliation
- Reports
- Tasks

User-facing business surfaces such as Journal Entry、Chart of Accounts、Invoices、Bills、Customers、Vendors、Receive Payment、Pay Bills、Inventory、Quotes、Sales Orders、Purchase Orders 等，必须落在上述批准的 root module 边界内，而不是临时发明新的 root module。

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

Navigation may use business-friendly labels such as Dashboard, Journal Entry, Receive Payment, Pay Bills, Inventory, and Settings.

Code and project boundaries must use approved root names only.

Approved root business modules:

- `Company`
- `CompanyAccess`
- `GL`
- `AR`
- `AP`
- `Inventory`
- `PaymentGateway`
- `Reconciliation`
- `Reports`
- `Tasks`

Approved root engines:

- `Posting`
- `Tax`
- `FX`
- `Costing`
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
- Customers, Quotes, Sales Orders, Invoices, Receive Payment, Customer Receipts, Payment Applications, Credit Notes, Customer Returns, Customer Refunds, and AR control outputs belong to `AR`.
- Vendors, Purchase Orders, Bills, Pay Bills, Vendor Prepayments, Vendor Credits, Vendor Returns, Vendor Refunds, and AP control outputs belong to `AP`.
- Inventory items, receipts, issues, adjustments, cost layers, valuation, COGS source truth, and inventory returns belong to `Inventory`.
- Provider-agnostic payment request, hosted payment session, gateway transaction normalization, gateway refund/dispute handling, and payment-channel orchestration belong to `PaymentGateway`.
- Company-level controlled areas such as Profile, Templates, Sales Tax, Numbering, Notifications, Security, Currencies, Books, Accounting Standards, Revaluation Profiles, inventory costing policy, and governed accounting policy settings belong to `Company`.
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
- changes to books, accounting standards, functional-currency bindings, rate-type policies, revaluation policies, and other governed accounting settings must be restricted to owners or users explicitly granted company-level book governance permission
- user permissions should be configurable by domain

Minimum recommended permission domains:

- AR
- AP
- approve
- reports
- settings access
- company accounting settings / books
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
- MFA / second-factor challenge lifecycle also belongs to the platform layer; it is part of identity assurance, not company-scoped business authorization
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

**Document -> Validation -> Tax Calculation -> FX / Currency Resolution -> Inventory / Cost Resolution (where applicable) -> Posting Fragments -> Aggregation -> Journal Entry -> Ledger Entries**

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
- inventory control / COGS / GRNI / landed-cost-clearing accounts where governed
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

### 10.4 Currency Layers and Book Concepts

Citus 在多币种 / 多账簿场景下，必须同时区分以下货币语义：

- **Document / Transaction Currency**：源单据币种（例如 Invoice = USD）。
- **Line Currency**：仅在受控的 manual GL multi-currency mode 下允许出现的行币种。
- **Book Base Currency**：某个 Accounting Book 记账和平账所使用的币种。
- **Functional Currency**：报告主体所处主要经济环境中的货币；这是会计判断结果，不是普通 UI 偏好。
- **Presentation Currency**：报表展示或合并折算目标币种。

**禁止把 Functional Currency、Book Base Currency、Presentation Currency 视为完全同义。**

### 10.5 Posting Rules for Subledger Documents and Manual GL

Citus 的默认规则应为：

- **Subledger-generated documents**（Invoice、Bill、Payment、Credit Memo 等）必须只有**一个 document currency**。
- **Manual GL Journal Entry** 默认使用一个 header transaction currency；若启用受控高级模式，可允许 line-level currency，但必须满足 account / entity / open-item 规则。
- 无论 entered currency 如何，**每个 Book 的 accounted debit = accounted credit** 才能过账。
- Source document 的 currency 一旦保存并进入正式流程，不得随意修改；如需变更，应通过 copy / void / reissue 或受控 amendment 流程完成。

每本账簿至少要记录：

- entered debit / credit
- entered currency（header 或 line）
- accounted debit / credit（per book）
- book_id
- exchange_rate
- exchange_rate_date
- exchange_rate_type
- exchange_rate_source
- quote_basis / inverse basis
- posting_reason（normal / settlement / revaluation / translation / adjustment）

### 10.6 Realized vs Unrealized vs Translation Difference

必须严格区分三类差异：

1. **Realized FX Gain/Loss（已实现汇兑损益）**
   - 在结算 / apply / settlement 时产生。
   - 由结算日金额与原始或最新账面金额比较得出。
   - 每个 Book 独立计算并生成自己的 realized gain/loss posting。

2. **Unrealized Remeasurement Gain/Loss（未实现重估损益）**
   - 仅针对**货币性项目**在期末进行 remeasurement。
   - 默认由 **Remeasurement Engine** 以 open item / monetary balance 为单位生成 revaluation JE。
   - 一般进入 **P&L / earnings**；不得把“P&L 还是 OCI”做成普通自由切换开关。

3. **Translation Difference / CTA / OCI（折算差额）**
   - 发生在 functional currency -> presentation currency，或 foreign operation translation / consolidation 层。
   - 属于 **Presentation / Consolidation Translation Engine** 的职责，不属于 transactional revaluation。
   - 不得与 open-item remeasurement 混为一谈。

补充规则：

- **Remeasurement 必须在每个 relevant reporting date / close date 执行**，例如月结、季结、年结或其他受控 reporting cycle；它不是“只在年底做一次”的概念。
- **Settlement FX** 的确认与 remeasurement 周期无关；只要发生 apply / settlement，就必须在当时确认 realized difference。

### 10.7 IFRS / US GAAP / ASPE Friendly Policies

- **IFRS / IAS 21**：外币交易先折算到 functional currency；货币性项目期末按 closing rate 重估；历史成本计量的非货币性项目不按期末汇率重估；presentation currency translation 的差额进入 OCI，净投资等特殊项目另行处理。
- **US GAAP / ASC 830**：外币项目按 functional currency 进行 remeasurement，汇率变动通常进入 earnings；translation adjustments 进入 equity / CTA。
- **ASPE / Section 1651**：foreign currency transactions 使用 temporal method，相关汇兑差额进入当期净利润；foreign operations 根据 integrated / self-sustaining 分类处理，其中 self-sustaining foreign operations 的折算差额进入单独的 shareholders’ equity 组成部分。

因此，Citus 必须：

- 把 **transaction remeasurement** 和 **presentation translation** 做成两个独立引擎。
- 把 **OCI / CTA / shareholders’ equity translation reserve** 作为**特定情形**支持，而不是普通 revaluation 设置项。
- 把 **ASPE integrated / self-sustaining** 限定为 **foreign operation classification**，而不是普通单据级 FX 选项。

### 10.8 Rounding and Precision Policy

Phase 1 不应写死为“所有币种一律 2 decimals”。

应改为：

- 使用 **currency precision**（按 ISO / 系统配置的 minor unit）决定 entered rounding 与 book rounding。
- conversion 过程保留更高内部精度；正式 posting 时才按目标 currency precision rounding。
- 默认逐行转换后再汇总。
- 若 book accounted totals 因 rounding 不平：
  - **严格模式**：阻止保存；
  - **受控模式**：仅允许过到 system-owned FX rounding account，且必须按 company / book 配置启用并保留审计轨迹。

### 10.9 Historical Honesty and Immutable FX Snapshot

Every posted JE must have an immutable read-only FX snapshot display path.

该路径至少应显示：

- source document currency
- line currency（如适用）
- book base currency
- exchange rate
- exchange rate type
- effective date / timestamp
- source label（manual / imported / provider / policy-derived）
- transaction amount
- accounted amount
- settlement rate（如适用）
- revaluation rate（如适用）
- translation rate（如适用）
- legacy-unavailable / reconstructed 标识（如适用）

List、detail、reversal、audit trail、report drill-down 不得对历史 FX 语义给出互相冲突的结果。

### 10.10 Accounting Standard per Book and Book Role

每本账簿必须独立记录：

- `book_id`
- `book_role`（primary / secondary / adjustment_only / tax / management）
- `accounting_standard`
- `book_base_currency`
- `functional_currency_binding_mode`
- `presentation_currency`
- `rate_type_policy`
- `revaluation_policy`
- `rounding_policy`
- `account_mapping_profile`
- `effective_from`
- `effective_to`（nullable）
- `change_governance_mode`

**Book-Specific Adjustment Journal Entries** 可以存在，但只应用于：
- standard-difference adjustments
- tax adjustments
- adjustment-only books
- closing adjustments

不应用来掩盖 source transaction 或 base posting 的缺陷。

一旦账簿存在 posted history，**Accounting Standard**、**Functional Currency binding**、以及影响记账真相的治理性 FX policy 变更都必须以前瞻性、effective-dated 的方式处理；系统不得静默重写既有已过账分录的语义标签。

## 11. Multi-Currency Architecture Beyond JE

### 11.1 Multi-Currency Positioning

Multi-currency is not a page feature.
It is a governed accounting capability.

It must be implemented through reusable modules and engines, not duplicated across forms.

### 11.2 Core Multi-Currency Modules

#### MultiCurrencyModule

Owns:

- company enabled currencies
- currency precision / minor-unit policy
- default document currency policy
- base vs foreign determination
- reusable FX form / read context

#### BookManagementModule

Owns:

- accounting book lifecycle
- book role（primary / secondary / adjustment_only / tax / management）
- accounting standard profile and defaulting strategy
- book base currency
- book account-mapping profile
- parallel posting enablement
- adjustment-only behavior
- per-book close and activation rules
- owner / governed-user mutation rules
- effective-dated accounting policy changes
- standard-migration / new-book rollout workflow after posted history exists

#### ExchangeRateModule

Owns:

- local-first exchange-rate lookup
- exchange rate types（spot / closing / average / historical / custom）
- quote basis / inverse basis
- company override vs system precedence
- provider import lifecycle
- source semantics
- effective date / timestamp policy
- fallback behavior

#### FXConversionEngine

Owns:

- transaction currency -> book base currency conversion
- line-level and document-level conversion
- accounted amount generation per book
- conversion precision handling
- save-time balance enforcement

#### SettlementFXEngine

Owns:

- apply / settlement FX calculation
- realized gain/loss calculation
- partial settlement allocation logic
- settlement-specific audit trail

#### RemeasurementEngine

Owns:

- period-end remeasurement of monetary items
- open-item / balance revaluation selection
- unrealized gain/loss JE generation
- reversal / next-period carry logic

#### PresentationTranslationEngine

Owns:

- functional currency -> presentation currency translation
- CTA / OCI / translation reserve handling
- consolidation-friendly translation outputs
- translation-only reporting artifacts

### 11.3 External Provider Rule

Exchange-rate providers are lookup sources, not accounting truth.

Rules:

- provider data is for **import / refresh / suggestion** only
- provider data becomes usable only after **local persistence**
- formal posting must use an **immutable FX snapshot**
- manual override must never mutate historical posted snapshots
- system must be **provider-agnostic**; Frankfurter may be a prototype / default provider, but production architecture must support alternative providers and custom internal rates
- source document posting, settlement, remeasurement, and translation may use **different rate types**, and the selected rate type must be stored explicitly

## 12. AR/AP Multi-Currency Control Accounts

### 12.1 Default Single-Currency Behavior

When multi-currency is not in use:

- Sales / Invoices post to the company default `AR`
- Bills post to the company default `AP`

### 12.2 Supported Subledger Control Models

Citus 应支持两种受控模型，而不是只绑定一种做法：

1. **Per-Currency Control Model（QuickBooks-like）**
   - 例如 `AR-USD`、`AP-EUR`
   - 简单直观，适合 SMB
   - account currency 固定，便于限制误用

2. **Shared-Control + Open-Item Currency Model（更接近 NetSuite / stronger ERP design）**
   - 使用共享 AR / AP control account
   - open items 自身携带 transaction currency、accounted amount、revaluation history
   - 更灵活，支持同一 customer / vendor 未来使用多种交易币种

系统应通过配置决定 company / book / document-type 使用哪种模型，而不是在代码里写死。

### 12.3 Customer / Vendor Currency Policy

Customer / Vendor 不应只有“exactly one default transaction currency”这一种表达。

更合理的模型是：

- `default_currency`
- `allowed_currencies[]`
- `currency_policy`（single / multi_allowed）
- `payment_currency_policy`（must_match_open_item / controlled_cross_currency_later）

规则：

- 新建 source document 时默认带出 `default_currency`
- document currency 必须属于 `allowed_currencies`
- document 保存后，currency 不得随意改动
- 可调整 `default_currency` 影响未来新单据，但不得改写历史交易
- 移除某个 allowed currency 前，必须检查是否仍有 open items / active drafts / pending settlements

### 12.4 Routing and Edit Rules

Posting routing 必须由后端 mapping 决定，而不是根据 UI 名称猜测：

- `company_id + book_id + document_type + currency_code -> control_account_id`
- 或 `company_id + book_id + document_type -> shared_control_account_id`

对于 edit rules：

- 锁定的应是**历史交易币种与已过账事实**，不是把 master data 永久锁死在单一币种
- 若使用 single-currency policy，可像 QuickBooks 一样严格限制
- 若使用 multi_allowed policy，应更接近 NetSuite：允许 entity 拥有多个可用币种，但每张 document 仍只有一个币种且保存后不可改

### 12.5 System Ownership Rules

System-owned foreign-currency control accounts must be:

- auto-created by governed system workflow
- mapped by backend control-account mapping, not guessed from UI text
- protected from user deletion / repurposing
- guarded by `system_role`, `currency_code`, `allow_manual_posting`, `book_id` where applicable
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
- Inventory
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

Citus should evolve toward a provider-agnostic payment gateway module plus provider-specific payment connectors.

Planned direction includes:

- Stripe
- PayPal
- other providers

Rules:

- provider-specific connectors are modular
- the provider-agnostic `PaymentGateway` module owns normalized gateway events and payment-channel orchestration
- accounting truth remains system-owned
- payment integration must not corrupt AR, AP, inventory, or posting consistency

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

### 13.6 AR Module Boundary

`AR` is the official module for customer-side receivables truth, invoice-linked open-item truth, customer receipt truth, payment application, customer credit outcomes, and AR control outputs.

AR owns the formal business and accounting-control lifecycle of customer receivables.

AR officially includes:

- `Customer`
- `Quote`
- `SalesOrder`
- `CustomerDeposit`
- `Invoice`
- `CustomerReceipt`
- `PaymentApplication`
- `CreditNote`
- `Return`
- `Refund`
- `CustomerStatement`
- `ARAging`
- `Collection`
- `WriteOff`

AR is responsible for:

- customer-side revenue-flow control
- receivable creation and balance truth
- receipt truth
- payment application / unapplication
- customer credit and deposit outcomes
- return / credit / refund business linkage
- customer statement and aging outputs
- collection and write-off control

AR must remain:

- source-linked
- company-scoped
- backend-authoritative
- posting-engine-aligned
- historically honest

AR does not own:

- payment provider transaction truth
- gateway webhook lifecycle
- payout-platform truth
- inventory truth
- warehouse / shipment core truth
- posting-engine truth
- tax-engine truth

AR may consume upstream or downstream facts from those modules, but it may not absorb or replace their authority.

### 13.7 AR Core Lifecycle

The recommended AR lifecycle is:

`Customer -> Quote -> SalesOrder -> CustomerDeposit(optional) -> Invoice -> CustomerReceipt -> PaymentApplication -> Return / CreditNote / Refund -> Statement / Collection / WriteOff`

Rules:

- `Quote` is a commercial quotation document and does not create formal accounting entries by default.
- `SalesOrder` is a commercial commitment document and does not create formal accounting entries by default.
- `CustomerDeposit` is optional, but must be independently modeled and must not be merged into generic receive-payment behavior.
- `Invoice` is the primary AR accounting source document.
- `CustomerReceipt` is the formal AR-side acknowledgment that value has been received from the customer.
- `PaymentApplication` is a first-class AR capability and must not be hidden as an undocumented side effect of a payment screen.
- `Return`, `CreditNote`, and `Refund` must remain separate objects with separate business and accounting semantics.
- `Statement`, `Aging`, `Collection`, and `WriteOff` are formal AR control outputs, not temporary or cosmetic reporting pages.

### 13.8 AR Accounting Boundary

The following objects do not normally create formal accounting entries by themselves:

- `Quote`
- `SalesOrder`
- `ReturnRequest`
- `PackingSlip` / `FulfillmentDocument` by itself, unless another governed module adds accounting consequences

The following objects may create or drive formal accounting outcomes through the Posting Engine:

- `CustomerDeposit`
- `Invoice`
- `CustomerReceipt`
- `CreditNote`
- `Refund`
- `WriteOff`

Rules:

- AR business objects own source truth and open-item truth.
- Formal accounting entries must still go through the Posting Engine.
- AR may not bypass the Posting Engine.
- AR status and open-item truth must remain synchronized with formal accounting outcomes where applicable.
- Historical AR truth must never be cosmetically rewritten to hide unapplied cash, unapplied credit, partial applications, or legacy uncertainty.

### 13.9 Customer Deposit Rule

`CustomerDeposit` must be treated as an independent AR-related object.

Rules:

- deposit is not revenue by default
- deposit may be unapplied, partially applied, fully applied, refunded, or voided
- deposit may later be applied to invoice settlement
- deposit history must remain auditable and source-linked
- deposit must not be collapsed into ordinary customer receipt logic without explicit deposit semantics

### 13.10 Customer Receipt and Payment Application Rule

`CustomerReceipt` and `PaymentApplication` are separate but strongly related AR capabilities.

Rules:

- receipt truth belongs to AR
- receipt is not the same thing as gateway transaction status
- receipt may come from multiple payment methods
- receipt may be fully applied, partially applied, unapplied, reversed, or voided
- application and unapplication must remain traceable
- unapplied cash and unapplied credit must be preserved honestly
- application results must update invoice balance truth and AR aging truth
- payment application legality is backend-owned

### 13.11 Credit Note / Return / Refund Separation Rule

The following must remain distinct:

- `Return` = business return fact
- `CreditNote` = AR reduction / customer credit outcome
- `Refund` = customer fund-outflow outcome

Rules:

- return does not automatically equal credit note
- credit note does not automatically equal refund
- refund may come from overpayment, deposit return, customer credit withdrawal, or paid-invoice reversal
- all related objects must preserve explicit linkage where applicable
- AI and implementation code must not collapse these three concepts into one generic adjustment object

### 13.12 AR Control Outputs

AR must formally support governed control outputs, including:

- `CustomerStatement`
- `ARAging`
- collection / reminder flow
- write-off / bad debt handling

Rules:

- these outputs are part of formal AR capability
- they must remain aligned with engine truth and open-item truth
- they must be company-scoped
- they must remain consistent across HTML / print / CSV / export where applicable
- they may use report acceleration, but acceleration must not replace AR truth

### 13.13 AP Module Boundary

`AP` is the official module for vendor-side payables truth, vendor bill truth, vendor payment truth, payment application, vendor credit outcomes, and AP control outputs.

AP owns the formal business and accounting-control lifecycle of supplier liabilities and purchase-side settlement.

AP officially includes:

- `Vendor`
- `PurchaseOrder`
- `VendorPrepayment`
- `VendorReceiptLinkage`
- `Bill`
- `VendorPayment`
- `PaymentApplication`
- `VendorCredit`
- `VendorReturn`
- `VendorRefund`
- `APAging`
- `DueControl`
- `WriteOff`

AP is responsible for:

- vendor-side purchase-flow control
- payable creation and balance truth
- vendor payment truth
- payment application / unapplication
- vendor prepayment and credit outcomes
- return / credit / refund business linkage
- due control and aging outputs

AP does not own:

- inventory quantity truth
- cost-layer truth
- warehouse receipt truth
- posting-engine truth
- tax-engine truth
- payment provider truth

AP may consume upstream or downstream facts from those modules, but it may not absorb or replace their authority.

### 13.14 AP Core Lifecycle

The recommended AP lifecycle is:

`Vendor -> PurchaseOrder -> VendorPrepayment(optional) -> Receipt(optional) -> Bill -> VendorPayment / PaymentApplication -> VendorReturn / VendorCredit / VendorRefund -> APAging / DueControl / WriteOff`

Rules:

- `PurchaseOrder` is a commercial commitment document and does not create formal accounting entries by default.
- `VendorPrepayment` is optional, but must be independently modeled and must not be merged into generic pay-bills behavior.
- `Receipt` is an operational or inventory-linked fact and must not be automatically collapsed into bill truth.
- `Bill` is the primary AP accounting source document.
- `VendorPayment` is the formal AP-side acknowledgment that value has been paid to the vendor.
- `PaymentApplication` is a first-class AP capability and must not be hidden as an undocumented side effect of a pay-bills screen.
- `VendorReturn`, `VendorCredit`, and `VendorRefund` must remain separate objects with separate business and accounting semantics.
- `APAging`, `DueControl`, and `WriteOff` are formal AP control outputs.

### 13.15 AP Accounting Boundary

The following objects do not normally create formal accounting entries by themselves:

- `PurchaseOrder`
- `VendorReturnRequest`
- `Receipt` by itself when receiving-accounting mode is disabled

The following objects may create or drive formal accounting outcomes through the Posting Engine:

- `VendorPrepayment`
- `Bill`
- `VendorPayment`
- `VendorCredit`
- `VendorRefund`
- `WriteOff`
- `Receipt` when governed receiving-accounting mode is enabled

Rules:

- AP business objects own source truth and open-item truth.
- Formal accounting entries must still go through the Posting Engine.
- AP may not bypass the Posting Engine.
- Historical AP truth must never cosmetically hide unapplied prepayments, unapplied credits, overpayments, or legacy uncertainty.

### 13.16 Vendor Prepayment / Vendor Payment / Payment Application Rule

`VendorPrepayment`、`VendorPayment`、and `PaymentApplication` are separate but strongly related AP capabilities.

Rules:

- vendor prepayment is not expense by default
- vendor payment truth belongs to AP
- payment may be fully applied, partially applied, unapplied, reversed, or voided
- application and unapplication must remain traceable
- unapplied vendor payment and unapplied vendor credit must be preserved honestly
- payment application legality is backend-owned
- bill balance truth and AP aging truth must reflect governed application results

### 13.17 Vendor Credit / Return / Vendor Refund Separation Rule

The following must remain distinct:

- `VendorReturn` = business return-to-vendor fact
- `VendorCredit` = AP reduction / vendor credit outcome
- `VendorRefund` = fund inflow back from vendor

Rules:

- vendor return does not automatically equal vendor credit
- vendor credit does not automatically equal vendor refund
- vendor refund may come from overpayment, prepayment reversal, vendor credit withdrawal, or post-return settlement
- all related objects must preserve explicit linkage where applicable
- AI and implementation code must not collapse these three concepts into one generic adjustment object

### 13.18 AP Control Outputs

AP must formally support governed control outputs, including:

- `APAging`
- due control / payment proposal flow
- write-off / small-balance handling

Rules:

- these outputs are part of formal AP capability
- they must remain aligned with engine truth and open-item truth
- they must be company-scoped
- they must remain consistent across HTML / print / CSV / export where applicable

### 13.19 Inventory Module Boundary

`Inventory` is the official module for quantity truth, receipt truth, issue truth, adjustment truth, cost-layer truth, inventory valuation truth, and COGS source truth.

Inventory owns the formal business and accounting-control lifecycle of stock movements and stock-cost semantics.

Inventory officially includes:

- `InventoryItemProfile`
- `InventoryReceipt`
- `InventoryIssue`
- `InventoryAdjustment`
- `InventoryReturn`
- `InventoryCostLayer`
- `InventoryBalance`
- `InventoryValuationSnapshot`
- `InventoryCostEvent`
- `InventoryCostingPolicy`

Inventory is responsible for:

- quantity on hand
- quantity available
- quantity committed
- receipt / issue / adjustment truth
- cost-layer creation and consumption
- inventory valuation truth
- COGS source truth
- return-to-stock truth
- vendor-return cost-out truth

Inventory does not own:

- receivable truth
- payable truth
- payment truth
- tax-engine truth
- final journal-entry truth

Inventory may consume upstream sales / purchase / fulfillment facts, but it may not let AR or AP overwrite quantity truth or cost truth.

### 13.20 Inventory Core Lifecycle and Source Boundaries

The recommended inventory lifecycle is:

`ItemProfile -> Receipt -> CostLayer Creation -> Issue / Consumption -> Adjustment / Return -> Valuation Snapshot / COGS Source Output`

Rules:

- purchase order and sales order are commercial commitments, not inventory truth by themselves
- receipt creates or confirms inbound quantity truth
- issue / shipment completion creates outbound quantity truth
- return creates inbound or outbound reversal truth depending on direction
- inventory truth must stay explicit even when billing and shipping timing differ
- AR and AP may reference inventory events, but they do not own inventory quantity semantics

### 13.21 Inventory Accounting and COGS Boundary

Inventory must remain the source of quantity and cost truth.  
The Posting Engine remains the only official path for formal accounting entries.

Rules:

- `COGS` must be driven by governed inventory cost truth, not guessed from invoice lines
- `InventoryReceipt` may or may not create formal accounting entries depending on receiving-accounting mode
- when receiving-accounting mode is disabled, receipt is operational truth and bill drives the first formal accounting entry
- when receiving-accounting mode is enabled, receipt may drive Inventory / GRNI style posting through the Posting Engine
- customer return and vendor return may affect inventory, but AR or AP return objects must not directly rewrite inventory valuation

### 13.22 Costing Methods and FX Interaction Rules

Inventory costing is a governed company-owned policy and must be explicitly configured.

Phase rules:

- Phase 1 default costing method: `moving_average`
- Phase 2 optional governed costing method: `fifo`
- no implementation may silently assume FIFO or moving average without explicit company policy

Core rules:

- inventory cost layers and valuation must be stored in company base currency or book base currency
- source transaction currency and FX snapshot must still be preserved for traceability
- moving average recomputation must use accounted / base currency cost, not floating live provider rates
- FIFO layers must be consumed using the historical accounted cost stored in each layer
- inventory layers are not monetary items and must not be remeasured like AR / AP open items
- FX changes after receipt may affect AR / AP settlement and remeasurement, but they must not silently rewrite historical inventory-layer cost
- purchase-bill timing differences, late vendor bills, and future landed-cost or variance handling must remain explicit and auditable

### 13.22A Inventory Phase D Operational Guardrails

The first governed `Inventory` implementation must assume multi-warehouse from the start.

Phase D guardrails:

- inventory quantity truth must distinguish at least `on_hand`, `reserved`, and transfer-related in-transit states
- `available` quantity is a read model derived from governed quantity truth, not a separately editable field
- movement history must be append-only and auditable; warehouse/item balance rows are caches, not the only truth
- warehouse transfer must remain a first-class lifecycle with explicit ship and receive stages; it must not collapse into two invisible stock mutations
- transfer ship and transfer receive must remain separately controllable authority points; one user role may be allowed to release stock from a source warehouse without automatically gaining the authority to receive it into the target warehouse
- `AP Bill` and future `Sales Out` may feed inventory workflows, but they must not directly rewrite inventory quantity truth or cost-layer truth
- outbound inventory cost must be system-derived from governed inventory costing truth, not manually keyed into a sales-side issue flow
- write-off, adjustment, and transfer changes must pass through explicit company-scoped authority hooks
- inventory costing policy belongs to company governance and must not be downgraded into a normal warehouse-operator capability
- negative stock must default to strict disallow mode; if a company later enables negative stock, that must happen through an explicit governed company-level switch with auditability
- `Product / Service Setup` should remain as a valid setup surface, but its evolution depends on company type:
  - trading / inventory-led companies may evolve it toward a stricter inventory item master
  - professional-service companies may evolve it toward a lightweight service catalog and later task-management integration
  - service-led companies may keep inventory-facing item controls disabled by default without deleting the setup entry point from the overall product model

### 13.23 Payment Gateway Boundary

`PaymentGateway` is a separate payment-channel module.

It owns external provider payment-channel truth, including:

- `PaymentRequest`
- `HostedPaymentSession`
- `PaymentAttempt`
- `GatewayTransaction`
- `GatewayRefundEvent`
- `GatewayDisputeEvent`
- `GatewayPayoutMetadata`

PaymentGateway is not the AR or AP module.

PaymentGateway is responsible for:

- payment request / hosted payment session lifecycle
- provider transaction status
- authorization / capture / fail / cancel / partial payment states
- refund status from provider
- dispute / chargeback status from provider
- payout / fee / settlement metadata
- webhook ingestion
- provider idempotency and replay protection
- provider-specific status normalization

PaymentGateway does not own:

- invoice balance truth
- AR or AP aging truth
- customer or vendor credit truth
- receipt or payment application truth
- formal accounting-entry truth

Gateway status must not directly replace AR or AP accounting truth.

### 13.24 Payment Gateway <-> AR/AP Interaction Rules

The official rule is:

`PaymentGateway status != AR/AP status`

But:

`PaymentGateway event -> may trigger AR/AP action`

Rules:

- PaymentGateway may report normalized outcomes such as:
  - `payment_confirmed`
  - `payment_partially_confirmed`
  - `refund_confirmed`
  - `dispute_opened`
  - `dispute_resolved`
  - `chargeback_confirmed`
- AR then decides whether to:
  - create customer receipt
  - create partial receipt
  - keep unapplied cash
  - trigger customer refund flow
  - trigger dispute / exception flow
  - update invoice balance through governed application logic
- AP then decides whether to:
  - create vendor payment or refund acknowledgment where appropriate
  - update vendor balance through governed application logic
  - preserve overpayment / unapplied payment truth honestly
- gateway-origin events must remain linked, but must not directly overwrite AR/AP history
- provider refund or dispute events do not automatically rewrite customer receipt truth or vendor payment truth
- formal accounting outcomes still belong to governed AR/AP flow plus the Posting Engine

### 13.25 Formal Boundary Conclusion

The final boundary is:

- `AR` owns customer-side receivables truth
- `AP` owns vendor-side payables truth
- `Inventory` owns quantity, cost-layer, valuation, and COGS source truth
- `PaymentGateway` owns external provider payment-channel truth
- the `Posting Engine` owns formal accounting-entry truth

AR/AP/Inventory may influence one another through governed source links, but no module may directly replace another module’s truth.

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

### 18.4 Multi-Factor Authentication

Multi-factor authentication (MFA) is a platform identity control, not a business-domain permission system.

Rules:

- MFA belongs to Platform Identity / Account and SysAdmin authentication, not to `CompanyAccess`, `AP`, `AR`, `GL`, or other business root modules.
- MFA proves actor identity only. It does not grant company membership, active company, company-scoped permissions, posting authority, or approval legality.
- When MFA is required, successful primary-credential validation may produce a governed challenge state, but it must not issue a final `business_sessions` or `sysadmin_sessions` token until the second factor succeeds.
- Active company resolution and company-scoped authorization must happen only after authentication is fully completed, including MFA where required.
- MFA must remain subordinate to existing system truth:
  - maintenance mode may still block normal business sign-in
  - company inactive / read-only state may still block writes
  - `CompanyAccess` membership and permission checks still decide company-scoped legality
- Email or SMS based second factors may only be offered when the corresponding notification channel is configured, tested, and verification-ready.
- MFA enrollment, reset, recovery, backup-code issuance, device replacement, and disable actions must be backend-owned and auditable.
- SysAdmin authentication should be governed more strictly than normal business-user authentication; production-grade SysAdmin access should require MFA by policy.

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

- company functional-currency judgment / primary-book base currency
- numbering rules
- tax setup
- sales tax reporting / filed-sales-tax workflow
- document templates
- posting defaults
- AR/AP account mappings
- inventory control and costing policy
- receiving-accounting mode / GRNI policy where applicable
- multi-currency control behavior
- Multi-Book Configuration：账簿列表、每本账簿的 Accounting Standard（ASPE / IFRS / US GAAP）、Book Role、Book Base Currency、Functional Currency binding、Presentation Currency、Rate Type Policy、Revaluation Policy、Rounding Policy、Account Mapping Profile、ASPE foreign operation classification、default primary book、effective-dated change policy、governed migration policy 等。

**Important rules:**

- company accounting settings must not be hidden inside generic ABP setting storage if they are part of accounting truth or posting behavior.
- accounting standard selection、book policy、functional currency binding、revaluation policy、以及 migration governance 不是 user preference，也不是 report-only toggle。
- 一旦存在 posted history，这类治理性设置变更必须是 **effective-dated、auditable、company-owned** 的；原地重写历史 posted truth 是禁止的。

### 19.3 Company Settings Direction

Settings > Company should progressively organize into clear domains such as:

- Profile
- Templates
- Sales Tax
- Numbering
- Notifications
- Security
- Currencies / Multi-Currency controls
- Inventory / Costing / Receiving policy
- Books / Accounting Standards / Accounting Policy

These are company-level controlled areas.

For `Sales Tax`, the direction is not only tax-code maintenance. It must eventually cover:

- tax code governance
- sales tax report visibility
- governed `File Sales Tax` workflow

These future surfaces must consume backend-owned line-level tax truth and posted tax semantics rather than recomputing tax from UI-only state.

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
- Inventory
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
- 报表必须支持按不同 Accounting Book（及其中选择的 Accounting Standard）生成，并清晰显示当前使用的准则。

#### 实现规则（必须遵守）：

Report Type 是报表级参数，而非公司全局默认会计方法（公司可有默认偏好，但用户生成报表时可切换）。
所有报表（尤其是 AR Aging、AP Aging、Profit & Loss、Balance Sheet 等）必须支持这三种 Report Type。
Backend Authority：报表的计算逻辑必须由后端引擎决定（使用 Dapper 或专用 Report Service），前端只负责传递选择参数和展示结果。不能让前端自行计算差异。
- 一致性：同一 Report Type 下，不同报表（例如 Invoice 列表 vs P&L）必须使用相同的确认规则。
- Accounting Book / Accounting Standard 选择与 Report Type 是两个不同维度；切换报表基础或列报视图不得改写底层 book truth。
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
- `Inventory`
- `PaymentGateway`
- `Reconciliation`
- `Reports`
- `Tasks`

Approved root engines:

- `Posting`
- `Tax`
- `FX`
- `Costing`
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
- `Citus.Modules.Inventory.Domain`
- `Citus.Modules.PaymentGateway.Application`
- `Citus.Engines.Posting`
- `Citus.Engines.Costing`
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
- inventory quantity, cost-layer, valuation, and COGS source logic must live under `Inventory`, not under `AR` or `AP`
- provider-specific gateway logic must live under `PaymentGateway` and/or `Connectors.Payment.<Provider>`, not inside AR/AP truth objects
- company membership and company-scoped authorization code must live under `CompanyAccess`, not under a generic `Users` root module
- before generating code, AI must first list the exact target file paths it plans to create or modify
- if no approved target path exists, AI must stop and report: `No approved target path found.`

## 28.5 Module Naming and File Placement Rules

All new projects, folders, namespaces, and files must follow the approved naming grammar and must remain consistent with Sections 3.5 and 28.4.

### Project name grammar
`Citus.<Category>[.<RootName>][.<Layer>]`

Allowed categories:
- Web
- SysAdmin
- DbMigrator
- SharedKernel
- Modules
- Engines
- Infrastructure
- Connectors
- Tests

Approved root business modules:
- Company
- CompanyAccess
- GL
- AR
- AP
- Inventory
- PaymentGateway
- Reconciliation
- Reports
- Tasks

Approved root engines:
- Posting
- Tax
- FX
- Costing
- Numbering
- ReconciliationControl

Approved root infrastructure names:
- AIAssist
- Notifications
- Caching
- SmartPicker
- Reporting

Approved connector root names include patterns such as:
- Payment.<Provider>
- Channel.<Provider>
- Rates.<Provider>

Allowed layers for business modules:
- Domain.Shared
- Domain
- Application.Contracts
- Application
- EntityFrameworkCore
- Blazor

Forbidden names:
- Users
- UserManagement
- Identity
- AccountingCore
- LedgerEngine
- Common
- Helpers
- Utils
- Temp
- Misc
- Manager
- Processor
- ServiceImpl

Rules:
- AI must not invent new project categories, root module names, root engine names, or layer names without explicit approval.
- AI must not create files outside approved module boundaries.
- One public type per file is the default rule.
- File name must match the main type name exactly.
- Vertical Slice use cases must be grouped by feature / use-case folder.


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
