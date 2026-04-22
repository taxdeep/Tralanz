using Citus.Modules.Inventory.Application.Contracts;
using Citus.Modules.Inventory.Domain.Shared;

namespace Web.Shell.Services;

public static class ShellInventoryFoundationRules
{
    public static ShellInventoryFoundationRuleResult ValidatePolicySave(
        InventoryCostingPolicyUpdateRequest? request,
        InventoryFoundationDashboard? dashboard)
    {
        if (request is null)
        {
            return Fail("missing_request", "Inventory costing policy input is required.");
        }

        if (dashboard is null)
        {
            return Fail("missing_dashboard", "Inventory foundation context is unavailable.");
        }

        if (request.CompanyId != dashboard.Summary.CompanyId)
        {
            return Fail("company_mismatch", "Inventory costing policy must stay inside the active company.");
        }

        return Success();
    }

    public static ShellInventoryFoundationRuleResult ValidateItemSave(
        InventoryItemUpsertRequest? request,
        InventoryFoundationDashboard? dashboard)
    {
        if (request is null)
        {
            return Fail("missing_request", "Inventory item input is required.");
        }

        if (dashboard is null)
        {
            return Fail("missing_dashboard", "Inventory foundation context is unavailable.");
        }

        if (request.CompanyId != dashboard.Summary.CompanyId)
        {
            return Fail("company_mismatch", "Inventory item management must stay inside the active company.");
        }

        var normalizedCode = request.ItemCode.Trim().ToUpperInvariant();
        var normalizedName = request.Name.Trim();

        if (string.IsNullOrWhiteSpace(normalizedCode))
        {
            return Fail("missing_item_code", "Item code is required.");
        }

        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return Fail("missing_item_name", "Item name is required.");
        }

        if (dashboard.Items.Any(
                item => item.Id != request.ItemId &&
                        string.Equals(item.ItemCode.Trim(), normalizedCode, StringComparison.OrdinalIgnoreCase)))
        {
            return Fail("duplicate_item_code", "Another inventory item already uses this company-scoped item code.");
        }

        if (dashboard.Items.Any(
                item => item.Id != request.ItemId &&
                        string.Equals(item.Name.Trim(), normalizedName, StringComparison.OrdinalIgnoreCase)))
        {
            return Fail("duplicate_item_name", "Another inventory item already uses this company-scoped item name.");
        }

        if (request.ItemKind == InventoryItemKind.Stock &&
            request.ManageInventoryMethod == ManageInventoryMethod.DontManageStock)
        {
            return Fail("stock_requires_inventory_tracking", "Stock items must use an inventory-tracking method.");
        }

        if (request.ItemKind != InventoryItemKind.Stock &&
            request.ManageInventoryMethod != ManageInventoryMethod.DontManageStock)
        {
            return Fail("non_stock_tracking_forbidden", "Non-stock and service items must stay on the non-tracked inventory method in this minimal foundation flow.");
        }

        var existingItem = request.ItemId.HasValue
            ? dashboard.Items.FirstOrDefault(item => item.Id == request.ItemId.Value)
            : null;

        if (request.ItemKind == InventoryItemKind.Stock &&
            request.ManageInventoryMethod == ManageInventoryMethod.ManageStockBySku &&
            existingItem?.ManageInventoryMethod != ManageInventoryMethod.ManageStockBySku)
        {
            return Fail(
                "tracked_mode_guarded",
                "Tracked lot/serial inventory remains under an operational guardrail until tracked receipt, shipment, opening, transfer, and build flows are ready.");
        }

        if (request.ItemKind == InventoryItemKind.Stock)
        {
            if (string.IsNullOrWhiteSpace(request.StockUomCode))
            {
                return Fail("missing_stock_uom", "Stock items must define a stock UOM.");
            }

            if (!request.DefaultInventoryAssetAccountId.HasValue)
            {
                return Fail("missing_inventory_asset_account", "Stock items must define a default inventory asset account.");
            }

            if (!request.DefaultCogsAccountId.HasValue)
            {
                return Fail("missing_cogs_account", "Stock items must define a default COGS account.");
            }

            if (!request.DefaultWriteOffAccountId.HasValue)
            {
                return Fail("missing_writeoff_account", "Stock items must define a default write-off account.");
            }

            if (!request.DefaultPurchaseVarianceAccountId.HasValue)
            {
                return Fail("missing_purchase_variance_account", "Stock items must define a default purchase variance account.");
            }

            if (!dashboard.InventoryAssetAccountOptions.Any(option => option.AccountId == request.DefaultInventoryAssetAccountId.Value))
            {
                return Fail("invalid_inventory_asset_account", "The selected inventory asset account is not available for this company.");
            }

            if (!dashboard.ExpenseAccountOptions.Any(option => option.AccountId == request.DefaultCogsAccountId.Value))
            {
                return Fail("invalid_cogs_account", "The selected COGS account is not available for this company.");
            }

            if (!dashboard.ExpenseAccountOptions.Any(option => option.AccountId == request.DefaultWriteOffAccountId.Value))
            {
                return Fail("invalid_writeoff_account", "The selected write-off account is not available for this company.");
            }

            if (!dashboard.ExpenseAccountOptions.Any(option => option.AccountId == request.DefaultPurchaseVarianceAccountId.Value))
            {
                return Fail("invalid_purchase_variance_account", "The selected purchase variance account is not available for this company.");
            }
        }

        return Success();
    }

    public static ShellInventoryFoundationRuleResult ValidateItemActiveStateChange(
        InventoryManagedItemSummary? item,
        bool isActive,
        InventoryFoundationDashboard? dashboard)
    {
        if (item is null)
        {
            return Fail("missing_item", "The selected inventory item could not be found.");
        }

        if (dashboard is null)
        {
            return Fail("missing_dashboard", "Inventory foundation context is unavailable.");
        }

        if (item.CompanyId != dashboard.Summary.CompanyId)
        {
            return Fail("company_mismatch", "Inventory item management must stay inside the active company.");
        }

        return Success();
    }

    public static ShellInventoryFoundationRuleResult ValidateWarehouseSave(
        InventoryWarehouseUpsertRequest? request,
        InventoryFoundationDashboard? dashboard)
    {
        if (request is null)
        {
            return Fail("missing_request", "Warehouse input is required.");
        }

        if (dashboard is null)
        {
            return Fail("missing_dashboard", "Inventory foundation context is unavailable.");
        }

        if (request.CompanyId != dashboard.Summary.CompanyId)
        {
            return Fail("company_mismatch", "Warehouse management must stay inside the active company.");
        }

        var normalizedCode = request.WarehouseCode.Trim().ToUpperInvariant();
        var normalizedName = request.Name.Trim();

        if (string.IsNullOrWhiteSpace(normalizedCode))
        {
            return Fail("missing_warehouse_code", "Warehouse code is required.");
        }

        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return Fail("missing_warehouse_name", "Warehouse name is required.");
        }

        if (dashboard.Warehouses.Any(
                warehouse => warehouse.Id != request.WarehouseId &&
                             string.Equals(warehouse.WarehouseCode.Trim(), normalizedCode, StringComparison.OrdinalIgnoreCase)))
        {
            return Fail("duplicate_warehouse_code", "Another warehouse already uses this company-scoped warehouse code.");
        }

        if (dashboard.Warehouses.Any(
                warehouse => warehouse.Id != request.WarehouseId &&
                             string.Equals(warehouse.Name.Trim(), normalizedName, StringComparison.OrdinalIgnoreCase)))
        {
            return Fail("duplicate_warehouse_name", "Another warehouse already uses this company-scoped warehouse name.");
        }

        return Success();
    }

    public static ShellInventoryFoundationRuleResult ValidateWarehouseActiveStateChange(
        InventoryManagedWarehouseSummary? warehouse,
        bool isActive,
        InventoryFoundationDashboard? dashboard)
    {
        if (warehouse is null)
        {
            return Fail("missing_warehouse", "The selected warehouse could not be found.");
        }

        if (dashboard is null)
        {
            return Fail("missing_dashboard", "Inventory foundation context is unavailable.");
        }

        if (warehouse.CompanyId != dashboard.Summary.CompanyId)
        {
            return Fail("company_mismatch", "Warehouse management must stay inside the active company.");
        }

        if (!isActive && warehouse.IsActive && dashboard.Warehouses.Count(item => item.IsActive) <= 1)
        {
            return Fail("last_active_warehouse_protected", "At least one active warehouse must remain available.");
        }

        return Success();
    }

    private static ShellInventoryFoundationRuleResult Success() => new()
    {
        Succeeded = true
    };

    private static ShellInventoryFoundationRuleResult Fail(string errorCode, string errorMessage) => new()
    {
        Succeeded = false,
        ErrorCode = errorCode,
        ErrorMessage = errorMessage
    };
}

public sealed record class ShellInventoryFoundationRuleResult
{
    public bool Succeeded { get; init; }

    public string ErrorCode { get; init; } = string.Empty;

    public string ErrorMessage { get; init; } = string.Empty;
}
