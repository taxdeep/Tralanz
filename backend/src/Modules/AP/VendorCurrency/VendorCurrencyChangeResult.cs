namespace Modules.AP.VendorCurrency;

public sealed record VendorCurrencyChangeResult(
    VendorCurrencyPreference Preference,
    bool CurrencyChanged,
    bool LockPersisted);
