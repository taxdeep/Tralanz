namespace Modules.AP.VendorCurrency;

public interface IVendorCurrencyWorkflow
{
    Task<VendorCurrencyPreference> GetPreferenceAsync(
        Guid vendorId,
        CancellationToken cancellationToken);

    Task<VendorCurrencyChangeResult> ChangeDefaultCurrencyAsync(
        Guid vendorId,
        string currencyCode,
        UserId userId,
        CancellationToken cancellationToken);
}
