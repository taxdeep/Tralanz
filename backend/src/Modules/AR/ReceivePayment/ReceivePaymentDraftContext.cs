namespace Modules.AR.ReceivePayment;

public sealed record ReceivePaymentDraftContext(
    CompanyId CompanyId,
    UserId UserId,
    Guid CustomerId,
    Guid BankAccountId,
    DateOnly PaymentDate,
    string? RequestedCurrencyCode,
    Guid? AcceptedFxSnapshotId,
    string? Memo);
