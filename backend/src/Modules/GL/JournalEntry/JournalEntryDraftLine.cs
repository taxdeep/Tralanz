namespace Modules.GL.JournalEntry;

public sealed class JournalEntryDraftLine
{
    public int LineNumber { get; set; }

    public JournalEntryAccountOption? Account { get; set; }

    public decimal? DebitAmount { get; set; }

    public decimal? CreditAmount { get; set; }

    public string Description { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string SalesTax { get; set; } = string.Empty;

    public bool HasContent =>
        Account is not null ||
        DebitAmount is not null ||
        CreditAmount is not null ||
        !string.IsNullOrWhiteSpace(Description) ||
        !string.IsNullOrWhiteSpace(Name) ||
        !string.IsNullOrWhiteSpace(SalesTax);

    public JournalEntryDraftLine Clone(int lineNumber) =>
        new()
        {
            LineNumber = lineNumber,
            Account = Account,
            DebitAmount = DebitAmount,
            CreditAmount = CreditAmount,
            Description = Description,
            Name = Name,
            SalesTax = SalesTax
        };

    public static JournalEntryDraftLine Blank(int lineNumber) => new()
    {
        LineNumber = lineNumber
    };
}

public sealed record class JournalEntryAccountOption
{
    public required Guid AccountId { get; init; }

    public required string Code { get; init; }

    public required string Name { get; init; }

    public string RootType { get; init; } = string.Empty;

    public string DetailType { get; init; } = string.Empty;

    public required string TypeLabel { get; init; }

    public required string CurrencyCode { get; init; }

    public required bool AllowManualPosting { get; init; }

    public string SearchText => $"{Code} {Name} {TypeLabel} {CurrencyCode}";

    public string DisplayText => $"{Code} {Name}";

    public override string ToString() => DisplayText;
}

public sealed record class JournalEntryCurrencyOption
{
    public required string Code { get; init; }

    public required string Label { get; init; }

    public required string Flag { get; init; }

    public required decimal DefaultRateToBase { get; init; }

    public bool IsBaseCurrency { get; init; }

    public string DisplayText => $"{Code} {Label}";
}
