namespace Modules.AP.PayBill;

public sealed record PayBillDraftContext(
    CompanyId CompanyId,
    UserId UserId,
    Guid VendorId,
    Guid BankAccountId,
    DateOnly PaymentDate,
    string? RequestedCurrencyCode,
    Guid? AcceptedFxSnapshotId,
    string? Memo);
