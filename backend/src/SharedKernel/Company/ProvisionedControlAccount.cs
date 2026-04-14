namespace SharedKernel.Company;

public sealed record class ProvisionedControlAccount(
    Guid AccountId,
    string Code,
    string Name,
    string CurrencyCode,
    string SystemRole,
    bool WasCreated);
