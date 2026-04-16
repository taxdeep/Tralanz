namespace Citus.SysAdmin.Api.Auth;

public sealed class SysAdminSetupStatusResponse
{
    public int AccountCount { get; init; }

    public bool HasAnyAccount { get; init; }

    public bool SetupRequired { get; init; }

    public bool BootstrapSeedingEnabled { get; init; }

    public bool BootstrapSeedingActive { get; init; }

    public string BootstrapEmailHint { get; init; } = string.Empty;
}
