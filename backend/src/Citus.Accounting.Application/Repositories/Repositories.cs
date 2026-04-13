using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Documents;
using Citus.Accounting.Domain.Journal;

namespace Citus.Accounting.Application.Repositories;

public interface IManualJournalDocumentRepository
{
    Task<ManualJournalDocument?> GetForPostingAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken);

    Task SaveAsync(
        ManualJournalDocument document,
        CancellationToken cancellationToken);
}

public interface IInvoiceDocumentRepository
{
    Task<InvoiceDocument?> GetForPostingAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken);
}

public interface ICreditNoteDocumentRepository
{
    Task<CreditNoteDocument?> GetForPostingAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken);
}

public interface IBillDocumentRepository
{
    Task<BillDocument?> GetForPostingAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken);
}

public interface IVendorCreditDocumentRepository
{
    Task<VendorCreditDocument?> GetForPostingAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken);
}

public interface IReceivePaymentDocumentRepository
{
    Task<ReceivePaymentDocument?> GetForPostingAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SettlementOpenItemCandidate>> ListOpenReceivableCandidatesAsync(
        CompanyId companyId,
        Guid customerId,
        CancellationToken cancellationToken);

    Task<SettlementDraftPreparationResult> PrepareDraftAsync(
        ReceivePaymentDraftPreparation request,
        CancellationToken cancellationToken);
}

public interface ICreditApplicationDocumentRepository
{
    Task<CreditApplicationDocument?> GetForPostingAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken);
}

public interface IPayBillDocumentRepository
{
    Task<PayBillDocument?> GetForPostingAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SettlementOpenItemCandidate>> ListOpenPayableCandidatesAsync(
        CompanyId companyId,
        Guid vendorId,
        CancellationToken cancellationToken);

    Task<SettlementDraftPreparationResult> PrepareDraftAsync(
        PayBillDraftPreparation request,
        CancellationToken cancellationToken);
}

public sealed record SettlementOpenItemCandidate(
    Guid OpenItemId,
    string SourceType,
    Guid SourceDocumentId,
    string DisplayNumber,
    DateOnly DocumentDate,
    DateOnly? DueDate,
    string DocumentCurrencyCode,
    string BaseCurrencyCode,
    decimal OriginalAmountTx,
    decimal OpenAmountTx,
    decimal OpenAmountBase,
    string BalanceSide,
    string Status);

public sealed record SettlementDraftLine(
    Guid TargetOpenItemId,
    decimal AppliedAmountTx);

public sealed record ReceivePaymentDraftPreparation(
    CompanyId CompanyId,
    UserId UserId,
    Guid CustomerId,
    Guid BankAccountId,
    DateOnly PaymentDate,
    Guid? AcceptedFxSnapshotId,
    string? Memo,
    IReadOnlyList<SettlementDraftLine> Lines);

public sealed record PayBillDraftPreparation(
    CompanyId CompanyId,
    UserId UserId,
    Guid VendorId,
    Guid BankAccountId,
    DateOnly PaymentDate,
    Guid? AcceptedFxSnapshotId,
    string? Memo,
    IReadOnlyList<SettlementDraftLine> Lines);

public sealed record SettlementDraftPreparationResult(
    Guid DocumentId,
    string EntityNumber,
    string DisplayNumber,
    int PreparedLineCount,
    decimal TotalAmount,
    string Status);

public interface IVendorCreditApplicationDocumentRepository
{
    Task<VendorCreditApplicationDocument?> GetForPostingAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken);
}

public sealed record FxRevaluationDraftPreparation(
    CompanyId CompanyId,
    UserId UserId,
    DateOnly RevaluationDate,
    CurrencyCode TransactionCurrencyCode,
    Guid? AcceptedFxSnapshotId,
    bool IncludeAccountsReceivable,
    bool IncludeAccountsPayable,
    string? Memo);

public sealed record FxRevaluationUnwindPreparation(
    CompanyId CompanyId,
    UserId UserId,
    Guid ReversalOfDocumentId,
    DateOnly UnwindDate,
    string? Memo);

public sealed record FxRevaluationDraftPreparationResult(
    Guid DocumentId,
    string EntityNumber,
    string DisplayNumber,
    int PreparedLineCount,
    string Status);

public sealed record FxRevaluationCascadeUnwindPlanStep(
    Guid DocumentId,
    string DisplayNumber,
    DateOnly RevaluationDate,
    DateTimeOffset PostedAt,
    bool IsRequestedBatch,
    bool IsNextStep);

public sealed record FxRevaluationCascadeUnwindPlanResult(
    Guid RequestedDocumentId,
    string RequestedDisplayNumber,
    Guid NextDocumentId,
    string NextDisplayNumber,
    bool RequestedBatchIsTail,
    IReadOnlyList<FxRevaluationCascadeUnwindPlanStep> ActiveRevaluationChain);

public interface IFxRevaluationDocumentRepository
{
    Task<FxRevaluationDocument?> GetForPostingAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken);

    Task<FxRevaluationCascadeUnwindPlanResult> GetCascadeUnwindPlanAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken);

    Task<FxRevaluationDraftPreparationResult> PrepareDraftAsync(
        FxRevaluationDraftPreparation request,
        CancellationToken cancellationToken);

    Task<FxRevaluationDraftPreparationResult> PrepareNextPeriodUnwindDraftAsync(
        FxRevaluationUnwindPreparation request,
        CancellationToken cancellationToken);
}

public interface IJournalEntryRepository
{
    Task<bool> ExistsByIdempotencyKeyAsync(
        CompanyId companyId,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task SaveAsync(
        JournalEntry entry,
        CancellationToken cancellationToken);
}

public interface IFxSnapshotRepository
{
    Task<FxSnapshotRef?> FindAcceptedSnapshotAsync(
        CompanyId companyId,
        CurrencyCode baseCurrencyCode,
        CurrencyCode quoteCurrencyCode,
        DateOnly requestedDate,
        Guid? snapshotId,
        CancellationToken cancellationToken);
}

public interface IArOpenItemRepository
{
    Task EnsureForInvoiceAsync(
        InvoiceDocument document,
        decimal originalAmountBase,
        CancellationToken cancellationToken);

    Task EnsureForCreditNoteAsync(
        CreditNoteDocument document,
        decimal originalAmountBase,
        CancellationToken cancellationToken);
}

public interface IApOpenItemRepository
{
    Task EnsureForBillAsync(
        BillDocument document,
        decimal originalAmountBase,
        CancellationToken cancellationToken);

    Task EnsureForVendorCreditAsync(
        VendorCreditDocument document,
        decimal originalAmountBase,
        CancellationToken cancellationToken);
}

public interface ISettlementApplicationRepository
{
    Task ApplyReceivePaymentAsync(
        ReceivePaymentDocument document,
        UserId createdByUserId,
        CancellationToken cancellationToken);

    Task ApplyCreditApplicationAsync(
        CreditApplicationDocument document,
        UserId createdByUserId,
        CancellationToken cancellationToken);

    Task ApplyPayBillAsync(
        PayBillDocument document,
        UserId createdByUserId,
        CancellationToken cancellationToken);

    Task ApplyVendorCreditApplicationAsync(
        VendorCreditApplicationDocument document,
        UserId createdByUserId,
        CancellationToken cancellationToken);
}

public interface IFxRevaluationApplyRepository
{
    Task ApplyAsync(
        FxRevaluationDocument document,
        UserId appliedByUserId,
        CancellationToken cancellationToken);
}
