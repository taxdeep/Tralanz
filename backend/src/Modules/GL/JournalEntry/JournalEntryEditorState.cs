using SharedKernel.Company;
using SharedKernel.FX;

namespace Modules.GL.JournalEntry;

public sealed class JournalEntryEditorState
{
    private const int DefaultVisibleRows = 8;
    private static readonly Guid DemoCompanyId = Guid.Parse("5e492df2-37ab-47df-a1bb-2d559c876cbc");
    private readonly List<JournalEntryCurrencyOption> _currencyOptions;
    private readonly List<JournalEntryAccountOption> _accountOptions;

    private JournalEntryEditorState(
        JournalEntryDraft draft,
        IEnumerable<JournalEntryCurrencyOption> currencyOptions,
        IEnumerable<JournalEntryAccountOption> accountOptions,
        IReadOnlyList<string> nameOptions,
        IReadOnlyList<string> salesTaxOptions)
    {
        Draft = draft;
        _currencyOptions = currencyOptions.ToList();
        _accountOptions = accountOptions.ToList();
        NameOptions = nameOptions;
        SalesTaxOptions = salesTaxOptions;
    }

    public JournalEntryDraft Draft { get; }

    public IReadOnlyList<JournalEntryCurrencyOption> CurrencyOptions => _currencyOptions;

    public IReadOnlyList<JournalEntryAccountOption> AccountOptions => _accountOptions;

    public IReadOnlyList<string> NameOptions { get; }

    public IReadOnlyList<string> SalesTaxOptions { get; }

    public bool IsPosted =>
        string.Equals(Draft.Status, "posted", StringComparison.OrdinalIgnoreCase);

    public bool CanEditJournal => !IsPosted;

    public static JournalEntryEditorState CreateDarkModeDemo()
    {
        var currencyOptions = new[]
        {
            new JournalEntryCurrencyOption { Code = "USD", Label = "United States Dollar", Flag = "USD", DefaultRateToBase = 1.37875m },
            new JournalEntryCurrencyOption { Code = "CAD", Label = "Canadian Dollar", Flag = "CAD", DefaultRateToBase = 1m, IsBaseCurrency = true },
            new JournalEntryCurrencyOption { Code = "EUR", Label = "Euro", Flag = "EUR", DefaultRateToBase = 1.47210m },
            new JournalEntryCurrencyOption { Code = "GBP", Label = "Pound Sterling", Flag = "GBP", DefaultRateToBase = 1.72130m },
            new JournalEntryCurrencyOption { Code = "CNY", Label = "Chinese Yuan", Flag = "CNY", DefaultRateToBase = 0.18940m }
        };

        var draft = new JournalEntryDraft
        {
            CompanyId = DemoCompanyId,
            JournalDate = new DateOnly(2026, 4, 13),
            CurrencyCode = "USD",
            CurrencyLabel = "United States Dollar",
            CurrencyFlag = "USD",
            BaseCurrencyCode = "CAD",
            BaseCurrencyFlag = "CAD",
            FxRate = 1.37875m,
            FxEffectiveDate = new DateOnly(2026, 4, 10),
            FxSourceSemantics = FxSourceSemantics.SystemStored,
            FxStatusLabel = "Local fallback 2026-04-10",
            FxProviderKey = "ECB",
            FxRateType = FxRateType.Spot,
            FxQuoteBasis = FxQuoteBasis.Direct,
            FxRateUseCase = FxRateUseCase.General,
            FxPostingReason = FxPostingReason.Normal,
            IsDarkMode = true
        };

        for (var lineNumber = 1; lineNumber <= DefaultVisibleRows; lineNumber++)
        {
            draft.Lines.Add(JournalEntryDraftLine.Blank(lineNumber));
        }

        return new JournalEntryEditorState(
            draft,
            currencyOptions,
            [],
            ["", "Office", "Payroll", "Northwind", "Scotia"],
            ["", "No tax", "GST/HST", "Zero-rated"]);
    }

    public void ApplyCompanyCurrencyProfile(CompanyCurrencyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var options = profile.EnabledCurrencies
            .Select(currency => new JournalEntryCurrencyOption
            {
                Code = currency.CurrencyCode,
                Label = currency.CurrencyName,
                Flag = currency.CurrencyCode,
                DefaultRateToBase = 1m,
                IsBaseCurrency = currency.IsBaseCurrency
            })
            .ToArray();

        if (options.Length == 0)
        {
            ReplaceCurrencyOptions(
            [
                new JournalEntryCurrencyOption
                {
                    Code = profile.BaseCurrencyCode,
                    Label = profile.BaseCurrencyCode,
                    Flag = profile.BaseCurrencyCode,
                    DefaultRateToBase = 1m,
                    IsBaseCurrency = true
                }
            ]);
        }
        else
        {
            ReplaceCurrencyOptions(options);
        }

        Draft.BaseCurrencyCode = profile.BaseCurrencyCode;
        Draft.BaseCurrencyFlag = profile.BaseCurrencyCode;

        var preferredCurrencyCode = _currencyOptions.Any(option =>
            string.Equals(option.Code, Draft.CurrencyCode, StringComparison.OrdinalIgnoreCase))
            ? Draft.CurrencyCode
            : profile.BaseCurrencyCode;

        UpdateCurrencyMetadata(preferredCurrencyCode);

        if (!Draft.IsForeignCurrency)
        {
            ApplyIdentityFxRate();
        }
    }

    public void ReplaceCurrencyOptions(IEnumerable<JournalEntryCurrencyOption> currencyOptions)
    {
        _currencyOptions.Clear();
        _currencyOptions.AddRange(currencyOptions);
    }

    public void ReplaceAccountOptions(IEnumerable<JournalEntryAccountOption> accountOptions)
    {
        _accountOptions.Clear();
        _accountOptions.AddRange(accountOptions);
    }

    public void SetCurrency(string currencyCode)
    {
        if (!TryFindCurrencyOption(currencyCode, out var option))
        {
            return;
        }

        Draft.CurrencyCode = option.Code;
        Draft.CurrencyLabel = option.Label;
        Draft.CurrencyFlag = option.Flag;
        Draft.FxRate = option.DefaultRateToBase <= 0 ? 1m : option.DefaultRateToBase;

        if (!Draft.IsForeignCurrency)
        {
            ApplyIdentityFxRate();
        }
    }

    public void SetJournalDate(DateOnly journalDate)
    {
        Draft.JournalDate = journalDate;
    }

    public void SetJournalNumber(string journalNumber)
    {
        Draft.JournalNumber = journalNumber;
    }

    public void ApplyResolvedFxRate(FxRateResolution resolution)
    {
        Draft.FxRate = resolution.Rate;
        Draft.FxEffectiveDate = resolution.EffectiveDate;
        Draft.FxSourceSemantics = resolution.SourceSemantics;
        Draft.FxStatusLabel = resolution.StatusLabel;
        Draft.FxSnapshotId = resolution.SnapshotId;
        Draft.FxRateType = resolution.RateType;
        Draft.FxQuoteBasis = resolution.QuoteBasis;
        Draft.FxRateUseCase = resolution.RateUseCase;
        Draft.FxPostingReason = resolution.PostingReason;
    }

    public void ApplyIdentityFxRate()
    {
        var resolution = FxRateResolution.Identity(Draft.JournalDate);
        Draft.FxRate = resolution.Rate;
        Draft.FxEffectiveDate = resolution.EffectiveDate;
        Draft.FxSourceSemantics = resolution.SourceSemantics;
        Draft.FxStatusLabel = resolution.StatusLabel;
        Draft.FxSnapshotId = null;
        Draft.FxRateType = resolution.RateType;
        Draft.FxQuoteBasis = resolution.QuoteBasis;
        Draft.FxRateUseCase = resolution.RateUseCase;
        Draft.FxPostingReason = resolution.PostingReason;
    }

    public void ApplyManualFxRate(decimal fxRate)
    {
        Draft.FxRate = fxRate <= 0 ? 1m : fxRate;
        Draft.FxEffectiveDate = Draft.JournalDate;
        Draft.FxSourceSemantics = FxSourceSemantics.Manual;
        Draft.FxStatusLabel = "Manual rate";
        Draft.FxSnapshotId = null;
        Draft.FxRateType = FxRateType.Spot;
        Draft.FxQuoteBasis = FxQuoteBasis.Direct;
        Draft.FxRateUseCase = FxRateUseCase.General;
        Draft.FxPostingReason = FxPostingReason.Normal;
    }

    public void AddLine() => InsertBlankAfter(Draft.Lines.Count);

    public void InsertBlankAfter(int lineNumber)
    {
        var insertIndex = Math.Clamp(lineNumber, 0, Draft.Lines.Count);
        Draft.Lines.Insert(insertIndex, JournalEntryDraftLine.Blank(insertIndex + 1));
        ReindexLines();
    }

    public void DuplicateLine(int lineNumber)
    {
        var line = Draft.Lines.FirstOrDefault(x => x.LineNumber == lineNumber);
        if (line is null)
        {
            return;
        }

        Draft.Lines.Insert(lineNumber, line.Clone(lineNumber + 1));
        ReindexLines();
    }

    public void DeleteLine(int lineNumber)
    {
        if (Draft.Lines.Count == 1)
        {
            Draft.Lines[0] = JournalEntryDraftLine.Blank(1);
            return;
        }

        var line = Draft.Lines.FirstOrDefault(x => x.LineNumber == lineNumber);
        if (line is null)
        {
            return;
        }

        Draft.Lines.Remove(line);
        ReindexLines();

        while (Draft.Lines.Count < DefaultVisibleRows)
        {
            Draft.Lines.Add(JournalEntryDraftLine.Blank(Draft.Lines.Count + 1));
        }
    }

    public void ClearAllLines()
    {
        Draft.Lines.Clear();
        for (var lineNumber = 1; lineNumber <= DefaultVisibleRows; lineNumber++)
        {
            Draft.Lines.Add(JournalEntryDraftLine.Blank(lineNumber));
        }
    }

    public JournalEntryGridTotals BuildTotals() => JournalEntryGridTotals.FromDraft(Draft);

    public bool ShouldShowBasePreview(decimal? transactionAmount) =>
        Draft.IsForeignCurrency && transactionAmount.GetValueOrDefault() > 0m;

    public decimal? ConvertToBase(decimal? transactionAmount)
    {
        if (!ShouldShowBasePreview(transactionAmount))
        {
            return null;
        }

        return Round2(transactionAmount!.Value * Draft.FxRate);
    }

    public string GetFxSnapshotLabel()
    {
        if (!Draft.IsForeignCurrency)
        {
            return "Identity base-currency posting";
        }

        return Draft.FxSnapshotId.HasValue
            ? $"Snapshot {Draft.FxSnapshotId.Value.ToString("N")[..8]}"
            : "No persisted snapshot";
    }

    public bool IsCurrentSnapshot(Guid snapshotId) =>
        Draft.FxSnapshotId.HasValue && Draft.FxSnapshotId.Value == snapshotId;

    public bool IsCurrentMarketRate(Guid marketRateId, IReadOnlyList<FxSnapshotRecord> snapshots)
    {
        if (!Draft.FxSnapshotId.HasValue)
        {
            return false;
        }

        var currentSnapshot = snapshots.FirstOrDefault(snapshot => snapshot.Id == Draft.FxSnapshotId.Value);
        return currentSnapshot?.SystemMarketRateId == marketRateId;
    }

    public string GetFxReviewTitle() =>
        IsPosted ? "Posted FX review" : "Current FX review";

    public string GetFxReviewCaption()
    {
        if (!Draft.IsForeignCurrency)
        {
            return "This journal entry posts in base currency without FX conversion.";
        }

        return IsPosted
            ? "This posted FX snapshot is now immutable for the journal entry read path."
            : "This is the current FX selection that will become immutable after posting.";
    }

    public Task<IEnumerable<JournalEntryAccountOption>> SearchAccountsAsync(string? term, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(term))
        {
            return Task.FromResult(AccountOptions.Take(8));
        }

        var normalized = term.Trim().ToLowerInvariant();

        var results = AccountOptions
            .Where(option => option.SearchText.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            .Take(8);

        return Task.FromResult(results);
    }

    private void UpdateCurrencyMetadata(string currencyCode)
    {
        if (!TryFindCurrencyOption(currencyCode, out var option))
        {
            return;
        }

        Draft.CurrencyCode = option.Code;
        Draft.CurrencyLabel = option.Label;
        Draft.CurrencyFlag = option.Flag;
    }

    private bool TryFindCurrencyOption(string currencyCode, out JournalEntryCurrencyOption option)
    {
        option = _currencyOptions.FirstOrDefault(x =>
            string.Equals(x.Code, currencyCode, StringComparison.OrdinalIgnoreCase))
            ?? new JournalEntryCurrencyOption
            {
                Code = string.Empty,
                Label = string.Empty,
                Flag = string.Empty,
                DefaultRateToBase = 1m
            };

        return !string.IsNullOrWhiteSpace(option.Code);
    }

    private void ReindexLines()
    {
        for (var index = 0; index < Draft.Lines.Count; index++)
        {
            Draft.Lines[index].LineNumber = index + 1;
        }
    }

    private static decimal Round2(decimal value) =>
        Math.Round(value, 2, MidpointRounding.ToEven);
}
