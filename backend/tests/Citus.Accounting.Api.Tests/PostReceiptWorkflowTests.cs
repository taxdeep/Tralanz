using Citus.Accounting.Application;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Documents;
using Citus.Modules.Inventory.Application.Contracts;

namespace Citus.Accounting.Api.Tests;

public sealed class PostReceiptWorkflowTests
{
    [Fact]
    public async Task PostAsync_PreflightsPostsAndActivatesDraftReceipt()
    {
        var companyId = CompanyId.FromOrdinal(1);
        var userId = UserId.FromOrdinal(1);
        var documentId = Guid.NewGuid();
        var repository = new FakeReceiptDocumentRepository(BuildReceipt(documentId, companyId, ReceiptDocumentStatuses.Draft));
        var activationStore = new FakeReceiptInventoryActivationStore();
        var valuationStore = new FakeReceiptInventoryValuationStore();
        var emissionStore = new FakeReceiptInventoryCostLayerEmissionStore();
        var workflow = new PostReceiptWorkflow(repository, activationStore, valuationStore, emissionStore, new FakeInventoryReceiptUnitOfWork());

        var result = await workflow.PostAsync(companyId, userId, documentId, CancellationToken.None);

        Assert.Equal(ReceiptDocumentStatuses.Posted, result.Status);
        Assert.Equal(1, activationStore.ValidateCalls);
        Assert.Equal(1, repository.PostCalls);
        Assert.Equal(1, activationStore.ActivateCalls);
        Assert.Equal(1, valuationStore.RefreshCalls);
        Assert.Equal(1, emissionStore.EmitCalls);
    }

    [Fact]
    public async Task PostAsync_ReusesPostedReceiptForIdempotentActivationRetry()
    {
        var companyId = CompanyId.FromOrdinal(1);
        var userId = UserId.FromOrdinal(1);
        var documentId = Guid.NewGuid();
        var repository = new FakeReceiptDocumentRepository(BuildReceipt(documentId, companyId, ReceiptDocumentStatuses.Posted));
        var activationStore = new FakeReceiptInventoryActivationStore();
        var valuationStore = new FakeReceiptInventoryValuationStore();
        var emissionStore = new FakeReceiptInventoryCostLayerEmissionStore();
        var workflow = new PostReceiptWorkflow(repository, activationStore, valuationStore, emissionStore, new FakeInventoryReceiptUnitOfWork());

        var result = await workflow.PostAsync(companyId, userId, documentId, CancellationToken.None);

        Assert.Equal(ReceiptDocumentStatuses.Posted, result.Status);
        Assert.Equal(0, activationStore.ValidateCalls);
        Assert.Equal(0, repository.PostCalls);
        Assert.Equal(1, activationStore.ActivateCalls);
        Assert.Equal(1, valuationStore.RefreshCalls);
        Assert.Equal(1, emissionStore.EmitCalls);
    }

    [Fact]
    public async Task PostAsync_RejectsMissingReceipt()
    {
        var companyId = CompanyId.FromOrdinal(1);
        var userId = UserId.FromOrdinal(1);
        var documentId = Guid.NewGuid();
        var repository = new FakeReceiptDocumentRepository(null);
        var activationStore = new FakeReceiptInventoryActivationStore();
        var valuationStore = new FakeReceiptInventoryValuationStore();
        var emissionStore = new FakeReceiptInventoryCostLayerEmissionStore();
        var workflow = new PostReceiptWorkflow(repository, activationStore, valuationStore, emissionStore, new FakeInventoryReceiptUnitOfWork());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => workflow.PostAsync(companyId, userId, documentId, CancellationToken.None));

        Assert.Contains("not found", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, repository.PostCalls);
        Assert.Equal(0, activationStore.ActivateCalls);
        Assert.Equal(0, valuationStore.RefreshCalls);
        Assert.Equal(0, emissionStore.EmitCalls);
    }

    private static ReceiptDocument BuildReceipt(Guid documentId, CompanyId companyId, string status) =>
        new(
            documentId,
            companyId,
            EntityNumber.Create(2026, 1),
            new DocumentNumber("RECEIPT-000001"),
            status,
            Guid.NewGuid(),
            Guid.NewGuid(),
            new DateOnly(2026, 4, 19),
            new[]
            {
                new ReceiptDocumentLine(1, Guid.NewGuid(), 3m, "EA")
            },
            null,
            null,
            null,
            string.Equals(status, ReceiptDocumentStatuses.Posted, StringComparison.OrdinalIgnoreCase)
                ? DateTimeOffset.UtcNow
                : null);

    private sealed class FakeReceiptDocumentRepository : IReceiptDocumentRepository
    {
        private ReceiptDocument? _document;

        public FakeReceiptDocumentRepository(ReceiptDocument? document)
        {
            _document = document;
        }

        public int PostCalls { get; private set; }

        public Task EnsureSchemaAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<ReceiptDocument?> GetAsync(CompanyId companyId, Guid documentId, CancellationToken cancellationToken) =>
            Task.FromResult<ReceiptDocument?>(_document is not null && _document.Id == documentId ? _document : null);

        public Task<IReadOnlyList<ReceiptDocumentListItem>> ListAsync(CompanyId companyId, int take, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReceiptDocumentListItem>>(Array.Empty<ReceiptDocumentListItem>());

        public Task<SourceDocumentDraftSaveResult> SaveDraftAsync(ReceiptDraftSaveModel draft, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SourceDocumentDraftSaveResult> PostAsync(CompanyId companyId, UserId userId, Guid documentId, CancellationToken cancellationToken)
        {
            PostCalls++;
            if (_document is null)
            {
                throw new InvalidOperationException("Receipt document was not found in the active company context.");
            }

            _document = BuildReceipt(_document.Id, _document.CompanyId, ReceiptDocumentStatuses.Posted);

            return Task.FromResult(new SourceDocumentDraftSaveResult(
                _document.Id,
                _document.EntityNumber.Value,
                _document.DisplayNumber.Value,
                ReceiptDocumentStatuses.Posted));
        }
    }

    private sealed class FakeReceiptInventoryActivationStore : IReceiptInventoryActivationStore
    {
        public int ValidateCalls { get; private set; }

        public int ActivateCalls { get; private set; }

        public Task EnsureSchemaAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task ValidateCanActivateAsync(CompanyId companyId, Guid receiptDocumentId, CancellationToken cancellationToken)
        {
            ValidateCalls++;
            return Task.CompletedTask;
        }

        public Task<ReceiptInventoryActivationSummary> ActivatePostedReceiptAsync(CompanyId companyId, UserId userId, Guid receiptDocumentId, CancellationToken cancellationToken)
        {
            ActivateCalls++;
            return Task.FromResult(new ReceiptInventoryActivationSummary(
                receiptDocumentId,
                ReceiptDocumentStatuses.Posted,
                "activated",
                Guid.NewGuid(),
                1,
                1,
                3m,
                3m,
                DateTimeOffset.UtcNow));
        }

        public Task RecordActivationFailureAsync(CompanyId companyId, UserId userId, Guid receiptDocumentId, string failureMessage, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<ReceiptInventoryActivationSummary?> GetReceiptActivationSummaryAsync(CompanyId companyId, Guid receiptDocumentId, CancellationToken cancellationToken) =>
            Task.FromResult<ReceiptInventoryActivationSummary?>(null);

        public Task<IReadOnlyDictionary<Guid, ReceiptInventoryActivationSummary>> GetReceiptActivationSummariesAsync(CompanyId companyId, IReadOnlyCollection<Guid> receiptDocumentIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, ReceiptInventoryActivationSummary>>(new Dictionary<Guid, ReceiptInventoryActivationSummary>());
    }

    private sealed class FakeReceiptInventoryValuationStore : IReceiptInventoryValuationStore
    {
        public int RefreshCalls { get; private set; }

        public Task EnsureSchemaAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<ReceiptInventoryValuationSummary> RefreshReceiptValuationAsync(CompanyId companyId, UserId userId, Guid receiptDocumentId, CancellationToken cancellationToken)
        {
            RefreshCalls++;
            return Task.FromResult(new ReceiptInventoryValuationSummary(
                receiptDocumentId,
                ReceiptInventoryValuationStatusPolicy.ValuationBoundaryComplete,
                3m,
                3m,
                3m,
                0m,
                1,
                30m,
                DateTimeOffset.UtcNow));
        }

        public Task<ReceiptInventoryValuationSummary?> GetReceiptValuationSummaryAsync(CompanyId companyId, Guid receiptDocumentId, CancellationToken cancellationToken) =>
            Task.FromResult<ReceiptInventoryValuationSummary?>(null);

        public Task<IReadOnlyDictionary<Guid, ReceiptInventoryValuationSummary>> GetReceiptValuationSummariesAsync(CompanyId companyId, IReadOnlyCollection<Guid> receiptDocumentIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, ReceiptInventoryValuationSummary>>(new Dictionary<Guid, ReceiptInventoryValuationSummary>());
    }

    private sealed class FakeReceiptInventoryCostLayerEmissionStore : IReceiptInventoryCostLayerEmissionStore
    {
        public int EmitCalls { get; private set; }

        public Task EnsureSchemaAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<ReceiptInventoryCostLayerEmissionSummary> EmitReceiptCostLayersAsync(CompanyId companyId, UserId userId, Guid receiptDocumentId, CancellationToken cancellationToken)
        {
            EmitCalls++;
            return Task.FromResult(new ReceiptInventoryCostLayerEmissionSummary(
                receiptDocumentId,
                ReceiptInventoryCostLayerEmissionStatusPolicy.FullyEmitted,
                3m,
                3m,
                3m,
                3m,
                0m,
                1,
                30m,
                DateTimeOffset.UtcNow));
        }

        public Task<ReceiptInventoryCostLayerEmissionSummary?> GetReceiptCostLayerEmissionSummaryAsync(CompanyId companyId, Guid receiptDocumentId, CancellationToken cancellationToken) =>
            Task.FromResult<ReceiptInventoryCostLayerEmissionSummary?>(null);

        public Task<IReadOnlyDictionary<Guid, ReceiptInventoryCostLayerEmissionSummary>> GetReceiptCostLayerEmissionSummariesAsync(CompanyId companyId, IReadOnlyCollection<Guid> receiptDocumentIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, ReceiptInventoryCostLayerEmissionSummary>>(new Dictionary<Guid, ReceiptInventoryCostLayerEmissionSummary>());

        public Task<ReceiptInventoryCostLayerEmissionReconciliationSummary?> GetReceiptCostLayerEmissionReconciliationSummaryAsync(CompanyId companyId, Guid receiptDocumentId, CancellationToken cancellationToken) =>
            Task.FromResult<ReceiptInventoryCostLayerEmissionReconciliationSummary?>(null);

        public Task<IReadOnlyDictionary<Guid, ReceiptInventoryCostLayerEmissionReconciliationSummary>> GetReceiptCostLayerEmissionReconciliationSummariesAsync(CompanyId companyId, IReadOnlyCollection<Guid> receiptDocumentIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, ReceiptInventoryCostLayerEmissionReconciliationSummary>>(new Dictionary<Guid, ReceiptInventoryCostLayerEmissionReconciliationSummary>());
    }

    private sealed class FakeInventoryReceiptUnitOfWork : IInventoryReceiptUnitOfWork
    {
        public Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken) =>
            action(cancellationToken);
    }
}
