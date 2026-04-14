namespace Modules.GL.JournalEntry;

public sealed class JournalEntryGridTotals
{
    public string TransactionCurrencyCode { get; init; } = string.Empty;

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public decimal TransactionDebitTotal { get; init; }

    public decimal TransactionCreditTotal { get; init; }

    public decimal BaseDebitTotal { get; init; }

    public decimal BaseCreditTotal { get; init; }

    public bool ShowBaseTotals { get; init; }

    public bool IsTransactionBalanced => TransactionDebitTotal == TransactionCreditTotal;

    public bool IsBaseBalanced => BaseDebitTotal == BaseCreditTotal;

    public static JournalEntryGridTotals FromDraft(JournalEntryDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var transactionDebit = Round2(draft.Lines.Sum(line => line.DebitAmount ?? 0m));
        var transactionCredit = Round2(draft.Lines.Sum(line => line.CreditAmount ?? 0m));
        var baseDebit = Round2(draft.Lines.Sum(line => Round2((line.DebitAmount ?? 0m) * draft.FxRate)));
        var baseCredit = Round2(draft.Lines.Sum(line => Round2((line.CreditAmount ?? 0m) * draft.FxRate)));

        return new JournalEntryGridTotals
        {
            TransactionCurrencyCode = draft.CurrencyCode,
            BaseCurrencyCode = draft.BaseCurrencyCode,
            TransactionDebitTotal = transactionDebit,
            TransactionCreditTotal = transactionCredit,
            BaseDebitTotal = baseDebit,
            BaseCreditTotal = baseCredit,
            ShowBaseTotals = draft.IsForeignCurrency
        };
    }

    private static decimal Round2(decimal value) =>
        Math.Round(value, 2, MidpointRounding.ToEven);
}
