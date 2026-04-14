namespace Modules.AR.CustomerCurrency;

public sealed record CustomerCurrencyChangeResult(
    CustomerCurrencyPreference Preference,
    bool CurrencyChanged,
    bool LockPersisted);
