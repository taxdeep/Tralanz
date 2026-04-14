namespace Modules.GL.JournalEntry;

public sealed class JournalEntryWorkflow : IJournalEntryWorkflow
{
    private readonly IJournalEntryAccountCatalog _accountCatalog;
    private readonly IJournalEntryDraftStore _draftStore;
    private readonly IJournalEntryPostingStore _postingStore;
    private readonly Engines.FX.FxRateLookup.IFxRateSelectionService _fxRateSelectionService;

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
        ValidateDraftShape(draft);
        await EnsureGovernedFxSnapshotAsync(draft, userId, cancellationToken);
        return await _draftStore.SaveAsync(draft, userId, cancellationToken);
    }

    public async Task<JournalEntryPostResult> PostDraftAsync(
        JournalEntryDraft draft,
        Guid userId,
        CancellationToken cancellationToken)
    {
        ValidateDraftShape(draft);

        var meaningfulLines = GetMeaningfulLines(draft).ToArray();
        if (meaningfulLines.Length < 2)
        {
            throw new InvalidOperationException("At least two journal lines are required before posting.");
        }

        var totals = JournalEntryGridTotals.FromDraft(draft);
        if (!totals.IsTransactionBalanced)
        {
            throw new InvalidOperationException("Transaction-currency debit and credit must balance before posting.");
        }

        if (!totals.IsBaseBalanced)
        {
            throw new InvalidOperationException("Base-currency debit and credit must balance before posting.");
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
            throw new InvalidOperationException("A company context is required.");
        }

        foreach (var line in GetMeaningfulLines(draft))
        {
            if (line.Account is null || line.Account.AccountId == Guid.Empty)
            {
                throw new InvalidOperationException($"Line {line.LineNumber} requires an account.");
            }

            var debit = line.DebitAmount ?? 0m;
            var credit = line.CreditAmount ?? 0m;

            if (debit < 0m || credit < 0m)
            {
                throw new InvalidOperationException($"Line {line.LineNumber} cannot contain negative amounts.");
            }

            if ((debit > 0m && credit > 0m) || (debit == 0m && credit == 0m))
            {
                throw new InvalidOperationException($"Line {line.LineNumber} must contain either a debit or a credit.");
            }

            if (!line.Account.AllowManualPosting)
            {
                throw new InvalidOperationException($"Line {line.LineNumber} uses an account that is not allowed for manual posting.");
            }
        }
    }

    private static IEnumerable<JournalEntryDraftLine> GetMeaningfulLines(JournalEntryDraft draft) =>
        draft.Lines.Where(static line => line.HasContent);

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
            throw new InvalidOperationException("FX rate must be greater than zero.");
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
                7),
            draft.FxRate,
            cancellationToken);

        draft.FxSnapshotId = resolution.SnapshotId;
        draft.FxEffectiveDate = resolution.EffectiveDate;
        draft.FxSourceSemantics = resolution.SourceSemantics;
        draft.FxStatusLabel = resolution.StatusLabel;
    }
}
