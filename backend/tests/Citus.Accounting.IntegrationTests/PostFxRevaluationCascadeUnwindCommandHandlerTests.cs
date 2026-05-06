using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Commands;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Documents;
using Citus.Accounting.Domain.Posting;
using Citus.Accounting.Infrastructure;
using Xunit;

namespace Citus.Accounting.IntegrationTests;

public sealed class PostFxRevaluationCascadeUnwindCommandHandlerTests
{
    [Fact]
    public async Task HandleAsync_WhenRequestedBatchHasDescendants_PostsEntireChainTailFirst()
    {
        var companyId = CompanyId.FromOrdinal(1);
        var userId = UserId.FromOrdinal(1);
        var requestedBatchId = Guid.NewGuid();
        var descendantBatchId = Guid.NewGuid();
        var repository = new FakeFxRevaluationDocumentRepository(
            companyId,
            [
                new ActiveBatch(descendantBatchId, "FXRV-0002"),
                new ActiveBatch(requestedBatchId, "FXRV-0001")
            ]);
        var postingEngine = new RecordingPostingEngine();
        var applyRepository = new RecordingFxRevaluationApplyRepository();
        var handler = new PostFxRevaluationCascadeUnwindCommandHandler(
            repository,
            postingEngine,
            applyRepository,
            new ImmediateUnitOfWork());

        var result = await handler.HandleAsync(
            new PostFxRevaluationCascadeUnwindCommand(
                companyId,
                requestedBatchId,
                userId,
                new DateOnly(2026, 6, 1),
                "Cascade unwind",
                "cascade-key"),
            CancellationToken.None);

        Assert.False(result.RequestedBatchWasTail);
        Assert.Equal(2, result.PostedStepCount);
        Assert.Collection(
            result.PostedSteps,
            first =>
            {
                Assert.Equal(descendantBatchId, first.SourceDocumentId);
                Assert.Equal("FXRV-0002", first.SourceDisplayNumber);
            },
            second =>
            {
                Assert.Equal(requestedBatchId, second.SourceDocumentId);
                Assert.Equal("FXRV-0001", second.SourceDisplayNumber);
            });
        Assert.Equal(
            [descendantBatchId, requestedBatchId],
            repository.PreparedSourceIds);
        Assert.Equal(2, postingEngine.PostedDocuments.Count);
        Assert.Equal(2, applyRepository.AppliedDocumentIds.Count);
        Assert.All(
            postingEngine.CapturedIdempotencyKeys,
            key => Assert.StartsWith("cascade-key:", key, StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleAsync_WhenRequestedBatchIsTail_PostsSingleUnwind()
    {
        var companyId = CompanyId.FromOrdinal(1);
        var userId = UserId.FromOrdinal(1);
        var requestedBatchId = Guid.NewGuid();
        var repository = new FakeFxRevaluationDocumentRepository(
            companyId,
            [
                new ActiveBatch(requestedBatchId, "FXRV-0001")
            ]);
        var postingEngine = new RecordingPostingEngine();
        var applyRepository = new RecordingFxRevaluationApplyRepository();
        var handler = new PostFxRevaluationCascadeUnwindCommandHandler(
            repository,
            postingEngine,
            applyRepository,
            new ImmediateUnitOfWork());

        var result = await handler.HandleAsync(
            new PostFxRevaluationCascadeUnwindCommand(
                companyId,
                requestedBatchId,
                userId,
                new DateOnly(2026, 6, 1),
                null,
                null),
            CancellationToken.None);

        Assert.True(result.RequestedBatchWasTail);
        Assert.Single(result.PostedSteps);
        Assert.Equal(requestedBatchId, result.PostedSteps[0].SourceDocumentId);
        Assert.Single(repository.PreparedSourceIds);
        Assert.Single(postingEngine.PostedDocuments);
        Assert.Single(applyRepository.AppliedDocumentIds);
        Assert.StartsWith(
            $"fx-revaluation-cascade:{companyId}:{requestedBatchId}:",
            postingEngine.CapturedIdempotencyKeys[0],
            StringComparison.Ordinal);
    }

    private sealed record ActiveBatch(Guid Id, string DisplayNumber);

    private sealed class FakeFxRevaluationDocumentRepository : IFxRevaluationDocumentRepository
    {
        private readonly CompanyId _companyId;
        private readonly List<ActiveBatch> _activeBatches;
        private readonly Dictionary<Guid, FxRevaluationDocument> _drafts = [];
        private int _draftNumber = 1;

        public FakeFxRevaluationDocumentRepository(
            CompanyId companyId,
            IEnumerable<ActiveBatch> activeBatches)
        {
            _companyId = companyId;
            _activeBatches = activeBatches.ToList();
        }

        public List<Guid> PreparedSourceIds { get; } = [];

        public Task<FxRevaluationDocument?> GetForPostingAsync(
            CompanyId companyId,
            Guid documentId,
            CancellationToken cancellationToken)
        {
            _drafts.TryGetValue(documentId, out var document);
            return Task.FromResult(document);
        }

        public Task<IReadOnlyList<FxRevaluationBatchListItem>> ListRecentAsync(
            CompanyId companyId,
            int take,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FxRevaluationBatchListItem>>(Array.Empty<FxRevaluationBatchListItem>());

        public Task<FxRevaluationCascadeUnwindPlanResult> GetCascadeUnwindPlanAsync(
            CompanyId companyId,
            Guid documentId,
            CancellationToken cancellationToken)
        {
            var requested = _activeBatches.Single(batch => batch.Id == documentId);
            var activeChain = _activeBatches
                .Select((batch, index) => new FxRevaluationCascadePlanner.ActiveRevaluationBatch(
                    batch.Id,
                    batch.DisplayNumber,
                    new DateOnly(2026, 5, 31).AddDays(-index),
                    new DateTimeOffset(2026, 5, 31 - index, 10, 0, 0, TimeSpan.Zero)))
                .ToArray();

            return Task.FromResult(
                FxRevaluationCascadePlanner.BuildPlan(
                    requested.Id,
                    requested.DisplayNumber,
                    activeChain));
        }

        public Task<FxRevaluationDraftPreparationResult> PrepareDraftAsync(
            FxRevaluationDraftPreparation request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<FxRevaluationDraftPreparationResult> PrepareNextPeriodUnwindDraftAsync(
            FxRevaluationUnwindPreparation request,
            CancellationToken cancellationToken)
        {
            var sourceBatch = _activeBatches.Single(batch => batch.Id == request.ReversalOfDocumentId);
            PreparedSourceIds.Add(sourceBatch.Id);

            var draftId = Guid.NewGuid();
            var draftDisplayNumber = $"FXUN-{_draftNumber:000000}";
            _draftNumber++;
            _drafts[draftId] = CreateDraftDocument(
                draftId,
                draftDisplayNumber,
                request.CompanyId,
                sourceBatch.Id);

            _activeBatches.Remove(sourceBatch);

            return Task.FromResult(new FxRevaluationDraftPreparationResult(
                draftId,
                $"EN2026{_draftNumber:000000}",
                draftDisplayNumber,
                null,
                null,
                null,
                null,
                null,
                1,
                "draft"));
        }

        private static FxRevaluationDocument CreateDraftDocument(
            Guid draftId,
            string displayNumber,
            CompanyId companyId,
            Guid sourceBatchId) =>
            new(
                draftId,
                companyId,
                EntityNumber.FromLegacy("EN-LEGACY-TEST"),
                new DocumentNumber(displayNumber),
                "draft",
                new DateOnly(2026, 6, 1),
                new CurrencyCode("EUR"),
                new CurrencyCode("USD"),
                new FxSnapshotRef(
                    Guid.NewGuid(),
                    new CurrencyCode("USD"),
                    new CurrencyCode("EUR"),
                    1.20m,
                    new DateOnly(2026, 6, 1),
                    new DateOnly(2026, 6, 1),
                    "company_override"),
                Guid.NewGuid(),
                Guid.NewGuid(),
                [
                    new FxRevaluationDocumentLine(
                        1,
                        "ar_open_item",
                        Guid.NewGuid(),
                        "debit",
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        "Cascade unwind line",
                        100m,
                        120m,
                        115m,
                        -5m)
                ],
                memo: "Auto-post cascade unwind",
                batchKind: "next_period_unwind",
                reversalOfDocumentId: sourceBatchId);
    }

    private sealed class RecordingPostingEngine : IPostingEngine
    {
        public List<Guid> PostedDocuments { get; } = [];

        public List<string> CapturedIdempotencyKeys { get; } = [];

        public Task<PostingResult> PostAsync(
            IPostingDocument document,
            PostingContext context,
            CancellationToken cancellationToken)
        {
            PostedDocuments.Add(document.Id);
            CapturedIdempotencyKeys.Add(context.IdempotencyKey ?? string.Empty);

            return Task.FromResult(new PostingResult(
                Guid.NewGuid(),
                $"JE-{PostedDocuments.Count:000000}",
                "posted",
                DateTimeOffset.UtcNow,
                Array.Empty<string>()));
        }
    }

    private sealed class RecordingFxRevaluationApplyRepository : IFxRevaluationApplyRepository
    {
        public List<Guid> AppliedDocumentIds { get; } = [];

        public Task ApplyAsync(
            FxRevaluationDocument document,
            UserId appliedByUserId,
            CancellationToken cancellationToken)
        {
            AppliedDocumentIds.Add(document.Id);
            return Task.CompletedTask;
        }
    }

    private sealed class ImmediateUnitOfWork : IUnitOfWork
    {
        public Task<T> ExecuteAsync<T>(
            Func<CancellationToken, Task<T>> action,
            CancellationToken cancellationToken) =>
            action(cancellationToken);
    }
}
