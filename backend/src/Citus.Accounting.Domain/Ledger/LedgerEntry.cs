using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;

namespace Citus.Accounting.Domain.Ledger;

public sealed record LedgerEntry(
    Guid Id,
    CompanyId CompanyId,
    Guid JournalEntryId,
    Guid JournalEntryLineId,
    DateOnly PostingDate,
    Guid AccountId,
    CurrencyCode TransactionCurrencyCode,
    decimal TxDebit,
    decimal TxCredit,
    decimal Debit,
    decimal Credit);
