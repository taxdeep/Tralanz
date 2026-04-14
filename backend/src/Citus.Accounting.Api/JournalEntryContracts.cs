namespace Citus.Accounting.Api;

public sealed record JournalEntryListLookupQuery(
    Guid CompanyId,
    int Take = 10);

public sealed record JournalEntryLookupQuery(Guid CompanyId);
