namespace Modules.GL.JournalEntry;

public sealed record class JournalEntryLifecycleResult(
    Guid OriginalJournalEntryId,
    string OriginalDisplayNumber,
    string OriginalStatus,
    DateTimeOffset LifecycleAt,
    Guid CompensationJournalEntryId,
    string CompensationDisplayNumber,
    string CompensationSourceType);
