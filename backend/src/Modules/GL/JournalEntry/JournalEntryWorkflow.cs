using Modules.Company.MultiCurrency;

namespace Modules.GL.JournalEntry;

public sealed class JournalEntryWorkflowException : InvalidOperationException
{
    public JournalEntryWorkflowException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

public sealed class JournalEntryWorkflow : IJournalEntryWorkflow
{
    private readonly IJournalEntryAccountCatalog _accountCatalog;
    private readonly IJournalEntryDraftStore _draftStore;
    private readonly IJournalEntryPostingStore _postingStore;
    private readonly Engines.FX.FxRateLookup.IFxRateSelectionService _fxRateSelectionService;
    private readonly ICompanyCurrencyCatalog? _companyCurrencyCatalog;

    public JournalEntryWorkflow(
        IJournalEntryAccountCatalog accountCatalog,
        IJournalEntryDraftStore draftStore,
        IJournalEntryPostingStore postingStore,
        Engines.FX.FxRateLookup.IFxRateSelectionService fxRateSelectionService)
    {
        _accountCatalog = accountCatalog ?? throw new ArgumentNullException(nameof(accountCatalog));
        _draftStore = draftStore ?? throw new ArgumentNullException(nameof(draftStore));
        _postingStore = postingStore ?? throw new ArgumentNullException(nameof(postingStore));
        _fxRateSelectionService = fxRateSelectionService ?? throw new ArgumentNullException(nameof(fxRateSelectionService));
        _companyCurrencyCatalog = null;
    }

    public JournalEntryWorkflow(
        IJournalEntryAccountCatalog accountCatalog,
        IJournalEntryDraftStore draftStore,
        IJournalEntryPostingStore postingStore,
        Engines.FX.FxRateLookup.IFxRateSelectionService fxRateSelectionService,
        ICompanyCurrencyCatalog companyCurrencyCatalog)
        : this(accountCatalog, draftStore, postingStore, fxRateSelectionService)
    {
        _companyCurrencyCatalog = companyCurrencyCatalog ?? throw new ArgumentNullException(nameof(companyCurrencyCatalog));
    }

    public Task<IReadOnlyList<JournalEntryAccountOption>> LoadAccountOptionsAsync(
        Guid companyId,
        CancellationToken cancellationToken) =>
        _accountCatalog.ListManualPostingAccountsAsync(companyId, cancellationToken);

    public Task<JournalEntryDraftSaveResult> SaveDraftAsync(
        JournalEntryDraft draft,
        Guid userId,
        CancellationToken cancellationToken) =>
        SaveDraftCoreAsync(draft, userId, cancellationToken);

    private async Task<JournalEntryDraftSaveResult> SaveDraftCoreAsync(
        JournalEntryDraft draft,
        Guid userId,
        CancellationToken cancellationToken)
    {
        await ValidateGovernedCurrencyAsync(draft, cancellationToken);
        ValidateDraftShape(draft);
        ValidateGovernedFxState(draft);
        await EnsureGovernedFxSnapshotAsync(draft, userId, cancellationToken);
        ValidatePersistedFxState(draft);
        return await _draftStore.SaveAsync(draft, userId, cancellationToken);
    }

    public async Task<JournalEntryPostResult> PostDraftAsync(
        JournalEntryDraft draft,
        Guid userId,
        CancellationToken cancellationToken)
    {
        await ValidateGovernedCurrencyAsync(draft, cancellationToken);
        ValidateDraftShape(draft);
        ValidateGovernedFxState(draft);

        var meaningfulLines = GetMeaningfulLines(draft).ToArray();
        if (meaningfulLines.Length < 2)
        {
            throw CreateWorkflowException("invalid_draft_shape", "At least two journal lines are required before posting.");
        }

        var totals = JournalEntryGridTotals.FromDraft(draft);
        if (!totals.IsTransactionBalanced)
        {
            throw CreateWorkflowException("invalid_draft_shape", "Transaction-currency debit and credit must balance before posting.");
        }

        if (!totals.IsBaseBalanced)
        {
            throw CreateWorkflowException("invalid_draft_shape", "Base-currency debit and credit must balance before posting.");
        }

        if (draft.DocumentId is null)
        {
            var saved = await SaveDraftCoreAsync(draft, userId, cancellationToken);
            draft.DocumentId = saved.DocumentId;
            draft.DocumentNumber = saved.DocumentNumber;
            draft.Status = saved.Status;
        }

        var result = await _postingStore.PostAsync(draft, userId, cancellationToken);
        draft.Status = "posted";
        draft.JournalNumber = result.JournalDisplayNumber;
        return result;
    }

    private static void ValidateDraftShape(JournalEntryDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (draft.CompanyId == Guid.Empty)
        {
            throw CreateWorkflowException("invalid_draft_shape", "A company context is required.");
        }

        foreach (var line in GetMeaningfulLines(draft))
        {
            if (line.Account is null || line.Account.AccountId == Guid.Empty)
            {
                throw CreateWorkflowException("invalid_draft_shape", $"Line {line.LineNumber} requires an account.");
            }

            var debit = line.DebitAmount ?? 0m;
            var credit = line.CreditAmount ?? 0m;

            if (debit < 0m || credit < 0m)
            {
                throw CreateWorkflowException("invalid_draft_shape", $"Line {line.LineNumber} cannot contain negative amounts.");
            }

            if ((debit > 0m && credit > 0m) || (debit == 0m && credit == 0m))
            {
                throw CreateWorkflowException("invalid_draft_shape", $"Line {line.LineNumber} must contain either a debit or a credit.");
            }

            if (!line.Account.AllowManualPosting)
            {
                throw CreateWorkflowException("invalid_draft_shape", $"Line {line.LineNumber} uses an account that is not allowed for manual posting.");
            }
        }
    }

    private static IEnumerable<JournalEntryDraftLine> GetMeaningfulLines(JournalEntryDraft draft) =>
        draft.Lines.Where(static line => line.HasContent);

    private async Task ValidateGovernedCurrencyAsync(
        JournalEntryDraft draft,
        CancellationToken cancellationToken)
    {
        if (_companyCurrencyCatalog is null)
        {
            return;
        }

        var profile = await _companyCurrencyCatalog.GetProfileAsync(draft.CompanyId, cancellationToken);

        if (!string.Equals(draft.BaseCurrencyCode, profile.BaseCurrencyCode, StringComparison.OrdinalIgnoreCase))
        {
            throw CreateWorkflowException(
                "invalid_currency_configuration",
                $"Draft base currency {draft.BaseCurrencyCode} does not match company base currency {profile.BaseCurrencyCode}.");
        }

        if (!profile.IsCurrencyEnabled(draft.CurrencyCode))
        {
            throw CreateWorkflowException(
                "invalid_currency_configuration",
                $"Currency {draft.CurrencyCode} is not enabled for company {draft.CompanyId:D}.");
        }
    }

    private async Task EnsureGovernedFxSnapshotAsync(
        JournalEntryDraft draft,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (!draft.IsForeignCurrency)
        {
            return;
        }

        if (draft.FxRate <= 0m)
        {
            throw CreateWorkflowException("invalid_fx_configuration", "FX rate must be greater than zero.");
        }

        if (draft.FxSourceSemantics != SharedKernel.FX.FxSourceSemantics.Manual || draft.FxSnapshotId.HasValue)
        {
            return;
        }

        var resolution = await _fxRateSelectionService.PersistManualSnapshotAsync(
            new Engines.FX.FxRateLookup.FxRateSelectionRequest(
                draft.CompanyId,
                userId,
                draft.CurrencyCode,
                draft.BaseCurrencyCode,
                draft.JournalDate,
                draft.FxProviderKey,
                7,
                draft.FxRateType,
                draft.FxQuoteBasis,
                draft.FxRateUseCase,
                draft.FxPostingReason),
            draft.FxRate,
            cancellationToken);

        draft.FxSnapshotId = resolution.SnapshotId;
        draft.FxEffectiveDate = resolution.EffectiveDate;
        draft.FxSourceSemantics = resolution.SourceSemantics;
        draft.FxStatusLabel = resolution.StatusLabel;
        draft.FxRateType = resolution.RateType;
        draft.FxQuoteBasis = resolution.QuoteBasis;
        draft.FxRateUseCase = resolution.RateUseCase;
        draft.FxPostingReason = resolution.PostingReason;
    }

    private static void ValidateGovernedFxState(JournalEntryDraft draft)
    {
        if (!IsSupportedRateType(draft.FxRateType))
        {
            throw CreateWorkflowException("invalid_fx_configuration", $"Unsupported FX rate type {draft.FxRateType}.");
        }

        if (!IsSupportedQuoteBasis(draft.FxQuoteBasis))
        {
            throw CreateWorkflowException("invalid_fx_configuration", $"Unsupported FX quote basis {draft.FxQuoteBasis}.");
        }

        if (!IsSupportedRateUseCase(draft.FxRateUseCase))
        {
            throw CreateWorkflowException("invalid_fx_configuration", $"Unsupported FX rate use case {draft.FxRateUseCase}.");
        }

        if (!IsSupportedPostingReason(draft.FxPostingReason))
        {
            throw CreateWorkflowException("invalid_fx_configuration", $"Unsupported FX posting reason {draft.FxPostingReason}.");
        }

        if (draft.IsForeignCurrency)
        {
            if (draft.FxRate <= 0m)
            {
                throw CreateWorkflowException("invalid_fx_configuration", "FX rate must be greater than zero.");
            }

            if (string.Equals(draft.FxSourceSemantics, SharedKernel.FX.FxSourceSemantics.Identity, StringComparison.OrdinalIgnoreCase))
            {
                throw CreateWorkflowException("invalid_fx_configuration", "Foreign-currency journal entries cannot use identity FX semantics.");
            }

            if (!string.Equals(draft.FxRateUseCase, SharedKernel.FX.FxRateUseCase.General, StringComparison.OrdinalIgnoreCase))
            {
                throw CreateWorkflowException("invalid_fx_configuration", "Manual journal entry FX use case must stay general.");
            }

            if (!string.Equals(draft.FxPostingReason, SharedKernel.FX.FxPostingReason.Normal, StringComparison.OrdinalIgnoreCase))
            {
                throw CreateWorkflowException("invalid_fx_configuration", "Manual journal entry FX posting reason must stay normal.");
            }

            return;
        }

        if (draft.FxSnapshotId.HasValue)
        {
            throw CreateWorkflowException("invalid_fx_configuration", "Base-currency journal entries cannot carry an FX snapshot.");
        }

        if (!string.Equals(draft.FxSourceSemantics, SharedKernel.FX.FxSourceSemantics.Identity, StringComparison.OrdinalIgnoreCase))
        {
            throw CreateWorkflowException("invalid_fx_configuration", "Base-currency journal entries must use identity FX semantics.");
        }

        if (draft.FxRate != 1m)
        {
            throw CreateWorkflowException("invalid_fx_configuration", "Base-currency journal entries must use an FX rate of 1.");
        }
    }

    private static void ValidatePersistedFxState(JournalEntryDraft draft)
    {
        if (!draft.IsForeignCurrency)
        {
            return;
        }

        if (!draft.FxSnapshotId.HasValue)
        {
            throw CreateWorkflowException("invalid_fx_configuration", "Foreign-currency journal entries require a persisted FX snapshot before save or post.");
        }

        if (draft.FxEffectiveDate == default)
        {
            throw CreateWorkflowException("invalid_fx_configuration", "Foreign-currency journal entries require an FX effective date.");
        }

        if (draft.FxEffectiveDate > draft.JournalDate)
        {
            throw CreateWorkflowException("invalid_fx_configuration", "FX effective date cannot be later than the journal date.");
        }
    }

    private static JournalEntryWorkflowException CreateWorkflowException(string errorCode, string message) =>
        new(errorCode, message);

    private static bool IsSupportedRateType(string value) =>
        value is SharedKernel.FX.FxRateType.Spot
            or SharedKernel.FX.FxRateType.Closing
            or SharedKernel.FX.FxRateType.Average
            or SharedKernel.FX.FxRateType.Historical
            or SharedKernel.FX.FxRateType.Custom;

    private static bool IsSupportedQuoteBasis(string value) =>
        value is SharedKernel.FX.FxQuoteBasis.Direct
            or SharedKernel.FX.FxQuoteBasis.Inverse;

    private static bool IsSupportedRateUseCase(string value) =>
        value is SharedKernel.FX.FxRateUseCase.General
            or SharedKernel.FX.FxRateUseCase.Settlement
            or SharedKernel.FX.FxRateUseCase.Remeasurement
            or SharedKernel.FX.FxRateUseCase.Translation;

    private static bool IsSupportedPostingReason(string value) =>
        value is SharedKernel.FX.FxPostingReason.Normal
            or SharedKernel.FX.FxPostingReason.Settlement
            or SharedKernel.FX.FxPostingReason.Revaluation
            or SharedKernel.FX.FxPostingReason.Translation
            or SharedKernel.FX.FxPostingReason.Adjustment;
}
