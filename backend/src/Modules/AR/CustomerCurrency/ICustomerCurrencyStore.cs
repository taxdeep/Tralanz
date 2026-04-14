namespace Modules.AR.CustomerCurrency;

public interface ICustomerCurrencyStore
{
    Task<CustomerCurrencyPreference> GetPreferenceAsync(
        Guid customerId,
        CancellationToken cancellationToken);

    Task<CustomerCurrencyPreference> SavePreferenceAsync(
        Guid customerId,
        string defaultCurrencyCode,
        bool currencyLocked,
        CancellationToken cancellationToken);
}
