namespace Citus.Accounting.Api;

public sealed record JournalEntryListLookupQuery(
    CompanyId CompanyId,
    int Take = 10);

public sealed record JournalEntryLookupQuery(CompanyId CompanyId);
