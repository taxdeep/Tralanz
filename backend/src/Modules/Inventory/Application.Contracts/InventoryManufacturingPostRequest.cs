namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryManufacturingPostRequest(
    Guid CompanyId,
    Guid UserId,
    Guid BomId,
    Guid WarehouseId,
    DateOnly PostingDate,
    decimal OutputQuantity,
    string? Memo);
