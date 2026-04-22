namespace Citus.Platform.Core.Runtime;

public sealed record class PlatformAuditEvent
{
    public Guid AuditId { get; init; }

    public Guid? CompanyId { get; init; }

    public string CompanyCode { get; init; } = string.Empty;

    public string CompanyName { get; init; } = string.Empty;

    public string ScopeLabel { get; init; } = "Platform";

    public string ActorType { get; init; } = string.Empty;

    public Guid? ActorId { get; init; }

    public string ActorDisplayName { get; init; } = string.Empty;

    public string ActorEmail { get; init; } = string.Empty;

    public string EntityType { get; init; } = string.Empty;

    public Guid EntityId { get; init; }

    public string EntityLabel { get; init; } = string.Empty;

    public string Action { get; init; } = string.Empty;

    public string ActionLabel { get; init; } = string.Empty;

    public string Detail { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public IReadOnlyList<string> Highlights { get; init; } = Array.Empty<string>();

    public DateTimeOffset CreatedAtUtc { get; init; }

    public static string GetActionLabel(string action) =>
        action.Trim().ToLowerInvariant() switch
        {
            "company_status_changed" => "Company Status Changed",
            "account_status_changed" => "Account Status Changed",
            "account_totp_enrollment_started" => "Account TOTP Enrollment Started",
            "account_totp_enrollment_confirmed" => "Account TOTP Enrollment Confirmed",
            "account_mfa_recovery_requested" => "Account MFA Recovery Requested",
            "account_mfa_recovery_approved" => "Account MFA Recovery Approved",
            "account_mfa_recovery_rejected" => "Account MFA Recovery Rejected",
            "account_mfa_recovery_executed" => "Account MFA Recovery Executed",
            "profile_display_name_saved" => "Profile Display Name Saved",
            "email_change_requested" => "Email Change Requested",
            "email_change_dispatched" => "Email Change Delivered",
            "email_change_dispatch_failed" => "Email Change Delivery Failed",
            "email_change_confirmed" => "Email Change Confirmed",
            "password_change_requested" => "Password Change Requested",
            "password_change_dispatched" => "Password Change Delivered",
            "password_change_dispatch_failed" => "Password Change Delivery Failed",
            "password_change_confirmed" => "Password Change Confirmed",
            "password_reset_requested" => "Password Reset Requested",
            "password_reset_dispatched" => "Password Reset Delivered",
            "password_reset_dispatch_failed" => "Password Reset Delivery Failed",
            "account_mfa_reset" => "Account MFA Reset",
            "membership_role_changed" => "Membership Role Changed",
            "membership_permissions_saved" => "Membership Permissions Saved",
            "sysadmin_first_account_created" => "First SysAdmin Created",
            "sysadmin_password_rotated" => "SysAdmin Secret Rotated",
            _ => "Governance Event"
        };

    public static string BuildScopeLabel(string companyName, string companyCode)
    {
        var normalizedName = companyName.Trim();
        var normalizedCode = companyCode.Trim();

        if (string.IsNullOrWhiteSpace(normalizedName) && string.IsNullOrWhiteSpace(normalizedCode))
        {
            return "Platform";
        }

        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return normalizedCode;
        }

        if (string.IsNullOrWhiteSpace(normalizedCode))
        {
            return normalizedName;
        }

        return $"{normalizedName} ({normalizedCode})";
    }

    public static string BuildPermissionChangeDetail(
        IReadOnlyList<string> addedTokens,
        IReadOnlyList<string> removedTokens)
    {
        var additions = addedTokens.Count == 0
            ? string.Empty
            : $"+ {string.Join(", ", addedTokens)}";
        var removals = removedTokens.Count == 0
            ? string.Empty
            : $"- {string.Join(", ", removedTokens)}";

        if (string.IsNullOrWhiteSpace(additions) && string.IsNullOrWhiteSpace(removals))
        {
            return "Permission set was saved without net token changes.";
        }

        if (string.IsNullOrWhiteSpace(additions))
        {
            return removals;
        }

        if (string.IsNullOrWhiteSpace(removals))
        {
            return additions;
        }

        return $"{additions} | {removals}";
    }
}
