namespace Citus.SysAdmin.Api.Auth;

public sealed class SysAdminFirstAccountSetupRequest
{
    public string Email { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;
}
