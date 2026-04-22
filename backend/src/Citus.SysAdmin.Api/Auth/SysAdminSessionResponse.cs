using Citus.Ui.Shared.Control;

namespace Citus.SysAdmin.Api.Auth;

public sealed class SysAdminSessionResponse
{
    public string SessionToken { get; set; } = string.Empty;

    public SysAdminAuthSessionSummary Session { get; set; } = new();
}
