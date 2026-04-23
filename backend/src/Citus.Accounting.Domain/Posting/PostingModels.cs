using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;

namespace Citus.Accounting.Domain.Posting;

public sealed record PostingContext(
    CompanyId CompanyId,
    UserId UserId,
    CurrencyCode ActiveCompanyBaseCurrencyCode,
    Guid? AcceptedFxSnapshotId,
    string? IdempotencyKey,
    DateTimeOffset RequestedAt);

public sealed record PostingResult(
    Guid JournalEntryId,
    string JournalEntryDisplayNumber,
    string Status,
    DateTimeOffset PostedAt,
    IReadOnlyList<string> Warnings);

public sealed record PostingFragment(
    Guid AccountId,
    CurrencyCode CurrencyCode,
    decimal TxDebit,
    decimal TxCredit,
    decimal Debit,
    decimal Credit,
    string Description,
    string? TaxComponentType = null,
    string? ControlRole = null,
    Guid? PartyId = null,
    string? PostingRole = null,
    int? SourceLineNumber = null);

public sealed record TaxComputationLine(
    int LineNumber,
    decimal TaxableAmount,
    decimal TaxAmount,
    Guid? TaxCodeId,
    string? TaxComponentType);

public sealed record TaxComputationResult(
    IReadOnlyList<TaxComputationLine> Lines,
    decimal TotalTaxAmount);

public sealed record FxResolutionRequest(
    CompanyId CompanyId,
    CurrencyCode BaseCurrencyCode,
    CurrencyCode QuoteCurrencyCode,
    DateOnly RequestedDate,
    Guid? AcceptedSnapshotId,
    string SourceType);

public sealed record FxResolutionResult(
    FxSnapshotRef Snapshot,
    IReadOnlyList<string> Notes);
