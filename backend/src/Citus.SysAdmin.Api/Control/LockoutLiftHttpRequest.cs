namespace Citus.SysAdmin.Api.Control;

public sealed record LockoutLiftHttpRequest(string? Reason);
