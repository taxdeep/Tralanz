using SharedKernel.FX;

namespace Modules.GL.JournalEntry;

public sealed class JournalEntryDraft
{
    public Guid? DocumentId { get; set; }

    public string DocumentNumber { get; set; } = string.Empty;

    public string Status { get; set; } = "draft";

    public Guid CompanyId { get; set; }

    public string JournalNumber { get; set; } = string.Empty;

    public DateOnly JournalDate { get; set; }

    public string CurrencyCode { get; set; } = string.Empty;

    public string CurrencyLabel { get; set; } = string.Empty;

    public string CurrencyFlag { get; set; } = string.Empty;

    public string BaseCurrencyCode { get; set; } = string.Empty;

    public string BaseCurrencyFlag { get; set; } = string.Empty;

    public decimal FxRate { get; set; } = 1m;

    public Guid? FxSnapshotId { get; set; }

    public DateOnly FxEffectiveDate { get; set; }

    public string FxSourceSemantics { get; set; } = SharedKernel.FX.FxSourceSemantics.Identity;

    public string FxStatusLabel { get; set; } = "Base currency";

    public string FxProviderKey { get; set; } = "ECB";

    public string FxRateType { get; set; } = SharedKernel.FX.FxRateType.Spot;

    public string FxQuoteBasis { get; set; } = SharedKernel.FX.FxQuoteBasis.Direct;

    public string FxRateUseCase { get; set; } = SharedKernel.FX.FxRateUseCase.General;

    public string FxPostingReason { get; set; } = SharedKernel.FX.FxPostingReason.Normal;

    public string Memo { get; set; } = string.Empty;

    public bool IsDarkMode { get; set; }

    public IList<JournalEntryDraftLine> Lines { get; } = new List<JournalEntryDraftLine>();

    public bool IsForeignCurrency =>
        !string.Equals(CurrencyCode, BaseCurrencyCode, StringComparison.OrdinalIgnoreCase);

    public string Title =>
        string.IsNullOrWhiteSpace(JournalNumber)
            ? "JE# Draft"
            : $"JE# {JournalNumber}";
}
