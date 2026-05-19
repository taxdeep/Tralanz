using Modules.CompanyAccess.Memberships;
using SharedKernel.Identity;

namespace Citus.SysAdmin.Api.Tests;

public class CompanyMembershipPermissionWorkflowApplyPresetTests
{
    private static readonly CompanyId CompanyA = CompanyId.FromOrdinal(1);
    private static readonly UserId Actor = UserId.Parse("U000001");
    private static readonly Guid MembershipId = Guid.NewGuid();

    [Fact]
    public async Task ApplyPresetFromSysAdmin_writes_full_owner_token_set_when_replacing()
    {
        var store = new FakeStore(
            existing: ItemWith(new[] { "ar" }));
        var workflow = new CompanyMembershipPermissionWorkflow(store);

        var result = await workflow.ApplyPresetFromSysAdminAsync(
            CompanyA,
            MembershipId,
            sysAdminAccountId: null,
            CompanyMembershipPermissionPresets.Owner,
            replaceExistingTokens: true,
            CancellationToken.None);

        Assert.Equal("permissions_preset_applied", result.OutcomeCode);
        Assert.Contains("settings.permissions.assign", store.LastSysAdminSavedTokens);
        Assert.Equal("sysadmin", store.LastSaveMode);
    }

    [Fact]
    public async Task ApplyPresetFromSysAdmin_unions_with_existing_when_not_replacing()
    {
        var store = new FakeStore(
            existing: ItemWith(new[] { "task.archive.read" }));
        var workflow = new CompanyMembershipPermissionWorkflow(store);

        await workflow.ApplyPresetFromSysAdminAsync(
            CompanyA,
            MembershipId,
            Actor,
            CompanyMembershipPermissionPresets.TaskOnly,
            replaceExistingTokens: false,
            CancellationToken.None);

        // Manual addition preserved
        Assert.Contains("task.archive.read", store.LastSysAdminSavedTokens);
        // Preset tokens added
        Assert.Contains("task.view", store.LastSysAdminSavedTokens);
        Assert.Contains("task.complete", store.LastSysAdminSavedTokens);
    }

    [Fact]
    public async Task ApplyPresetFromSysAdmin_rejects_unknown_preset()
    {
        var workflow = new CompanyMembershipPermissionWorkflow(new FakeStore(existing: ItemWith(Array.Empty<string>())));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.ApplyPresetFromSysAdminAsync(
                CompanyA,
                MembershipId,
                Actor,
                "preset.unknown",
                replaceExistingTokens: true,
                CancellationToken.None));
    }

    [Fact]
    public async Task ApplyPresetFromSysAdmin_passes_normalized_tokens_to_store()
    {
        var store = new FakeStore(existing: ItemWith(Array.Empty<string>()));
        var workflow = new CompanyMembershipPermissionWorkflow(store);

        await workflow.ApplyPresetFromSysAdminAsync(
            CompanyA,
            MembershipId,
            Actor,
            CompanyMembershipPermissionPresets.Viewer,
            replaceExistingTokens: true,
            CancellationToken.None);

        // Normalized = sorted ordinally, deduped
        var saved = store.LastSysAdminSavedTokens;
        var sorted = saved.OrderBy(static t => t, StringComparer.Ordinal).ToArray();
        Assert.Equal(sorted, saved);
    }

    private static CompanyMembershipPermissionListItem ItemWith(IReadOnlyList<string> tokens) =>
        new()
        {
            MembershipId = MembershipId,
            CompanyId = CompanyA,
            UserId = UserId.Parse("U000002"),
            Email = "target@example.com",
            Username = "target",
            DisplayName = "Target",
            Role = "user",
            PermissionTokens = tokens,
            IsActive = true,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    private sealed class FakeStore : ICompanyMembershipPermissionStore
    {
        private readonly CompanyMembershipPermissionListItem _existing;

        public FakeStore(CompanyMembershipPermissionListItem existing)
        {
            _existing = existing;
        }

        public IReadOnlyList<string> LastSysAdminSavedTokens { get; private set; } = Array.Empty<string>();

        public string LastSaveMode { get; private set; } = string.Empty;

        public Task EnsureSchemaAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<CompanyMembershipPermissionListItem>> ListAsync(
            CompanyId companyId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CompanyMembershipPermissionListItem>>([_existing]);

        public Task<IReadOnlyList<CompanyMembershipPermissionAuditRecord>> ListRecentAuditAsync(
            CompanyId companyId, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CompanyMembershipPermissionAuditRecord>>(Array.Empty<CompanyMembershipPermissionAuditRecord>());

        public Task<CompanyMembershipPermissionListItem?> GetAsync(
            CompanyId companyId, Guid membershipId, CancellationToken cancellationToken) =>
            Task.FromResult<CompanyMembershipPermissionListItem?>(_existing);

        public Task<CompanyMembershipPermissionActorAuthority?> GetActorAuthorityAsync(
            CompanyId companyId, UserId actorUserId, CancellationToken cancellationToken) =>
            // Not used by the SysAdmin path — return null to fail loud
            // if any test accidentally hits the business pathway.
            Task.FromResult<CompanyMembershipPermissionActorAuthority?>(null);

        public Task<CompanyMembershipPermissionListItem?> SavePermissionsAsync(
            CompanyId companyId, Guid membershipId, UserId actorUserId,
            IReadOnlyList<string> permissionTokens, CancellationToken cancellationToken)
        {
            LastSaveMode = "user";
            LastSysAdminSavedTokens = permissionTokens;
            return Task.FromResult<CompanyMembershipPermissionListItem?>(_existing with
            {
                PermissionTokens = permissionTokens,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        public Task<CompanyMembershipPermissionListItem?> SavePermissionsFromSysAdminAsync(
            CompanyId companyId, Guid membershipId, UserId? sysAdminAccountId,
            IReadOnlyList<string> permissionTokens, CancellationToken cancellationToken)
        {
            LastSaveMode = "sysadmin";
            LastSysAdminSavedTokens = permissionTokens;
            return Task.FromResult<CompanyMembershipPermissionListItem?>(_existing with
            {
                PermissionTokens = permissionTokens,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
    }
}
