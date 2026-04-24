using Citus.Modules.UnitySearch.Domain.Shared;

namespace Citus.Modules.UnitySearch.Application;

public sealed class UnitySearchPolicyRegistry
{
    private static readonly IReadOnlyDictionary<string, SearchPolicyDefinition> Policies =
        new Dictionary<string, SearchPolicyDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            [SearchScopeContext.GlobalTopbar] = new(
                SearchScopeContext.GlobalTopbar,
                new[]
                {
                    SearchDocumentType.JumpTo,
                    SearchDocumentType.Report,
                    SearchDocumentType.Customer,
                    SearchDocumentType.Vendor,
                    SearchDocumentType.ProductService,
                    SearchDocumentType.InventoryItem,
                    SearchDocumentType.Quote,
                    SearchDocumentType.SalesOrder,
                    SearchDocumentType.PurchaseOrder,
                    SearchDocumentType.Invoice,
                    SearchDocumentType.Bill,
                    SearchDocumentType.CreditNote,
                    SearchDocumentType.VendorCredit,
                    SearchDocumentType.JournalEntry,
                    SearchDocumentType.Account
                },
                EnforceActiveOnly: false,
                EnforceBusinessEligibility: false),
            [SearchScopeContext.GlobalTransactions] = new(
                SearchScopeContext.GlobalTransactions,
                new[]
                {
                    SearchDocumentType.JumpTo,
                    SearchDocumentType.Report,
                    SearchDocumentType.Customer,
                    SearchDocumentType.Vendor,
                    SearchDocumentType.ProductService,
                    SearchDocumentType.InventoryItem,
                    SearchDocumentType.Quote,
                    SearchDocumentType.SalesOrder,
                    SearchDocumentType.PurchaseOrder,
                    SearchDocumentType.Invoice,
                    SearchDocumentType.Bill,
                    SearchDocumentType.CreditNote,
                    SearchDocumentType.VendorCredit,
                    SearchDocumentType.JournalEntry,
                    SearchDocumentType.Account
                },
                EnforceActiveOnly: false,
                EnforceBusinessEligibility: false),
            [SearchScopeContext.QuoteCustomerPicker] = new(
                SearchScopeContext.QuoteCustomerPicker,
                new[] { SearchDocumentType.Customer },
                EnforceActiveOnly: true,
                EnforceBusinessEligibility: true),
            [SearchScopeContext.QuoteProductServicePicker] = new(
                SearchScopeContext.QuoteProductServicePicker,
                new[] { SearchDocumentType.ProductService },
                EnforceActiveOnly: true,
                EnforceBusinessEligibility: true),
            [SearchScopeContext.QuoteInventoryItemPicker] = new(
                SearchScopeContext.QuoteInventoryItemPicker,
                new[] { SearchDocumentType.InventoryItem },
                EnforceActiveOnly: true,
                EnforceBusinessEligibility: true),
            [SearchScopeContext.SalesOrderCustomerPicker] = new(
                SearchScopeContext.SalesOrderCustomerPicker,
                new[] { SearchDocumentType.Customer },
                EnforceActiveOnly: true,
                EnforceBusinessEligibility: true),
            [SearchScopeContext.SalesOrderProductServicePicker] = new(
                SearchScopeContext.SalesOrderProductServicePicker,
                new[] { SearchDocumentType.ProductService },
                EnforceActiveOnly: true,
                EnforceBusinessEligibility: true),
            [SearchScopeContext.SalesOrderInventoryItemPicker] = new(
                SearchScopeContext.SalesOrderInventoryItemPicker,
                new[] { SearchDocumentType.InventoryItem },
                EnforceActiveOnly: true,
                EnforceBusinessEligibility: true),
            [SearchScopeContext.PurchaseOrderVendorPicker] = new(
                SearchScopeContext.PurchaseOrderVendorPicker,
                new[] { SearchDocumentType.Vendor },
                EnforceActiveOnly: true,
                EnforceBusinessEligibility: true),
            [SearchScopeContext.PurchaseOrderInventoryItemPicker] = new(
                SearchScopeContext.PurchaseOrderInventoryItemPicker,
                new[] { SearchDocumentType.InventoryItem },
                EnforceActiveOnly: true,
                EnforceBusinessEligibility: true),
            [SearchScopeContext.InvoiceCustomerPicker] = new(
                SearchScopeContext.InvoiceCustomerPicker,
                new[] { SearchDocumentType.Customer },
                EnforceActiveOnly: true,
                EnforceBusinessEligibility: true),
            [SearchScopeContext.InvoiceItemPicker] = new(
                SearchScopeContext.InvoiceItemPicker,
                new[] { SearchDocumentType.ProductService, SearchDocumentType.InventoryItem },
                EnforceActiveOnly: true,
                EnforceBusinessEligibility: true),
            [SearchScopeContext.BillVendorPicker] = new(
                SearchScopeContext.BillVendorPicker,
                new[] { SearchDocumentType.Vendor },
                EnforceActiveOnly: true,
                EnforceBusinessEligibility: true),
            [SearchScopeContext.JournalEntryAccountPicker] = new(
                SearchScopeContext.JournalEntryAccountPicker,
                new[] { SearchDocumentType.Account },
                EnforceActiveOnly: true,
                EnforceBusinessEligibility: true)
        };

    public SearchPolicyDefinition Resolve(string? context)
    {
        if (!string.IsNullOrWhiteSpace(context) && Policies.TryGetValue(context.Trim(), out var policy))
        {
            return policy;
        }

        return Policies[SearchScopeContext.GlobalTopbar];
    }
}
