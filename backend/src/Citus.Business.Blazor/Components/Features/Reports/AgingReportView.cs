using Citus.Ui.Shared.Reports;

namespace Citus.Business.Blazor.Components.Features.Reports;

/// <summary>
/// Neutral, presentation-only view model for A/R and A/P aging. The two
/// backend DTOs (ArAgingReportSummary / ApAgingReportSummary) carry the same
/// shape under Customer/Vendor names; <see cref="FromAr"/> / <see cref="FromAp"/>
/// map either onto this so the shared aging components render once. The
/// per-row mappers are also reused by the open-item statement (which filters
/// the aging rows to one picked party). Keeps the shared report DTOs untouched.
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
        r.CustomerRows.Select(PartyRowFromAr).ToList(),
        r.DetailRows.Select(OpenItemFromAr).ToList());

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
        r.VendorRows.Select(PartyRowFromAp).ToList(),
        r.DetailRows.Select(OpenItemFromAp).ToList());

    public static AgingPartyRow PartyRowFromAr(ArAgingCustomerSummary c) => new(
        c.CustomerDisplayName,
        c.CustomerEntityNumber,
        c.CustomerIsActive,
        c.OldestDueDate,
        c.CurrentAmountBase,
        c.Days1To30AmountBase,
        c.Days31To60AmountBase,
        c.Days61To90AmountBase,
        c.DaysOver90AmountBase,
        c.TotalOutstandingAmountBase);

    public static AgingOpenItemRow OpenItemFromAr(ArAgingOpenItemSummary d) => new(
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
        d.DocumentCurrencyCode);

    public static AgingPartyRow PartyRowFromAp(ApAgingVendorSummary v) => new(
        v.VendorDisplayName,
        v.VendorEntityNumber,
        v.VendorIsActive,
        v.OldestDueDate,
        v.CurrentAmountBase,
        v.Days1To30AmountBase,
        v.Days31To60AmountBase,
        v.Days61To90AmountBase,
        v.DaysOver90AmountBase,
        v.TotalOutstandingAmountBase);

    public static AgingOpenItemRow OpenItemFromAp(ApAgingOpenItemSummary d) => new(
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
        d.DocumentCurrencyCode);
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
