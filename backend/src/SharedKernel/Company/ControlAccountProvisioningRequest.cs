namespace SharedKernel.Company;

public sealed record class ControlAccountProvisioningRequest(
    string Code,
    string Name,
    string RootType,
    string DetailType,
    string CurrencyCode,
    string SystemKey,
    string SystemRole,
    bool AllowManualPosting);
