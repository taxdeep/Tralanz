using Citus.Modules.Inventory.Application;
using Citus.Modules.Inventory.Application.Contracts;

namespace Citus.Accounting.Api.Tests;

public sealed class InventoryReceiptWorkflowLegacyGuardTests
{
    [Theory]
    [InlineData("receipt_document")]
    [InlineData("first_class_receipt")]
    public async Task PostAsync_RejectsFirstClassReceiptSourceModules_OnLegacyPath(string sourceModule)
    {
        var workflow = new InventoryReceiptWorkflow(new FakeInventoryReceiptStore());
        var request = BuildRequest(sourceModule);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => workflow.PostAsync(request, CancellationToken.None));

        Assert.Contains("receipt activation workflow", exception.Message);
    }

    [Theory]
    [InlineData("web_shell_inventory")]
    [InlineData(null)]
    public async Task PostAsync_AllowsTransitionalLegacySourceModules(string? sourceModule)
    {
        var store = new FakeInventoryReceiptStore();
        var workflow = new InventoryReceiptWorkflow(store);

        await workflow.PostAsync(BuildRequest(sourceModule), CancellationToken.None);

        Assert.Equal(1, store.PostCalls);
    }

    [Fact]
    public async Task PostAsync_RejectsApBillFallback_WhenFirstClassReceiptCoverageExists()
    {
        var request = BuildRequest("ap_bill");
        var store = new FakeInventoryReceiptStore
        {
            Snapshot = BuildSnapshot(
                request.SourceDocumentId!.Value,
                request.Lines[0],
                firstClassCoveredQuantity: 1m,
                legacyReceivedQuantity: 0m)
        };
        var workflow = new InventoryReceiptWorkflow(store);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => workflow.PostAsync(request, CancellationToken.None));

        Assert.Contains("first-class receipt matching coverage", exception.Message);
        Assert.Equal(0, store.PostCalls);
    }

    [Fact]
    public async Task PostAsync_RejectsApBillFallback_WhenRequestExceedsRemainingBillQuantity()
    {
        var request = BuildRequest("ap_bill", quantity: 2m);
        var store = new FakeInventoryReceiptStore
        {
            Snapshot = BuildSnapshot(
                request.SourceDocumentId!.Value,
                request.Lines[0],
                billQuantity: 2m,
                firstClassCoveredQuantity: 0m,
                legacyReceivedQuantity: 1m)
        };
        var workflow = new InventoryReceiptWorkflow(store);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => workflow.PostAsync(request, CancellationToken.None));

        Assert.Contains("remaining bill quantity", exception.Message);
        Assert.Equal(0, store.PostCalls);
    }

    [Fact]
    public async Task PostAsync_AllowsApBillFallback_WhenNoFirstClassCoverageAndWithinRemainingQuantity()
    {
        var request = BuildRequest("ap_bill", quantity: 1m);
        var store = new FakeInventoryReceiptStore
        {
            Snapshot = BuildSnapshot(
                request.SourceDocumentId!.Value,
                request.Lines[0],
                billQuantity: 2m,
                firstClassCoveredQuantity: 0m,
                legacyReceivedQuantity: 1m)
        };
        var workflow = new InventoryReceiptWorkflow(store);

        await workflow.PostAsync(request, CancellationToken.None);

        Assert.Equal(1, store.PostCalls);
    }

    private static InventoryPurchaseReceiptPostRequest BuildRequest(string? sourceModule, decimal quantity = 1m) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            new DateOnly(2026, 4, 19),
            "CAD",
            1m,
            sourceModule,
            Guid.NewGuid(),
            "SRC-1",
            null,
            new[]
            {
                new InventoryPurchaseReceiptLineInput(
                    1,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    "EA",
                    quantity,
                    0m,
                    null,
                    null)
            });

    private static LegacyInboundReceiptPathSnapshot BuildSnapshot(
        Guid billDocumentId,
        InventoryPurchaseReceiptLineInput line,
        decimal firstClassCoveredQuantity,
        decimal legacyReceivedQuantity,
        decimal billQuantity = 1m) =>
        new(
            billDocumentId,
            1,
            billQuantity,
            legacyReceivedQuantity > 0m ? 1 : 0,
            legacyReceivedQuantity,
            firstClassCoveredQuantity > 0m ? 1 : 0,
            firstClassCoveredQuantity,
            new[]
            {
                new LegacyInboundReceiptPathLineSnapshot(
                    line.ItemId,
                    "ITEM",
                    line.WarehouseId,
                    "MAIN",
                    line.UomCode,
                    billQuantity,
                    legacyReceivedQuantity,
                    firstClassCoveredQuantity)
            });

    private sealed class FakeInventoryReceiptStore : IInventoryReceiptStore
    {
        public int PostCalls { get; private set; }

        public Task<InventoryPurchaseReceiptDashboard> GetDashboardAsync(Guid companyId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<InventoryBillReceiptHandoffSummary> GetBillHandoffSummaryAsync(Guid companyId, Guid billDocumentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, InventoryBillReceiptPostingGateSnapshot>> GetBillPostingGateSnapshotsAsync(Guid companyId, IReadOnlyCollection<Guid> billDocumentIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public LegacyInboundReceiptPathSnapshot? Snapshot { get; init; }

        public Task<LegacyInboundReceiptPathSnapshot?> GetLegacyInboundReceiptPathSnapshotAsync(Guid companyId, Guid billDocumentId, CancellationToken cancellationToken) =>
            Task.FromResult(Snapshot);

        public Task<InventoryPurchaseReceiptSummary> PostAsync(InventoryPurchaseReceiptPostRequest request, CancellationToken cancellationToken)
        {
            PostCalls++;
            return Task.FromResult(new InventoryPurchaseReceiptSummary(
                Guid.NewGuid(),
                request.CompanyId,
                "PR-TEST",
                "posted",
                request.PostingDate,
                request.VendorId,
                "Vendor",
                request.TransactionCurrencyCode,
                request.TransactionCurrencyCode,
                request.FxRateToBase,
                request.Lines.Sum(static line => line.Quantity),
                0m,
                request.Lines.Count,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                null));
        }
    }
}
