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
        UserId? sysAdminAccountId,
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

    public async Task<CompanyMembershipOwnershipTransferResult> TransferOwnershipFromSysAdminAsync(
        CompanyId companyId,
        Guid fromMembershipId,
        Guid toMembershipId,
        string reason,
        UserId? sysAdminAccountId,
        CancellationToken cancellationToken)
    {
        if (companyId.Value is null)
        {
            throw new InvalidOperationException("Company context is required to transfer ownership.");
        }

        if (fromMembershipId == Guid.Empty || toMembershipId == Guid.Empty)
        {
            throw new InvalidOperationException("Both source and target membership ids are required.");
        }

        if (fromMembershipId == toMembershipId)
        {
            throw new InvalidOperationException("Ownership transfer requires distinct source and target memberships.");
        }

        var normalizedReason = string.IsNullOrWhiteSpace(reason)
            ? "Company ownership transferred by SysAdmin governance."
            : reason.Trim();

        var ownerTokens = CompanyMembershipPermissionPresets.Expand(CompanyMembershipPermissionPresets.Owner);

        var result = await store.TransferOwnershipFromSysAdminAsync(
            companyId,
            fromMembershipId,
            toMembershipId,
            normalizedReason,
            sysAdminAccountId,
            ownerTokens,
            cancellationToken);

        return result ??
            throw new InvalidOperationException("Ownership transfer failed: one or both memberships were not found.");
    }

    public async Task<CompanyMembershipOwnershipTransferResult> TransferOwnershipFromOwnerAsync(
        CompanyId companyId,
        UserId currentOwnerUserId,
        UserId targetUserId,
        string reason,
        CancellationToken cancellationToken)
    {
        if (companyId.Value is null)
        {
            throw new InvalidOperationException("Company context is required to transfer ownership.");
        }

        if (string.IsNullOrEmpty(currentOwnerUserId.Value))
        {
            throw new InvalidOperationException("Current owner identity is required.");
        }

        if (string.IsNullOrEmpty(targetUserId.Value))
        {
            throw new InvalidOperationException("Target user is required.");
        }

        // Self-transfer is a no-op and confusing in the audit log.
        // Reject loudly rather than silently succeed.
        if (string.Equals(currentOwnerUserId.Value, targetUserId.Value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Cannot transfer ownership to yourself.");
        }

        var normalizedReason = string.IsNullOrWhiteSpace(reason)
            ? "Company ownership transferred by current owner."
            : reason.Trim();

        // Mirror the SysAdmin path so legacy code reading the new
        // owner's permissions jsonb sees the full Owner preset. The
        // new authorization model (is_owner=true → implied-all) does
        // not require it, but pre-PR-4C call sites still read the
        // jsonb and we keep them consistent until they migrate.
        var ownerTokens = CompanyMembershipPermissionPresets.Expand(CompanyMembershipPermissionPresets.Owner);

        var result = await store.TransferOwnershipFromOwnerAsync(
            companyId,
            currentOwnerUserId,
            targetUserId,
            normalizedReason,
            ownerTokens,
            cancellationToken);

        return result ??
            throw new InvalidOperationException(
                "Ownership transfer failed: caller or target is not an active member of this company.");
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
