namespace Modules.AP.PayBill;

public sealed record PayBillDraftContext(
    Guid CompanyId,
    Guid UserId,
    Guid VendorId,
    Guid BankAccountId,
    DateOnly PaymentDate,
    string? RequestedCurrencyCode,
    Guid? AcceptedFxSnapshotId,
    string? Memo);
