using Modules.GL.JournalEntry;

namespace Tests.GL;

public sealed class JournalEntryGridTotalsTests
{
    [Fact]
    public void ForeignCurrencyTotals_ConvertToBaseWithBankersRounding()
    {
        var draft = new JournalEntryDraft
        {
            JournalNumber = "2001",
            JournalDate = new DateOnly(2026, 4, 13),
            CurrencyCode = "USD",
            CurrencyLabel = "United States Dollar",
            CurrencyFlag = "USD",
            BaseCurrencyCode = "CAD",
            BaseCurrencyFlag = "CAD",
            FxRate = 1.37875m
        };

        draft.Lines.Add(new JournalEntryDraftLine { LineNumber = 1, DebitAmount = 100m });
        draft.Lines.Add(new JournalEntryDraftLine { LineNumber = 2, CreditAmount = 100m });

        var totals = JournalEntryGridTotals.FromDraft(draft);

        Assert.Equal(100m, totals.TransactionDebitTotal);
        Assert.Equal(100m, totals.TransactionCreditTotal);
        Assert.Equal(137.88m, totals.BaseDebitTotal);
        Assert.Equal(137.88m, totals.BaseCreditTotal);
        Assert.True(totals.IsTransactionBalanced);
        Assert.True(totals.IsBaseBalanced);
        Assert.True(totals.ShowBaseTotals);
    }

    [Fact]
    public void BaseCurrencyTotals_DoNotShowSecondTotalRow()
    {
        var draft = new JournalEntryDraft
        {
            JournalNumber = "2002",
            JournalDate = new DateOnly(2026, 4, 13),
            CurrencyCode = "CAD",
            CurrencyLabel = "Canadian Dollar",
            CurrencyFlag = "CAD",
            BaseCurrencyCode = "CAD",
            BaseCurrencyFlag = "CAD",
            FxRate = 1m
        };

        draft.Lines.Add(new JournalEntryDraftLine { LineNumber = 1, DebitAmount = 25m });
        draft.Lines.Add(new JournalEntryDraftLine { LineNumber = 2, CreditAmount = 25m });

        var totals = JournalEntryGridTotals.FromDraft(draft);

        Assert.False(totals.ShowBaseTotals);
        Assert.Equal(25m, totals.BaseDebitTotal);
        Assert.Equal(25m, totals.BaseCreditTotal);
    }

    [Fact]
    public void LineLevelBasePreview_UsesBankersRoundingPerLine()
    {
        var state = JournalEntryEditorState.CreateDarkModeDemo();
        state.Draft.CurrencyCode = "USD";
        state.Draft.BaseCurrencyCode = "CAD";
        state.Draft.FxRate = 1.37875m;

        var baseAmount = state.ConvertToBase(100m);

        Assert.Equal(137.88m, baseAmount);
    }
}
