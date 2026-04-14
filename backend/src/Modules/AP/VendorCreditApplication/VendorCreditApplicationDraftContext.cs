namespace Modules.AP.VendorCreditApplication;

public sealed record VendorCreditApplicationDraftContext(
    Guid CompanyId,
    Guid UserId,
    Guid VendorId,
    DateOnly ApplicationDate,
    string? RequestedCurrencyCode,
    string? Memo);
