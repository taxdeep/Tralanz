namespace Modules.AR.CreditApplication;

public sealed record CreditApplicationDraftContext(
    Guid CompanyId,
    Guid UserId,
    Guid CustomerId,
    DateOnly ApplicationDate,
    string? RequestedCurrencyCode,
    string? Memo);
