using Citus.Modules.UnitySearch.Application.Contracts;
using Citus.Modules.UnitySearch.Domain.Shared;

namespace Citus.Accounting.Api;

public static class UnitySearchPermissionFilter
{
    public static UnitySearchResult Filter(UnitySearchResult result, BusinessSessionContext? session)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (HasUnrestrictedAccess(session))
        {
            return result;
        }

        var groups = result.Groups
            .Select(group => group with
            {
                Items = group.Items
                    .Where(item => CanAccess(item.EntityType, item.NavigationHref, session))
                    .ToArray()
            })
            .Where(group => group.Items.Count > 0)
            .ToArray();

        var recentSelections = result.RecentSelections
            .Where(item => CanAccess(item.EntityType, item.NavigationHref, session))
            .ToArray();

        return result with
        {
            Groups = groups,
            RecentSelections = recentSelections,
            TotalCount = groups.Sum(group => group.Items.Count)
        };
    }

    public static IReadOnlyList<UnitySearchRecentSelectionRecord> FilterRecentSelections(
        IReadOnlyList<UnitySearchRecentSelectionRecord> selections,
        BusinessSessionContext? session)
    {
        ArgumentNullException.ThrowIfNull(selections);

        if (HasUnrestrictedAccess(session))
        {
            return selections;
        }

        return selections
            .Where(item => CanAccess(item.EntityType, item.NavigationHref, session))
            .ToArray();
    }

    public static bool CanAccess(string entityType, string? navigationHref, BusinessSessionContext? session)
    {
        if (HasUnrestrictedAccess(session))
        {
            return true;
        }

        return Normalize(entityType) switch
        {
            SearchDocumentType.Customer => HasSalesAccess(session),
            SearchDocumentType.Vendor => HasPurchaseAccess(session),
            SearchDocumentType.Quote => HasSalesAccess(session),
            SearchDocumentType.SalesOrder => HasSalesAccess(session),
            SearchDocumentType.Invoice => HasSalesAccess(session),
            SearchDocumentType.CreditNote => HasSalesAccess(session),
            SearchDocumentType.PurchaseOrder => HasPurchaseAccess(session),
            SearchDocumentType.Bill => HasPurchaseAccess(session),
            SearchDocumentType.VendorCredit => HasPurchaseAccess(session),
            SearchDocumentType.ProductService => HasCatalogAccess(session),
            SearchDocumentType.InventoryItem => HasInventoryAccess(session),
            SearchDocumentType.InventoryStockItem => HasInventoryAccess(session),
            SearchDocumentType.Warehouse => HasInventoryAccess(session),
            SearchDocumentType.JournalEntry => HasAccountingAccess(session),
            SearchDocumentType.Account => HasAccountingAccess(session),
            SearchDocumentType.Report => HasReportAccess(session),
            SearchDocumentType.JumpTo => CanAccessJumpTarget(navigationHref, session),
            _ => false
        };
    }

    private static bool CanAccessJumpTarget(string? navigationHref, BusinessSessionContext? session)
    {
        if (string.IsNullOrWhiteSpace(navigationHref))
        {
            return false;
        }

        var href = navigationHref.Trim();

        if (Contains(href, "/ar/") ||
            Contains(href, "sourcetype=quote") ||
            Contains(href, "sourcetype=sales_order") ||
            Contains(href, "sourcetype=invoice") ||
            Contains(href, "sourcetype=credit_note"))
        {
            return HasSalesAccess(session);
        }

        if (Contains(href, "/ap/") ||
            Contains(href, "sourcetype=purchase_order") ||
            Contains(href, "sourcetype=bill") ||
            Contains(href, "sourcetype=vendor_credit"))
        {
            return HasPurchaseAccess(session);
        }

        if (Contains(href, "/gl/"))
        {
            return HasAccountingAccess(session);
        }

        if (Contains(href, "/transactions") ||
            Contains(href, "/reports"))
        {
            return HasReportAccess(session);
        }

        if (Contains(href, "inventory-foundation"))
        {
            return HasInventoryAccess(session) || HasSettingsAccess(session);
        }

        if (Contains(href, "product-service-setup"))
        {
            return HasCatalogAccess(session) || HasSettingsAccess(session);
        }

        if (Contains(href, "customer-vendor-setup"))
        {
            return HasSettingsAccess(session);
        }

        return false;
    }

    private static bool HasUnrestrictedAccess(BusinessSessionContext? session) =>
        HasAnyRole(session, "owner");

    private static bool HasSalesAccess(BusinessSessionContext? session) =>
        HasAnyRole(session, "ar", "sales");

    private static bool HasPurchaseAccess(BusinessSessionContext? session) =>
        HasAnyRole(session, "ap", "purchases");

    private static bool HasInventoryAccess(BusinessSessionContext? session) =>
        HasAnyRole(session, "inventory", "sales", "purchases", "ar", "ap");

    private static bool HasCatalogAccess(BusinessSessionContext? session) =>
        HasAnyRole(session, "sales", "purchases", "ar", "ap", "inventory");

    private static bool HasAccountingAccess(BusinessSessionContext? session) =>
        HasAnyRole(session, "company_book_governance", "company_accounting_settings");

    private static bool HasReportAccess(BusinessSessionContext? session) =>
        HasAnyRole(session, "reports", "company_book_governance", "company_accounting_settings");

    private static bool HasSettingsAccess(BusinessSessionContext? session) =>
        HasAnyRole(session, "settings_access", "company_book_governance", "company_accounting_settings");

    private static bool HasAnyRole(BusinessSessionContext? session, params string[] roles)
    {
        if (session?.Roles is null || session.Roles.Count == 0)
        {
            return false;
        }

        var normalizedRoles = session.Roles
            .Where(static role => !string.IsNullOrWhiteSpace(role))
            .Select(Normalize)
            .ToHashSet(StringComparer.Ordinal);

        return roles.Any(role => normalizedRoles.Contains(Normalize(role)));
    }

    private static string Normalize(string value) =>
        value.Trim().ToLowerInvariant();

    private static bool Contains(string value, string pattern) =>
        value.Contains(pattern, StringComparison.OrdinalIgnoreCase);
}
