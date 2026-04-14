namespace Web.Business.AR.SettlementPosting;

public sealed record class ArSettlementPostingResult
{
    public Guid JournalEntryId { get; init; }

    public string JournalEntryDisplayNumber { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public DateTimeOffset PostedAt { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

