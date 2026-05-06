using Modules.Company.MultiCurrency;

namespace Modules.AR.CustomerCurrency;

public sealed class CustomerCurrencyWorkflow : ICustomerCurrencyWorkflow
{
    private readonly ICustomerCurrencyStore _store;
    private readonly ICompanyCurrencyCatalog _companyCurrencyCatalog;

    public CustomerCurrencyWorkflow(
        ICustomerCurrencyStore store,
        ICompanyCurrencyCatalog companyCurrencyCatalog)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _companyCurrencyCatalog = companyCurrencyCatalog ?? throw new ArgumentNullException(nameof(companyCurrencyCatalog));
    }

    public async Task<CustomerCurrencyPreference> GetPreferenceAsync(
        Guid customerId,
        CancellationToken cancellationToken)
    {
        var preference = await _store.GetPreferenceAsync(customerId, cancellationToken);
        if (!preference.CurrencyLocked && preference.HasTransactionHistory)
        {
            preference = await _store.SavePreferenceAsync(
                preference.CustomerId,
                preference.DefaultCurrencyCode,
                currencyLocked: true,
                cancellationToken);
        }

        return preference;
    }

    public async Task<CustomerCurrencyChangeResult> ChangeDefaultCurrencyAsync(
        Guid customerId,
        string currencyCode,
        UserId userId,
        CancellationToken cancellationToken)
    {
        _ = userId;

        var normalizedCurrencyCode = NormalizeCurrencyCode(currencyCode);
        var preference = await _store.GetPreferenceAsync(customerId, cancellationToken);
        var lockPersisted = false;
        if (!preference.CurrencyLocked && preference.HasTransactionHistory)
        {
            preference = await _store.SavePreferenceAsync(
                preference.CustomerId,
                preference.DefaultCurrencyCode,
                currencyLocked: true,
                cancellationToken);
            lockPersisted = true;
        }

        if (preference.IsLocked &&
            !string.Equals(preference.DefaultCurrencyCode, normalizedCurrencyCode, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Customer {preference.DisplayName} already has transaction history, so its default currency is locked at {preference.DefaultCurrencyCode}.");
        }

        if (string.Equals(preference.DefaultCurrencyCode, normalizedCurrencyCode, StringComparison.OrdinalIgnoreCase))
        {
            return new CustomerCurrencyChangeResult(preference, CurrencyChanged: false, LockPersisted: lockPersisted);
        }

        var companyProfile = await _companyCurrencyCatalog.GetProfileAsync(preference.CompanyId, cancellationToken);
        if (!companyProfile.IsCurrencyEnabled(normalizedCurrencyCode))
        {
            throw new InvalidOperationException(
                $"Customer currency {normalizedCurrencyCode} is not enabled for company {preference.CompanyId:D}.");
        }

        var updatedPreference = await _store.SavePreferenceAsync(
            preference.CustomerId,
            normalizedCurrencyCode,
            preference.CurrencyLocked,
            cancellationToken);

        return new CustomerCurrencyChangeResult(updatedPreference, CurrencyChanged: true, LockPersisted: lockPersisted);
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
