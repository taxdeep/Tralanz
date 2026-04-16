namespace Citus.Platform.Core.Runtime;

public sealed record class SysAdminSetupStatus
{
    public int AccountCount { get; init; }

    public bool HasAnyAccount => AccountCount > 0;

    public bool SetupRequired => !HasAnyAccount;
}
