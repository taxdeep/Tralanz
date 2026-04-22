using Citus.Modules.Inventory.Application.Contracts;

namespace Web.Shell.Services;

public static class ShellInventoryManufacturingRules
{
    public static ShellInventoryManufacturingRuleResult ValidateBom(
        InventoryBomUpsertRequest? request,
        InventoryManufacturingDashboard? dashboard)
    {
        if (request is null)
        {
            return Fail("missing_request", "Manufacturing BOM input is required.");
        }

        if (dashboard is null)
        {
            return Fail("missing_dashboard", "Manufacturing context is unavailable.");
        }

        if (request.CompanyId != dashboard.CompanyId)
        {
            return Fail("company_mismatch", "Manufacturing BOM must stay inside the active company.");
        }

        var outputItem = dashboard.ActiveItems.FirstOrDefault(item => item.Id == request.OutputItemId);
        if (outputItem is null)
        {
            return Fail("invalid_output_item", "Choose an active stock item as the BOM output.");
        }

        if (request.OutputQuantity <= 0)
        {
            return Fail("invalid_output_qty", "BOM output quantity must be greater than zero.");
        }

        if (request.Components is null || request.Components.Count == 0)
        {
            return Fail("missing_components", "At least one BOM component line is required.");
        }

        var seenLineNumbers = new HashSet<int>();
        var seenComponentIds = new HashSet<Guid>();
        foreach (var component in request.Components)
        {
            if (component.LineNo <= 0 || !seenLineNumbers.Add(component.LineNo))
            {
                return Fail("invalid_line_numbers", "BOM line numbers must be positive and unique.");
            }

            if (component.ComponentItemId == request.OutputItemId)
            {
                return Fail("self_component", "BOM output item cannot also appear as a component.");
            }

            if (!seenComponentIds.Add(component.ComponentItemId))
            {
                return Fail("duplicate_component", "BOM cannot contain the same component item more than once.");
            }

            if (!dashboard.ActiveItems.Any(item => item.Id == component.ComponentItemId))
            {
                return Fail("invalid_component_item", "Each BOM component must select an active stock item.");
            }

            if (component.Quantity <= 0)
            {
                return Fail("invalid_component_qty", "BOM component quantity must be greater than zero.");
            }

            if (component.WastagePercent < 0)
            {
                return Fail("invalid_wastage", "BOM wastage percent cannot be negative.");
            }
        }

        return Success();
    }

    public static ShellInventoryManufacturingRuleResult ValidatePost(
        InventoryManufacturingPostRequest? request,
        InventoryManufacturingDashboard? dashboard)
    {
        if (request is null)
        {
            return Fail("missing_request", "Manufacturing post input is required.");
        }

        if (dashboard is null)
        {
            return Fail("missing_dashboard", "Manufacturing context is unavailable.");
        }

        if (request.CompanyId != dashboard.CompanyId)
        {
            return Fail("company_mismatch", "Manufacturing post must stay inside the active company.");
        }

        if (!dashboard.ActiveWarehouses.Any(warehouse => warehouse.Id == request.WarehouseId))
        {
            return Fail("invalid_warehouse", "Choose an active warehouse before posting manufacturing.");
        }

        var bom = dashboard.Boms.FirstOrDefault(candidate => candidate.BomId == request.BomId);
        if (bom is null)
        {
            return Fail("invalid_bom", "Choose a valid BOM before posting manufacturing.");
        }

        if (!bom.IsActive)
        {
            return Fail("inactive_bom", "Selected BOM is inactive and cannot post manufacturing.");
        }

        if (request.OutputQuantity <= 0)
        {
            return Fail("invalid_output_qty", "Manufacturing output quantity must be greater than zero.");
        }

        return Success();
    }

    private static ShellInventoryManufacturingRuleResult Success() => new()
    {
        Succeeded = true
    };

    private static ShellInventoryManufacturingRuleResult Fail(string errorCode, string errorMessage) => new()
    {
        Succeeded = false,
        ErrorCode = errorCode,
        ErrorMessage = errorMessage
    };
}

public sealed record class ShellInventoryManufacturingRuleResult
{
    public bool Succeeded { get; init; }

    public string ErrorCode { get; init; } = string.Empty;

    public string ErrorMessage { get; init; } = string.Empty;
}
