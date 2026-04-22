namespace Citus.SysAdmin.Api.Auth;

public sealed class SysAdminRotateSecretRequest
{
    public string CurrentPassword { get; init; } = string.Empty;

    public string NewPassword { get; init; } = string.Empty;
}
