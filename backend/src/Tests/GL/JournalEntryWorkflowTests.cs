using Modules.Company.MultiCurrency;
using Modules.GL.JournalEntry;
using SharedKernel.Company;

namespace Tests.GL;

public sealed class JournalEntryWorkflowTests
{
    private static readonly CompanyId CompanyId = Guid.Parse("5e492df2-37ab-47df-a1bb-2d559c876cbc");
    private static readonly UserId UserId = Guid.Parse("7bd0e908-cfe7-4f7b-8a0d-f19292e4186d");
    private static readonly JournalEntryAccountOption OfficeExpense = new()
    {
        AccountId = Guid.Parse("60000000-0000-0000-0000-000000000001"),
        Code = "6100",
        Name = "Office Expense",
        TypeLabel = "Operating Expense",
        CurrencyCode = "",
        AllowManualPosting = true
    };
    private static readonly JournalEntryAccountOption OwnerCapital = new()
    {
        AccountId = Guid.Parse("30000000-0000-0000-0000-000000000001"),
        Code = "3100",
        Name = "Owner Capital",
        TypeLabel = "Capital",
        CurrencyCode = "",
        AllowManualPosting = true
    };

    [Fact]
    public async Task SaveDraftAsync_RejectsIncompleteLine()
    {
        var workflow = new JournalEntryWorkflow(
            new StubAccountCatalog(),
            new StubDraftStore(),
            new StubPostingStore(),
            new StubFxRateSelectionService(),
            new StubCompanyCurrencyCatalog());
        var draft = JournalEntryEditorState.CreateDarkModeDemo().Draft;
        draft.BaseCurrencyCode = "USD";
        draft.CurrencyCode = "USD";
        draft.Lines[0].Description = "Incomplete";

        var exception = await Assert.ThrowsAsync<JournalEntryWorkflowException>(() =>
            workflow.SaveDraftAsync(draft, UserId, CancellationToken.None));

        Assert.Equal("Line 1 requires an account.", exception.Message);
    }

    [Fact]
    public async Task SaveDraftAsync_PersistsStructurallyValidDraft()
    {
        var draftStore = new StubDraftStore();
        var workflow = new JournalEntryWorkflow(
            new StubAccountCatalog(),
            draftStore,
            new StubPostingStore(),
            new StubFxRateSelectionService(),
            new StubCompanyCurrencyCatalog());
        var draft = CreateBalancedDraft();
        draft.Lines[1].CreditAmount = 90m;

        var result = await workflow.SaveDraftAsync(draft, UserId, CancellationToken.None);

        Assert.Equal("MJ-000999", result.DocumentNumber);
        Assert.Equal(CompanyId, draftStore.SavedDraft!.CompanyId);
    }

    [Fact]
    public async Task SaveDraftAsync_PersistsManualSnapshotBeforeDraftSave()
    {
        var draftStore = new StubDraftStore();
        var fxSelectionService = new StubFxRateSelectionService();
        var workflow = new JournalEntryWorkflow(
            new StubAccountCatalog(),
            draftStore,
            new StubPostingStore(),
            fxSelectionService,
            new StubCompanyCurrencyCatalog());
        var draft = CreateBalancedDraft();
        draft.CurrencyCode = "CAD";
        draft.BaseCurrencyCode = "USD";
        draft.FxRate = 1.25m;
        draft.FxSourceSemantics = SharedKernel.FX.FxSourceSemantics.Manual;
        draft.FxSnapshotId = null;

        await workflow.SaveDraftAsync(draft, UserId, CancellationToken.None);

        Assert.True(fxSelectionService.PersistManualCalled);
        Assert.NotNull(draft.FxSnapshotId);
        Assert.Equal(SharedKernel.FX.FxSourceSemantics.Manual, draft.FxSourceSemantics);
    }

    [Fact]
    public async Task PostDraftAsync_RejectsUnbalancedTransactionTotals()
    {
        var workflow = new JournalEntryWorkflow(
            new StubAccountCatalog(),
            new StubDraftStore(),
            new StubPostingStore(),
            new StubFxRateSelectionService(),
            new StubCompanyCurrencyCatalog());
        var draft = CreateBalancedDraft();
        draft.Lines[0].DebitAmount = 10m;
        draft.Lines[1].CreditAmount = 9.99m;

        var exception = await Assert.ThrowsAsync<JournalEntryWorkflowException>(() =>
            workflow.PostDraftAsync(draft, UserId, CancellationToken.None));

        Assert.Equal("Transaction-currency debit and credit must balance before posting.", exception.Message);
    }

    [Fact]
    public async Task PostDraftAsync_SavesDraftBeforePosting_WhenDocumentDoesNotExist()
    {
        var draftStore = new StubDraftStore();
        var postingStore = new StubPostingStore();
        var workflow = new JournalEntryWorkflow(
            new StubAccountCatalog(),
            draftStore,
            postingStore,
            new StubFxRateSelectionService(),
            new StubCompanyCurrencyCatalog());
        var draft = CreateBalancedDraft();

        var result = await workflow.PostDraftAsync(draft, UserId, CancellationToken.None);

        Assert.NotNull(draftStore.SavedDraft);
        Assert.NotNull(postingStore.PostedDraft);
        Assert.Equal("JE-001001", result.JournalDisplayNumber);
        Assert.Equal("posted", draft.Status);
    }

    [Fact]
    public async Task SaveDraftAsync_RejectsCurrencyOutsideCompanyGovernance()
    {
        var workflow = new JournalEntryWorkflow(
            new StubAccountCatalog(),
            new StubDraftStore(),
            new StubPostingStore(),
            new StubFxRateSelectionService(),
            new StubCompanyCurrencyCatalog());
        var draft = CreateBalancedDraft();
        draft.CurrencyCode = "EUR";

        var exception = await Assert.ThrowsAsync<JournalEntryWorkflowException>(() =>
            workflow.SaveDraftAsync(draft, UserId, CancellationToken.None));

        Assert.Equal($"Currency EUR is not enabled for company {CompanyId:D}.", exception.Message);
    }

    [Fact]
    public async Task SaveDraftAsync_RejectsBaseCurrencyMismatch()
    {
        var workflow = new JournalEntryWorkflow(
            new StubAccountCatalog(),
            new StubDraftStore(),
            new StubPostingStore(),
            new StubFxRateSelectionService(),
            new StubCompanyCurrencyCatalog());
        var draft = CreateBalancedDraft();
        draft.BaseCurrencyCode = "CAD";

        var exception = await Assert.ThrowsAsync<JournalEntryWorkflowException>(() =>
            workflow.SaveDraftAsync(draft, UserId, CancellationToken.None));

        Assert.Equal("Draft base currency CAD does not match company base currency USD.", exception.Message);
    }

    [Fact]
    public async Task SaveDraftAsync_RejectsBaseCurrencyDraftWithNonIdentityFx()
    {
        var workflow = new JournalEntryWorkflow(
            new StubAccountCatalog(),
            new StubDraftStore(),
            new StubPostingStore(),
            new StubFxRateSelectionService(),
            new StubCompanyCurrencyCatalog());
        var draft = CreateBalancedDraft();
        draft.FxRate = 1.25m;
        draft.FxSourceSemantics = SharedKernel.FX.FxSourceSemantics.Manual;

        var exception = await Assert.ThrowsAsync<JournalEntryWorkflowException>(() =>
            workflow.SaveDraftAsync(draft, UserId, CancellationToken.None));

        Assert.Equal("Base-currency journal entries must use identity FX semantics.", exception.Message);
    }

    [Fact]
    public async Task SaveDraftAsync_RejectsForeignCurrencyDraftWithoutPersistedSnapshot()
    {
        var workflow = new JournalEntryWorkflow(
            new StubAccountCatalog(),
            new StubDraftStore(),
            new StubPostingStore(),
            new StubFxRateSelectionService(persistManualSnapshot: false),
            new StubCompanyCurrencyCatalog());
        var draft = CreateBalancedDraft();
        draft.CurrencyCode = "CAD";
        draft.BaseCurrencyCode = "USD";
        draft.FxRate = 1.25m;
        draft.FxSourceSemantics = SharedKernel.FX.FxSourceSemantics.Manual;
        draft.FxSnapshotId = null;

        var exception = await Assert.ThrowsAsync<JournalEntryWorkflowException>(() =>
            workflow.SaveDraftAsync(draft, UserId, CancellationToken.None));

        Assert.Equal("Foreign-currency journal entries require a persisted FX snapshot before save or post.", exception.Message);
    }

    [Fact]
    public async Task SaveDraftAsync_RejectsForeignCurrencyDraftWithIdentitySemantics()
    {
        var workflow = new JournalEntryWorkflow(
            new StubAccountCatalog(),
            new StubDraftStore(),
            new StubPostingStore(),
            new StubFxRateSelectionService(),
            new StubCompanyCurrencyCatalog());
        var draft = CreateBalancedDraft();
        draft.CurrencyCode = "CAD";
        draft.BaseCurrencyCode = "USD";
        draft.FxRate = 1.25m;
        draft.FxSourceSemantics = SharedKernel.FX.FxSourceSemantics.Identity;

        var exception = await Assert.ThrowsAsync<JournalEntryWorkflowException>(() =>
            workflow.SaveDraftAsync(draft, UserId, CancellationToken.None));

        Assert.Equal("Foreign-currency journal entries cannot use identity FX semantics.", exception.Message);
    }

    [Fact]
    public async Task SaveDraftAsync_RejectsSettlementUseCaseForManualJournal()
    {
        var workflow = new JournalEntryWorkflow(
            new StubAccountCatalog(),
            new StubDraftStore(),
            new StubPostingStore(),
            new StubFxRateSelectionService(),
            new StubCompanyCurrencyCatalog());
        var draft = CreateBalancedDraft();
        draft.CurrencyCode = "CAD";
        draft.BaseCurrencyCode = "USD";
        draft.FxRate = 1.25m;
        draft.FxSourceSemantics = SharedKernel.FX.FxSourceSemantics.SystemStored;
        draft.FxSnapshotId = Guid.NewGuid();
        draft.FxEffectiveDate = draft.JournalDate;
        draft.FxRateUseCase = SharedKernel.FX.FxRateUseCase.Settlement;

        var exception = await Assert.ThrowsAsync<JournalEntryWorkflowException>(() =>
            workflow.SaveDraftAsync(draft, UserId, CancellationToken.None));

        Assert.Equal("Manual journal entry FX use case must stay general.", exception.Message);
    }

    [Fact]
    public async Task SaveDraftAsync_RejectsFxEffectiveDateAfterJournalDate()
    {
        var workflow = new JournalEntryWorkflow(
            new StubAccountCatalog(),
            new StubDraftStore(),
            new StubPostingStore(),
            new StubFxRateSelectionService(),
            new StubCompanyCurrencyCatalog());
        var draft = CreateBalancedDraft();
        draft.CurrencyCode = "CAD";
        draft.BaseCurrencyCode = "USD";
        draft.FxRate = 1.25m;
        draft.FxSourceSemantics = SharedKernel.FX.FxSourceSemantics.SystemStored;
        draft.FxSnapshotId = Guid.NewGuid();
        draft.FxEffectiveDate = draft.JournalDate.AddDays(1);

        var exception = await Assert.ThrowsAsync<JournalEntryWorkflowException>(() =>
            workflow.SaveDraftAsync(draft, UserId, CancellationToken.None));

        Assert.Equal("FX effective date cannot be later than the journal date.", exception.Message);
    }

    private static JournalEntryDraft CreateBalancedDraft()
    {
        var draft = JournalEntryEditorState.CreateDarkModeDemo().Draft;
        draft.CompanyId = CompanyId;
        draft.CurrencyCode = "USD";
        draft.BaseCurrencyCode = "USD";
        draft.FxRate = 1m;
        draft.FxSourceSemantics = SharedKernel.FX.FxSourceSemantics.Identity;
        draft.FxSnapshotId = null;
        draft.FxEffectiveDate = draft.JournalDate;
        draft.Lines[0].Account = OfficeExpense;
        draft.Lines[0].DebitAmount = 100m;
        draft.Lines[1].Account = OwnerCapital;
        draft.Lines[1].CreditAmount = 100m;
        return draft;
    }

    private sealed class StubAccountCatalog : IJournalEntryAccountCatalog
    {
        public Task<IReadOnlyList<JournalEntryAccountOption>> ListManualPostingAccountsAsync(
            CompanyId companyId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<JournalEntryAccountOption>>([OfficeExpense, OwnerCapital]);
    }

    private sealed class StubDraftStore : IJournalEntryDraftStore
    {
        public JournalEntryDraft? SavedDraft { get; private set; }

        public Task<JournalEntryDraftSaveResult> SaveAsync(
            JournalEntryDraft draft,
            UserId userId,
            CancellationToken cancellationToken)
        {
            SavedDraft = draft;
            return Task.FromResult(new JournalEntryDraftSaveResult(
                Guid.NewGuid(),
                "MJ-000999",
                "draft"));
        }
    }

    private sealed class StubPostingStore : IJournalEntryPostingStore
    {
        public JournalEntryDraft? PostedDraft { get; private set; }

        public Task<JournalEntryPostResult> PostAsync(
            JournalEntryDraft draft,
            UserId userId,
            CancellationToken cancellationToken)
        {
            PostedDraft = draft;
            return Task.FromResult(new JournalEntryPostResult(
                draft.DocumentId ?? Guid.NewGuid(),
                draft.DocumentNumber,
                Guid.NewGuid(),
                "JE-001001"));
        }
    }

    private sealed class StubFxRateSelectionService : Engines.FX.FxRateLookup.IFxRateSelectionService
    {
        private readonly bool _persistManualSnapshot;

        public bool PersistManualCalled { get; private set; }

        public StubFxRateSelectionService(bool persistManualSnapshot = true)
        {
            _persistManualSnapshot = persistManualSnapshot;
        }

        public Task<Engines.FX.FxRateLookup.FxRateSelectionData> LoadAsync(
            Engines.FX.FxRateLookup.FxRateSelectionRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new Engines.FX.FxRateLookup.FxRateSelectionData([], []));

        public Task<SharedKernel.FX.FxRateResolution> UseCompanySnapshotAsync(
            CompanyId companyId,
            Guid snapshotId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SharedKernel.FX.FxRateResolution> UseMarketRateAsync(
            Engines.FX.FxRateLookup.FxRateSelectionRequest request,
            Guid marketRateId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SharedKernel.FX.FxRateResolution> PersistManualSnapshotAsync(
            Engines.FX.FxRateLookup.FxRateSelectionRequest request,
            decimal rate,
            CancellationToken cancellationToken)
        {
            PersistManualCalled = true;
            return Task.FromResult(new SharedKernel.FX.FxRateResolution(
                rate,
                request.RequestedDate,
                request.RequestedDate,
                SharedKernel.FX.FxSourceSemantics.Manual,
                "Manual company snapshot",
                request.RateType,
                request.QuoteBasis,
                request.RateUseCase,
                request.PostingReason,
                request.ProviderKey,
                _persistManualSnapshot ? Guid.NewGuid() : null));
        }
    }

    private sealed class StubCompanyCurrencyCatalog : ICompanyCurrencyCatalog
    {
        public Task<CompanyCurrencyProfile> GetProfileAsync(CompanyId companyId, CancellationToken cancellationToken) =>
            Task.FromResult(new CompanyCurrencyProfile(
                companyId,
                "Northwind Studio Ltd.",
                "USD",
                true,
                [
                    new CompanyCurrencyOption("USD", "US Dollar", true, true),
                    new CompanyCurrencyOption("CAD", "Canadian Dollar", false, true)
                ]));
    }
}
