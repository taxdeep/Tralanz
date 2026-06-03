namespace Citus.Business.Blazor.Components.Features.Reports;

/// <summary>
/// Neutral, presentation-only view model for A/R and A/P aging. The two
/// backend DTOs (ArAgingReportSummary / ApAgingReportSummary) carry the same
/// shape under Customer/Vendor names; the thin AR/AP wrappers map their DTO
/// into this so the shared <c>AgingReport</c> component renders once. Keeps
/// the shared report DTOs untouched.
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
    IReadOnlyList<AgingOpenItemRow> OpenItemRows);

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
