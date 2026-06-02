using Infrastructure.PostgreSQL.Inventory;

public sealed class InventoryAdjustmentAccountPolicyTests
{
    [Fact]
    public void InventoryAssetAccountRequiresAssetRootType()
    {
        Assert.True(InventoryAdjustmentAccountPolicy.AllowsInventoryAssetRootType("asset"));
        Assert.True(InventoryAdjustmentAccountPolicy.AllowsInventoryAssetRootType(" Asset "));

        Assert.False(InventoryAdjustmentAccountPolicy.AllowsInventoryAssetRootType("expense"));
        Assert.False(InventoryAdjustmentAccountPolicy.AllowsInventoryAssetRootType("liability"));
        Assert.False(InventoryAdjustmentAccountPolicy.AllowsInventoryAssetRootType(null));
    }

    [Theory]
    [InlineData("revenue")]
    [InlineData("cost_of_sales")]
    [InlineData("expense")]
    public void AdjustmentOffsetAllowsIncomeStatementRootTypes(string rootType)
    {
        Assert.True(InventoryAdjustmentAccountPolicy.AllowsAdjustmentOffsetRootType(rootType));
    }

    [Theory]
    [InlineData("asset")]
    [InlineData("liability")]
    [InlineData("equity")]
    public void AdjustmentOffsetRejectsBalanceSheetRootTypes(string rootType)
    {
        Assert.False(InventoryAdjustmentAccountPolicy.AllowsAdjustmentOffsetRootType(rootType));
    }
}
