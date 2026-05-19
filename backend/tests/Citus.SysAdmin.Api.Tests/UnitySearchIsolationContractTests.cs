using Citus.Modules.UnitySearch.Application.Contracts;
using Citus.Modules.UnitySearch.Domain.Shared;
using Modules.Company.FeatureManagement;
using SharedKernel.Identity;

namespace Citus.SysAdmin.Api.Tests;

/// <summary>
/// Batch-2 isolation contract for UnitySearch. The query SQL hard-codes
/// the names + semantics of the four new columns (module_key,
/// required_permissions, owner_user_id, visibility_scope) and the
/// toggleable-module-keys parameter; these tests pin the C# shape so a
/// drift between record and SQL trips a unit test before a runtime
/// query failure.
/// </summary>
public class UnitySearchIsolationContractTests
{
    private static readonly CompanyId CompanyA = CompanyId.FromOrdinal(1);

    [Fact]
    public void SearchDocumentRecord_defaults_to_company_visibility_and_core_module()
    {
        var record = new SearchDocumentRecord(
            CompanyA,
            EntityType: "customer",
            SourceId: Guid.NewGuid(),
            GroupKey: "contacts",
            PrimaryText: "Acme",
            SecondaryText: string.Empty,
            SearchText: "Acme",
            ExactCodeNorm: "acme",
            NavigationHref: "/",
            MetadataJson: "{}",
            EffectiveDate: null,
            Amount: null,
            IsActive: true,
            IsVoided: false,
            RankBoost: 0m,
            Version: 1L);

        Assert.Equal("core", record.ModuleKey);
        Assert.Equal("company", record.VisibilityScope);
        Assert.Null(record.OwnerUserId);
        // Default is null (record optional default); seeder / consumer
        // should coalesce null to an empty array.
        Assert.Null(record.RequiredPermissions);
    }

    [Fact]
    public void SearchDocumentRecord_allows_assignee_only_scope_with_owner()
    {
        var owner = UserId.Parse("U000001");
        var record = new SearchDocumentRecord(
            CompanyA,
            EntityType: "task",
            SourceId: Guid.NewGuid(),
            GroupKey: "transactions",
            PrimaryText: "TSK-1",
            SecondaryText: string.Empty,
            SearchText: "TSK-1",
            ExactCodeNorm: "tsk-1",
            NavigationHref: "/",
            MetadataJson: "{}",
            EffectiveDate: null,
            Amount: null,
            IsActive: true,
            IsVoided: false,
            RankBoost: 0m,
            Version: 1L,
            ComputedScore: 0m,
            ModuleKey: CompanyModuleFlagCatalog.Task,
            RequiredPermissions: new[] { "task.view" },
            OwnerUserId: owner,
            VisibilityScope: "assignee_only");

        Assert.Equal(CompanyModuleFlagCatalog.Task, record.ModuleKey);
        Assert.Equal("assignee_only", record.VisibilityScope);
        Assert.Equal(owner, record.OwnerUserId);
        Assert.NotNull(record.RequiredPermissions);
        Assert.Equal(new[] { "task.view" }, record.RequiredPermissions);
    }

    [Fact]
    public void UnitySearchQuery_permissions_default_is_empty()
    {
        var query = new UnitySearchQuery
        {
            CompanyId = CompanyA,
            Context = "global.topbar",
        };

        Assert.NotNull(query.Permissions);
        Assert.Empty(query.Permissions);
    }

    [Fact]
    public void UnitySearchQuery_carries_permission_list_verbatim()
    {
        var query = new UnitySearchQuery
        {
            CompanyId = CompanyA,
            Context = "global.topbar",
            Permissions = new[] { "ar", "ap", "reports" },
        };

        Assert.Equal(new[] { "ar", "ap", "reports" }, query.Permissions);
    }

    [Fact]
    public void CompanyModuleFlagCatalog_drives_toggleable_module_keys_in_sql()
    {
        // The SQL gate uses CompanyModuleFlagCatalog.KnownKeys verbatim
        // as the @toggleable_module_keys parameter. If the catalog
        // changes shape this test exposes the wire-contract drift
        // before a runtime query failure.
        Assert.Contains("task", CompanyModuleFlagCatalog.KnownKeys);
        Assert.DoesNotContain("ar", CompanyModuleFlagCatalog.KnownKeys);
        Assert.DoesNotContain("ap", CompanyModuleFlagCatalog.KnownKeys);
        Assert.DoesNotContain("gl", CompanyModuleFlagCatalog.KnownKeys);
        Assert.DoesNotContain("inventory", CompanyModuleFlagCatalog.KnownKeys);
        Assert.DoesNotContain("core", CompanyModuleFlagCatalog.KnownKeys);
    }
}
