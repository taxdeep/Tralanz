namespace Modules.CompanyAccess.Memberships;

public sealed class CompanyMembershipGovernanceWorkflow(
    ICompanyMembershipGovernanceStore store) : ICompanyMembershipGovernanceWorkflow
{
    private static readonly string[] AllowedRoles = ["owner", "user"];

    public async Task<CompanyMembershipRoleChangeResult> ChangeRoleFromSysAdminAsync(
        CompanyId companyId,
        Guid membershipId,
        string role,
        string reason,
        Guid? sysAdminAccountId,
        CancellationToken cancellationToken)
    {
        if (companyId.Value is null)
        {
            throw new InvalidOperationException("Company context is required to change company membership role.");
        }

        if (membershipId == Guid.Empty)
        {
            throw new InvalidOperationException("Membership id is required to change company membership role.");
        }

        var normalizedRole = NormalizeRole(role);
        var normalizedReason = string.IsNullOrWhiteSpace(reason)
            ? "Company membership role changed by SysAdmin governance."
            : reason.Trim();

        var result = await store.ChangeRoleFromSysAdminAsync(
            companyId,
            membershipId,
            normalizedRole,
            normalizedReason,
            sysAdminAccountId,
            cancellationToken);

        return result ??
            throw new InvalidOperationException("Company membership was not found in the target company context.");
    }

    private static string NormalizeRole(string role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            throw new InvalidOperationException("Company membership role is required.");
        }

        var normalized = role.Trim().ToLowerInvariant();
        if (!AllowedRoles.Contains(normalized, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported company membership role '{normalized}'.");
        }

        return normalized;
    }
}
