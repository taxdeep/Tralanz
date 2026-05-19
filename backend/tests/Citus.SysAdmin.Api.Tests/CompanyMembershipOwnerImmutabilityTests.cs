using Modules.CompanyAccess.Memberships;
using SharedKernel.Identity;

namespace Citus.SysAdmin.Api.Tests;

/// <summary>
/// Batch 3.5: workflow-layer owner immutability + transfer governance
/// tests. The Postgres-level guard (PersistPermissionsAsync rejecting
/// when target is_owner=true) is verified separately by integration
/// tests against a real database; here we cover the workflow contract
/// the SysAdmin endpoint flows through.
/// </summary>
public class CompanyMembershipOwnerImmutabilityTests
{
    private static readonly CompanyId CompanyA = CompanyId.FromOrdinal(1);
    private static readonly Guid MembershipId = Guid.NewGuid();
    private static readonly UserId Actor = UserId.Parse("U000001");

    [Fact]
    public async Task ApplyPresetFromSysAdmin_passes_through_to_store_when_target_is_not_owner()
    {
        var store = new TransparentStore(target: MembershipFor(IsOwner: false));
        var workflow = new CompanyMembershipPermissionWorkflow(store);

        await workflow.ApplyPresetFromSysAdminAsync(
            CompanyA,
            MembershipId,
            sysAdminAccountId: Actor,
            CompanyMembershipPermissionPresets.Viewer,
            replaceExistingTokens: true,
            CancellationToken.None);

        Assert.True(store.SysAdminSaveCalled);
    }

    [Fact]
    public async Task TransferOwnership_workflow_rejects_blank_or_equal_ids()
    {
        var workflow = new CompanyMembershipGovernanceWorkflow(new RecordingTransferStore());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.TransferOwnershipFromSysAdminAsync(
                CompanyA, Guid.Empty, Guid.NewGuid(), "transfer", Actor, CancellationToken.None));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.TransferOwnershipFromSysAdminAsync(
                CompanyA, Guid.NewGuid(), Guid.Empty, "transfer", Actor, CancellationToken.None));

        var same = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.TransferOwnershipFromSysAdminAsync(
                CompanyA, same, same, "transfer", Actor, CancellationToken.None));
    }

    [Fact]
    public async Task TransferOwnership_workflow_passes_preset_owner_expansion_to_store()
    {
        var store = new RecordingTransferStore();
        var workflow = new CompanyMembershipGovernanceWorkflow(store);

        await workflow.TransferOwnershipFromSysAdminAsync(
            CompanyA,
            fromMembershipId: Guid.NewGuid(),
            toMembershipId: Guid.NewGuid(),
            reason: "founder departing",
            Actor,
            CancellationToken.None);

        Assert.NotNull(store.LastNewOwnerPermissions);
        // The new owner gets the full owner preset.
        var expected = CompanyMembershipPermissionPresets.Expand(CompanyMembershipPermissionPresets.Owner);
        Assert.Equal(expected, store.LastNewOwnerPermissions);
    }

    [Fact]
    public async Task TransferOwnership_workflow_normalizes_blank_reason()
    {
        var store = new RecordingTransferStore();
        var workflow = new CompanyMembershipGovernanceWorkflow(store);

        await workflow.TransferOwnershipFromSysAdminAsync(
            CompanyA,
            Guid.NewGuid(),
            Guid.NewGuid(),
            reason: "   ",
            Actor,
            CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(store.LastReason));
    }

    [Fact]
    public async Task TransferOwnership_workflow_propagates_null_store_result_as_error()
    {
        var store = new RecordingTransferStore { NextResult = null };
        var workflow = new CompanyMembershipGovernanceWorkflow(store);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.TransferOwnershipFromSysAdminAsync(
                CompanyA, Guid.NewGuid(), Guid.NewGuid(), "transfer", Actor, CancellationToken.None));
    }

    private static CompanyMembershipPermissionListItem MembershipFor(bool IsOwner) =>
        new()
        {
            MembershipId = MembershipId,
            CompanyId = CompanyA,
            UserId = UserId.Parse("U000002"),
            Email = "x@x",
            Username = "x",
            DisplayName = "x",
            Role = IsOwner ? "owner" : "user",
            PermissionTokens = Array.Empty<string>(),
            IsActive = true,
            IsOwner = IsOwner,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    private sealed class TransparentStore(CompanyMembershipPermissionListItem target) : ICompanyMembershipPermissionStore
    {
        public bool SysAdminSaveCalled { get; private set; }

        public Task EnsureSchemaAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<CompanyMembershipPermissionListItem>> ListAsync(
            CompanyId companyId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CompanyMembershipPermissionListItem>>([target]);

        public Task<IReadOnlyList<CompanyMembershipPermissionAuditRecord>> ListRecentAuditAsync(
            CompanyId companyId, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CompanyMembershipPermissionAuditRecord>>(Array.Empty<CompanyMembershipPermissionAuditRecord>());

        public Task<CompanyMembershipPermissionListItem?> GetAsync(
            CompanyId companyId, Guid membershipId, CancellationToken ct) =>
            Task.FromResult<CompanyMembershipPermissionListItem?>(target);

        public Task<CompanyMembershipPermissionActorAuthority?> GetActorAuthorityAsync(
            CompanyId companyId, UserId actorUserId, CancellationToken ct) =>
            Task.FromResult<CompanyMembershipPermissionActorAuthority?>(null);

        public Task<CompanyMembershipPermissionListItem?> SavePermissionsAsync(
            CompanyId companyId, Guid membershipId, UserId actorUserId,
            IReadOnlyList<string> permissionTokens, CancellationToken ct) =>
            Task.FromResult<CompanyMembershipPermissionListItem?>(target with { PermissionTokens = permissionTokens });

        public Task<CompanyMembershipPermissionListItem?> SavePermissionsFromSysAdminAsync(
            CompanyId companyId, Guid membershipId, UserId? sysAdminAccountId,
            IReadOnlyList<string> permissionTokens, CancellationToken ct)
        {
            SysAdminSaveCalled = true;
            return Task.FromResult<CompanyMembershipPermissionListItem?>(target with { PermissionTokens = permissionTokens });
        }
    }

    private sealed class RecordingTransferStore : ICompanyMembershipGovernanceStore
    {
        public CompanyMembershipOwnershipTransferResult? NextResult { get; set; } = new()
        {
            CompanyId = CompanyA,
            FromMembershipId = Guid.NewGuid(),
            FromUserId = UserId.Parse("U000002"),
            ToMembershipId = Guid.NewGuid(),
            ToUserId = UserId.Parse("U000003"),
            Reason = "set-by-store",
            TransferredAtUtc = DateTimeOffset.UtcNow,
        };

        public string LastReason { get; private set; } = string.Empty;

        public IReadOnlyList<string>? LastNewOwnerPermissions { get; private set; }

        public Task<CompanyMembershipRoleChangeResult?> ChangeRoleFromSysAdminAsync(
            CompanyId companyId, Guid membershipId, string role, string reason,
            UserId? sysAdminAccountId, CancellationToken ct) =>
            Task.FromResult<CompanyMembershipRoleChangeResult?>(null);

        public Task<CompanyMembershipOwnershipTransferResult?> TransferOwnershipFromSysAdminAsync(
            CompanyId companyId, Guid fromMembershipId, Guid toMembershipId,
            string reason, UserId? sysAdminAccountId,
            IReadOnlyList<string> newOwnerPermissions, CancellationToken ct)
        {
            LastReason = reason;
            LastNewOwnerPermissions = newOwnerPermissions;
            return Task.FromResult(NextResult);
        }
    }
}
