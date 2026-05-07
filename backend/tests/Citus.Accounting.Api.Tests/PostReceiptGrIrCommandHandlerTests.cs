using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.Commands;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Documents;
using Citus.Accounting.Domain.Posting;
using Citus.Modules.Inventory.Application.Contracts;

namespace Citus.Accounting.Api.Tests;

public sealed class PostReceiptGrIrCommandHandlerTests
{
    [Fact]
    public async Task HandleAsync_RefreshesBridgePostsThroughPostingEngineAndCompletesBatch()
    {
        var companyId = CompanyId.FromOrdinal(1);
        var userId = UserId.FromOrdinal(1);
        var receiptId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var grIrClearingAccountId = Guid.NewGuid();
        var document = BuildDocument(companyId, receiptId, batchId, grIrClearingAccountId);
        var bridgeStore = new FakeReceiptGrIrBridgeStore();
        var clearingAccountPolicyRepository = new FakeReceiptGrIrClearingAccountPolicyRepository(null);
        var repository = new FakeReceiptGrIrPostingRepository(document);
        var postingEngine = new FakePostingEngine();
        var handler = new PostReceiptGrIrCommandHandler(
            bridgeStore,
            clearingAccountPolicyRepository,
            repository,
            postingEngine,
            new InlineUnitOfWork());

        var result = await handler.HandleAsync(
            new PostReceiptGrIrCommand(companyId, userId, receiptId, grIrClearingAccountId, IdempotencyKey: null),
            CancellationToken.None);

        Assert.Equal(1, bridgeStore.RefreshCalls);
        Assert.Equal(1, repository.PrepareCalls);
        Assert.Equal(1, postingEngine.PostCalls);
        Assert.Equal(1, repository.CompleteCalls);
        Assert.Equal(grIrClearingAccountId, repository.LastGrIrClearingAccountId);
        Assert.Equal(batchId, result.PostingBatchId);
        Assert.Equal(postingEngine.JournalEntryId, result.JournalEntryId);
        Assert.Equal($"receipt-grir:{companyId}:{batchId}", postingEngine.LastContext?.IdempotencyKey);
    }

    [Fact]
    public async Task HandleAsync_UsesCompanyDefaultClearingAccountWhenRequestOmitsAccount()
    {
        var companyId = CompanyId.FromOrdinal(1);
        var userId = UserId.FromOrdinal(1);
        var receiptId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var grIrClearingAccountId = Guid.NewGuid();
        var document = BuildDocument(companyId, receiptId, batchId, grIrClearingAccountId);
        var repository = new FakeReceiptGrIrPostingRepository(document);
        var handler = new PostReceiptGrIrCommandHandler(
            new FakeReceiptGrIrBridgeStore(),
            new FakeReceiptGrIrClearingAccountPolicyRepository(grIrClearingAccountId),
            repository,
            new FakePostingEngine(),
            new InlineUnitOfWork());

        _ = await handler.HandleAsync(
            new PostReceiptGrIrCommand(companyId, userId, receiptId, GrIrClearingAccountId: null, IdempotencyKey: null),
            CancellationToken.None);

        Assert.Equal(grIrClearingAccountId, repository.LastGrIrClearingAccountId);
    }

    private static ReceiptGrIrPostingDocument BuildDocument(
        CompanyId companyId,
        Guid receiptId,
        Guid batchId,
        Guid grIrClearingAccountId) =>
        new(
            batchId,
            companyId,
            EntityNumber.Create(2026, 1),
            new DocumentNumber("GRIR-TEST"),
            "draft",
            receiptId,
            new DateOnly(2026, 4, 19),
            new CurrencyCode("USD"),
            grIrClearingAccountId,
            new[]
            {
                new ReceiptGrIrPostingDocumentLine(
                    1,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    grIrClearingAccountId,
                    "Receipt GR/IR bridge line 1",
                    50m)
            });

    private sealed class FakeReceiptGrIrBridgeStore : IReceiptGrIrBridgeStore
    {
        public int RefreshCalls { get; private set; }

        public Task<ReceiptGrIrBridgeSummary> RefreshReceiptGrIrBridgeAsync(
            CompanyId companyId,
            UserId userId,
            Guid receiptDocumentId,
            CancellationToken cancellationToken)
        {
            RefreshCalls++;
            return Task.FromResult(new ReceiptGrIrBridgeSummary(
                receiptDocumentId,
                ReceiptGrIrBridgeStatusPolicy.EligibleNotPosted,
                1,
                1,
                0,
                0,
                0,
                5m,
                50m,
                50m,
                0m,
                0m,
                null,
                null,
                null,
                DateTimeOffset.UtcNow));
        }

        public Task<ReceiptGrIrBridgeSummary?> GetReceiptGrIrBridgeSummaryAsync(
            CompanyId companyId,
            Guid receiptDocumentId,
            CancellationToken cancellationToken) =>
            Task.FromResult<ReceiptGrIrBridgeSummary?>(null);

        public Task<IReadOnlyDictionary<Guid, ReceiptGrIrBridgeSummary>> GetReceiptGrIrBridgeSummariesAsync(
            CompanyId companyId,
            IReadOnlyCollection<Guid> receiptDocumentIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, ReceiptGrIrBridgeSummary>>(
                new Dictionary<Guid, ReceiptGrIrBridgeSummary>());
    }

    private sealed class FakeReceiptGrIrPostingRepository : IReceiptGrIrPostingRepository
    {
        private readonly ReceiptGrIrPostingDocument _document;

        public FakeReceiptGrIrPostingRepository(ReceiptGrIrPostingDocument document)
        {
            _document = document;
        }

        public int PrepareCalls { get; private set; }

        public int CompleteCalls { get; private set; }

        public Guid? LastGrIrClearingAccountId { get; private set; }

        public Task<ReceiptGrIrPostingDocument> PreparePostingDocumentAsync(
            CompanyId companyId,
            UserId userId,
            Guid receiptDocumentId,
            Guid grIrClearingAccountId,
            CancellationToken cancellationToken)
        {
            PrepareCalls++;
            LastGrIrClearingAccountId = grIrClearingAccountId;
            return Task.FromResult(_document);
        }

        public Task CompletePostingAsync(
            CompanyId companyId,
            UserId userId,
            Guid postingBatchId,
            Guid journalEntryId,
            string journalEntryDisplayNumber,
            CancellationToken cancellationToken)
        {
            CompleteCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeReceiptGrIrClearingAccountPolicyRepository : IReceiptGrIrClearingAccountPolicyRepository
    {
        private readonly Guid? _defaultGrIrClearingAccountId;

        public FakeReceiptGrIrClearingAccountPolicyRepository(Guid? defaultGrIrClearingAccountId)
        {
            _defaultGrIrClearingAccountId = defaultGrIrClearingAccountId;
        }

        public Task<Guid?> GetDefaultGrIrClearingAccountIdAsync(
            CompanyId companyId,
            CancellationToken cancellationToken) =>
            Task.FromResult(_defaultGrIrClearingAccountId);

        public Task SaveDefaultGrIrClearingAccountAsync(
            CompanyId companyId,
            UserId userId,
            Guid grIrClearingAccountId,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FakePostingEngine : IPostingEngine
    {
        public Guid JournalEntryId { get; } = Guid.NewGuid();

        public int PostCalls { get; private set; }

        public PostingContext? LastContext { get; private set; }

        public Task<PostingResult> PostAsync(
            IPostingDocument document,
            PostingContext context,
            CancellationToken cancellationToken)
        {
            PostCalls++;
            LastContext = context;
            return Task.FromResult(new PostingResult(
                JournalEntryId,
                "JE-TEST",
                "posted",
                DateTimeOffset.UtcNow,
                Array.Empty<string>()));
        }
    }

    private sealed class InlineUnitOfWork : IUnitOfWork
    {
        public Task<T> ExecuteAsync<T>(
            Func<CancellationToken, Task<T>> action,
            CancellationToken cancellationToken) =>
            action(cancellationToken);
    }
}
