using Citus.Modules.Inventory.Application;
using Citus.Modules.Inventory.Application.Contracts;
using Citus.Modules.Inventory.Domain.Shared;
using SharedKernel.Identity;

public sealed class InventoryAdjustmentWorkflowIdempotencyTests
{
    [Fact]
    public async Task PostAsyncRejectsEmptyClientRequestId()
    {
        var workflow = new InventoryAdjustmentWorkflow(new ThrowingInventoryAdjustmentStore());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.PostAsync(
                new InventoryAdjustmentPostRequest(
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    InventoryAdjustmentKind.Gain,
                    Guid.NewGuid(),
                    new DateOnly(2026, 5, 23),
                    "test",
                    new[]
                    {
                        new InventoryAdjustmentLineInput(
                            1,
                            Guid.NewGuid(),
                            "EA",
                            1m,
                            10m,
                            null,
                            null)
                    },
                    ClientRequestId: Guid.Empty),
                CancellationToken.None));

        Assert.Contains("Client request id cannot be empty", exception.Message);
    }

    [Fact]
    public async Task RequestWriteOffAsyncRejectsEmptyClientRequestId()
    {
        var workflow = new InventoryAdjustmentWorkflow(new ThrowingInventoryAdjustmentStore());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.RequestWriteOffAsync(
                new InventoryWriteOffRequestPostRequest(
                    CompanyId.FromOrdinal(1),
                    UserId.FromOrdinal(1),
                    Guid.NewGuid(),
                    new DateOnly(2026, 5, 23),
                    "test",
                    new[]
                    {
                        new InventoryAdjustmentLineInput(
                            1,
                            Guid.NewGuid(),
                            "EA",
                            1m,
                            null,
                            null,
                            null)
                    },
                    ClientRequestId: Guid.Empty),
                CancellationToken.None));

        Assert.Contains("Client request id cannot be empty", exception.Message);
    }

    [Fact]
    public async Task PostAsyncPassesNonEmptyClientRequestIdToStore()
    {
        var store = new CapturingInventoryAdjustmentStore();
        var workflow = new InventoryAdjustmentWorkflow(store);
        var clientRequestId = Guid.NewGuid();

        await workflow.PostAsync(
            new InventoryAdjustmentPostRequest(
                CompanyId.FromOrdinal(1),
                UserId.FromOrdinal(1),
                InventoryAdjustmentKind.Gain,
                Guid.NewGuid(),
                new DateOnly(2026, 5, 23),
                "test",
                new[]
                {
                    new InventoryAdjustmentLineInput(
                        1,
                        Guid.NewGuid(),
                        "EA",
                        1m,
                        10m,
                        null,
                        null)
                },
                ClientRequestId: clientRequestId),
            CancellationToken.None);

        Assert.Equal(clientRequestId, store.PostRequest?.ClientRequestId);
    }

    [Fact]
    public async Task RequestWriteOffAsyncPassesNonEmptyClientRequestIdToStore()
    {
        var store = new CapturingInventoryAdjustmentStore();
        var workflow = new InventoryAdjustmentWorkflow(store);
        var clientRequestId = Guid.NewGuid();

        await workflow.RequestWriteOffAsync(
            new InventoryWriteOffRequestPostRequest(
                CompanyId.FromOrdinal(1),
                UserId.FromOrdinal(1),
                Guid.NewGuid(),
                new DateOnly(2026, 5, 23),
                "test",
                new[]
                {
                    new InventoryAdjustmentLineInput(
                        1,
                        Guid.NewGuid(),
                        "EA",
                        1m,
                        null,
                        null,
                        null)
                },
                ClientRequestId: clientRequestId),
            CancellationToken.None);

        Assert.Equal(clientRequestId, store.WriteOffRequest?.ClientRequestId);
    }

    private sealed class CapturingInventoryAdjustmentStore : IInventoryAdjustmentStore
    {
        public InventoryAdjustmentPostRequest? PostRequest { get; private set; }

        public InventoryWriteOffRequestPostRequest? WriteOffRequest { get; private set; }

        public Task EnsureSchemaAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<InventoryAdjustmentDashboard> GetDashboardAsync(
            CompanyId companyId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<InventoryAdjustmentSummary> RequestWriteOffAsync(
            InventoryWriteOffRequestPostRequest request,
            CancellationToken cancellationToken)
        {
            WriteOffRequest = request;
            return Task.FromResult(CreateSummary(
                request.CompanyId,
                request.ClientRequestId ?? Guid.NewGuid(),
                InventoryAdjustmentKind.WriteOff));
        }

        public Task<InventoryAdjustmentSummary> ApproveWriteOffAsync(
            InventoryWriteOffApprovePostRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<InventoryAdjustmentSummary> PostApprovedWriteOffAsync(
            InventoryWriteOffApprovePostRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<InventoryAdjustmentSummary> PostAsync(
            InventoryAdjustmentPostRequest request,
            CancellationToken cancellationToken)
        {
            PostRequest = request;
            return Task.FromResult(CreateSummary(
                request.CompanyId,
                request.ClientRequestId ?? Guid.NewGuid(),
                request.AdjustmentKind));
        }
    }

    private sealed class ThrowingInventoryAdjustmentStore : IInventoryAdjustmentStore
    {
        public Task EnsureSchemaAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<InventoryAdjustmentDashboard> GetDashboardAsync(
            CompanyId companyId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<InventoryAdjustmentSummary> RequestWriteOffAsync(
            InventoryWriteOffRequestPostRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Workflow should reject before calling the store.");

        public Task<InventoryAdjustmentSummary> ApproveWriteOffAsync(
            InventoryWriteOffApprovePostRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<InventoryAdjustmentSummary> PostApprovedWriteOffAsync(
            InventoryWriteOffApprovePostRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<InventoryAdjustmentSummary> PostAsync(
            InventoryAdjustmentPostRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Workflow should reject before calling the store.");
    }

    private static InventoryAdjustmentSummary CreateSummary(
        CompanyId companyId,
        Guid documentId,
        InventoryAdjustmentKind adjustmentKind) =>
        new(
            documentId,
            companyId,
            "INV-TEST",
            "posted",
            adjustmentKind,
            new DateOnly(2026, 5, 23),
            Guid.NewGuid(),
            "MAIN",
            "Main",
            1m,
            10m,
            1,
            DateTimeOffset.UtcNow,
            null,
            DateTimeOffset.UtcNow,
            null);
}
