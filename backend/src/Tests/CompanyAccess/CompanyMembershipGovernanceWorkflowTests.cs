using Modules.CompanyAccess.Memberships;

namespace Tests.CompanyAccess;

public sealed class CompanyMembershipGovernanceWorkflowTests
{
    private static readonly Guid CompanyId = Guid.NewGuid();
    private static readonly Guid MembershipId = Guid.NewGuid();
    private static readonly Guid AccountId = Guid.NewGuid();
    private static readonly Guid SysAdminAccountId = Guid.NewGuid();

    [Fact]
    public async Task ChangeRoleFromSysAdminAsync_NormalizesRoleAndDelegatesToCompanyAccessStore()
    {
        var store = new StubCompanyMembershipGovernanceStore(CreateResult("user", "owner"));
        var workflow = new CompanyMembershipGovernanceWorkflow(store);

        var result = await workflow.ChangeRoleFromSysAdminAsync(
            CompanyId,
            MembershipId,
            " Owner ",
            "Promote controller to owner.",
            SysAdminAccountId,
            CancellationToken.None);

        Assert.Equal("owner", result.Role);
        Assert.Equal("owner", store.SavedRole);
        Assert.Equal("Promote controller to owner.", store.SavedReason);
        Assert.Equal(SysAdminAccountId, store.SavedSysAdminAccountId);
    }

    [Fact]
    public async Task ChangeRoleFromSysAdminAsync_RejectsUnsupportedRole()
    {
        var store = new StubCompanyMembershipGovernanceStore(CreateResult("user", "owner"));
        var workflow = new CompanyMembershipGovernanceWorkflow(store);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => workflow.ChangeRoleFromSysAdminAsync(
                CompanyId,
                MembershipId,
                "super_owner",
                "Invalid role.",
                SysAdminAccountId,
                CancellationToken.None));

        Assert.Equal("Unsupported company membership role 'super_owner'.", ex.Message);
    }

    [Fact]
    public async Task ChangeRoleFromSysAdminAsync_RejectsUnknownMembership()
    {
        var store = new StubCompanyMembershipGovernanceStore(result: null);
        var workflow = new CompanyMembershipGovernanceWorkflow(store);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => workflow.ChangeRoleFromSysAdminAsync(
                CompanyId,
                MembershipId,
                "owner",
                "Promote missing membership.",
                SysAdminAccountId,
                CancellationToken.None));

        Assert.Equal("Company membership was not found in the target company context.", ex.Message);
    }

    private static CompanyMembershipRoleChangeResult CreateResult(string previousRole, string role) =>
        new()
        {
            CompanyId = CompanyId,
            MembershipId = MembershipId,
            AccountId = AccountId,
            Email = "member@example.test",
            Username = "member",
            PreviousRole = previousRole,
            Role = role,
            Reason = "Role changed.",
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

    private sealed class StubCompanyMembershipGovernanceStore(
        CompanyMembershipRoleChangeResult? result) : ICompanyMembershipGovernanceStore
    {
        public string SavedRole { get; private set; } = string.Empty;

        public string SavedReason { get; private set; } = string.Empty;

        public Guid? SavedSysAdminAccountId { get; private set; }

        public Task<CompanyMembershipRoleChangeResult?> ChangeRoleFromSysAdminAsync(
            Guid companyId,
            Guid membershipId,
            string role,
            string reason,
            Guid? sysAdminAccountId,
            CancellationToken cancellationToken)
        {
            SavedRole = role;
            SavedReason = reason;
            SavedSysAdminAccountId = sysAdminAccountId;

            return Task.FromResult(result is null
                ? null
                : result with
                {
                    Role = role,
                    Reason = reason
                });
        }
    }
}
