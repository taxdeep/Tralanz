namespace Modules.AP.VendorCurrency;

public interface IVendorCurrencyStore
{
    Task<VendorCurrencyPreference> GetPreferenceAsync(
        Guid vendorId,
        CancellationToken cancellationToken);

    Task<VendorCurrencyPreference> SavePreferenceAsync(
        Guid vendorId,
        string defaultCurrencyCode,
        bool currencyLocked,
        CancellationToken cancellationToken);
}
