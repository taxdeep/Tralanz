using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Application.Queries;

public sealed record class GetJournalReportQuery(
    CompanyId CompanyId,
    DateOnly DateFrom,
    DateOnly DateTo);

/// <summary>
/// One debit/credit line of a journal entry, as listed on the Journal report.
/// Flat (ordered by date, then entry, then line) — the UI groups consecutive
/// lines by <see cref="JournalNumber"/> for the per-entry subtotals.
/// </summary>
public sealed record class JournalReportLine
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

    public static JournalReportLine Create(
        string internalNumber,
        Guid journalEntryId,
        string sourceType,
        Guid sourceId,
        string? referenceNumber,
        DateOnly postingDate,
        string? partyName,
        string? description,
        string accountCode,
        string accountName,
        decimal debit,
        decimal credit) =>
        new()
        {
            InternalNumber = internalNumber.Trim(),
            JournalEntryId = journalEntryId,
            SourceType = sourceType.Trim(),
            SourceId = sourceId,
            ReferenceNumber = (referenceNumber ?? string.Empty).Trim(),
            PostingDate = postingDate,
            PartyName = (partyName ?? string.Empty).Trim(),
            Description = (description ?? string.Empty).Trim(),
            AccountCode = accountCode.Trim(),
            AccountName = accountName.Trim(),
            Debit = Round6(debit),
            Credit = Round6(credit)
        };

    private static decimal Round6(decimal value) => Math.Round(value, 6, MidpointRounding.ToEven);
}

public sealed record class JournalReport
{
    public CompanyId CompanyId { get; init; }

    public DateOnly DateFrom { get; init; }

    public DateOnly DateTo { get; init; }

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public int EntryCount { get; init; }

    public int LineCount { get; init; }

    public decimal TotalDebit { get; init; }

    public decimal TotalCredit { get; init; }

    public bool IsBalanced => TotalDebit == TotalCredit;

    public IReadOnlyList<JournalReportLine> Lines { get; init; } = Array.Empty<JournalReportLine>();

    public static JournalReport Create(
        CompanyId companyId,
        DateOnly dateFrom,
        DateOnly dateTo,
        string baseCurrencyCode,
        IEnumerable<JournalReportLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var rows = lines.ToArray();

        return new JournalReport
        {
            CompanyId = companyId,
            DateFrom = dateFrom,
            DateTo = dateTo,
            BaseCurrencyCode = baseCurrencyCode.Trim().ToUpperInvariant(),
            EntryCount = rows.Select(static row => row.JournalEntryId).Distinct().Count(),
            LineCount = rows.Length,
            TotalDebit = Round6(rows.Sum(static row => row.Debit)),
            TotalCredit = Round6(rows.Sum(static row => row.Credit)),
            Lines = rows
        };
    }

    private static decimal Round6(decimal value) => Math.Round(value, 6, MidpointRounding.ToEven);
}
