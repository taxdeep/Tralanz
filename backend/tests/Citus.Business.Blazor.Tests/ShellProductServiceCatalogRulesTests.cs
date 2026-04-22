using Modules.GL.JournalEntry;
using Web.Shell.Services;

namespace Citus.Business.Blazor.Tests;

public sealed class ShellProductServiceCatalogRulesTests
{
    private static readonly JournalEntryAccountOption[] SalesAccounts =
    [
        new()
        {
            AccountId = Guid.Parse("b760efc1-4b4b-4c75-a018-29568d0349d4"),
            Code = "5000",
            Name = "Sales Revenue",
            RootType = "income",
            DetailType = "sales_revenue",
            TypeLabel = "Revenue",
            CurrencyCode = "CAD",
            AllowManualPosting = true
        }
    ];

    private static readonly JournalEntryAccountOption[] PurchaseAccounts =
    [
        new()
        {
            AccountId = Guid.Parse("fc9368da-26a3-481a-b251-595d7d52f3f4"),
            Code = "5200",
            Name = "Office Expense",
            RootType = "expense",
            DetailType = "operating_expense",
            TypeLabel = "Expense",
            CurrencyCode = "CAD",
            AllowManualPosting = true
        }
    ];

    private static readonly ShellTaxCodeLookupOption[] SalesTaxCodes =
    [
        new()
        {
            Id = Guid.Parse("7401de8b-76f9-4453-860b-a08a2e645fac"),
            Code = "GST5",
            Name = "GST 5%",
            RatePercent = 5m,
            AppliesTo = "sales"
        }
    ];

    private static readonly ShellTaxCodeLookupOption[] PurchaseTaxCodes =
    [
        new()
        {
            Id = Guid.Parse("b8764bf1-bbfd-44e5-86ef-c4d3e4e39bf2"),
            Code = "GSTREC",
            Name = "Recoverable GST",
            RatePercent = 5m,
            AppliesTo = "purchase",
            IsRecoverableOnPurchase = true
        }
    ];

    [Fact]
    public void Validate_Fails_WhenNameMissing()
    {
        var result = ShellProductServiceCatalogRules.Validate(
            new ShellProductServiceUpsertRequest
            {
                CatalogType = ShellProductServiceCatalogRules.CatalogTypeService,
                Name = "   "
            },
            Array.Empty<ShellManagedProductServiceSummary>(),
            SalesAccounts,
            PurchaseAccounts,
            SalesTaxCodes,
            PurchaseTaxCodes);

        Assert.False(result.Succeeded);
        Assert.Equal("missing_name", result.ErrorCode);
    }

    [Fact]
    public void Validate_Fails_WhenCatalogTypeInvalid()
    {
        var result = ShellProductServiceCatalogRules.Validate(
            new ShellProductServiceUpsertRequest
            {
                CatalogType = "bundle",
                Name = "Consulting"
            },
            Array.Empty<ShellManagedProductServiceSummary>(),
            SalesAccounts,
            PurchaseAccounts,
            SalesTaxCodes,
            PurchaseTaxCodes);

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_catalog_type", result.ErrorCode);
    }

    [Fact]
    public void Validate_Fails_WhenCompanyScopedNameAlreadyExists()
    {
        var result = ShellProductServiceCatalogRules.Validate(
            new ShellProductServiceUpsertRequest
            {
                CatalogType = ShellProductServiceCatalogRules.CatalogTypeService,
                Name = "consulting"
            },
            [
                new ShellManagedProductServiceSummary
                {
                    Id = Guid.Parse("0049b9ff-fdb2-428a-b93e-a8ca4492bdd0"),
                    CatalogType = ShellProductServiceCatalogRules.CatalogTypeService,
                    Name = "Consulting"
                }
            ],
            SalesAccounts,
            PurchaseAccounts,
            SalesTaxCodes,
            PurchaseTaxCodes);

        Assert.False(result.Succeeded);
        Assert.Equal("duplicate_name", result.ErrorCode);
    }

    [Fact]
    public void Validate_Fails_WhenSalesTaxCodeIsNotActiveForCompany()
    {
        var result = ShellProductServiceCatalogRules.Validate(
            new ShellProductServiceUpsertRequest
            {
                CatalogType = ShellProductServiceCatalogRules.CatalogTypeService,
                Name = "Consulting",
                DefaultSalesTaxCodeId = Guid.NewGuid()
            },
            Array.Empty<ShellManagedProductServiceSummary>(),
            SalesAccounts,
            PurchaseAccounts,
            SalesTaxCodes,
            PurchaseTaxCodes);

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_sales_tax_code", result.ErrorCode);
    }

    [Fact]
    public void Validate_Succeeds_ForSalesAndPurchaseDefaults()
    {
        var result = ShellProductServiceCatalogRules.Validate(
            new ShellProductServiceUpsertRequest
            {
                CatalogType = ShellProductServiceCatalogRules.CatalogTypeProduct,
                Name = "Office Supply Pack",
                Description = "Office supply pack",
                SalesRevenueAccountId = SalesAccounts[0].AccountId,
                PurchaseExpenseAccountId = PurchaseAccounts[0].AccountId,
                DefaultSalesTaxCodeId = SalesTaxCodes[0].Id,
                DefaultPurchaseTaxCodeId = PurchaseTaxCodes[0].Id
            },
            Array.Empty<ShellManagedProductServiceSummary>(),
            SalesAccounts,
            PurchaseAccounts,
            SalesTaxCodes,
            PurchaseTaxCodes);

        Assert.True(result.Succeeded);
    }
}
