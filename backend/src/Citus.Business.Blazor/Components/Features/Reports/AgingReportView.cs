using Citus.Ui.Shared.Reports;

namespace Citus.Business.Blazor.Components.Features.Reports;

/// <summary>
/// Neutral, presentation-only view model for A/R and A/P aging. The two
/// backend DTOs (ArAgingReportSummary / ApAgingReportSummary) carry the same
/// shape under Customer/Vendor names; <see cref="FromAr"/> / <see cref="FromAp"/>
/// map either onto this so the shared <c>AgingReport</c> (summary) and
/// <c>AgingDetailReport</c> components render once. Keeps the shared report
/// DTOs untouched.
/// </summary>
public sealed record AgingReportView(
    DateOnly AsOfDate,
    string BaseCurrencyCode,
    int PartyCount,
    int OpenItemCount,
    decimal TotalOutstanding,
    decimal TotalOverdue,
    decimal Current,
    decimal Days1To30,
    decimal Days31To60,
    decimal Days61To90,
    decimal DaysOver90,
    IReadOnlyList<AgingPartyRow> PartyRows,
    IReadOnlyList<AgingOpenItemRow> OpenItemRows)
{
    public static AgingReportView FromAr(ArAgingReportSummary r) => new(
        r.AsOfDate,
        r.BaseCurrencyCode,
        r.CustomerCount,
        r.OpenItemCount,
        r.TotalOutstandingAmountBase,
        r.TotalOverdueAmountBase,
        r.CurrentAmountBase,
        r.Days1To30AmountBase,
        r.Days31To60AmountBase,
        r.Days61To90AmountBase,
        r.DaysOver90AmountBase,
        r.CustomerRows.Select(c => new AgingPartyRow(
            c.CustomerDisplayName,
            c.CustomerEntityNumber,
            c.CustomerIsActive,
            c.OldestDueDate,
            c.CurrentAmountBase,
            c.Days1To30AmountBase,
            c.Days31To60AmountBase,
            c.Days61To90AmountBase,
            c.DaysOver90AmountBase,
            c.TotalOutstandingAmountBase)).ToList(),
        r.DetailRows.Select(d => new AgingOpenItemRow(
            d.CustomerDisplayName,
            d.CustomerEntityNumber,
            d.DisplayNumber,
            d.SourceType,
            d.SourceDocumentId,
            d.DocumentDate,
            d.DueDate,
            d.DaysPastDue,
            d.AgingBucket,
            d.SignedOpenAmountBase,
            d.SignedOpenAmountTx,
            d.DocumentCurrencyCode)).ToList());

    public static AgingReportView FromAp(ApAgingReportSummary r) => new(
        r.AsOfDate,
        r.BaseCurrencyCode,
        r.VendorCount,
        r.OpenItemCount,
        r.TotalOutstandingAmountBase,
        r.TotalOverdueAmountBase,
        r.CurrentAmountBase,
        r.Days1To30AmountBase,
        r.Days31To60AmountBase,
        r.Days61To90AmountBase,
        r.DaysOver90AmountBase,
        r.VendorRows.Select(v => new AgingPartyRow(
            v.VendorDisplayName,
            v.VendorEntityNumber,
            v.VendorIsActive,
            v.OldestDueDate,
            v.CurrentAmountBase,
            v.Days1To30AmountBase,
            v.Days31To60AmountBase,
            v.Days61To90AmountBase,
            v.DaysOver90AmountBase,
            v.TotalOutstandingAmountBase)).ToList(),
        r.DetailRows.Select(d => new AgingOpenItemRow(
            d.VendorDisplayName,
            d.VendorEntityNumber,
            d.DisplayNumber,
            d.SourceType,
            d.SourceDocumentId,
            d.DocumentDate,
            d.DueDate,
            d.DaysPastDue,
            d.AgingBucket,
            d.SignedOpenAmountBase,
            d.SignedOpenAmountTx,
            d.DocumentCurrencyCode)).ToList());
}

public sealed record AgingPartyRow(
    string DisplayName,
    string EntityNumber,
    bool IsActive,
    DateOnly? OldestDueDate,
    decimal Current,
    decimal Days1To30,
    decimal Days31To60,
    decimal Days61To90,
    decimal DaysOver90,
    decimal TotalOutstanding);

public sealed record AgingOpenItemRow(
    string PartyDisplayName,
    string PartyEntityNumber,
    string DisplayNumber,
    string SourceType,
    Guid SourceDocumentId,
    DateOnly DocumentDate,
    DateOnly? DueDate,
    int DaysPastDue,
    string AgingBucket,
    decimal SignedOpenAmountBase,
    decimal SignedOpenAmountTx,
    string DocumentCurrencyCode);
