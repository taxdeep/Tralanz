namespace Citus.SysAdmin.Api.Control;

/// <summary>
/// PUT /control/operations/smtp body. NewPassword is the only secret —
/// the operator either types a fresh password (which the endpoint
/// encrypts) or leaves it null to preserve whatever was already stored.
/// ClearPassword=true is the explicit "remove the password" path.
/// </summary>
public sealed record SmtpConfigHttpRequest(
    string? Provider,
    string? FromEmail,
    string? FromDisplayName,
    string? Host,
    int? Port,
    bool? UseSsl,
    string? Username,
    string? NewPassword,
    bool? ClearPassword);

public sealed record SmtpTestSendHttpRequest(string ToEmail);
