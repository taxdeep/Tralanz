using Modules.GL.JournalEntry;

namespace Tests.GL;

public sealed class JournalEntryWorkflowTests
{
    private static readonly Guid CompanyId = Guid.Parse("5e492df2-37ab-47df-a1bb-2d559c876cbc");
    private static readonly Guid UserId = Guid.Parse("7bd0e908-cfe7-4f7b-8a0d-f19292e4186d");
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
            new StubFxRateSelectionService());
        var draft = JournalEntryEditorState.CreateDarkModeDemo().Draft;
        draft.Lines[0].Description = "Incomplete";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
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
            new StubFxRateSelectionService());
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
            fxSelectionService);
        var draft = CreateBalancedDraft();
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
            new StubFxRateSelectionService());
        var draft = CreateBalancedDraft();
        draft.Lines[0].DebitAmount = 10m;
        draft.Lines[1].CreditAmount = 9.99m;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
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
            new StubFxRateSelectionService());
        var draft = CreateBalancedDraft();

        var result = await workflow.PostDraftAsync(draft, UserId, CancellationToken.None);

        Assert.NotNull(draftStore.SavedDraft);
        Assert.NotNull(postingStore.PostedDraft);
        Assert.Equal("JE-001001", result.JournalDisplayNumber);
        Assert.Equal("posted", draft.Status);
    }

    private static JournalEntryDraft CreateBalancedDraft()
    {
        var draft = JournalEntryEditorState.CreateDarkModeDemo().Draft;
        draft.CompanyId = CompanyId;
        draft.CurrencyCode = "USD";
        draft.BaseCurrencyCode = "CAD";
        draft.FxRate = 1.25m;
        draft.Lines[0].Account = OfficeExpense;
        draft.Lines[0].DebitAmount = 100m;
        draft.Lines[1].Account = OwnerCapital;
        draft.Lines[1].CreditAmount = 100m;
        return draft;
    }

    private sealed class StubAccountCatalog : IJournalEntryAccountCatalog
    {
        public Task<IReadOnlyList<JournalEntryAccountOption>> ListManualPostingAccountsAsync(
            Guid companyId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<JournalEntryAccountOption>>([OfficeExpense, OwnerCapital]);
    }

    private sealed class StubDraftStore : IJournalEntryDraftStore
    {
        public JournalEntryDraft? SavedDraft { get; private set; }

        public Task<JournalEntryDraftSaveResult> SaveAsync(
            JournalEntryDraft draft,
            Guid userId,
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
            Guid userId,
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
        public bool PersistManualCalled { get; private set; }

        public Task<Engines.FX.FxRateLookup.FxRateSelectionData> LoadAsync(
            Engines.FX.FxRateLookup.FxRateSelectionRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new Engines.FX.FxRateLookup.FxRateSelectionData([], []));

        public Task<SharedKernel.FX.FxRateResolution> UseCompanySnapshotAsync(
            Guid companyId,
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
                request.ProviderKey,
                Guid.NewGuid()));
        }
    }
}
