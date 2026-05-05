namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryBomUpsertRequest(
    CompanyId CompanyId,
    UserId UserId,
    Guid? BomId,
    string BomCode,
    Guid OutputItemId,
    decimal OutputQuantity,
    bool IsActive,
    IReadOnlyList<InventoryBomComponentInput> Components);
