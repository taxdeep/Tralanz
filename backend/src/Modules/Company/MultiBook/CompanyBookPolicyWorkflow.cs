namespace Modules.Company.MultiBook;

public sealed class CompanyBookPolicyWorkflow : ICompanyBookPolicyWorkflow
{
    private readonly ICompanyBookPolicyStore _store;

    public CompanyBookPolicyWorkflow(ICompanyBookPolicyStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<IReadOnlyList<CompanyBookGovernanceOverview>> ListBookGovernanceAsync(
        Guid companyId,
        DateOnly asOfDate,
        CancellationToken cancellationToken)
    {
        var states = await _store.ListBookGovernanceAsync(companyId, asOfDate, cancellationToken);
        return states.Select(MapGovernanceOverview).ToArray();
    }

    public Task<CompanyBookGovernanceSignalSummary> GetGovernanceSignalsAsync(
        Guid companyId,
        Guid bookId,
        DateOnly asOfDate,
        CancellationToken cancellationToken) =>
        _store.GetGovernanceSignalsAsync(companyId, bookId, asOfDate, cancellationToken);

    public async Task<CompanyBookGovernanceSignalWriteResult> CreateGovernanceSignalAsync(
        Guid companyId,
        Guid bookId,
        string signalType,
        DateOnly signalDate,
        string? referenceLabel,
        string? notes,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var normalizedSignalType = NormalizeSignalType(signalType);
        var state = (await _store.ListBookGovernanceAsync(companyId, signalDate, cancellationToken))
            .FirstOrDefault(item => item.Book.BookId == bookId);
        if (state is null)
        {
            throw new InvalidOperationException($"Company {companyId:D} book {bookId:D} was not found for governance signal registration.");
        }

        var signal = await _store.CreateGovernanceSignalAsync(
            companyId,
            bookId,
            normalizedSignalType,
            signalDate,
            NormalizeOptionalValue(referenceLabel),
            NormalizeOptionalValue(notes),
            userId,
            cancellationToken);
        var summary = await _store.GetGovernanceSignalsAsync(companyId, bookId, signalDate, cancellationToken);
        return new CompanyBookGovernanceSignalWriteResult(signal, summary);
    }

    public Task<CompanyBookGovernanceSignalWriteResult> RegisterClosedPeriodAsync(
        Guid companyId,
        Guid bookId,
        DateOnly periodEndDate,
        string? referenceLabel,
        string? notes,
        Guid userId,
        CancellationToken cancellationToken) =>
        CreateTypedGovernanceSignalAsync(
            companyId,
            bookId,
            "closed_period",
            periodEndDate,
            string.IsNullOrWhiteSpace(referenceLabel) ? $"Period close {periodEndDate:yyyy-MM-dd}" : referenceLabel,
            notes,
            userId,
            cancellationToken);

    public Task<CompanyBookGovernanceSignalWriteResult> RegisterIssuedStatementAsync(
        Guid companyId,
        Guid bookId,
        DateOnly issuedOn,
        string statementLabel,
        string? notes,
        Guid userId,
        CancellationToken cancellationToken) =>
        CreateTypedGovernanceSignalAsync(
            companyId,
            bookId,
            "reported_statement",
            issuedOn,
            RequireLabel(statementLabel, "Issued statement label"),
            notes,
            userId,
            cancellationToken);

    public Task<CompanyBookGovernanceSignalWriteResult> RegisterFiledTaxAsync(
        Guid companyId,
        Guid bookId,
        DateOnly filedOn,
        string filingLabel,
        string? notes,
        Guid userId,
        CancellationToken cancellationToken) =>
        CreateTypedGovernanceSignalAsync(
            companyId,
            bookId,
            "filed_tax",
            filedOn,
            RequireLabel(filingLabel, "Filed tax label"),
            notes,
            userId,
            cancellationToken);

    public async Task<CompanyBookGovernedChangeRequestDraft> PrepareGovernedChangeRequestDraftAsync(
        Guid companyId,
        Guid userId,
        Guid? bookId,
        DateOnly asOfDate,
        DateOnly effectiveFrom,
        CompanyBookProposedChangeSet proposedChanges,
        CancellationToken cancellationToken)
    {
        if (effectiveFrom < asOfDate)
        {
            throw new InvalidOperationException("Governed change draft effective_from must be on or after the evaluation date.");
        }

        var preview = await PreviewGovernedChangeAsync(
            companyId,
            bookId,
            asOfDate,
            proposedChanges,
            cancellationToken);

        if (!preview.ChangeImpact.HasAnyChange)
        {
            throw new InvalidOperationException("Governed change draft requires at least one book-governing change.");
        }

        return await _store.CreateGovernedChangeRequestDraftAsync(
            preview,
            asOfDate,
            effectiveFrom,
            userId,
            cancellationToken);
    }

    public async Task<CompanyBookGovernedChangeRequestDraft> SubmitGovernedChangeRequestDraftAsync(
        Guid companyId,
        Guid requestId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var draft = await RequireGovernedChangeRequestDraftAsync(companyId, requestId, cancellationToken);
        if (!string.Equals(draft.Status, "draft", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Governed change request {requestId:D} cannot be submitted from status '{draft.Status}'.");
        }

        return await _store.SubmitGovernedChangeRequestDraftAsync(
            companyId,
            requestId,
            userId,
            cancellationToken);
    }

    public async Task<CompanyBookGovernedChangeRequestDraft> CancelGovernedChangeRequestDraftAsync(
        Guid companyId,
        Guid requestId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var draft = await RequireGovernedChangeRequestDraftAsync(companyId, requestId, cancellationToken);
        if (string.Equals(draft.Status, "cancelled", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Governed change request {requestId:D} is already cancelled.");
        }

        if (string.Equals(draft.Status, "applied", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Governed change request {requestId:D} has already been applied and cannot be cancelled.");
        }

        return await _store.CancelGovernedChangeRequestDraftAsync(
            companyId,
            requestId,
            userId,
            cancellationToken);
    }

    public Task<IReadOnlyList<CompanyBookGovernedChangeRequestDraft>> ListGovernedChangeRequestDraftsAsync(
        Guid companyId,
        CancellationToken cancellationToken) =>
        _store.ListGovernedChangeRequestDraftsAsync(companyId, cancellationToken);

    public async Task<CompanyBookGovernedChangeRequestReadiness> ValidateGovernedChangeRequestApplyReadinessAsync(
        Guid companyId,
        Guid requestId,
        DateOnly asOfDate,
        CancellationToken cancellationToken)
    {
        var draft = await RequireGovernedChangeRequestDraftAsync(companyId, requestId, cancellationToken);
        var currentState = (await _store.ListBookGovernanceAsync(companyId, asOfDate, cancellationToken))
            .FirstOrDefault(state => state.Book.BookId == draft.BookId);

        var blockers = new List<string>();
        var warnings = new List<string>();

        if (!string.Equals(draft.Status, "submitted", StringComparison.OrdinalIgnoreCase))
        {
            blockers.Add("Only submitted governed change requests can be evaluated as apply-ready.");
        }

        if (asOfDate < draft.EffectiveFrom)
        {
            blockers.Add("The governed change request has not reached its effective date yet.");
        }

        if (currentState is null)
        {
            blockers.Add("The target company book is no longer active in current governance state.");
        }

        var currentTruthMatchesDraft = currentState is not null && CurrentTruthMatchesDraft(currentState, draft);
        if (currentState is not null && !currentTruthMatchesDraft)
        {
            blockers.Add("Current book or remeasurement policy truth has changed since this draft was prepared. Recreate the preview before applying.");
        }

        if (currentState is not null)
        {
            AddGovernanceSignalBlockers(blockers, currentState, draft);
        }

        if (!string.Equals(draft.RequestedAction, "direct_update_in_place", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("This draft still requires a governed cutover or new-book rollout; readiness does not execute the migration.");
        }
        else
        {
            warnings.Add("Execution is not implemented yet; readiness only confirms whether the current direct-update path is still legally consistent.");
        }

        warnings.Add("Execution is not implemented yet; readiness only validates whether the current governance truth still supports the drafted path.");

        return new CompanyBookGovernedChangeRequestReadiness(
            draft.RequestId,
            draft.Status,
            draft.EffectiveFrom,
            asOfDate,
            currentTruthMatchesDraft,
            blockers.Count == 0,
            !string.Equals(draft.RequestedAction, "direct_update_in_place", StringComparison.OrdinalIgnoreCase),
            blockers,
            warnings);
    }

    public async Task<CompanyBookGovernedChangePreview> PreviewGovernedChangeAsync(
        Guid companyId,
        Guid? bookId,
        DateOnly asOfDate,
        CompanyBookProposedChangeSet proposedChanges,
        CancellationToken cancellationToken)
    {
        var states = await _store.ListBookGovernanceAsync(companyId, asOfDate, cancellationToken);
        var targetState = bookId.HasValue
            ? states.FirstOrDefault(state => state.Book.BookId == bookId.Value)
            : states.FirstOrDefault(state => state.Book.IsPrimary);

        if (targetState is null)
        {
            throw new InvalidOperationException(
                bookId.HasValue
                    ? $"Company {companyId:D} book {bookId.Value:D} was not found for governance preview."
                    : $"Company {companyId:D} does not have an active primary book for governance preview.");
        }

        return BuildGovernedChangePreview(targetState, proposedChanges);
    }

    public async Task<CompanyBookPolicyGovernanceResult> GetRemeasurementPolicyAsync(
        Guid companyId,
        Guid? bookId,
        DateOnly asOfDate,
        CancellationToken cancellationToken)
    {
        var policy = bookId.HasValue
            ? await _store.TryGetRemeasurementPolicyAsync(companyId, bookId.Value, asOfDate, cancellationToken)
            : await _store.TryGetDefaultRemeasurementPolicyAsync(companyId, asOfDate, cancellationToken);

        return policy ?? throw new InvalidOperationException(
            bookId.HasValue
                ? $"Company {companyId:D} book {bookId.Value:D} does not have an active remeasurement policy."
                : $"Company {companyId:D} does not have an active primary book remeasurement policy.");
    }

    public async Task<CompanyBookPolicyGovernanceResult> GetDefaultRemeasurementPolicyAsync(
        Guid companyId,
        DateOnly asOfDate,
        CancellationToken cancellationToken)
    {
        return await GetRemeasurementPolicyAsync(
            companyId,
            bookId: null,
            asOfDate,
            cancellationToken);
    }

    public Task<CompanyBookPolicyGovernanceResult> EnsureDefaultPrimaryBookPolicyAsync(
        Guid companyId,
        Guid userId,
        DateOnly asOfDate,
        CancellationToken cancellationToken) =>
        _store.EnsureDefaultPrimaryBookPolicyAsync(
            companyId,
            userId,
            asOfDate,
            cancellationToken);

    private static CompanyBookGovernanceOverview MapGovernanceOverview(CompanyBookGovernanceState state)
    {
        var directEditAllowed = !state.HasCompanyPostedHistory;
        var hasFormalGovernanceSignals = HasFormalGovernanceSignals(state);
        var reason = hasFormalGovernanceSignals
            ? "Closed-period, issued-report, or filed-tax governance signals exist for this book, so only a new secondary or adjustment book path remains acceptable."
            : directEditAllowed
                ? "No posted journal history exists for the company, so direct book-governing edits remain allowed."
                : state.HasBookSpecificRevaluationHistory
                    ? "Posted journal history exists for the company and this book already has posted FX revaluation history, so governed migration is required."
                    : "Posted journal history exists for the company, so direct book-governing edits are blocked and a governed migration or future-dated cutover is required.";

        return new CompanyBookGovernanceOverview(
            state.Book,
            state.RemeasurementPolicy,
            new CompanyBookMigrationEligibility(
                hasFormalGovernanceSignals || !directEditAllowed ? "governed_migration_required" : "direct_edit_allowed",
                hasFormalGovernanceSignals ? "formal_governance_signals" : "company_posted_history",
                state.HasCompanyPostedHistory,
                state.HasBookSpecificRevaluationHistory,
                directEditAllowed && !hasFormalGovernanceSignals,
                reason),
            state.GovernanceSignals);
    }

    private static CompanyBookGovernedChangePreview BuildGovernedChangePreview(
        CompanyBookGovernanceState state,
        CompanyBookProposedChangeSet proposedChanges)
    {
        var changedFields = new List<string>();
        AddChangedField(changedFields, "is_primary", proposedChanges.IsPrimary.HasValue && proposedChanges.IsPrimary.Value != state.Book.IsPrimary);
        AddChangedField(changedFields, "accounting_standard", IsChanged(proposedChanges.AccountingStandard, state.Book.AccountingStandard));
        AddChangedField(changedFields, "book_base_currency_code", IsChanged(proposedChanges.BookBaseCurrencyCode, state.Book.BookBaseCurrencyCode));
        AddChangedField(changedFields, "functional_currency_code", IsChanged(proposedChanges.FunctionalCurrencyCode, state.Book.FunctionalCurrencyCode));
        AddChangedField(changedFields, "presentation_currency_code", IsChanged(proposedChanges.PresentationCurrencyCode, state.Book.PresentationCurrencyCode));
        AddChangedField(changedFields, "rate_type", IsChanged(proposedChanges.RateType, state.RemeasurementPolicy?.RateType));
        AddChangedField(changedFields, "quote_basis", IsChanged(proposedChanges.QuoteBasis, state.RemeasurementPolicy?.QuoteBasis));
        AddChangedField(changedFields, "rate_use_case", IsChanged(proposedChanges.RateUseCase, state.RemeasurementPolicy?.RateUseCase));
        AddChangedField(changedFields, "posting_reason", IsChanged(proposedChanges.PostingReason, state.RemeasurementPolicy?.PostingReason));
        AddChangedField(changedFields, "revaluation_profile", IsChanged(proposedChanges.RevaluationProfile, state.RemeasurementPolicy?.RevaluationProfile));
        AddChangedField(changedFields, "fx_rounding_policy", IsChanged(proposedChanges.FxRoundingPolicy, state.RemeasurementPolicy?.FxRoundingPolicy));

        var categories = BuildChangeCategories(changedFields);
        var impact = BuildChangeImpact(state, changedFields, categories);

        return new CompanyBookGovernedChangePreview(
            state.Book,
            state.RemeasurementPolicy,
            NormalizeProposedChanges(proposedChanges),
            impact);
    }

    private static CompanyBookChangeImpact BuildChangeImpact(
        CompanyBookGovernanceState state,
        IReadOnlyList<string> changedFields,
        IReadOnlyList<string> categories)
    {
        if (changedFields.Count == 0)
        {
            return new CompanyBookChangeImpact(
                false,
                [],
                [],
                false,
                false,
                "no_change",
                "current_book_truth",
                "The proposed values do not change any governed book or remeasurement fields.");
        }

        if (HasFormalGovernanceSignals(state))
        {
            return new CompanyBookChangeImpact(
                true,
                changedFields,
                categories,
                false,
                true,
                "new_secondary_or_adjustment_book",
                "formal_governance_signals",
                "Closed-period, issued-report, or filed-tax governance signals already exist for this book, so preserve the current book truth and route the change through a new secondary or adjustment book.");
        }

        if (!state.HasCompanyPostedHistory)
        {
            return new CompanyBookChangeImpact(
                true,
                changedFields,
                categories,
                true,
                false,
                "direct_update_in_place",
                "company_posted_history",
                "No posted journal history exists for the company, so the proposed governed change can still be applied in place.");
        }

        var recommendedPath = state.HasBookSpecificRevaluationHistory
            ? "new_secondary_or_adjustment_book"
            : "future_dated_cutover_or_new_book";
        var reason = state.HasBookSpecificRevaluationHistory
            ? "Posted journal history exists for the company and this book already has posted remeasurement history, so preserve the existing book truth and route the change through a new secondary or adjustment book."
            : "Posted journal history exists for the company, so do not rewrite the existing book truth in place; route the change through a future-dated cutover or a newly governed book.";

        return new CompanyBookChangeImpact(
            true,
            changedFields,
            categories,
            false,
            true,
            recommendedPath,
            "company_posted_history_and_book_remeasurement_history",
            reason);
    }

    private static CompanyBookProposedChangeSet NormalizeProposedChanges(CompanyBookProposedChangeSet proposedChanges) =>
        proposedChanges with
        {
            AccountingStandard = NormalizeValue(proposedChanges.AccountingStandard),
            BookBaseCurrencyCode = NormalizeValue(proposedChanges.BookBaseCurrencyCode),
            FunctionalCurrencyCode = NormalizeValue(proposedChanges.FunctionalCurrencyCode),
            PresentationCurrencyCode = NormalizeValue(proposedChanges.PresentationCurrencyCode),
            RateType = NormalizeValue(proposedChanges.RateType),
            QuoteBasis = NormalizeValue(proposedChanges.QuoteBasis),
            RateUseCase = NormalizeValue(proposedChanges.RateUseCase),
            PostingReason = NormalizeValue(proposedChanges.PostingReason),
            RevaluationProfile = NormalizeValue(proposedChanges.RevaluationProfile),
            FxRoundingPolicy = NormalizeValue(proposedChanges.FxRoundingPolicy)
        };

    private static IReadOnlyList<string> BuildChangeCategories(IReadOnlyList<string> changedFields)
    {
        var categories = new List<string>();
        if (changedFields.Any(field => field is "is_primary" or "accounting_standard"))
        {
            categories.Add("accounting_framework_governance");
        }

        if (changedFields.Any(field => field is "book_base_currency_code" or "functional_currency_code" or "presentation_currency_code"))
        {
            categories.Add("currency_binding_governance");
        }

        if (changedFields.Any(field => field is "rate_type" or "quote_basis" or "rate_use_case" or "posting_reason" or "revaluation_profile" or "fx_rounding_policy"))
        {
            categories.Add("fx_policy_governance");
        }

        return categories;
    }

    private static bool IsChanged(string? proposedValue, string? currentValue) =>
        NormalizeValue(proposedValue) is string normalizedProposed &&
        !string.Equals(normalizedProposed, NormalizeValue(currentValue), StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToUpperInvariant();
    }

    private static void AddChangedField(List<string> changedFields, string fieldName, bool isChanged)
    {
        if (isChanged)
        {
            changedFields.Add(fieldName);
        }
    }

    private static bool HasFormalGovernanceSignals(CompanyBookGovernanceState state) =>
        state.GovernanceSignals.HasClosedPeriods ||
        state.GovernanceSignals.HasIssuedReports ||
        state.GovernanceSignals.HasFiledTax;

    private static void AddGovernanceSignalBlockers(
        List<string> blockers,
        CompanyBookGovernanceState currentState,
        CompanyBookGovernedChangeRequestDraft draft)
    {
        if (!HasFormalGovernanceSignals(currentState))
        {
            return;
        }

        if (string.Equals(draft.RequestedAction, "new_secondary_or_adjustment_book", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (currentState.GovernanceSignals.HasClosedPeriods)
        {
            blockers.Add("Closed periods already exist for this book, so this draft can no longer apply through an in-place or future-dated cutover path.");
        }

        if (currentState.GovernanceSignals.HasIssuedReports)
        {
            blockers.Add("Issued reporting signals already exist for this book, so preserve historical truth and move the change to a new secondary or adjustment book.");
        }

        if (currentState.GovernanceSignals.HasFiledTax)
        {
            blockers.Add("Filed tax signals already exist for this book, so preserve historical truth and move the change to a new secondary or adjustment book.");
        }
    }

    private static string NormalizeSignalType(string signalType)
    {
        var normalized = NormalizeOptionalValue(signalType)?.ToLowerInvariant();
        if (normalized is not ("closed_period" or "reported_statement" or "filed_tax"))
        {
            throw new InvalidOperationException("Governance signal type must be one of: closed_period, reported_statement, filed_tax.");
        }

        return normalized;
    }

    private static string RequireLabel(string? value, string labelName) =>
        NormalizeOptionalValue(value) ?? throw new InvalidOperationException($"{labelName} is required.");

    private static string? NormalizeOptionalValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private Task<CompanyBookGovernanceSignalWriteResult> CreateTypedGovernanceSignalAsync(
        Guid companyId,
        Guid bookId,
        string signalType,
        DateOnly signalDate,
        string? referenceLabel,
        string? notes,
        Guid userId,
        CancellationToken cancellationToken) =>
        CreateGovernanceSignalAsync(
            companyId,
            bookId,
            signalType,
            signalDate,
            referenceLabel,
            notes,
            userId,
            cancellationToken);

    private async Task<CompanyBookGovernedChangeRequestDraft> RequireGovernedChangeRequestDraftAsync(
        Guid companyId,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        return await _store.GetGovernedChangeRequestDraftAsync(companyId, requestId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Governed change request {requestId:D} was not found for company {companyId:D}.");
    }

    private static bool CurrentTruthMatchesDraft(
        CompanyBookGovernanceState currentState,
        CompanyBookGovernedChangeRequestDraft draft)
    {
        var book = currentState.Book;
        var currentPolicy = currentState.RemeasurementPolicy;
        var draftBook = draft.Preview.Book;
        var draftPolicy = draft.Preview.CurrentRemeasurementPolicy;

        if (!string.Equals(book.BookCode, draftBook.BookCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(book.BookName, draftBook.BookName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(book.BookRole, draftBook.BookRole, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(book.AccountingStandard, draftBook.AccountingStandard, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(book.BookBaseCurrencyCode, draftBook.BookBaseCurrencyCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(book.FunctionalCurrencyCode, draftBook.FunctionalCurrencyCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(book.PresentationCurrencyCode, draftBook.PresentationCurrencyCode, StringComparison.OrdinalIgnoreCase) ||
            book.IsPrimary != draftBook.IsPrimary ||
            book.IsAdjustmentOnly != draftBook.IsAdjustmentOnly ||
            book.EffectiveFrom != draftBook.EffectiveFrom)
        {
            return false;
        }

        if (currentPolicy is null && draftPolicy is null)
        {
            return true;
        }

        if (currentPolicy is null || draftPolicy is null)
        {
            return false;
        }

        return string.Equals(currentPolicy.RateType, draftPolicy.RateType, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(currentPolicy.QuoteBasis, draftPolicy.QuoteBasis, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(currentPolicy.RateUseCase, draftPolicy.RateUseCase, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(currentPolicy.PostingReason, draftPolicy.PostingReason, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(currentPolicy.RevaluationProfile, draftPolicy.RevaluationProfile, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(currentPolicy.FxRoundingPolicy, draftPolicy.FxRoundingPolicy, StringComparison.OrdinalIgnoreCase) &&
               currentPolicy.EffectiveFrom == draftPolicy.EffectiveFrom;
    }
}
