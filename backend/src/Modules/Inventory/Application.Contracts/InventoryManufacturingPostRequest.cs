namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryManufacturingPostRequest(
    CompanyId CompanyId,
    UserId UserId,
    Guid BomId,
    Guid WarehouseId,
    DateOnly PostingDate,
    decimal OutputQuantity,
    string? Memo);
