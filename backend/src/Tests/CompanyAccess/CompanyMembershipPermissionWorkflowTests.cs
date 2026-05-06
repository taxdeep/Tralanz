using Modules.CompanyAccess.Memberships;

namespace Tests.CompanyAccess;

public sealed class CompanyMembershipPermissionWorkflowTests
{
    private static readonly CompanyId CompanyId = CompanyId.FromOrdinal(101);
    private static readonly UserId OwnerUserId = UserId.FromOrdinal(101);
    private static readonly UserId UserId = UserId.FromOrdinal(101);
    private static readonly Guid MembershipId = Guid.NewGuid();

    [Fact]
    public async Task SavePermissionsAsync_SavesNormalizedTokens_WhenActorIsOwner()
    {
        var store = new StubCompanyMembershipPermissionStore(
            actorRole: "owner",
            target: CreateMembership(["reports"]));
        var workflow = new CompanyMembershipPermissionWorkflow(store);

        var result = await workflow.SavePermissionsAsync(
            CompanyId,
            MembershipId,
            OwnerUserId,
            ["Company_Book_Governance", "ap", "ap"],
            CancellationToken.None);

        Assert.Equal("permissions_saved", result.OutcomeCode);
        Assert.Equal(["ap", "company_book_governance"], result.Membership.PermissionTokens);
        Assert.Equal(["ap", "company_book_governance"], store.SavedTokens);
    }

    [Fact]
    public async Task SavePermissionsAsync_RejectsNonOwnerActor()
    {
        var store = new StubCompanyMembershipPermissionStore(
            actorRole: "user",
            target: CreateMembership(["reports"]));
        var workflow = new CompanyMembershipPermissionWorkflow(store);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => workflow.SavePermissionsAsync(
                CompanyId,
                MembershipId,
                UserId,
                ["ap"],
                CancellationToken.None));

        Assert.Equal("Only a company owner can manage company membership permissions.", ex.Message);
    }

    [Fact]
    public async Task SavePermissionsAsync_RejectsUnknownPermissionToken()
    {
        var store = new StubCompanyMembershipPermissionStore(
            actorRole: "owner",
            target: CreateMembership(["reports"]));
        var workflow = new CompanyMembershipPermissionWorkflow(store);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => workflow.SavePermissionsAsync(
                CompanyId,
                MembershipId,
                OwnerUserId,
                ["launch_missiles"],
                CancellationToken.None));

        Assert.Equal("Unknown company membership permission token 'launch_missiles'.", ex.Message);
    }

    private static CompanyMembershipPermissionListItem CreateMembership(IReadOnlyList<string> permissionTokens) =>
        new()
        {
            MembershipId = MembershipId,
            CompanyId = CompanyId,
            UserId = UserId,
            Email = "member@example.test",
            Username = "member",
            DisplayName = "Member",
            Role = "user",
            PermissionTokens = permissionTokens,
            IsActive = true,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    private sealed class StubCompanyMembershipPermissionStore(
        string actorRole,
        CompanyMembershipPermissionListItem? target) : ICompanyMembershipPermissionStore
    {
        public IReadOnlyList<string> SavedTokens { get; private set; } = Array.Empty<string>();

        public Task<IReadOnlyList<CompanyMembershipPermissionListItem>> ListAsync(
            CompanyId companyId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CompanyMembershipPermissionListItem>>(
                target is null ? Array.Empty<CompanyMembershipPermissionListItem>() : [target]);

        public Task<IReadOnlyList<CompanyMembershipPermissionAuditRecord>> ListRecentAuditAsync(
            CompanyId companyId,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CompanyMembershipPermissionAuditRecord>>(Array.Empty<CompanyMembershipPermissionAuditRecord>());

        public Task<CompanyMembershipPermissionListItem?> GetAsync(
            CompanyId companyId,
            Guid membershipId,
            CancellationToken cancellationToken) =>
            Task.FromResult(target);

        public Task<CompanyMembershipPermissionActorAuthority?> GetActorAuthorityAsync(
            CompanyId companyId,
            UserId actorUserId,
            CancellationToken cancellationToken) =>
            Task.FromResult<CompanyMembershipPermissionActorAuthority?>(
                new CompanyMembershipPermissionActorAuthority(
                    companyId,
                    actorUserId,
                    actorRole,
                    Array.Empty<string>()));

        public Task<CompanyMembershipPermissionListItem?> SavePermissionsAsync(
            CompanyId companyId,
            Guid membershipId,
            UserId actorUserId,
            IReadOnlyList<string> permissionTokens,
            CancellationToken cancellationToken)
        {
            SavedTokens = permissionTokens;
            return Task.FromResult<CompanyMembershipPermissionListItem?>(
                target is null
                    ? null
                    : target with
                    {
                        PermissionTokens = permissionTokens,
                        UpdatedAt = DateTimeOffset.UtcNow
                    });
        }
    }
}
