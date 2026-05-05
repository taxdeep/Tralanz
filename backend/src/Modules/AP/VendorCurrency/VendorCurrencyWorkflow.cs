using Modules.Company.MultiCurrency;

namespace Modules.AP.VendorCurrency;

public sealed class VendorCurrencyWorkflow : IVendorCurrencyWorkflow
{
    private readonly IVendorCurrencyStore _store;
    private readonly ICompanyCurrencyCatalog _companyCurrencyCatalog;

    public VendorCurrencyWorkflow(
        IVendorCurrencyStore store,
        ICompanyCurrencyCatalog companyCurrencyCatalog)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _companyCurrencyCatalog = companyCurrencyCatalog ?? throw new ArgumentNullException(nameof(companyCurrencyCatalog));
    }

    public async Task<VendorCurrencyPreference> GetPreferenceAsync(
        Guid vendorId,
        CancellationToken cancellationToken)
    {
        var preference = await _store.GetPreferenceAsync(vendorId, cancellationToken);
        if (!preference.CurrencyLocked && preference.HasTransactionHistory)
        {
            preference = await _store.SavePreferenceAsync(
                preference.VendorId,
                preference.DefaultCurrencyCode,
                currencyLocked: true,
                cancellationToken);
        }

        return preference;
    }

    public async Task<VendorCurrencyChangeResult> ChangeDefaultCurrencyAsync(
        Guid vendorId,
        string currencyCode,
        UserId userId,
        CancellationToken cancellationToken)
    {
        _ = userId;

        var normalizedCurrencyCode = NormalizeCurrencyCode(currencyCode);
        var preference = await _store.GetPreferenceAsync(vendorId, cancellationToken);
        var lockPersisted = false;
        if (!preference.CurrencyLocked && preference.HasTransactionHistory)
        {
            preference = await _store.SavePreferenceAsync(
                preference.VendorId,
                preference.DefaultCurrencyCode,
                currencyLocked: true,
                cancellationToken);
            lockPersisted = true;
        }

        if (preference.IsLocked &&
            !string.Equals(preference.DefaultCurrencyCode, normalizedCurrencyCode, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Vendor {preference.DisplayName} already has transaction history, so its default currency is locked at {preference.DefaultCurrencyCode}.");
        }

        if (string.Equals(preference.DefaultCurrencyCode, normalizedCurrencyCode, StringComparison.OrdinalIgnoreCase))
        {
            return new VendorCurrencyChangeResult(preference, CurrencyChanged: false, LockPersisted: lockPersisted);
        }

        var companyProfile = await _companyCurrencyCatalog.GetProfileAsync(preference.CompanyId, cancellationToken);
        if (!companyProfile.IsCurrencyEnabled(normalizedCurrencyCode))
        {
            throw new InvalidOperationException(
                $"Vendor currency {normalizedCurrencyCode} is not enabled for company {preference.CompanyId:D}.");
        }

        var updatedPreference = await _store.SavePreferenceAsync(
            preference.VendorId,
            normalizedCurrencyCode,
            preference.CurrencyLocked,
            cancellationToken);

        return new VendorCurrencyChangeResult(updatedPreference, CurrencyChanged: true, LockPersisted: lockPersisted);
    }

    private static string NormalizeCurrencyCode(string currencyCode)
    {
        if (string.IsNullOrWhiteSpace(currencyCode))
        {
            throw new InvalidOperationException("A currency code is required.");
        }

        return currencyCode.Trim().ToUpperInvariant();
    }
}
