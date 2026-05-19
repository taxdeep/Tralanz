namespace Citus.SysAdmin.Api.Control;

/// <summary>
/// PUT body for
/// <c>/control/companies/{companyId}/memberships/{membershipId}/permissions/preset</c>.
/// <see cref="PresetCode"/> must be one of the codes exposed by
/// <c>CompanyMembershipPermissionPresets.KnownPresets</c>.
/// <see cref="Replace"/> defaults to <c>false</c> so existing manual
/// additions are preserved on re-apply.
/// </summary>
public sealed record CompanyMembershipPresetApplyRequest(
    string PresetCode,
    bool Replace = false);
