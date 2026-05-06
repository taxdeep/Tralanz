namespace Modules.AR.CustomerCurrency;

public sealed record CustomerCurrencyPreference(
    Guid CustomerId,
    CompanyId CompanyId,
    string DisplayName,
    string DefaultCurrencyCode,
    bool CurrencyLocked,
    bool HasTransactionHistory)
{
    public bool IsLocked => CurrencyLocked || HasTransactionHistory;
}
