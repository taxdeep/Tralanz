namespace Modules.AP.VendorCreditApplication;

public sealed record VendorCreditApplicationDraftContext(
    CompanyId CompanyId,
    UserId UserId,
    Guid VendorId,
    DateOnly ApplicationDate,
    string? RequestedCurrencyCode,
    string? Memo);
