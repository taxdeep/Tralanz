namespace Citus.Accounting.Application.Statements;

/// <summary>
/// Presentation model for an open-item statement PDF (customer or vendor).
/// Mirrors the shape of <c>InvoiceRenderModel</c> — issuer letterhead, the
/// party it is addressed to, the open items, and the aging totals — so the
/// QuestPDF renderer stays a thin layout over plain data.
/// </summary>
public sealed record StatementRenderModel
{
    public required StatementIssuerSummary Issuer { get; init; }

    public required StatementPartySummary Party { get; init; }

    /// <summary>"Customer" or "Vendor".</summary>
    public required string PartyKind { get; init; }

    public required DateOnly AsOfDate { get; init; }

    public required string BaseCurrencyCode { get; init; }

    public IReadOnlyList<StatementRenderLine> Lines { get; init; } = Array.Empty<StatementRenderLine>();

    public required StatementTotalsSummary Totals { get; init; }
}

public sealed record StatementIssuerSummary(
    string CompanyName,
    string CompanyCode,
    string? AddressBlock,
    string? Email,
    string? Phone);

public sealed record StatementPartySummary(
    string DisplayName,
    string EntityNumber,
    string? AddressBlock,
    string? Email,
    string? Phone);

public sealed record StatementRenderLine(
    string DisplayNumber,
    string SourceType,
    DateOnly DocumentDate,
    DateOnly? DueDate,
    int DaysPastDue,
    string AgingBucket,
    decimal OpenAmountBase,
    decimal OpenAmountTx,
    string DocumentCurrencyCode);

public sealed record StatementTotalsSummary(
    decimal Current,
    decimal Days1To30,
    decimal Days31To60,
    decimal Days61To90,
    decimal DaysOver90,
    decimal TotalOverdue,
    decimal TotalOutstanding);
