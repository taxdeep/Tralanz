namespace Modules.AP.VendorCurrency;

public sealed record VendorCurrencyPreference(
    Guid VendorId,
    Guid CompanyId,
    string DisplayName,
    string DefaultCurrencyCode,
    bool CurrencyLocked,
    bool HasTransactionHistory)
{
    public bool IsLocked => CurrencyLocked || HasTransactionHistory;
}
