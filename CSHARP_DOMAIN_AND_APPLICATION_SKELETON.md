# C# Domain And Application Skeleton

## 1. Purpose

This document defines the recommended C# solution shape for moving Citus accounting core into a backend-authoritative .NET implementation.

It covers:

- project boundaries
- domain model shape
- application service shape
- key interfaces
- API boundary direction
- phased migration from current TS implementation

Current scaffold status:

- an initial manual skeleton now exists under [backend/README.md](./backend/README.md)
- the current machine does not have a usable .NET SDK installed, so compile verification is still pending

Authority order:

`CITUS_PRODUCT_ENGINEERING_AUTHORITY.md > POSTING_TAX_FX_ENGINE_EXECUTION_SPEC.md > this document > task notes > temporary implementation habits`

## 2. Recommended Solution Layout

```text
/backend
  /src
    /Citus.Accounting.Domain
    /Citus.Accounting.Application
    /Citus.Accounting.Infrastructure
    /Citus.Accounting.Api
    /Citus.SysAdmin.Api
  /tests
    /Citus.Accounting.Domain.Tests
    /Citus.Accounting.Application.Tests
    /Citus.Accounting.IntegrationTests
```

Recommended responsibilities:

- `Domain`: entities, value objects, domain rules, domain services, status/state invariants
- `Application`: commands, queries, orchestrators, transaction boundaries, DTOs
- `Infrastructure`: PostgreSQL persistence, provider adapters, audit/outbox, caching
- `Api`: authenticated business endpoints
- `SysAdmin.Api`: separate operational/system administration surface

## 3. Core Namespace Direction

Recommended namespaces:

- `Citus.Accounting.Domain.Common`
- `Citus.Accounting.Domain.Companies`
- `Citus.Accounting.Domain.Currencies`
- `Citus.Accounting.Domain.ChartOfAccounts`
- `Citus.Accounting.Domain.Tax`
- `Citus.Accounting.Domain.Documents`
- `Citus.Accounting.Domain.Posting`
- `Citus.Accounting.Domain.Journal`
- `Citus.Accounting.Domain.Ledger`
- `Citus.Accounting.Domain.OpenItems`
- `Citus.Accounting.Application.Commands`
- `Citus.Accounting.Application.Queries`

## 4. Key Value Objects

Recommended value objects:

- `CompanyId`
- `UserId`
- `EntityNumber`
- `CurrencyCode`
- `Money`
- `ExchangeRate`
- `FxSnapshotRef`
- `DocumentNumber`
- `PostingRunId`

### 4.1 `Money`

Recommended shape:

```csharp
public sealed record Money(decimal Amount, string CurrencyCode)
{
    public static Money Zero(string currencyCode) => new(0m, currencyCode);
}
```

Rules:

- use `decimal`
- carry currency explicitly
- do not use floating-point math

### 4.2 `FxSnapshotRef`

Recommended shape:

```csharp
public sealed record FxSnapshotRef(
    Guid SnapshotId,
    string BaseCurrencyCode,
    string QuoteCurrencyCode,
    decimal Rate,
    DateOnly RequestedDate,
    DateOnly EffectiveDate,
    string SourceSemantics);
```

## 5. Core Domain Entities

Recommended first-wave entities:

- `Company`
- `CompanyMembership`
- `CurrencyDefinition`
- `CompanyCurrency`
- `FxRateSnapshot`
- `Account`
- `TaxCode`
- `Customer`
- `Vendor`
- `InvoiceDocument`
- `BillDocument`
- `ManualJournalDocument`
- `JournalEntry`
- `JournalEntryLine`
- `LedgerEntry`
- `ArOpenItem`
- `ApOpenItem`
- `SettlementApplication`

## 6. Document Abstraction

All postable business documents should implement a common contract.

```csharp
public interface IPostingDocument
{
    Guid Id { get; }
    Guid CompanyId { get; }
    string SourceType { get; }
    string Status { get; }
    DateOnly DocumentDate { get; }
    string TransactionCurrencyCode { get; }
    string BaseCurrencyCode { get; }
    IReadOnlyList<IPostingDocumentLine> Lines { get; }
}

public interface IPostingDocumentLine
{
    int LineNumber { get; }
    string Description { get; }
}
```

Recommended implementations:

- `InvoiceDocument : IPostingDocument`
- `BillDocument : IPostingDocument`
- `ManualJournalDocument : IPostingDocument`

## 7. Posting Engine Core Interfaces

```csharp
public interface IPostingEngine
{
    Task<PostingResult> PostAsync(
        IPostingDocument document,
        PostingContext context,
        CancellationToken cancellationToken);
}

public interface IPostingValidator
{
    Task ValidateAsync(
        IPostingDocument document,
        PostingContext context,
        CancellationToken cancellationToken);
}

public interface ITaxEngine
{
    Task<TaxComputationResult> CalculateAsync(
        IPostingDocument document,
        CancellationToken cancellationToken);
}

public interface IFxResolutionService
{
    Task<FxResolutionResult> ResolveAsync(
        FxResolutionRequest request,
        CancellationToken cancellationToken);
}

public interface IPostingFragmentBuilder
{
    Task<IReadOnlyList<PostingFragment>> BuildAsync(
        IPostingDocument document,
        TaxComputationResult taxResult,
        FxResolutionResult fxResult,
        CancellationToken cancellationToken);
}

public interface IJournalAggregator
{
    JournalEntryDraft Aggregate(
        IPostingDocument document,
        IReadOnlyList<PostingFragment> fragments);
}

public interface IJournalEntryWriter
{
    Task<JournalEntryWriteResult> WriteAsync(
        JournalEntryDraft draft,
        CancellationToken cancellationToken);
}
```

## 8. Application Layer Shape

Application services should orchestrate use cases, not own accounting math.

Recommended command handlers:

- `PostInvoiceCommandHandler`
- `PostBillCommandHandler`
- `PostManualJournalCommandHandler`
- `ReceivePaymentCommandHandler`
- `PayBillsCommandHandler`
- `ReverseJournalEntryCommandHandler`

Recommended query handlers:

- `GetInvoiceDetailQueryHandler`
- `GetBillDetailQueryHandler`
- `GetJournalEntryDetailQueryHandler`
- `GetFxSnapshotForDocumentDateQueryHandler`
- `GetArAgingReportQueryHandler`
- `GetApAgingReportQueryHandler`

## 9. Posting Context

Recommended shape:

```csharp
public sealed record PostingContext(
    Guid CompanyId,
    Guid UserId,
    string ActiveCompanyBaseCurrencyCode,
    Guid? AcceptedFxSnapshotId,
    string? IdempotencyKey,
    DateTimeOffset RequestedAt);
```

Rules:

- company context must be explicit
- frontend may not invent company/accounting context
- accepted FX snapshot identity should be explicit at save/post time

## 10. Repository Interfaces

Recommended repository boundaries:

```csharp
public interface IInvoiceRepository
{
    Task<InvoiceDocument?> GetForPostingAsync(Guid companyId, Guid invoiceId, CancellationToken cancellationToken);
    Task SaveAsync(InvoiceDocument invoice, CancellationToken cancellationToken);
}

public interface IJournalEntryRepository
{
    Task<bool> ExistsByIdempotencyKeyAsync(Guid companyId, string idempotencyKey, CancellationToken cancellationToken);
    Task SaveAsync(JournalEntry journalEntry, CancellationToken cancellationToken);
}

public interface IFxSnapshotRepository
{
    Task<FxRateSnapshot?> FindAcceptedSnapshotAsync(
        Guid companyId,
        string baseCurrencyCode,
        string quoteCurrencyCode,
        DateOnly requestedDate,
        Guid? snapshotId,
        CancellationToken cancellationToken);
}
```

Repository rule:

- repositories enforce company scoping
- repositories do not hide cross-company ambiguity

## 11. Infrastructure Adapters

Recommended infrastructure services:

- `FrankfurterRateProviderAdapter`
- `CompanyFxSnapshotStore`
- `PostgresJournalEntryRepository`
- `PostgresInvoiceRepository`
- `PostgresBillRepository`
- `AuditLogWriter`
- `OutboxWriter`

Important rule:

- provider adapters may fetch data for lookup/refresh flows
- provider adapters are not allowed in save/post path

## 12. Transaction Boundary

Recommended application transaction pattern:

```csharp
public interface IUnitOfWork
{
    Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken);
}
```

Use cases that create posted accounting truth must run in one transaction:

- source lock
- validation
- duplicate-post check
- JE creation
- ledger creation
- open-item update
- audit/outbox write

## 13. API Boundary Direction

Recommended short-term architecture:

- Next.js remains frontend/BFF
- C# API becomes authoritative core-accounting backend

Recommended API families:

- `/api/accounting/invoices`
- `/api/accounting/bills`
- `/api/accounting/manual-journals`
- `/api/accounting/journal-entries`
- `/api/accounting/receipts`
- `/api/accounting/vendor-payments`
- `/api/accounting/fx`
- `/api/accounting/reports`

Important rule:

- UI sends intent and source fields
- C# backend decides validation, FX acceptance, posting, numbering, and ledger truth

## 14. Suggested Project Start Order

Recommended implementation order:

1. `Domain.Common` value objects and error model
2. currency + FX domain
3. journal/posting contracts
4. `ManualJournalDocument` + post flow
5. `BillDocument` + post flow
6. `InvoiceDocument` + post flow
7. open items + AP/AR settlement
8. reporting queries

## 15. Migration From Current TS Code

Current codebase still contains posting-adjacent logic in TS server actions and Prisma models.

Recommended migration path:

1. keep Next.js as UI shell
2. freeze new accounting-rule duplication in TS
3. implement C# Posting Engine and FX/tax services
4. move formal posting endpoints behind C# API
5. let TS consume read models and command APIs
6. gradually retire accounting-write logic from TS

## 16. Test Strategy

Important test layers:

- domain tests for invariants and rounding behavior
- application tests for command orchestration
- integration tests for PostgreSQL transactionality
- provider adapter tests for contract/fallback correctness
- end-to-end tests for cross-company rejection and lifecycle transitions

## 17. Immediate Next Scaffold

When ready to create the actual .NET workspace, start with:

- `backend/Citus.Accounting.sln`
- `src/Citus.Accounting.Domain`
- `src/Citus.Accounting.Application`
- `src/Citus.Accounting.Infrastructure`
- `src/Citus.Accounting.Api`

Then implement first:

- `Money`
- `CurrencyCode`
- `FxRateSnapshot`
- `ManualJournalDocument`
- `IPostingEngine`
- `PostManualJournalCommandHandler`
