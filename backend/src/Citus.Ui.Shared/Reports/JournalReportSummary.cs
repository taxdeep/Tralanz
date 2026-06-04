namespace Citus.Ui.Shared.Reports;

/// <summary>
/// Journal report: every posted debit/credit line in a date range, flat and
/// ordered by date then entry then line. The UI groups consecutive lines by
/// <see cref="JournalReportLineSummary.JournalNumber"/> for per-entry subtotals.
/// </summary>
public sealed record class JournalReportSummary
{
    public DateOnly DateFrom { get; init; }

    public DateOnly DateTo { get; init; }

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public int EntryCount { get; init; }

    public int LineCount { get; init; }

    public decimal TotalDebit { get; init; }

    public decimal TotalCredit { get; init; }

    public bool IsBalanced { get; init; }

    public IReadOnlyList<JournalReportLineSummary> Lines { get; init; } = Array.Empty<JournalReportLineSummary>();
}

public sealed record class JournalReportLineSummary
{
    public string InternalNumber { get; init; } = string.Empty;

    public Guid JournalEntryId { get; init; }

    public string SourceType { get; init; } = string.Empty;

    public Guid SourceId { get; init; }

    public string ReferenceNumber { get; init; } = string.Empty;

    public DateOnly PostingDate { get; init; }

    public string PartyName { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string AccountCode { get; init; } = string.Empty;

    public string AccountName { get; init; } = string.Empty;

    public decimal Debit { get; init; }

    public decimal Credit { get; init; }
}
