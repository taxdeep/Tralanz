using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Companies;
using Citus.Accounting.Application.Queries;

namespace Citus.Accounting.Application.Statements;

/// <summary>
/// Pure builder: assembles a <see cref="StatementRenderModel"/> from the
/// company profile, the party's contact record, and that party's aging
/// balance (the open items + bucket totals already computed by the aging
/// report). No IO — the endpoint pre-loads the inputs.
/// </summary>
public static class StatementRenderModelBuilder
{
    public static StatementRenderModel BuildForCustomer(
        CompanyProfileSnapshot company,
        CustomerRecord customer,
        ArAgingCustomerBalance? balance,
        DateOnly asOfDate,
        string baseCurrencyCode) =>
        new()
        {
            Issuer = Issuer(company),
            Party = new StatementPartySummary(
                customer.DisplayName,
                customer.EntityNumber,
                ComposeAddress(customer.AddressLine, customer.City, customer.ProvinceState, customer.PostalCode, customer.Country),
                customer.Email,
                customer.Phone),
            PartyKind = "Customer",
            AsOfDate = asOfDate,
            BaseCurrencyCode = baseCurrencyCode,
            Lines = balance is null
                ? Array.Empty<StatementRenderLine>()
                : balance.OpenItems.Select(Line).ToArray(),
            Totals = Totals(
                balance?.CurrentAmountBase ?? 0m,
                balance?.Days1To30AmountBase ?? 0m,
                balance?.Days31To60AmountBase ?? 0m,
                balance?.Days61To90AmountBase ?? 0m,
                balance?.DaysOver90AmountBase ?? 0m,
                balance?.TotalOverdueAmountBase ?? 0m,
                balance?.TotalOutstandingAmountBase ?? 0m)
        };

    public static StatementRenderModel BuildForVendor(
        CompanyProfileSnapshot company,
        VendorRecord vendor,
        ApAgingVendorBalance? balance,
        DateOnly asOfDate,
        string baseCurrencyCode) =>
        new()
        {
            Issuer = Issuer(company),
            Party = new StatementPartySummary(
                vendor.DisplayName,
                vendor.EntityNumber,
                ComposeAddress(vendor.AddressLine, vendor.City, vendor.ProvinceState, vendor.PostalCode, vendor.Country),
                vendor.Email,
                vendor.Phone),
            PartyKind = "Vendor",
            AsOfDate = asOfDate,
            BaseCurrencyCode = baseCurrencyCode,
            Lines = balance is null
                ? Array.Empty<StatementRenderLine>()
                : balance.OpenItems.Select(Line).ToArray(),
            Totals = Totals(
                balance?.CurrentAmountBase ?? 0m,
                balance?.Days1To30AmountBase ?? 0m,
                balance?.Days31To60AmountBase ?? 0m,
                balance?.Days61To90AmountBase ?? 0m,
                balance?.DaysOver90AmountBase ?? 0m,
                balance?.TotalOverdueAmountBase ?? 0m,
                balance?.TotalOutstandingAmountBase ?? 0m)
        };

    private static StatementIssuerSummary Issuer(CompanyProfileSnapshot company) => new(
        company.LegalName,
        company.EntityNumber,
        ComposeAddress(company.AddressLine, company.City, company.ProvinceState, company.PostalCode, company.Country),
        company.Email,
        company.Phone);

    private static StatementRenderLine Line(ArAgingOpenItemAmount item) => new(
        item.DisplayNumber,
        item.SourceType,
        item.DocumentDate,
        item.DueDate,
        item.DaysPastDue,
        item.AgingBucket,
        item.SignedOpenAmountBase,
        item.SignedOpenAmountTx,
        item.DocumentCurrencyCode);

    private static StatementRenderLine Line(ApAgingOpenItemAmount item) => new(
        item.DisplayNumber,
        item.SourceType,
        item.DocumentDate,
        item.DueDate,
        item.DaysPastDue,
        item.AgingBucket,
        item.SignedOpenAmountBase,
        item.SignedOpenAmountTx,
        item.DocumentCurrencyCode);

    private static StatementTotalsSummary Totals(
        decimal current, decimal d1, decimal d2, decimal d3, decimal d4, decimal overdue, decimal outstanding) =>
        new(current, d1, d2, d3, d4, overdue, outstanding);

    private static string? ComposeAddress(string? line, string? city, string? province, string? postal, string? country)
    {
        var cityLine = string.Join(", ", new[] { city, province, postal }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var block = string.Join(
            "\n",
            new[] { line, cityLine, country }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return string.IsNullOrWhiteSpace(block) ? null : block;
    }
}
