namespace Citus.Platform.Core.Runtime;

public sealed record class SysAdminSetupStatus
{
    public int AccountCount { get; init; }

    public int CompanyCount { get; init; }

    public int OwnerMembershipCount { get; init; }

    public bool FirstCompanySetupDeferred { get; init; }

    public DateTimeOffset? FirstCompanySetupDeferredAtUtc { get; init; }

    public bool HasAnyAccount => AccountCount > 0;

    public bool HasAnyCompany => CompanyCount > 0;

    public bool HasAnyOwnerMembership => OwnerMembershipCount > 0;

    public bool SetupRequired => !HasAnyAccount;

    public bool BusinessInitializationPending => HasAnyAccount && !BusinessReady;

    public bool BusinessReady => HasAnyCompany && HasAnyOwnerMembership;

    public bool FirstCompanySetupRequired => HasAnyAccount && !BusinessReady && !FirstCompanySetupDeferred;

    public string SetupStage =>
        !HasAnyAccount
            ? "uninitialized"
            : BusinessReady
                ? "business_ready"
                : HasAnyCompany
                    ? "business_initializing"
                    : FirstCompanySetupDeferred
                        ? "platform_ready_deferred"
                        : "platform_ready";
}
