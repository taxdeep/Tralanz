using Citus.Modules.Inventory.Application.Contracts;

namespace Citus.Accounting.Api.Tests;

public sealed class LegacyInboundReceiptPathPolicyTests
{
    [Theory]
    [InlineData("receipt_document", LegacyInboundReceiptPathPolicy.BlockedFirstClassReceiptSource)]
    [InlineData("first_class_receipt", LegacyInboundReceiptPathPolicy.BlockedFirstClassReceiptSource)]
    public void Evaluate_RejectsFirstClassReceiptSources(string sourceModule, string expectedCode)
    {
        var request = BuildRequest(sourceModule);

        var decision = LegacyInboundReceiptPathPolicy.Evaluate(request, snapshot: null);

        Assert.False(decision.IsAllowed);
        Assert.Equal(expectedCode, decision.Code);
    }

    [Fact]
    public void Evaluate_RejectsApBillFallbackWithoutBillAnchor()
    {
        var request = BuildRequest("ap_bill", sourceDocumentId: null);

        var decision = LegacyInboundReceiptPathPolicy.Evaluate(request, snapshot: null);

        Assert.False(decision.IsAllowed);
        Assert.Equal(LegacyInboundReceiptPathPolicy.BlockedMissingBillAnchor, decision.Code);
    }

    [Fact]
    public void Evaluate_RejectsApBillFallbackWhenFirstClassCoverageExists()
    {
        var sourceDocumentId = Guid.NewGuid();
        var request = BuildRequest("ap_bill", sourceDocumentId: sourceDocumentId);
        var snapshot = BuildSnapshot(sourceDocumentId, request.Lines[0], firstClassCoveredQuantity: 1m);

        var decision = LegacyInboundReceiptPathPolicy.Evaluate(request, snapshot);

        Assert.False(decision.IsAllowed);
        Assert.Equal(LegacyInboundReceiptPathPolicy.BlockedFirstClassCoveragePresent, decision.Code);
    }

    [Fact]
    public void Evaluate_RejectsApBillFallbackWhenQuantityExceedsRemainingLegacyCeiling()
    {
        var sourceDocumentId = Guid.NewGuid();
        var request = BuildRequest("ap_bill", sourceDocumentId: sourceDocumentId, quantity: 2m);
        var snapshot = BuildSnapshot(
            sourceDocumentId,
            request.Lines[0],
            billQuantity: 2m,
            legacyReceivedQuantity: 1m);

        var decision = LegacyInboundReceiptPathPolicy.Evaluate(request, snapshot);

        Assert.False(decision.IsAllowed);
        Assert.Equal(LegacyInboundReceiptPathPolicy.BlockedQuantityCeilingExceeded, decision.Code);
    }

    [Fact]
    public void Evaluate_AllowsApBillFallbackWhenNoFirstClassCoverageAndWithinLegacyCeiling()
    {
        var sourceDocumentId = Guid.NewGuid();
        var request = BuildRequest("ap_bill", sourceDocumentId: sourceDocumentId, quantity: 1m);
        var snapshot = BuildSnapshot(
            sourceDocumentId,
            request.Lines[0],
            billQuantity: 2m,
            legacyReceivedQuantity: 1m);

        var decision = LegacyInboundReceiptPathPolicy.Evaluate(request, snapshot);

        Assert.True(decision.IsAllowed);
        Assert.Equal(LegacyInboundReceiptPathPolicy.AllowedTransitionalFallback, decision.Code);
    }

    [Fact]
    public void Evaluate_AllowsNonBillSources()
    {
        var request = BuildRequest("web_shell_inventory");

        var decision = LegacyInboundReceiptPathPolicy.Evaluate(request, snapshot: null);

        Assert.True(decision.IsAllowed);
        Assert.Equal(LegacyInboundReceiptPathPolicy.AllowedNonBillSource, decision.Code);
    }

    private static InventoryPurchaseReceiptPostRequest BuildRequest(
        string? sourceModule,
        Guid? sourceDocumentId = null,
        decimal quantity = 1m)
    {
        var effectiveSourceDocumentId =
            sourceModule == "ap_bill"
                ? sourceDocumentId
                : sourceDocumentId ?? Guid.NewGuid();

        return new InventoryPurchaseReceiptPostRequest(
            CompanyId.FromOrdinal(1),
            UserId.FromOrdinal(1),
            Guid.NewGuid(),
            new DateOnly(2026, 4, 19),
            "CAD",
            1m,
            sourceModule,
            effectiveSourceDocumentId,
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
    }

    private static LegacyInboundReceiptPathSnapshot BuildSnapshot(
        Guid billDocumentId,
        InventoryPurchaseReceiptLineInput line,
        decimal billQuantity = 1m,
        decimal legacyReceivedQuantity = 0m,
        decimal firstClassCoveredQuantity = 0m) =>
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
}
