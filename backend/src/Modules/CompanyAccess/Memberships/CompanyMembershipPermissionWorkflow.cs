namespace Modules.CompanyAccess.Memberships;

public sealed class CompanyMembershipPermissionWorkflow : ICompanyMembershipPermissionWorkflow
{
    private readonly ICompanyMembershipPermissionStore _store;

    public CompanyMembershipPermissionWorkflow(ICompanyMembershipPermissionStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public IReadOnlyList<CompanyMembershipPermissionOption> GetAvailablePermissions() =>
        CompanyMembershipPermissionCatalog.Options;

    public IReadOnlyList<CompanyMembershipPermissionPresetOption> GetAvailablePresets() =>
        CompanyMembershipPermissionPresets.Options;

    public Task<IReadOnlyList<CompanyMembershipPermissionListItem>> ListAsync(
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        if (companyId.Value is null)
        {
            throw new InvalidOperationException("Company context is required to list company membership permissions.");
        }

        return _store.ListAsync(companyId, cancellationToken);
    }

    public Task<IReadOnlyList<CompanyMembershipPermissionAuditRecord>> ListRecentAuditAsync(
        CompanyId companyId,
        int limit,
        CancellationToken cancellationToken)
    {
        if (companyId.Value is null)
        {
            throw new InvalidOperationException("Company context is required to list company membership permission audit events.");
        }

        var safeLimit = Math.Clamp(limit, 1, 50);
        return _store.ListRecentAuditAsync(companyId, safeLimit, cancellationToken);
    }

    public async Task<CompanyMembershipPermissionSaveResult> SavePermissionsAsync(
        CompanyId companyId,
        Guid membershipId,
        UserId actorUserId,
        IReadOnlyList<string> permissionTokens,
        CancellationToken cancellationToken)
    {
        if (companyId.Value is null)
        {
            throw new InvalidOperationException("Company context is required to save company membership permissions.");
        }

        if (membershipId == Guid.Empty)
        {
            throw new InvalidOperationException("Membership id is required to save company membership permissions.");
        }

        if (actorUserId.Value is null)
        {
            throw new InvalidOperationException("An acting user is required to save company membership permissions.");
        }

        var actor = await _store.GetActorAuthorityAsync(companyId, actorUserId, cancellationToken);
        if (actor is null)
        {
            throw new InvalidOperationException("The acting user does not have an active membership in the active company.");
        }

        if (!actor.CanManageMembershipPermissions)
        {
            throw new InvalidOperationException("Only a company owner can manage company membership permissions.");
        }

        var target = await _store.GetAsync(companyId, membershipId, cancellationToken);
        if (target is null)
        {
            throw new InvalidOperationException("Company membership was not found in the active company context.");
        }

        if (!target.IsActive)
        {
            throw new InvalidOperationException("Inactive company memberships cannot receive permission changes.");
        }

        var normalizedTokens = CompanyMembershipPermissionCatalog.NormalizeTokens(permissionTokens);
        var saved = await _store.SavePermissionsAsync(
            companyId,
            membershipId,
            actorUserId,
            normalizedTokens,
            cancellationToken);

        if (saved is null)
        {
            throw new InvalidOperationException("Company membership was not found while saving permissions.");
        }

        return new CompanyMembershipPermissionSaveResult(
            saved,
            CompanyMembershipPermissionCatalog.Options,
            "permissions_saved",
            "Company membership permissions were saved from CompanyAccess truth.");
    }

    public Task<CompanyMembershipPermissionSaveResult> ApplyPresetAsync(
        CompanyId companyId,
        Guid membershipId,
        UserId actorUserId,
        string presetCode,
        bool replaceExistingTokens,
        CancellationToken cancellationToken) =>
        ApplyPresetCoreAsync(
            companyId,
            membershipId,
            presetCode,
            replaceExistingTokens,
            (tokens, ct) => SavePermissionsAsync(companyId, membershipId, actorUserId, tokens, ct),
            cancellationToken);

    public Task<CompanyMembershipPermissionSaveResult> ApplyPresetFromSysAdminAsync(
        CompanyId companyId,
        Guid membershipId,
        UserId? sysAdminAccountId,
        string presetCode,
        bool replaceExistingTokens,
        CancellationToken cancellationToken) =>
        ApplyPresetCoreAsync(
            companyId,
            membershipId,
            presetCode,
            replaceExistingTokens,
            async (tokens, ct) =>
            {
                var normalizedTokens = CompanyMembershipPermissionCatalog.NormalizeTokens(tokens);
                var saved = await _store.SavePermissionsFromSysAdminAsync(
                    companyId,
                    membershipId,
                    sysAdminAccountId,
                    normalizedTokens,
                    ct);

                if (saved is null)
                {
                    throw new InvalidOperationException("Company membership was not found while applying the preset.");
                }

                return new CompanyMembershipPermissionSaveResult(
                    saved,
                    CompanyMembershipPermissionCatalog.Options,
                    "permissions_preset_applied",
                    $"Permission preset '{presetCode}' applied by SysAdmin governance.");
            },
            cancellationToken);

    private async Task<CompanyMembershipPermissionSaveResult> ApplyPresetCoreAsync(
        CompanyId companyId,
        Guid membershipId,
        string presetCode,
        bool replaceExistingTokens,
        Func<IReadOnlyList<string>, CancellationToken, Task<CompanyMembershipPermissionSaveResult>> persist,
        CancellationToken cancellationToken)
    {
        if (companyId.Value is null)
        {
            throw new InvalidOperationException("Company context is required to apply a permission preset.");
        }

        if (!CompanyMembershipPermissionPresets.IsKnown(presetCode))
        {
            throw new InvalidOperationException($"Unknown permission preset '{presetCode}'.");
        }

        var presetTokens = CompanyMembershipPermissionPresets.Expand(presetCode);

        IReadOnlyList<string> targetTokens;
        if (replaceExistingTokens)
        {
            targetTokens = presetTokens;
        }
        else
        {
            var existing = await _store.GetAsync(companyId, membershipId, cancellationToken)
                ?? throw new InvalidOperationException("Company membership was not found in the active company context.");
            targetTokens = presetTokens
                .Concat(existing.PermissionTokens)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static t => t, StringComparer.Ordinal)
                .ToArray();
        }

        return await persist(targetTokens, cancellationToken);
    }
}
