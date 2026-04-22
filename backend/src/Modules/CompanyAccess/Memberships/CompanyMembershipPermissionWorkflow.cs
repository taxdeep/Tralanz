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

    public Task<IReadOnlyList<CompanyMembershipPermissionListItem>> ListAsync(
        Guid companyId,
        CancellationToken cancellationToken)
    {
        if (companyId == Guid.Empty)
        {
            throw new InvalidOperationException("Company context is required to list company membership permissions.");
        }

        return _store.ListAsync(companyId, cancellationToken);
    }

    public Task<IReadOnlyList<CompanyMembershipPermissionAuditRecord>> ListRecentAuditAsync(
        Guid companyId,
        int limit,
        CancellationToken cancellationToken)
    {
        if (companyId == Guid.Empty)
        {
            throw new InvalidOperationException("Company context is required to list company membership permission audit events.");
        }

        var safeLimit = Math.Clamp(limit, 1, 50);
        return _store.ListRecentAuditAsync(companyId, safeLimit, cancellationToken);
    }

    public async Task<CompanyMembershipPermissionSaveResult> SavePermissionsAsync(
        Guid companyId,
        Guid membershipId,
        Guid actorUserId,
        IReadOnlyList<string> permissionTokens,
        CancellationToken cancellationToken)
    {
        if (companyId == Guid.Empty)
        {
            throw new InvalidOperationException("Company context is required to save company membership permissions.");
        }

        if (membershipId == Guid.Empty)
        {
            throw new InvalidOperationException("Membership id is required to save company membership permissions.");
        }

        if (actorUserId == Guid.Empty)
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
}
