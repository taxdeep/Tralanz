# Naming Whitelist Refactor Plan

This document is the temporary execution plan for bringing the current Citus codebase into the approved naming whitelist before adding more accounting features.

## 1. Active Naming Constraints

Approved root categories:

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

Approved engine names:

- `Posting`
- `Tax`
- `FX`
- `Numbering`
- `ReconciliationControl`

Forbidden naming direction:

- do not introduce new root categories
- do not introduce new root business modules
- do not introduce new root engines
- do not introduce category folders such as `Users`, `Identity`, `Common`, `Utils`, `Helpers`, `Misc`, `Temp`, `Manager`, `Processor`

## 2. Current Non-Compliant Roots

The current implementation is running from these non-whitelist roots under `backend/src`:

- `Citus.Accounting.Api`
- `Citus.Accounting.Application`
- `Citus.Accounting.Domain`
- `Citus.Accounting.Infrastructure`
- `Citus.Business.Blazor`
- `Citus.ConsoleApp`
- `Citus.Gateway`
- `Citus.Platform.Core`
- `Citus.Platform.Infrastructure`
- `Citus.SysAdmin.Api`
- `Citus.SysAdmin.Blazor`
- `Citus.Ui.Shared`

Because of that, any new feature work added directly there would violate the naming whitelist.

## 3. Approved Target Mapping

The current code should be migrated into whitelist-compliant roots using this direction.

### 3.1 Web

- `backend/src/Web/Business`
- `backend/src/Web/Business/GL`
- `backend/src/Web/Business/AR`
- `backend/src/Web/Business/AP`
- `backend/src/Web/Business/Reports`
- `backend/src/Web/Shell`

Purpose:

- Blazor business app
- report pages
- journal entry review pages
- source document review pages
- UI state and page composition

### 3.2 SysAdmin

- `backend/src/SysAdmin/Web`
- `backend/src/SysAdmin/Control`

Purpose:

- SysAdmin API host
- SysAdmin Blazor host
- system control surfaces

### 3.3 SharedKernel

- `backend/src/SharedKernel/Company`
- `backend/src/SharedKernel/CompanyAccess`
- `backend/src/SharedKernel/Reports`
- `backend/src/SharedKernel/FX`
- `backend/src/SharedKernel/Numbering`

Purpose:

- cross-module value objects
- company scope rules
- report type definitions
- shared contracts that are not business-module specific

### 3.4 Modules

- `backend/src/Modules/GL`
- `backend/src/Modules/AR`
- `backend/src/Modules/AP`
- `backend/src/Modules/Reports`
- `backend/src/Modules/Reconciliation`
- `backend/src/Modules/Company`
- `backend/src/Modules/CompanyAccess`
- `backend/src/Modules/Tasks`

Purpose:

- business rules
- read models
- write models
- document review logic
- report projections

### 3.5 Engines

- `backend/src/Engines/Posting`
- `backend/src/Engines/Tax`
- `backend/src/Engines/FX`
- `backend/src/Engines/Numbering`
- `backend/src/Engines/ReconciliationControl`

Purpose:

- formal engine-owned truth
- reusable cross-module accounting execution

### 3.6 Infrastructure

- `backend/src/Infrastructure/PostgreSQL`
- `backend/src/Infrastructure/Runtime`
- `backend/src/Infrastructure/Observability`

Purpose:

- database persistence
- runtime state
- health, logging, integration plumbing

### 3.7 Connectors

- `backend/src/Connectors/FX`
- `backend/src/Connectors/Notifications`
- `backend/src/Connectors/Payments`

Purpose:

- external adapters only

### 3.8 Tests

- `backend/src/Tests/GL`
- `backend/src/Tests/AR`
- `backend/src/Tests/AP`
- `backend/src/Tests/Reports`
- `backend/src/Tests/Infrastructure`
- `backend/src/Tests/Web`
- `backend/src/Tests/SysAdmin`

Purpose:

- whitelist-compliant test roots

## 4. Feature Mapping Under The Approved Structure

### 4.1 Receive Payment

Target roots:

- `Modules/AR`
- `Web/Business/AR`
- `Tests/AR`

### 4.2 Pay Bill

Target roots:

- `Modules/AP`
- `Web/Business/AP`
- `Tests/AP`

### 4.3 Credit Application

Target roots:

- `Modules/AR`
- `Web/Business/AR`
- `Tests/AR`

### 4.4 Report Type / Accounting Basis Selection

Target roots:

- `SharedKernel/Reports`
- `Modules/Reports`
- `Web/Business/Reports`
- `Tests/Reports`

Required options:

- `Accrual`
- `CashBasis`
- `CashOnly`

Default:

- `Accrual`

Rules:

- the selected report type must be backend-owned
- report projections must decide legality per report
- UI may present the selector, but UI must not invent accounting basis truth

## 5. Next Execution Step

Before any more feature code is added, create whitelist-compliant scaffolding and begin moving the current behavior behind it.

Recommended order:

1. Create approved root folders under `backend/src`.
2. Create shared report-type definitions under `SharedKernel/Reports`.
3. Create `Modules/Reports` scaffolding for accounting-basis-aware report queries.
4. Create `Web/Business/Reports` scaffolding for the report type selector.
5. Create `Modules/AR` and `Modules/AP` scaffolding for settlement review flows.
6. Move or wrap existing non-compliant logic behind the new roots slice by slice.

## 6. Complete Path List For The Next Code Step

These are the exact paths that should be created or modified in the next implementation step.

### 6.1 Create

- `backend/src/SharedKernel/Reports/ReportType.cs`
- `backend/src/SharedKernel/Reports/ReportTypeOption.cs`
- `backend/src/SharedKernel/Reports/ReportTypeDefaults.cs`
- `backend/src/Modules/Reports/ReportType/ReportTypeSelection.cs`
- `backend/src/Modules/Reports/ReportType/ReportTypePolicy.cs`
- `backend/src/Web/Business/Reports/ReportType/ReportTypeSelectorState.cs`
- `backend/src/Web/Business/Reports/ReportType/ReportTypeSelectorViewModel.cs`
- `backend/src/Tests/Reports/ReportTypePolicyTests.cs`
- `backend/src/Modules/AR/ReceivePayment/ReceivePaymentReview.cs`
- `backend/src/Modules/AR/CreditApplication/CreditApplicationReview.cs`
- `backend/src/Modules/AP/PayBill/PayBillReview.cs`
- `backend/src/Tests/AR/ReceivePaymentReviewTests.cs`
- `backend/src/Tests/AR/CreditApplicationReviewTests.cs`
- `backend/src/Tests/AP/PayBillReviewTests.cs`

### 6.2 Modify

- `backend/src/Citus.Accounting.Api/Program.cs`
- `backend/src/Citus.Business.Blazor/Components/Features/Reports/ReportsPage.razor`
- `backend/src/Citus.Business.Blazor/Components/Features/Documents/AccountingDocumentDetailPage.razor`
- `backend/src/Citus.Business.Blazor/Components/Features/JournalEntry/JournalEntryReviewPage.razor`
- `backend/src/Citus.Accounting.Infrastructure/Persistence/PostgresAccountingDocumentReviewRepository.cs`

## 7. Enforcement Rule

If the next requested implementation cannot be placed under the approved roots listed above, stop and respond with:

`No approved target path found.`
