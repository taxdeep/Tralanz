namespace Modules.AR.CustomerCurrency;

public interface ICustomerCurrencyWorkflow
{
    Task<CustomerCurrencyPreference> GetPreferenceAsync(
        Guid customerId,
        CancellationToken cancellationToken);

    Task<CustomerCurrencyChangeResult> ChangeDefaultCurrencyAsync(
        Guid customerId,
        string currencyCode,
        Guid userId,
        CancellationToken cancellationToken);
}
