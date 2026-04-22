namespace Citus.SysAdmin.Api.Auth;

public sealed class SysAdminLoginRequest
{
    public string Email { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;
}
