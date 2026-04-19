using Citus.Modules.Inventory.Application.Contracts;
using Citus.Modules.Inventory.Domain.Shared;
using Web.Shell.Services;

namespace Citus.Business.Blazor.Tests;

public sealed class ShellInventoryAdjustmentRulesTests
{
    private static readonly Guid CompanyId = Guid.Parse("2780d28c-582f-4eb4-9fd4-1768c942d6f0");
    private static readonly Guid UserId = Guid.Parse("b9017dc6-0d61-4782-a330-b1cde0b33a39");
    private static readonly Guid ItemId = Guid.Parse("7a0b7e0d-0116-422a-af6c-c9d7829d6814");
    private static readonly Guid WarehouseId = Guid.Parse("2b99e420-f278-420e-ab7d-3bf1f8a1479a");

    [Fact]
    public void ValidatePost_Fails_WhenWriteOffRequiresApproval()
    {
        var result = ShellInventoryAdjustmentRules.ValidatePost(
            BuildRequest(InventoryAdjustmentKind.WriteOff),
            BuildDashboard(requireWriteOffApproval: true));

        Assert.False(result.Succeeded);
        Assert.Equal("writeoff_requires_approval", result.ErrorCode);
    }

    [Fact]
    public void ValidateWriteOffRequest_Succeeds_WhenContextIsValid()
    {
        var result = ShellInventoryAdjustmentRules.ValidateWriteOffRequest(
            new InventoryWriteOffRequestPostRequest(
                CompanyId,
                UserId,
                WarehouseId,
                new DateOnly(2026, 4, 17),
                "Shrink review",
                [
                    new InventoryAdjustmentLineInput(1, ItemId, "EA", 3m, null, "damage", null)
                ]),
            BuildDashboard(requireWriteOffApproval: true));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ValidateWriteOffApproval_Fails_WhenDocumentIsMissing()
    {
        var result = ShellInventoryAdjustmentRules.ValidateWriteOffApproval(
            new InventoryWriteOffApprovePostRequest(CompanyId, UserId, Guid.NewGuid()),
            BuildDashboard());

        Assert.False(result.Succeeded);
        Assert.Equal("missing_document", result.ErrorCode);
    }

    [Fact]
    public void ValidateWriteOffPost_Fails_WhenDocumentIsNotApproved()
    {
        var documentId = Guid.Parse("4668a76c-6e15-4639-99f6-e4f1d225d6f8");
        var dashboard = BuildDashboard(
            recentAdjustments:
            [
                new InventoryAdjustmentSummary(
                    documentId,
                    CompanyId,
                    "IWO-20260417-ABCD1234",
                    "submitted",
                    InventoryAdjustmentKind.WriteOff,
                    new DateOnly(2026, 4, 17),
                    WarehouseId,
                    "MAIN",
                    "Main Warehouse",
                    3m,
                    0m,
                    1,
                    DateTimeOffset.UtcNow,
                    null,
                    null,
                    "Shrink review")
            ]);

        var result = ShellInventoryAdjustmentRules.ValidateWriteOffPost(
            new InventoryWriteOffApprovePostRequest(CompanyId, UserId, documentId),
            dashboard);

        Assert.False(result.Succeeded);
        Assert.Equal("approval_required", result.ErrorCode);
    }

    [Fact]
    public void ValidatePost_Fails_WhenGainMissingUnitCost()
    {
        var result = ShellInventoryAdjustmentRules.ValidatePost(
            BuildRequest(
                InventoryAdjustmentKind.Gain,
                [
                    new InventoryAdjustmentLineInput(1, ItemId, "EA", 3m, null, null, null)
                ]),
            BuildDashboard());

        Assert.False(result.Succeeded);
        Assert.Equal("missing_unit_cost", result.ErrorCode);
    }

    [Fact]
    public void ValidatePost_Fails_WhenUomMismatches()
    {
        var result = ShellInventoryAdjustmentRules.ValidatePost(
            BuildRequest(
                InventoryAdjustmentKind.Loss,
                [
                    new InventoryAdjustmentLineInput(1, ItemId, "BOX", 3m, null, null, null)
                ]),
            BuildDashboard());

        Assert.False(result.Succeeded);
        Assert.Equal("uom_mismatch", result.ErrorCode);
    }

    [Fact]
    public void ValidatePost_Succeeds_WhenContextIsValid()
    {
        var result = ShellInventoryAdjustmentRules.ValidatePost(
            BuildRequest(InventoryAdjustmentKind.Loss),
            BuildDashboard());

        Assert.True(result.Succeeded);
    }

    private static InventoryAdjustmentPostRequest BuildRequest(
        InventoryAdjustmentKind kind,
        IReadOnlyList<InventoryAdjustmentLineInput>? lines = null) =>
        new(
            CompanyId,
            UserId,
            kind,
            WarehouseId,
            new DateOnly(2026, 4, 17),
            "Cycle count",
            lines ??
            [
                new InventoryAdjustmentLineInput(1, ItemId, "EA", 3m, kind == InventoryAdjustmentKind.Gain ? 25m : null, "count_variance", null)
            ]);

    private static InventoryAdjustmentDashboard BuildDashboard(
        bool requireWriteOffApproval = false,
        IReadOnlyList<InventoryAdjustmentSummary>? recentAdjustments = null) =>
        new(
            CompanyId,
            "CAD",
            new InventoryCostingPolicyRecord(
                CompanyId,
                InventoryCostingMethod.MovingAverage,
                NegativeStockAllowed: false,
                RequireWriteOffApproval: requireWriteOffApproval,
                UserId,
                DateTimeOffset.UtcNow,
                null,
                DateTimeOffset.UtcNow),
            [
                new InventoryManagedItemSummary(
                    ItemId,
                    CompanyId,
                    "STK300",
                    "Cycle Count Item",
                    null,
                    InventoryItemKind.Stock,
                    "EA",
                    ManageInventoryMethod.ManageStock,
                    InventoryCostingMethod.Fifo,
                    InventoryBackorderMode.Disallow,
                    InventoryLowStockActivity.Warn,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    true,
                    DateTimeOffset.UtcNow)
            ],
            [
                new InventoryManagedWarehouseSummary(
                    WarehouseId,
                    CompanyId,
                    "MAIN",
                    "Main Warehouse",
                    null,
                    true,
                    DateTimeOffset.UtcNow)
            ],
            recentAdjustments ?? []);
}
