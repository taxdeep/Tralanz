namespace Web.Business.AR.SettlementLookup;

public interface IArSettlementLookupService
{
    Task<IReadOnlyList<(Guid Id, string DisplayLabel, string CurrencyCode)>> ListCustomersAsync(
        Guid companyId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<(Guid Id, string DisplayLabel)>> ListBankAccountsAsync(
        Guid companyId,
        CancellationToken cancellationToken);
}
