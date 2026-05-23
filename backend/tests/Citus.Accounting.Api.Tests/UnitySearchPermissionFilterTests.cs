using Citus.Modules.UnitySearch.Application.Contracts;
using Citus.Modules.UnitySearch.Domain.Shared;

namespace Citus.Accounting.Api.Tests;

public sealed class UnitySearchPermissionFilterTests
{
    [Fact]
    public void Filter_HidesSearchResultsOutsideSessionRoles()
    {
        var result = new UnitySearchResult
        {
            Context = SearchScopeContext.GlobalTopbar,
            Groups =
            [
                new UnitySearchGroupResult
                {
                    GroupKey = SearchGroupKey.Transactions,
                    Items =
                    [
                        BuildSuggestion(SearchDocumentType.Invoice, "/documents/invoice/1"),
                        BuildSuggestion(SearchDocumentType.Bill, "/documents/bill/1"),
                        BuildSuggestion(SearchDocumentType.Report, "/ap/aging")
                    ]
                }
            ],
            RecentSelections =
            [
                BuildRecentSelection(SearchDocumentType.Customer, "/customers/1"),
                BuildRecentSelection(SearchDocumentType.Vendor, "/vendors/1")
            ],
            TotalCount = 3
        };

        var filtered = UnitySearchPermissionFilter.Filter(result, CreateSession("ar"));

        var items = Assert.Single(filtered.Groups).Items;
        var item = Assert.Single(items);
        Assert.Equal(SearchDocumentType.Invoice, item.EntityType);
        Assert.Equal(1, filtered.TotalCount);

        var recent = Assert.Single(filtered.RecentSelections);
        Assert.Equal(SearchDocumentType.Customer, recent.EntityType);
    }

    [Fact]
    public void Filter_AllowsOwnerToSeeAllResults()
    {
        var result = new UnitySearchResult
        {
            Context = SearchScopeContext.GlobalTopbar,
            Groups =
            [
                new UnitySearchGroupResult
                {
                    GroupKey = SearchGroupKey.Transactions,
                    Items =
                    [
                        BuildSuggestion(SearchDocumentType.Invoice, "/documents/invoice/1"),
                        BuildSuggestion(SearchDocumentType.Bill, "/documents/bill/1"),
                        BuildSuggestion(SearchDocumentType.JournalEntry, "/gl/journal-entries/1")
                    ]
                }
            ],
            TotalCount = 3
        };

        var filtered = UnitySearchPermissionFilter.Filter(result, CreateSession("owner"));

        Assert.Equal(3, Assert.Single(filtered.Groups).Items.Count);
        Assert.Equal(3, filtered.TotalCount);
    }

    [Theory]
    [InlineData(SearchDocumentType.Invoice, "/documents/invoice/1", "ar", true)]
    [InlineData(SearchDocumentType.Invoice, "/documents/invoice/1", "ap", false)]
    [InlineData(SearchDocumentType.Bill, "/documents/bill/1", "ap", true)]
    [InlineData(SearchDocumentType.Bill, "/documents/bill/1", "ar", false)]
    [InlineData(SearchDocumentType.Report, "/ar/aging", "reports", true)]
    [InlineData(SearchDocumentType.JournalEntry, "/gl/journal-entries/1", "reports", false)]
    [InlineData(SearchDocumentType.JumpTo, "/ap/pay-bill", "ap", true)]
    [InlineData(SearchDocumentType.JumpTo, "/ap/pay-bill", "ar", false)]
    public void CanAccess_EnforcesModuleBoundary(
        string entityType,
        string href,
        string role,
        bool expected)
    {
        Assert.Equal(expected, UnitySearchPermissionFilter.CanAccess(entityType, href, CreateSession(role)));
    }

    private static BusinessSessionContext CreateSession(params string[] roles) =>
        new()
        {
            UserId = UserId.FromOrdinal(1),
            ActiveCompanyId = CompanyId.FromOrdinal(1),
            Roles = roles
        };

    private static UnitySearchSuggestion BuildSuggestion(string entityType, string href) =>
        new()
        {
            SourceId = Guid.NewGuid(),
            EntityType = entityType,
            GroupKey = SearchGroupKey.Transactions,
            PrimaryText = entityType,
            NavigationHref = href
        };

    private static UnitySearchRecentSelectionRecord BuildRecentSelection(string entityType, string href) =>
        new()
        {
            SourceId = Guid.NewGuid(),
            EntityType = entityType,
            GroupKey = SearchGroupKey.Transactions,
            PrimaryText = entityType,
            NavigationHref = href,
            LastClickedAtUtc = DateTimeOffset.UtcNow
        };
}
