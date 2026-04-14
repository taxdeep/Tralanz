namespace Modules.AR.ReceivePayment;

public sealed record ReceivePaymentDraftResult(
    Guid DocumentId,
    string EntityNumber,
    string PaymentNumber,
    string DocumentCurrencyCode,
    string BaseCurrencyCode,
    Guid? FxSnapshotId,
    decimal FxRate,
    DateOnly FxRequestedDate,
    DateOnly FxEffectiveDate,
    string FxSource,
    decimal TotalAmount,
    int LineCount,
    string Status);
