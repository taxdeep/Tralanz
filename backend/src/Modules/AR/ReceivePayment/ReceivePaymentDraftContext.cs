namespace Modules.AR.ReceivePayment;

public sealed record ReceivePaymentDraftContext(
    Guid CompanyId,
    Guid UserId,
    Guid CustomerId,
    Guid BankAccountId,
    DateOnly PaymentDate,
    string? RequestedCurrencyCode,
    Guid? AcceptedFxSnapshotId,
    string? Memo);
