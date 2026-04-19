namespace Citus.SysAdmin.Api.Auth;

public sealed class SysAdminSetupStatusResponse
{
    public string SetupStage { get; init; } = "uninitialized";

    public int AccountCount { get; init; }

    public int CompanyCount { get; init; }

    public int OwnerMembershipCount { get; init; }

    public bool HasAnyAccount { get; init; }

    public bool HasAnyCompany { get; init; }

    public bool HasAnyOwnerMembership { get; init; }

    public bool SetupRequired { get; init; }

    public bool BusinessInitializationPending { get; init; }

    public bool BusinessReady { get; init; }

    public bool FirstCompanySetupRequired { get; init; }

    public bool FirstCompanySetupDeferred { get; init; }

    public DateTimeOffset? FirstCompanySetupDeferredAtUtc { get; init; }

    public bool BootstrapSeedingEnabled { get; init; }

    public bool BootstrapSeedingActive { get; init; }

    public string BootstrapEmailHint { get; init; } = string.Empty;
}
