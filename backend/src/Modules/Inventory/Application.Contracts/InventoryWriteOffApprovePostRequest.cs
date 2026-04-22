namespace Citus.Modules.Inventory.Application.Contracts;

public sealed record class InventoryWriteOffApprovePostRequest(
    Guid CompanyId,
    Guid UserId,
    Guid DocumentId);
