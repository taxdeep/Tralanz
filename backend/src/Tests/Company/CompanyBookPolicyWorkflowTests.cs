using Modules.Company.MultiBook;
using SharedKernel.Company;

namespace Tests.Company;

public sealed class CompanyBookPolicyWorkflowTests
{
    private static readonly Guid CompanyId = Guid.Parse("6ec42a06-eabf-47bc-8e56-8b2ee0f9a4b2");

    [Fact]
    public async Task EnsureDefaultPrimaryBookPolicyAsync_SeedsGovernedDefaultsWhenMissing()
    {
        var store = new StubStore();
        var workflow = new CompanyBookPolicyWorkflow(store);

        var result = await workflow.EnsureDefaultPrimaryBookPolicyAsync(
            CompanyId,
            Guid.NewGuid(),
            new DateOnly(2026, 4, 14),
            CancellationToken.None);

        Assert.True(result.WasProvisioned);
        Assert.Equal("PRIMARY", result.Book.BookCode);
        Assert.Equal("ASPE", result.Book.AccountingStandard);
        Assert.Equal("closing", result.RemeasurementPolicy.RateType);
        Assert.Equal("remeasurement", result.RemeasurementPolicy.RateUseCase);
        Assert.Equal("revaluation", result.RemeasurementPolicy.PostingReason);
    }

    [Fact]
    public async Task GetDefaultRemeasurementPolicyAsync_ReturnsExistingGovernedPolicy()
    {
        var existing = CreateResult(wasProvisioned: false);
        var store = new StubStore(existing);
        var workflow = new CompanyBookPolicyWorkflow(store);

        var result = await workflow.GetDefaultRemeasurementPolicyAsync(
            CompanyId,
            new DateOnly(2026, 4, 14),
            CancellationToken.None);

        Assert.False(result.WasProvisioned);
        Assert.Equal(existing.Book.BookId, result.Book.BookId);
        Assert.Equal(existing.RemeasurementPolicy.PolicyId, result.RemeasurementPolicy.PolicyId);
    }

    [Fact]
    public async Task GetRemeasurementPolicyAsync_WithExplicitBookId_UsesBookScopedLookup()
    {
        var existing = CreateResult(wasProvisioned: false);
        var store = new StubStore(existing);
        var workflow = new CompanyBookPolicyWorkflow(store);

        var result = await workflow.GetRemeasurementPolicyAsync(
            CompanyId,
            existing.Book.BookId,
            new DateOnly(2026, 4, 14),
            CancellationToken.None);

        Assert.Equal(existing.Book.BookId, result.Book.BookId);
        Assert.Equal(existing.RemeasurementPolicy.PolicyId, result.RemeasurementPolicy.PolicyId);
    }

    [Fact]
    public async Task ListBookGovernanceAsync_WithoutPostedHistory_AllowsDirectEdit()
    {
        var state = CreateState(hasCompanyPostedHistory: false, hasBookSpecificRevaluationHistory: false);
        var store = new StubStore(bookGovernance: [state]);
        var workflow = new CompanyBookPolicyWorkflow(store);

        var result = await workflow.ListBookGovernanceAsync(
            CompanyId,
            new DateOnly(2026, 4, 14),
            CancellationToken.None);

        var single = Assert.Single(result);
        Assert.True(single.MigrationEligibility.DirectEditAllowed);
        Assert.Equal("direct_edit_allowed", single.MigrationEligibility.ChangeMode);
    }

    [Fact]
    public async Task ListBookGovernanceAsync_WithPostedHistory_RequiresGovernedMigration()
    {
        var state = CreateState(hasCompanyPostedHistory: true, hasBookSpecificRevaluationHistory: true);
        var store = new StubStore(bookGovernance: [state]);
        var workflow = new CompanyBookPolicyWorkflow(store);

        var result = await workflow.ListBookGovernanceAsync(
            CompanyId,
            new DateOnly(2026, 4, 14),
            CancellationToken.None);

        var single = Assert.Single(result);
        Assert.False(single.MigrationEligibility.DirectEditAllowed);
        Assert.Equal("governed_migration_required", single.MigrationEligibility.ChangeMode);
        Assert.True(single.MigrationEligibility.HasBookSpecificRevaluationHistory);
    }

    [Fact]
    public async Task PreviewGovernedChangeAsync_WithoutPostedHistory_AllowsDirectUpdate()
    {
        var state = CreateState(hasCompanyPostedHistory: false, hasBookSpecificRevaluationHistory: false);
        var store = new StubStore(bookGovernance: [state]);
        var workflow = new CompanyBookPolicyWorkflow(store);

        var result = await workflow.PreviewGovernedChangeAsync(
            CompanyId,
            state.Book.BookId,
            new DateOnly(2026, 4, 14),
            new CompanyBookProposedChangeSet(
                IsPrimary: true,
                AccountingStandard: "IFRS",
                BookBaseCurrencyCode: null,
                FunctionalCurrencyCode: null,
                PresentationCurrencyCode: null,
                RateType: null,
                QuoteBasis: null,
                RateUseCase: null,
                PostingReason: null,
                RevaluationProfile: null,
                FxRoundingPolicy: null),
            CancellationToken.None);

        Assert.True(result.ChangeImpact.HasAnyChange);
        Assert.True(result.ChangeImpact.DirectUpdateAllowed);
        Assert.False(result.ChangeImpact.GovernedMigrationRequired);
        Assert.Equal("direct_update_in_place", result.ChangeImpact.RecommendedPath);
        Assert.Contains("accounting_standard", result.ChangeImpact.ChangedFields);
    }

    [Fact]
    public async Task PreviewGovernedChangeAsync_WithPostedHistoryAndRemeasurementHistory_RequiresNewBookPath()
    {
        var state = CreateState(hasCompanyPostedHistory: true, hasBookSpecificRevaluationHistory: true);
        var store = new StubStore(bookGovernance: [state]);
        var workflow = new CompanyBookPolicyWorkflow(store);

        var result = await workflow.PreviewGovernedChangeAsync(
            CompanyId,
            state.Book.BookId,
            new DateOnly(2026, 4, 14),
            new CompanyBookProposedChangeSet(
                IsPrimary: null,
                AccountingStandard: null,
                BookBaseCurrencyCode: null,
                FunctionalCurrencyCode: null,
                PresentationCurrencyCode: null,
                RateType: "average",
                QuoteBasis: null,
                RateUseCase: null,
                PostingReason: null,
                RevaluationProfile: null,
                FxRoundingPolicy: null),
            CancellationToken.None);

        Assert.True(result.ChangeImpact.HasAnyChange);
        Assert.False(result.ChangeImpact.DirectUpdateAllowed);
        Assert.True(result.ChangeImpact.GovernedMigrationRequired);
        Assert.Equal("new_secondary_or_adjustment_book", result.ChangeImpact.RecommendedPath);
        Assert.Contains("fx_policy_governance", result.ChangeImpact.ChangeCategories);
    }

    [Fact]
    public async Task PreviewGovernedChangeAsync_WithClosedPeriodSignal_RequiresNewBookPath()
    {
        var state = CreateState(
            hasCompanyPostedHistory: true,
            hasBookSpecificRevaluationHistory: false,
            governanceSignals: new CompanyBookGovernanceSignalSummary(
                HasClosedPeriods: true,
                HasIssuedReports: false,
                HasFiledTax: false,
                Signals:
                [
                    new CompanyBookGovernanceSignalRecord(
                        Guid.NewGuid(),
                        CompanyId,
                        Guid.NewGuid(),
                        "closed_period",
                        new DateOnly(2026, 3, 31),
                        "2026-Q1 closed",
                        null,
                        null,
                        DateTimeOffset.UtcNow)
                ]));
        var store = new StubStore(bookGovernance: [state]);
        var workflow = new CompanyBookPolicyWorkflow(store);

        var result = await workflow.PreviewGovernedChangeAsync(
            CompanyId,
            state.Book.BookId,
            new DateOnly(2026, 4, 14),
            new CompanyBookProposedChangeSet(
                IsPrimary: null,
                AccountingStandard: "IFRS",
                BookBaseCurrencyCode: null,
                FunctionalCurrencyCode: null,
                PresentationCurrencyCode: null,
                RateType: null,
                QuoteBasis: null,
                RateUseCase: null,
                PostingReason: null,
                RevaluationProfile: null,
                FxRoundingPolicy: null),
            CancellationToken.None);

        Assert.True(result.ChangeImpact.GovernedMigrationRequired);
        Assert.Equal("new_secondary_or_adjustment_book", result.ChangeImpact.RecommendedPath);
        Assert.Equal("formal_governance_signals", result.ChangeImpact.EvaluationBasis);
    }

    [Fact]
    public async Task CreateGovernanceSignalAsync_WhenBookExists_ReturnsSignalAndUpdatedSummary()
    {
        var state = CreateState(hasCompanyPostedHistory: true, hasBookSpecificRevaluationHistory: false);
        var store = new StubStore(bookGovernance: [state]);
        var workflow = new CompanyBookPolicyWorkflow(store);

        var result = await workflow.CreateGovernanceSignalAsync(
            CompanyId,
            state.Book.BookId,
            "closed_period",
            new DateOnly(2026, 3, 31),
            "2026-Q1 close",
            "Controller approved close lock.",
            Guid.Parse("a45f1b27-3fb3-4493-8f7f-1e894f144f8a"),
            CancellationToken.None);

        Assert.Equal("closed_period", result.Signal.SignalType);
        Assert.Equal(state.Book.BookId, result.Signal.BookId);
        Assert.True(result.Summary.HasClosedPeriods);
        Assert.False(result.Summary.HasIssuedReports);
        Assert.False(result.Summary.HasFiledTax);
        Assert.Contains(result.Summary.Signals, signal => signal.SignalId == result.Signal.SignalId);
    }

    [Fact]
    public async Task CreateGovernanceSignalAsync_WhenDuplicateSignalExists_Rejects()
    {
        var seedState = CreateState(hasCompanyPostedHistory: true, hasBookSpecificRevaluationHistory: false);
        var existingSignal = new CompanyBookGovernanceSignalRecord(
            Guid.NewGuid(),
            CompanyId,
            seedState.Book.BookId,
            "reported_statement",
            new DateOnly(2026, 4, 5),
            "FY2025 statements",
            null,
            null,
            DateTimeOffset.UtcNow);
        var state = new CompanyBookGovernanceState(
            seedState.Book,
            seedState.RemeasurementPolicy,
            true,
            false,
            new CompanyBookGovernanceSignalSummary(
                HasClosedPeriods: false,
                HasIssuedReports: true,
                HasFiledTax: false,
                Signals: [existingSignal]));
        var store = new StubStore(bookGovernance: [state]);
        var workflow = new CompanyBookPolicyWorkflow(store);
        var seededSummary = await workflow.GetGovernanceSignalsAsync(
            CompanyId,
            state.Book.BookId,
            new DateOnly(2026, 4, 5),
            CancellationToken.None);

        Assert.Single(seededSummary.Signals);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.CreateGovernanceSignalAsync(
                CompanyId,
                state.Book.BookId,
                "reported_statement",
                new DateOnly(2026, 4, 5),
                "FY2025 statements",
                null,
                Guid.NewGuid(),
                CancellationToken.None));

        Assert.Contains("already registered", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RegisterClosedPeriodAsync_WithoutLabel_UsesDefaultReferenceLabel()
    {
        var state = CreateState(hasCompanyPostedHistory: true, hasBookSpecificRevaluationHistory: false);
        var store = new StubStore(bookGovernance: [state]);
        var workflow = new CompanyBookPolicyWorkflow(store);

        var result = await workflow.RegisterClosedPeriodAsync(
            CompanyId,
            state.Book.BookId,
            new DateOnly(2026, 3, 31),
            referenceLabel: null,
            notes: null,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.Equal("closed_period", result.Signal.SignalType);
        Assert.Equal("Period close 2026-03-31", result.Signal.ReferenceLabel);
    }

    [Fact]
    public async Task RegisterIssuedStatementAsync_WithoutStatementLabel_Rejects()
    {
        var state = CreateState(hasCompanyPostedHistory: true, hasBookSpecificRevaluationHistory: false);
        var store = new StubStore(bookGovernance: [state]);
        var workflow = new CompanyBookPolicyWorkflow(store);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.RegisterIssuedStatementAsync(
                CompanyId,
                state.Book.BookId,
                new DateOnly(2026, 4, 15),
                "   ",
                null,
                Guid.NewGuid(),
                CancellationToken.None));

        Assert.Contains("Issued statement label is required", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PrepareGovernedChangeRequestDraftAsync_WithPostedHistory_CreatesDraft()
    {
        var state = CreateState(hasCompanyPostedHistory: true, hasBookSpecificRevaluationHistory: false);
        var store = new StubStore(bookGovernance: [state]);
        var workflow = new CompanyBookPolicyWorkflow(store);

        var result = await workflow.PrepareGovernedChangeRequestDraftAsync(
            CompanyId,
            Guid.Parse("2f27875f-af35-42fb-86a6-f36f3a8ea7b9"),
            state.Book.BookId,
            new DateOnly(2026, 4, 14),
            new DateOnly(2026, 5, 1),
            new CompanyBookProposedChangeSet(
                IsPrimary: null,
                AccountingStandard: "IFRS",
                BookBaseCurrencyCode: null,
                FunctionalCurrencyCode: null,
                PresentationCurrencyCode: null,
                RateType: null,
                QuoteBasis: null,
                RateUseCase: null,
                PostingReason: null,
                RevaluationProfile: null,
                FxRoundingPolicy: null),
            CancellationToken.None);

        Assert.Equal("draft", result.Status);
        Assert.Equal("future_dated_cutover_or_new_book", result.RequestedAction);
        Assert.Equal(new DateOnly(2026, 5, 1), result.EffectiveFrom);
        Assert.Equal("IFRS", result.Preview.ProposedChanges.AccountingStandard);
    }

    [Fact]
    public async Task PrepareGovernedChangeRequestDraftAsync_WithoutChange_Rejects()
    {
        var state = CreateState(hasCompanyPostedHistory: false, hasBookSpecificRevaluationHistory: false);
        var store = new StubStore(bookGovernance: [state]);
        var workflow = new CompanyBookPolicyWorkflow(store);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.PrepareGovernedChangeRequestDraftAsync(
                CompanyId,
                Guid.NewGuid(),
                state.Book.BookId,
                new DateOnly(2026, 4, 14),
                new DateOnly(2026, 4, 14),
                new CompanyBookProposedChangeSet(
                    IsPrimary: state.Book.IsPrimary,
                    AccountingStandard: state.Book.AccountingStandard,
                    BookBaseCurrencyCode: state.Book.BookBaseCurrencyCode,
                    FunctionalCurrencyCode: state.Book.FunctionalCurrencyCode,
                    PresentationCurrencyCode: state.Book.PresentationCurrencyCode,
                    RateType: state.RemeasurementPolicy?.RateType,
                    QuoteBasis: state.RemeasurementPolicy?.QuoteBasis,
                    RateUseCase: state.RemeasurementPolicy?.RateUseCase,
                    PostingReason: state.RemeasurementPolicy?.PostingReason,
                    RevaluationProfile: state.RemeasurementPolicy?.RevaluationProfile,
                    FxRoundingPolicy: state.RemeasurementPolicy?.FxRoundingPolicy),
                CancellationToken.None));

        Assert.Contains("at least one book-governing change", exception.Message);
    }

    [Fact]
    public async Task SubmitGovernedChangeRequestDraftAsync_FromDraft_MarksSubmitted()
    {
        var state = CreateState(hasCompanyPostedHistory: true, hasBookSpecificRevaluationHistory: false);
        var store = new StubStore(bookGovernance: [state]);
        var workflow = new CompanyBookPolicyWorkflow(store);
        var draft = await workflow.PrepareGovernedChangeRequestDraftAsync(
            CompanyId,
            Guid.NewGuid(),
            state.Book.BookId,
            new DateOnly(2026, 4, 14),
            new DateOnly(2026, 5, 1),
            new CompanyBookProposedChangeSet(
                IsPrimary: null,
                AccountingStandard: "IFRS",
                BookBaseCurrencyCode: null,
                FunctionalCurrencyCode: null,
                PresentationCurrencyCode: null,
                RateType: null,
                QuoteBasis: null,
                RateUseCase: null,
                PostingReason: null,
                RevaluationProfile: null,
                FxRoundingPolicy: null),
            CancellationToken.None);

        var result = await workflow.SubmitGovernedChangeRequestDraftAsync(
            CompanyId,
            draft.RequestId,
            Guid.Parse("fe935bb9-7f6c-4fb4-a238-b663c70ddcb3"),
            CancellationToken.None);

        Assert.Equal("submitted", result.Status);
        Assert.NotNull(result.SubmittedAt);
        Assert.NotNull(result.SubmittedByUserId);
    }

    [Fact]
    public async Task CancelGovernedChangeRequestDraftAsync_FromSubmitted_MarksCancelled()
    {
        var state = CreateState(hasCompanyPostedHistory: true, hasBookSpecificRevaluationHistory: false);
        var store = new StubStore(bookGovernance: [state]);
        var workflow = new CompanyBookPolicyWorkflow(store);
        var draft = await workflow.PrepareGovernedChangeRequestDraftAsync(
            CompanyId,
            Guid.NewGuid(),
            state.Book.BookId,
            new DateOnly(2026, 4, 14),
            new DateOnly(2026, 5, 1),
            new CompanyBookProposedChangeSet(
                IsPrimary: null,
                AccountingStandard: "IFRS",
                BookBaseCurrencyCode: null,
                FunctionalCurrencyCode: null,
                PresentationCurrencyCode: null,
                RateType: null,
                QuoteBasis: null,
                RateUseCase: null,
                PostingReason: null,
                RevaluationProfile: null,
                FxRoundingPolicy: null),
            CancellationToken.None);
        await workflow.SubmitGovernedChangeRequestDraftAsync(CompanyId, draft.RequestId, Guid.NewGuid(), CancellationToken.None);

        var result = await workflow.CancelGovernedChangeRequestDraftAsync(
            CompanyId,
            draft.RequestId,
            Guid.Parse("4dfc65ec-533d-4874-9e6c-d2df9530e16b"),
            CancellationToken.None);

        Assert.Equal("cancelled", result.Status);
        Assert.NotNull(result.CancelledAt);
        Assert.NotNull(result.CancelledByUserId);
    }

    [Fact]
    public async Task ValidateGovernedChangeRequestApplyReadinessAsync_WithSubmittedMatchingDraft_IsReady()
    {
        var state = CreateState(hasCompanyPostedHistory: true, hasBookSpecificRevaluationHistory: false);
        var store = new StubStore(bookGovernance: [state]);
        var workflow = new CompanyBookPolicyWorkflow(store);
        var draft = await workflow.PrepareGovernedChangeRequestDraftAsync(
            CompanyId,
            Guid.NewGuid(),
            state.Book.BookId,
            new DateOnly(2026, 4, 14),
            new DateOnly(2026, 4, 14),
            new CompanyBookProposedChangeSet(
                IsPrimary: null,
                AccountingStandard: "IFRS",
                BookBaseCurrencyCode: null,
                FunctionalCurrencyCode: null,
                PresentationCurrencyCode: null,
                RateType: null,
                QuoteBasis: null,
                RateUseCase: null,
                PostingReason: null,
                RevaluationProfile: null,
                FxRoundingPolicy: null),
            CancellationToken.None);
        await workflow.SubmitGovernedChangeRequestDraftAsync(CompanyId, draft.RequestId, Guid.NewGuid(), CancellationToken.None);

        var result = await workflow.ValidateGovernedChangeRequestApplyReadinessAsync(
            CompanyId,
            draft.RequestId,
            new DateOnly(2026, 4, 14),
            CancellationToken.None);

        Assert.True(result.IsReadyToApply);
        Assert.True(result.CurrentTruthMatchesDraft);
        Assert.Empty(result.Blockers);
    }

    [Fact]
    public async Task ValidateGovernedChangeRequestApplyReadinessAsync_WhenTruthDrifts_BlocksApply()
    {
        var state = CreateState(hasCompanyPostedHistory: true, hasBookSpecificRevaluationHistory: false);
        var driftedState = state with
        {
            Book = state.Book with
            {
                AccountingStandard = "IFRS"
            }
        };
        var store = new StubStore(bookGovernance: [state]);
        var workflow = new CompanyBookPolicyWorkflow(store);
        var draft = await workflow.PrepareGovernedChangeRequestDraftAsync(
            CompanyId,
            Guid.NewGuid(),
            state.Book.BookId,
            new DateOnly(2026, 4, 14),
            new DateOnly(2026, 4, 14),
            new CompanyBookProposedChangeSet(
                IsPrimary: null,
                AccountingStandard: "US_GAAP",
                BookBaseCurrencyCode: null,
                FunctionalCurrencyCode: null,
                PresentationCurrencyCode: null,
                RateType: null,
                QuoteBasis: null,
                RateUseCase: null,
                PostingReason: null,
                RevaluationProfile: null,
                FxRoundingPolicy: null),
            CancellationToken.None);
        await workflow.SubmitGovernedChangeRequestDraftAsync(CompanyId, draft.RequestId, Guid.NewGuid(), CancellationToken.None);
        store.ReplaceGovernance([driftedState]);

        var result = await workflow.ValidateGovernedChangeRequestApplyReadinessAsync(
            CompanyId,
            draft.RequestId,
            new DateOnly(2026, 4, 14),
            CancellationToken.None);

        Assert.False(result.IsReadyToApply);
        Assert.False(result.CurrentTruthMatchesDraft);
        Assert.Contains(result.Blockers, blocker => blocker.Contains("changed since this draft was prepared", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ValidateGovernedChangeRequestApplyReadinessAsync_WithFormalGovernanceSignals_BlocksNonNewBookPath()
    {
        var originalState = CreateState(hasCompanyPostedHistory: true, hasBookSpecificRevaluationHistory: false);
        var store = new StubStore(bookGovernance: [originalState]);
        var workflow = new CompanyBookPolicyWorkflow(store);
        var draft = await workflow.PrepareGovernedChangeRequestDraftAsync(
            CompanyId,
            Guid.NewGuid(),
            originalState.Book.BookId,
            new DateOnly(2026, 4, 14),
            new DateOnly(2026, 4, 14),
            new CompanyBookProposedChangeSet(
                IsPrimary: null,
                AccountingStandard: "IFRS",
                BookBaseCurrencyCode: null,
                FunctionalCurrencyCode: null,
                PresentationCurrencyCode: null,
                RateType: null,
                QuoteBasis: null,
                RateUseCase: null,
                PostingReason: null,
                RevaluationProfile: null,
                FxRoundingPolicy: null),
            CancellationToken.None);
        await workflow.SubmitGovernedChangeRequestDraftAsync(CompanyId, draft.RequestId, Guid.NewGuid(), CancellationToken.None);

        var signalledState = originalState with
        {
            GovernanceSignals = new CompanyBookGovernanceSignalSummary(
                HasClosedPeriods: true,
                HasIssuedReports: false,
                HasFiledTax: true,
                Signals:
                [
                    new CompanyBookGovernanceSignalRecord(
                        Guid.NewGuid(),
                        CompanyId,
                        originalState.Book.BookId,
                        "closed_period",
                        new DateOnly(2026, 3, 31),
                        "2026-Q1 closed",
                        null,
                        null,
                        DateTimeOffset.UtcNow),
                    new CompanyBookGovernanceSignalRecord(
                        Guid.NewGuid(),
                        CompanyId,
                        originalState.Book.BookId,
                        "filed_tax",
                        new DateOnly(2026, 4, 10),
                        "2025 T2 filed",
                        null,
                        null,
                        DateTimeOffset.UtcNow)
                ])
        };
        store.ReplaceGovernance([signalledState]);

        var result = await workflow.ValidateGovernedChangeRequestApplyReadinessAsync(
            CompanyId,
            draft.RequestId,
            new DateOnly(2026, 4, 14),
            CancellationToken.None);

        Assert.False(result.IsReadyToApply);
        Assert.Contains(result.Blockers, blocker => blocker.Contains("Closed periods", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Blockers, blocker => blocker.Contains("Filed tax", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class StubStore : ICompanyBookPolicyStore
    {
        private CompanyBookPolicyGovernanceResult? _current;
        private IReadOnlyList<CompanyBookGovernanceState> _bookGovernance;
        private readonly List<CompanyBookGovernedChangeRequestDraft> _drafts = [];

        public StubStore(
            CompanyBookPolicyGovernanceResult? current = null,
            IReadOnlyList<CompanyBookGovernanceState>? bookGovernance = null)
        {
            _current = current;
            _bookGovernance = bookGovernance ?? [];
        }

        public Task<IReadOnlyList<CompanyBookGovernanceState>> ListBookGovernanceAsync(
            Guid companyId,
            DateOnly asOfDate,
            CancellationToken cancellationToken) =>
            Task.FromResult(_bookGovernance);

        public Task<CompanyBookGovernanceSignalSummary> GetGovernanceSignalsAsync(
            Guid companyId,
            Guid bookId,
            DateOnly asOfDate,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                _bookGovernance.FirstOrDefault(state => state.Book.CompanyId == companyId && state.Book.BookId == bookId)?.GovernanceSignals ??
                new CompanyBookGovernanceSignalSummary(false, false, false, []));

        public Task<CompanyBookGovernedChangeRequestDraft?> GetGovernedChangeRequestDraftAsync(
            Guid companyId,
            Guid requestId,
            CancellationToken cancellationToken) =>
            Task.FromResult(_drafts.FirstOrDefault(d => d.CompanyId == companyId && d.RequestId == requestId));

        public Task<CompanyBookGovernanceSignalRecord> CreateGovernanceSignalAsync(
            Guid companyId,
            Guid bookId,
            string signalType,
            DateOnly signalDate,
            string? referenceLabel,
            string? notes,
            Guid userId,
            CancellationToken cancellationToken)
        {
            var index = _bookGovernance.ToList().FindIndex(state => state.Book.CompanyId == companyId && state.Book.BookId == bookId);
            if (index < 0)
            {
                throw new InvalidOperationException($"Company {companyId:D} book {bookId:D} was not found for governance signal registration.");
            }

            var currentState = _bookGovernance[index];
            var duplicate = currentState.GovernanceSignals.Signals.Any(signal =>
                signal.BookId == bookId &&
                string.Equals(signal.SignalType, signalType, StringComparison.OrdinalIgnoreCase) &&
                signal.SignalDate == signalDate &&
                string.Equals(signal.ReferenceLabel ?? string.Empty, referenceLabel ?? string.Empty, StringComparison.OrdinalIgnoreCase));
            if (duplicate)
            {
                throw new InvalidOperationException(
                    $"Governance signal '{signalType}' on {signalDate:yyyy-MM-dd} is already registered for this book.");
            }

            var created = new CompanyBookGovernanceSignalRecord(
                Guid.NewGuid(),
                companyId,
                bookId,
                signalType,
                signalDate,
                referenceLabel,
                notes,
                userId,
                DateTimeOffset.UtcNow);
            var updatedSignals = currentState.GovernanceSignals.Signals
                .Concat([created])
                .OrderByDescending(signal => signal.SignalDate)
                .ThenByDescending(signal => signal.CreatedAt)
                .ToArray();
            var updatedSummary = new CompanyBookGovernanceSignalSummary(
                updatedSignals.Any(signal => string.Equals(signal.SignalType, "closed_period", StringComparison.OrdinalIgnoreCase)),
                updatedSignals.Any(signal => string.Equals(signal.SignalType, "reported_statement", StringComparison.OrdinalIgnoreCase)),
                updatedSignals.Any(signal => string.Equals(signal.SignalType, "filed_tax", StringComparison.OrdinalIgnoreCase)),
                updatedSignals);

            var governance = _bookGovernance.ToArray();
            governance[index] = currentState with
            {
                GovernanceSignals = updatedSummary
            };
            _bookGovernance = governance;
            return Task.FromResult(created);
        }

        public Task<CompanyBookGovernedChangeRequestDraft> CreateGovernedChangeRequestDraftAsync(
            CompanyBookGovernedChangePreview preview,
            DateOnly asOfDate,
            DateOnly effectiveFrom,
            Guid userId,
            CancellationToken cancellationToken)
        {
            var draft = new CompanyBookGovernedChangeRequestDraft(
                Guid.NewGuid(),
                preview.Book.CompanyId,
                preview.Book.BookId,
                "draft",
                preview.ChangeImpact.RecommendedPath,
                asOfDate,
                effectiveFrom,
                userId,
                DateTimeOffset.UtcNow,
                null,
                null,
                null,
                null,
                null,
                preview);
            _drafts.Add(draft);
            return Task.FromResult(draft);
        }

        public Task<IReadOnlyList<CompanyBookGovernedChangeRequestDraft>> ListGovernedChangeRequestDraftsAsync(
            Guid companyId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CompanyBookGovernedChangeRequestDraft>>(_drafts.Where(d => d.CompanyId == companyId).ToArray());

        public Task<CompanyBookGovernedChangeRequestDraft> SubmitGovernedChangeRequestDraftAsync(
            Guid companyId,
            Guid requestId,
            Guid userId,
            CancellationToken cancellationToken)
        {
            var index = _drafts.FindIndex(d => d.CompanyId == companyId && d.RequestId == requestId);
            var current = _drafts[index];
            var updated = current with
            {
                Status = "submitted",
                SubmittedByUserId = userId,
                SubmittedAt = DateTimeOffset.UtcNow
            };
            _drafts[index] = updated;
            return Task.FromResult(updated);
        }

        public Task<CompanyBookGovernedChangeRequestDraft> CancelGovernedChangeRequestDraftAsync(
            Guid companyId,
            Guid requestId,
            Guid userId,
            CancellationToken cancellationToken)
        {
            var index = _drafts.FindIndex(d => d.CompanyId == companyId && d.RequestId == requestId);
            var current = _drafts[index];
            var updated = current with
            {
                Status = "cancelled",
                CancelledByUserId = userId,
                CancelledAt = DateTimeOffset.UtcNow
            };
            _drafts[index] = updated;
            return Task.FromResult(updated);
        }

        public Task<CompanyBookPolicyGovernanceResult?> TryGetDefaultRemeasurementPolicyAsync(
            Guid companyId,
            DateOnly asOfDate,
            CancellationToken cancellationToken) =>
            Task.FromResult(_current);

        public Task<CompanyBookPolicyGovernanceResult?> TryGetRemeasurementPolicyAsync(
            Guid companyId,
            Guid bookId,
            DateOnly asOfDate,
            CancellationToken cancellationToken) =>
            Task.FromResult(_current);

        public Task<CompanyBookPolicyGovernanceResult> EnsureDefaultPrimaryBookPolicyAsync(
            Guid companyId,
            Guid userId,
            DateOnly asOfDate,
            CancellationToken cancellationToken)
        {
            _current ??= CreateResult(wasProvisioned: true);
            return Task.FromResult(_current);
        }

        public void ReplaceGovernance(IReadOnlyList<CompanyBookGovernanceState> bookGovernance)
        {
            _bookGovernance = bookGovernance;
        }
    }

    private static CompanyBookPolicyGovernanceResult CreateResult(bool wasProvisioned)
    {
        var bookId = Guid.NewGuid();

        return new CompanyBookPolicyGovernanceResult(
            new CompanyBookRecord(
                bookId,
                CompanyId,
                "PRIMARY",
                "Primary Book",
                "primary",
                "ASPE",
                "CAD",
                "CAD",
                null,
                true,
                false,
                new DateOnly(2026, 4, 14),
                true),
            new CompanyBookRemeasurementPolicy(
                Guid.NewGuid(),
                CompanyId,
                bookId,
                "closing",
                "direct",
                "remeasurement",
                "revaluation",
                "monetary_open_item_closing",
                "currency_precision",
                new DateOnly(2026, 4, 14),
                true),
            wasProvisioned);
    }

    private static CompanyBookGovernanceState CreateState(
        bool hasCompanyPostedHistory,
        bool hasBookSpecificRevaluationHistory,
        CompanyBookGovernanceSignalSummary? governanceSignals = null)
    {
        var result = CreateResult(wasProvisioned: false);
        return new CompanyBookGovernanceState(
            result.Book,
            result.RemeasurementPolicy,
            hasCompanyPostedHistory,
            hasBookSpecificRevaluationHistory,
            governanceSignals ?? new CompanyBookGovernanceSignalSummary(false, false, false, []));
    }
}
