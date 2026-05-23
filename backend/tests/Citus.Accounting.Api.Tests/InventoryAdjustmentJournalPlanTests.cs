using Citus.Modules.Inventory.Domain.Shared;
using Infrastructure.PostgreSQL.Inventory;

public sealed class InventoryAdjustmentJournalPlanTests
{
    [Fact]
    public void GainDebitsInventoryAssetAndCreditsAdjustmentOffset()
    {
        var inventoryAssetAccountId = Guid.NewGuid();
        var adjustmentAccountId = Guid.NewGuid();

        var lines = InventoryAdjustmentJournalPlan.Build(
            InventoryAdjustmentKind.Gain,
            "IAG-20260522-ABC",
            new[]
            {
                new InventoryAdjustmentJournalCandidate(
                    1,
                    "SKU-001",
                    inventoryAssetAccountId,
                    adjustmentAccountId,
                    125.456789m)
            });

        Assert.Equal(2, lines.Count);
        Assert.Equal(inventoryAssetAccountId, lines[0].AccountId);
        Assert.Equal(125.456789m, lines[0].Debit);
        Assert.Equal(0m, lines[0].Credit);
        Assert.Equal(adjustmentAccountId, lines[1].AccountId);
        Assert.Equal(0m, lines[1].Debit);
        Assert.Equal(125.456789m, lines[1].Credit);
        AssertBalanced(lines);
    }

    [Theory]
    [InlineData(InventoryAdjustmentKind.Loss)]
    [InlineData(InventoryAdjustmentKind.WriteOff)]
    public void LossAndWriteOffDebitAdjustmentAndCreditInventoryAsset(InventoryAdjustmentKind adjustmentKind)
    {
        var inventoryAssetAccountId = Guid.NewGuid();
        var adjustmentAccountId = Guid.NewGuid();

        var lines = InventoryAdjustmentJournalPlan.Build(
            adjustmentKind,
            "IWO-20260522-ABC",
            new[]
            {
                new InventoryAdjustmentJournalCandidate(
                    3,
                    "SKU-003",
                    inventoryAssetAccountId,
                    adjustmentAccountId,
                    88.12m)
            });

        Assert.Equal(2, lines.Count);
        Assert.Equal(adjustmentAccountId, lines[0].AccountId);
        Assert.Equal(88.12m, lines[0].Debit);
        Assert.Equal(0m, lines[0].Credit);
        Assert.Equal(inventoryAssetAccountId, lines[1].AccountId);
        Assert.Equal(0m, lines[1].Debit);
        Assert.Equal(88.12m, lines[1].Credit);
        AssertBalanced(lines);
    }

    [Fact]
    public void ZeroValueAdjustmentDoesNotCreateZeroAmountJournalLines()
    {
        var lines = InventoryAdjustmentJournalPlan.Build(
            InventoryAdjustmentKind.Gain,
            "IAG-20260522-ZERO",
            new[]
            {
                new InventoryAdjustmentJournalCandidate(
                    1,
                    "SKU-ZERO",
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    0m)
            });

        Assert.Empty(lines);
    }

    private static void AssertBalanced(IReadOnlyList<InventoryAdjustmentJournalLine> lines)
    {
        Assert.Equal(
            Math.Round(lines.Sum(static line => line.Debit), 6, MidpointRounding.ToEven),
            Math.Round(lines.Sum(static line => line.Credit), 6, MidpointRounding.ToEven));
        Assert.Equal(
            Math.Round(lines.Sum(static line => line.TxDebit), 6, MidpointRounding.ToEven),
            Math.Round(lines.Sum(static line => line.TxCredit), 6, MidpointRounding.ToEven));
    }
}
