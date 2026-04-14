namespace Web.Business.AP.SettlementLookup;

public interface IApSettlementLookupService
{
    Task<IReadOnlyList<(Guid Id, string DisplayLabel, string CurrencyCode)>> ListVendorsAsync(
        Guid companyId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<(Guid Id, string DisplayLabel)>> ListBankAccountsAsync(
        Guid companyId,
        CancellationToken cancellationToken);
}
